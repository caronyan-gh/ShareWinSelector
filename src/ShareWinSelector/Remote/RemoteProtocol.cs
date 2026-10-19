using System.Buffers.Binary;
using System.IO;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携の通信の決まりごと。
/// TCP: 子が親へつなぎに行き、まず暗号化する（SecureChannel。お互いの公開鍵を出し合い、ペアリングで覚えた相手かを確かめる）。
/// その中で、子が自己紹介を送り、親が許可（初めての子なら画面で確かめてから）を返したら、合成した絵（JPEG）を送る。
/// 各メッセージは [種類 1 バイト][長さ 4 バイト][中身]。
/// UDP: 子が LAN 全体に「親いますか」と呼びかけ、親が名前とポートを返す。
/// </summary>
internal static class RemoteProtocol
{
    /// <summary>子 → 親: 子の名前など（JSON）。つないだ直後に 1 回。</summary>
    public const byte Hello = 1;

    /// <summary>子 → 親: 合成した絵（JPEG）。変わったときと、つながっている確認のため 2 秒ごと。</summary>
    public const byte Frame = 2;

    /// <summary>親 → 子: つないでよい（ペアリング済み、または親の画面で許可された）。</summary>
    public const byte Accepted = 3;

    /// <summary>親 → 子: つながない（親の画面で拒否された、または時間切れ）。</summary>
    public const byte Rejected = 4;

    /// <summary>1 通の上限（壊れたデータで巨大な配列を作らないように）。</summary>
    public const int MaxMessage = 32 * 1024 * 1024;

    public const string FindRequest = "SWS-FIND/1";
    public const string FoundPrefix = "SWS-HERE/1|";

    /// <summary>
    /// 開発用（Debug 版で環境変数 SWS_REMOTE_LOOPBACK=1）: この PC の中だけでつなぐ。
    /// 1 台で親と子を確かめるとき、LAN へ口を開けない（ファイアウォールの確認も出さない）。
    /// </summary>
    public static bool LoopbackOnly { get; } =
#if DEBUG
        Environment.GetEnvironmentVariable("SWS_REMOTE_LOOPBACK") == "1";
#else
        false;
#endif

    public static void Write(Stream stream, byte type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>1 通読む。相手が切ったら null。</summary>
    public static (byte Type, byte[] Payload)? Read(Stream stream)
    {
        var header = new byte[5];
        if (!ReadExactly(stream, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxMessage) throw new InvalidDataException($"大きすぎるメッセージ: {length} バイト");
        var payload = new byte[length];
        if (!ReadExactly(stream, payload)) return null;
        return (header[0], payload);
    }

    public static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>子が最初に送る自己紹介。</summary>
internal sealed record RemoteHello(string Name, string Version);
