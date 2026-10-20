using System.Buffers.Binary;
using System.IO;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携の通信の決まりごと。
/// TCP: 子が親へつなぎに行き、まず暗号化する（SecureChannel。お互いの公開鍵を出し合い、ペアリングで覚えた相手かを確かめる）。
/// その中で、子が自己紹介を送り、親と子がそれぞれ許可を送り合ったら（初めての相手なら、両方の画面で番号を確かめてから）、
/// 子が合成した絵（JPEG）を送る。許可が出るまでは、絵・一覧・サムネイル・切り替えのどれも送らない。
/// 各メッセージは [種類 1 バイト][長さ 4 バイト][中身]。
/// UDP: 子が LAN 全体に「親いますか」と呼びかけ、親が名前とポートを返す。
/// </summary>
internal static class RemoteProtocol
{
    /// <summary>子 → 親: 子の名前など（JSON）。つないだ直後に 1 回。</summary>
    public const byte Hello = 1;

    /// <summary>子 → 親: 合成した絵（JPEG）。変わったときと、つながっている確認のため 2 秒ごと。</summary>
    public const byte Frame = 2;

    /// <summary>親 ⇄ 子: つないでよい（ペアリング済み、またはその PC の画面で許可された）。</summary>
    public const byte Accepted = 3;

    /// <summary>親 ⇄ 子: つながない（その PC の画面で拒否された、または時間切れ）。</summary>
    public const byte Rejected = 4;

    /// <summary>子 → 親: 子のウィンドウの一覧（JSON の RemoteWindow の配列）。変わったときに送る。</summary>
    public const byte WindowList = 5;

    /// <summary>親 → 子: このウィンドウを共有する／外す（JSON の RemoteShareRequest）。</summary>
    public const byte SetShared = 6;

    /// <summary>親 → 子: サムネイルを送るか（1 バイト、1 = 送る）。親がサムネイル表示にしている間だけ送ってもらう。</summary>
    public const byte ThumbnailsWanted = 7;

    /// <summary>子 → 親: ウィンドウのサムネイル（子の PC でのウィンドウの番号 8 バイト ＋ JPEG）。変わったものだけ数秒おきに。</summary>
    public const byte Thumbnail = 8;

    /// <summary>
    /// 親 → 子: 親がこの子を覚えているか（1 バイト、1 = 覚えている）。自己紹介への返事として 1 回。
    /// どちらかが相手を覚えていなければ、両方の画面に番号を出して許可してもらう（片方だけ解除したときも見比べられるように）。
    /// </summary>
    public const byte PeerStatus = 9;

    /// <summary>親 → 子: 全部外す（子の画面の「全部外す」と同じ。元に戻す用に、外したものは子が覚えておく）。</summary>
    public const byte UncheckAll = 10;

    /// <summary>親 → 子: 全部外したのを元に戻す。</summary>
    public const byte RestoreAll = 11;

    /// <summary>
    /// 親 ⇄ 子: ペアリングを解除した（受け取った側も相手を忘れる。次につなぐときは番号の確認から）。
    /// 子が「つなぎますか？」で［ペアリングを解除］を選んだときは、許可の答えの代わりにこれを送る。
    /// </summary>
    public const byte Unpair = 12;

    /// <summary>1 通の上限（壊れたデータで巨大な配列を作らないように）。</summary>
    public const int MaxMessage = 32 * 1024 * 1024;

    /// <summary>両方の許可がそろう前に受け取る 1 通の上限（自己紹介・返事だけなので小さく）。</summary>
    public const int PreAuthMaxMessage = 64 * 1024;

    /// <summary>ウィンドウの一覧 1 通の上限。</summary>
    public const int MaxWindowListBytes = 2 * 1024 * 1024;

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

