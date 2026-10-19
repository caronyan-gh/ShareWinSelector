using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携の暗号化した通信路。TLS と同じ考え方を、.NET の標準の暗号部品だけで組んだもの
/// （Windows の TLS は鍵を保管庫に置く必要があり、落ちたときにファイルが残るため使わない）。
///
/// 手順（子＝つなぎに行く側、親＝待つ側）:
///   1. 子 → 親: 印と「封筒」＝子の値（公開鍵・使い捨ての鍵交換用の公開鍵・乱数）のハッシュだけ
///   2. 親 → 子: 親の値（公開鍵・使い捨ての鍵交換用の公開鍵・名前）と、親の乱数の「封筒」（ハッシュ）、ここまでへの親の署名
///   3. 子 → 親: 封筒の中身（子の値）と、ここまでへの子の署名。親は中身が封筒と合うかを確かめる
///   4. 親 → 子: 親の乱数そのもの。子は親の封筒と合うかを確かめる
/// 署名で「相手がその公開鍵の持ち主であること」を確かめ、使い捨ての鍵交換（ECDH）で共通の鍵を作る。
/// 以降のメッセージは、向きごとに別の鍵で AES-GCM により暗号化・改ざん検知する（番号は 1 通ごとに増やし、使い回さない）。
///
/// ペアリングの番号（6 桁）は、両方の値と両方の乱数から作る。どちらも相手の値を見る前に自分の値を封じているので、
/// 番号をそろえるために値を選び直すことはできない（ZRTP と同じ考え方）。
/// また親は、相手の値と身元を受け取って確かめてから自分の乱数を明かす（4）。番号が分かるのはそのあとなので、
/// 呼び出し側は 4 の前に「初めての相手には少し間をあける」などの制限をかけられる（beforeReveal）。
/// こうしないと、割り込んだ人が親につないでは番号を見て切る、を何万回も繰り返して、番号がそろうまで粘れてしまう。
/// 相手の公開鍵が本物の相手のものかは、初回はこの番号を両方の画面で見比べ、以降はペアリングで覚えた指紋と比べる（呼び出し側）。
/// </summary>
internal sealed class SecureChannel : IDisposable
{
    private const byte ClientCommit = 20, ServerHello = 21, ClientReveal = 22, ServerReveal = 23;
    private const int NonceLength = 32;
    private const int PointLength = 65;
    private const int TagLength = 16;
    // 手順の中のメッセージは数百バイトなので、それより大きいものは受け取らない（認証前に大きな配列を作らせない）
    private const int HandshakeMaxLength = 4096;
    private static ReadOnlySpan<byte> HandshakeMagic => "SWS5"u8;

    private readonly Stream _stream;
    private readonly AesGcm _send, _receive;
    private readonly object _sendGate = new();
    private ulong _sendCounter, _receiveCounter;

    /// <summary>相手の公開鍵（SubjectPublicKeyInfo 形式）。</summary>
    public byte[] PeerPublicKey { get; }

    /// <summary>相手が名乗った名前（子から見た親の名前。親から見ると空）。</summary>
    public string PeerName { get; }

    /// <summary>ペアリングの番号（6 桁）。両方の画面に同じ番号が出ていれば、間に誰も割り込んでいない。</summary>
    public string PairCode { get; }

