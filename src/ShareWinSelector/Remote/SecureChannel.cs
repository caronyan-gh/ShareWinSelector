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
///   1. 子 → 親: 印・子の公開鍵・使い捨ての鍵交換用の公開鍵・乱数
///   2. 親 → 子: 親の公開鍵・使い捨ての鍵交換用の公開鍵・乱数・ここまでの内容への親の署名
///   3. 子 → 親: ここまでの内容への子の署名
/// 署名で「相手がその公開鍵の持ち主であること」を確かめ、使い捨ての鍵交換（ECDH）で共通の鍵を作る。
/// 以降のメッセージは、向きごとに別の鍵で AES-GCM により暗号化・改ざん検知する（番号は 1 通ごとに増やし、使い回さない）。
/// 相手の公開鍵が本物の相手のものかは、ペアリングで覚えた指紋と比べて確かめる（呼び出し側）。
/// </summary>
internal sealed class SecureChannel : IDisposable
{
    private const byte ClientHello = 10, ServerHello = 11, ClientFinish = 12;
    private const int NonceLength = 32;
    private const int TagLength = 16;
    private static ReadOnlySpan<byte> HandshakeMagic => "SWS3"u8;

    private readonly Stream _stream;
    private readonly AesGcm _send, _receive;
    private ulong _sendCounter, _receiveCounter;

    /// <summary>相手の公開鍵（SubjectPublicKeyInfo 形式）。</summary>
    public byte[] PeerPublicKey { get; }

    private SecureChannel(Stream stream, byte[] sendKey, byte[] receiveKey, byte[] peerPublicKey)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagLength);
        _receive = new AesGcm(receiveKey, TagLength);
        PeerPublicKey = peerPublicKey;
    }

    /// <summary>子（つなぎに行く側）として通信路を作る。</summary>
    public static SecureChannel Connect(Stream stream, RemoteIdentity identity)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var hello = Concat(HandshakeMagic.ToArray(), WithLength(identity.PublicKey), PointOf(ephemeral), nonce);
        RemoteProtocol.Write(stream, ClientHello, hello);

        var server = RemoteProtocol.Read(stream) is { Type: ServerHello } m ? m.Payload : throw new InvalidDataException("親から想定外の返事");
        var reader = new Reader(server);
        var serverPublic = reader.ReadWithLength();
        var serverPoint = reader.Read(65);
        var serverNonce = reader.Read(NonceLength);
        int signedLength = reader.Position;
        var serverSignature = reader.ReadWithLength();
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(serverPublic, out _);
            if (!verifier.VerifyData(Concat("SWS3 parent"u8.ToArray(), hello, server[..signedLength]), serverSignature, HashAlgorithmName.SHA256))
                throw new CryptographicException("親の署名が正しくありません");
        }
        var signature = identity.Key.SignData(Concat("SWS3 child"u8.ToArray(), hello, server), HashAlgorithmName.SHA256);
        RemoteProtocol.Write(stream, ClientFinish, WithLength(signature));

        var (childToParent, parentToChild) = DeriveKeys(ephemeral, serverPoint, hello, server);
        return new SecureChannel(stream, childToParent, parentToChild, serverPublic);
    }

    /// <summary>親（待つ側）として通信路を作る。</summary>
    public static SecureChannel Accept(Stream stream, RemoteIdentity identity)
    {
        var hello = RemoteProtocol.Read(stream) is { Type: ClientHello } m ? m.Payload : throw new InvalidDataException("子から想定外のメッセージ");
        var reader = new Reader(hello);
        if (!reader.Read(HandshakeMagic.Length).AsSpan().SequenceEqual(HandshakeMagic)) throw new InvalidDataException("印が違います（別のアプリか古い版）");
        var clientPublic = reader.ReadWithLength();
        var clientPoint = reader.Read(65);
        reader.Read(NonceLength);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var signed = Concat(WithLength(identity.PublicKey), PointOf(ephemeral), nonce);
        var signature = identity.Key.SignData(Concat("SWS3 parent"u8.ToArray(), hello, signed), HashAlgorithmName.SHA256);
        var server = Concat(signed, WithLength(signature));
        RemoteProtocol.Write(stream, ServerHello, server);

        var finish = RemoteProtocol.Read(stream) is { Type: ClientFinish } f ? f.Payload : throw new InvalidDataException("子から想定外のメッセージ");
        var clientSignature = new Reader(finish).ReadWithLength();
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(clientPublic, out _);
            if (!verifier.VerifyData(Concat("SWS3 child"u8.ToArray(), hello, server), clientSignature, HashAlgorithmName.SHA256))
                throw new CryptographicException("子の署名が正しくありません");
        }

        var (childToParent, parentToChild) = DeriveKeys(ephemeral, clientPoint, hello, server);
        return new SecureChannel(stream, parentToChild, childToParent, clientPublic);
    }

    /// <summary>使い捨ての鍵交換で共通の秘密を作り、手順の中身全体と混ぜて、向きごとの鍵にする。</summary>
    private static (byte[] ChildToParent, byte[] ParentToChild) DeriveKeys(ECDiffieHellman ephemeral, byte[] peerPoint, byte[] hello, byte[] server)
    {
        using var peer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = peerPoint[1..33], Y = peerPoint[33..65] },
        });
        var secret = ephemeral.DeriveRawSecretAgreement(peer.PublicKey);
        var salt = SHA256.HashData(Concat(hello, server));
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, "SWS3 session keys"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return (keys[..32], keys[32..]);
    }

    /// <summary>暗号化して 1 通送る。</summary>
    public void Write(byte type, ReadOnlySpan<byte> payload)
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

    /// <summary>1 通受け取って戻す。相手が切ったら null。改ざんされていたら例外。</summary>
    public (byte Type, byte[] Payload)? Read()
    {
        var header = new byte[4];
        if (!RemoteProtocol.ReadExactly(_stream, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 + TagLength || length > RemoteProtocol.MaxMessage) throw new InvalidDataException($"おかしな長さ: {length}");
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
            if (Position + count > data.Length) throw new InvalidDataException("短すぎるメッセージ");
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