    /// <summary>1 通読む。相手が切ったら null。maxLength より長いものは受け取らない。</summary>
    public static (byte Type, byte[] Payload)? Read(Stream stream, int maxLength = MaxMessage)
    {
        var header = new byte[5];
        if (!ReadExactly(stream, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > maxLength) throw new InvalidDataException($"大きすぎるメッセージ: {length} バイト");
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

    /// <summary>
    /// ペアリングの答えをそろえる: 自分の答え（local。画面で許可・拒否、許可済みの相手ならすぐ true）を相手に送り、相手の答えを受け取る。
    /// 両方とも許可なら true。自分の答えを待つ間に、相手が切った・断ってきたら、待つのをやめて false（cancelLocal で窓を閉じてもらう）。
    /// </summary>
    /// <param name="refusalType">断るときに送る種類（既定は Rejected。子がペアリングを解除したときは Unpair）。</param>
    /// <param name="peerAnswer">相手の答えの種類を受け取る（相手がペアリングを解除したかを知るため）。</param>
    public static bool Agree(SecureChannel channel, System.Net.Sockets.TcpClient client, Task<bool> local, TimeSpan localWait, TimeSpan peerWait,
        Func<bool> running, CancellationTokenSource cancelLocal, Func<byte>? refusalType = null, Action<byte>? peerAnswer = null)
    {
        bool? peer = null;
        bool mine;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (local.IsCompleted)
            {
                mine = local.IsCompletedSuccessfully && local.Result;
                break;
            }
            if (!running() || waited.Elapsed > localWait)
            {
                mine = false;
                break;
            }
            if (peer is null && client.Available > 0)
            {
                // 相手が先に答えた。断られたなら、こちらの窓はもう要らない
                var early = channel.Read(PreAuthMaxMessage);
                if (early is { } e) peerAnswer?.Invoke(e.Type);
                peer = early is { Type: Accepted };
                if (peer == false)
                {
                    mine = false;
                    break;
                }
            }
            else if (IsClosed(client))
            {
                mine = false;
                break;
            }
            ((IAsyncResult)local).AsyncWaitHandle.WaitOne(200);
        }
        cancelLocal.Cancel();
        if (!mine)
        {
            Refuse(channel, client, refusalType?.Invoke() ?? Rejected);
            return false;
        }
        channel.Write(Accepted, []);
        if (peer is null)
        {
            client.ReceiveTimeout = (int)peerWait.TotalMilliseconds;
            var answer = channel.Read(PreAuthMaxMessage);
            if (answer is { } a) peerAnswer?.Invoke(a.Type);
            peer = answer is { Type: Accepted };
        }
        return peer == true;
    }

    /// <summary>
    /// 断ったことを伝えて、相手が受け取るのを少し待つ。相手が送ってきたものを読まずに閉じると接続がリセットされ、
    /// 相手は「断られた」を読めずに、ただの通信の失敗としてすぐつなぎ直してしまう（窓が何度も出る）。
    /// </summary>
    private static void Refuse(SecureChannel channel, System.Net.Sockets.TcpClient client, byte type)
    {
        try
        {
            channel.Write(type, []);
            client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            client.ReceiveTimeout = 2000;
            for (int i = 0; i < 8 && channel.Read(PreAuthMaxMessage) is not null; i++) { }
        }
        catch
        {
            // 届かなくてもよい
        }
    }

    /// <summary>相手が切ったか（受け取れるデータが無いのに「読める」になっていれば、切れている）。</summary>
    private static bool IsClosed(System.Net.Sockets.TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && socket.Available == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>子から届いたウィンドウの一覧を読み、数・文字・番号の重なりを整える。</summary>
    public static List<RemoteWindow> ReadWindowList(byte[] json, int maxCount)
    {
        if (json.Length > MaxWindowListBytes) throw new InvalidDataException($"大きすぎる一覧: {json.Length} バイト");
        var windows = System.Text.Json.JsonSerializer.Deserialize<List<RemoteWindow?>>(json) ?? [];
        return windows.OfType<RemoteWindow>()
            .DistinctBy(w => w.Id)
            .Take(maxCount)
            .Select(w => w with { Title = CleanText(w.Title, 260), Process = CleanText(w.Process, 64) })
            .ToList();
    }

    /// <summary>相手から届いた文字（名前・タイトルなど）を、画面に出してよい形にする: 制御文字を除き、長すぎれば切る。</summary>
    public static string CleanText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char c in text)
        {
            if (sb.Length >= maxLength) break;
            if (char.IsControl(c) || c is '‎' or '‏' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩')) continue;
            sb.Append(c);
        }
        // サロゲートの片割れで終わらないように
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString();
    }
}

/// <summary>子が最初に送る自己紹介。KnowsParent は、子がこの親を覚えているか（ペアリング済みか）。</summary>
internal sealed record RemoteHello(string Name, string Version, bool KnowsParent = false);

/// <summary>
/// 子のウィンドウ 1 つ分（親の一覧に並べる）。Id は子の PC でのウィンドウの番号。
/// Pending は、共有に選ばれているが、子の画面で確認・クロップの範囲を決めるのを待っている（まだ相手に見えていない）。
/// </summary>
internal sealed record RemoteWindow(long Id, string Title, string Process, bool Shared, bool Pending = false);

/// <summary>
/// 親から子への「共有する／外す」。CropDefault は親の「上をクロップ」
/// （子は、そのアプリを個別に決めていなければ、これと自分の「上をクロップ」のどちらかがオンならクロップする）。
/// </summary>
internal sealed record RemoteShareRequest(long Id, bool Shared, bool? CropDefault = null);