    private SecureChannel(Stream stream, byte[] sendKey, byte[] receiveKey, byte[] peerPublicKey, string peerName, string pairCode)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagLength);
        _receive = new AesGcm(receiveKey, TagLength);
        PeerPublicKey = peerPublicKey;
        PeerName = peerName;
        PairCode = pairCode;
    }

    /// <summary>子（つなぎに行く側）として通信路を作る。</summary>
    public static SecureChannel Connect(Stream stream, RemoteIdentity identity)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var mine = Concat(WithLength(identity.PublicKey), PointOf(ephemeral), nonce);
        var commit = Concat(HandshakeMagic.ToArray(), SHA256.HashData(Concat("SWS5 commit"u8.ToArray(), mine)));
        RemoteProtocol.Write(stream, ClientCommit, commit);

        var server = Expect(stream, ServerHello);
        var reader = new Reader(server);
        var serverPublic = reader.ReadWithLength();
        var serverPoint = ReadPoint(reader);
        var serverNonceHash = reader.Read(32);
        var serverName = RemoteProtocol.CleanText(Encoding.UTF8.GetString(reader.ReadWithLength()), 64);
        int signedLength = reader.Position;
        var serverSignature = reader.ReadWithLength();
        var serverPart = server[..signedLength];
        Verify(serverPublic, Concat("SWS5 parent"u8.ToArray(), commit, serverPart), serverSignature, "親");

        var signature = identity.Key.SignData(Concat("SWS5 child"u8.ToArray(), commit, server, mine), HashAlgorithmName.SHA256);
        var reveal = Concat(mine, WithLength(signature));
        RemoteProtocol.Write(stream, ClientReveal, reveal);

        // 親の乱数が、親が最初に封じたものと合うか
        var serverNonce = Expect(stream, ServerReveal);
        if (serverNonce.Length != NonceLength ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Concat("SWS5 nonce"u8.ToArray(), serverNonce)), serverNonceHash))
            throw new CryptographicException("親の乱数が最初に封じたものと違います");

        var (childToParent, parentToChild) = DeriveKeys(ephemeral, serverPoint, commit, server, reveal, serverNonce);
        return new SecureChannel(stream, childToParent, parentToChild, serverPublic, serverName, CodeOf(mine, serverPart, serverNonce));
    }

    /// <summary>
    /// 親（待つ側）として通信路を作る。name は子に名乗る名前。
    /// beforeReveal は、子の値と身元（公開鍵）を確かめたあと、番号が決まる親の乱数を明かす前に呼ぶ（待たせる・例外で断る）。
    /// </summary>
    public static SecureChannel Accept(Stream stream, RemoteIdentity identity, string name, Action<byte[]>? beforeReveal = null)
    {
        var commit = Expect(stream, ClientCommit);
        if (commit.Length != HandshakeMagic.Length + 32 || !commit.AsSpan(0, HandshakeMagic.Length).SequenceEqual(HandshakeMagic))
            throw new InvalidDataException("印が違います（別のアプリか古い版）");

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var nonceHash = SHA256.HashData(Concat("SWS5 nonce"u8.ToArray(), nonce));
        var serverPart = Concat(WithLength(identity.PublicKey), PointOf(ephemeral), nonceHash, WithLength(Encoding.UTF8.GetBytes(name)));
        var signature = identity.Key.SignData(Concat("SWS5 parent"u8.ToArray(), commit, serverPart), HashAlgorithmName.SHA256);
        var server = Concat(serverPart, WithLength(signature));
        RemoteProtocol.Write(stream, ServerHello, server);

        var reveal = Expect(stream, ClientReveal);
        var reader = new Reader(reveal);
        var clientPublic = reader.ReadWithLength();
        var clientPoint = ReadPoint(reader);
        reader.Read(NonceLength);
        int mineLength = reader.Position;
        var clientSignature = reader.ReadWithLength();
        var mine = reveal[..mineLength];
        // 封筒の中身が、最初に送られてきた封筒（ハッシュ）と合うか
        var expected = SHA256.HashData(Concat("SWS5 commit"u8.ToArray(), mine));
        if (!CryptographicOperations.FixedTimeEquals(expected, commit.AsSpan(HandshakeMagic.Length)))
            throw new CryptographicException("子の値が最初に封じたものと違います");
        Verify(clientPublic, Concat("SWS5 child"u8.ToArray(), commit, server, mine), clientSignature, "子");

        // ここで初めて番号が決まる乱数を明かす（その前に、呼び出し側が相手に応じて待たせる・断る）
        beforeReveal?.Invoke(clientPublic);
        RemoteProtocol.Write(stream, ServerReveal, nonce);

        var (childToParent, parentToChild) = DeriveKeys(ephemeral, clientPoint, commit, server, reveal, nonce);
        return new SecureChannel(stream, parentToChild, childToParent, clientPublic, "", CodeOf(mine, serverPart, nonce));
    }

    private static byte[] Expect(Stream stream, byte type)
    {
        var message = RemoteProtocol.Read(stream, HandshakeMaxLength) ?? throw new EndOfStreamException("手順の途中で切れました");
        return message.Type == type ? message.Payload : throw new InvalidDataException("手順の中で想定外のメッセージ");
    }

    private static void Verify(byte[] publicKey, byte[] data, byte[] signature, string who)
    {
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
        if (verifier.KeySize != 256 || !verifier.VerifyData(data, signature, HashAlgorithmName.SHA256))
            throw new CryptographicException($"{who}の署名が正しくありません");
    }

    /// <summary>ペアリングの番号: 子と親の値と、両方の乱数から 6 桁を作る。</summary>
    private static string CodeOf(byte[] childPart, byte[] serverPart, byte[] serverNonce)
    {
        var hash = SHA256.HashData(Concat("SWS5 pair code"u8.ToArray(), childPart, serverPart, serverNonce));
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6");
    }

    /// <summary>使い捨ての鍵交換で共通の秘密を作り、手順の中身全体と混ぜて、向きごとの鍵にする。</summary>
    private static (byte[] ChildToParent, byte[] ParentToChild) DeriveKeys(ECDiffieHellman ephemeral, byte[] peerPoint, byte[] commit, byte[] server, byte[] reveal, byte[] serverNonce)
    {
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPoint[1..33], Y = peerPoint[33..65] },
        });
        var secret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        var salt = SHA256.HashData(Concat(commit, server, reveal, serverNonce));
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, "SWS5 session keys"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return (keys[..32], keys[32..]);
    }

    /// <summary>暗号化して 1 通送る（いくつのスレッドから呼んでもよい。受け取るのは 1 つのスレッドだけ）。</summary>
    public void Write(byte type, ReadOnlySpan<byte> payload)
    {
        lock (_sendGate) WriteCore(type, payload);
    }

    private void WriteCore(byte type, ReadOnlySpan<byte> payload)
    {
        int length = 1 + payload.Length + TagLength;
        var buffer = new byte[4 + length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, length);
        var plain = new byte[1 + payload.Length];
        plain[0] = type;
        payload.CopyTo(plain.AsSpan(1));
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sendCounter++);
        _send.Encrypt(nonce, plain, buffer.AsSpan(4, plain.Length), buffer.AsSpan(4 + plain.Length, TagLength), buffer.AsSpan(0, 4));
        _stream.Write(buffer);
        _stream.Flush();
    }

    /// <summary>1 通受け取って戻す。相手が切ったら null。改ざんされていたら例外。maxLength より長いものは受け取らない。</summary>
    public (byte Type, byte[] Payload)? Read(int maxLength = RemoteProtocol.MaxMessage)
    {
        var header = new byte[4];
        if (!RemoteProtocol.ReadExactly(_stream, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 + TagLength || length > maxLength + 1 + TagLength) throw new InvalidDataException($"おかしな長さ: {length}");
        var body = new byte[length];
        if (!RemoteProtocol.ReadExactly(_stream, body)) return null;
        var plain = new byte[length - TagLength];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _receiveCounter++);
        _receive.Decrypt(nonce, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain, header);
        return (plain[0], plain[1..]);
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }

    // --- 小道具 ---

    private static byte[] PointOf(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false).Q;
        return Concat([0x04], p.X!, p.Y!);
    }

    /// <summary>鍵交換用の公開鍵（圧縮しない形の曲線上の点）を読む。形が違えば切る（曲線上かは取り込むときに確かめられる）。</summary>
    private static byte[] ReadPoint(Reader reader)
    {
        var point = reader.Read(PointLength);
        if (point[0] != 0x04) throw new InvalidDataException("鍵交換用の公開鍵の形が違います");
        return point;
    }

    private static byte[] WithLength(byte[] data)
    {
        var result = new byte[2 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        data.CopyTo(result, 2);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }

        public byte[] Read(int count)
        {
            if (count < 0 || Position + count > data.Length) throw new InvalidDataException("短すぎるメッセージ");
            var result = data[Position..(Position + count)];
            Position += count;
            return result;
        }

        public byte[] ReadWithLength()
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(Read(2));
            return Read(length);
        }
    }
}
