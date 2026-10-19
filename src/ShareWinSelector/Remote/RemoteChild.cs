using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShareWinSelector.Remote;

/// <summary>LAN の中で見つかった親。</summary>
internal sealed record FoundParent(string Name, string Address, int Port)
{
    public override string ToString() => $"{Name}  ({Address})";
}

/// <summary>子の今の状態。</summary>
internal enum RemoteChildState
{
    Connecting,
    Connected,
    Retrying,
    /// <summary>初めての親: 親の画面で許可されるのを待っている（詳細はペアリングの番号）。</summary>
    WaitingApproval,
    /// <summary>親の画面で拒否された。</summary>
    Rejected,
    /// <summary>ペアリングで覚えた親と鍵が違う（親が設定を消した、または別の PC）。</summary>
    KeyChanged,
}

/// <summary>
/// リモート連携の子: 親へつなぎに行き、合成した絵を JPEG にして送り続ける。切れたら数秒おきにつなぎ直す。
/// 通信は暗号化し（SecureChannel）、親の公開鍵がペアリングで覚えたものと同じかを確かめる。覚えた親がまだ無ければ、
/// ペアリングの番号を出して親の画面で許可されるのを待ち、許可されたらその親を覚える。
/// 絵は合成の側から SubmitFrame で渡される（最新の 1 枚だけ持つので、送るのが追いつかなくても溜まらない）。
/// </summary>
internal sealed class RemoteChild : IDisposable
{
    private const int JpegQuality = 80;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    // 絵が変わらなくても、この間隔で最後の絵を送り直す（つながっていることを親に知らせる）
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    // 親の画面で許可を押してもらうまで待つ時間（親の側の待ち時間より少し長く）
    private static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(75);
    // 拒否された・証明書が違うときは、すぐにはつなぎ直さない
    private static readonly TimeSpan RefusedRetryInterval = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _port;
    private readonly string _name;
    private readonly RemoteIdentity _identity;
    private string? _pinnedParent;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private (byte[] Pixels, int Width, int Height)? _latest;
    private TcpClient? _client;

    /// <summary>状態が変わった（送る側のスレッドから通知）。詳細は親の住所、待っているときはペアリングの番号。</summary>
    public event Action<RemoteChildState, string>? StateChanged;

    /// <summary>初めての親に許可された（親の公開鍵の指紋）。覚えておいて、次からはこの親だけにつなぐ。</summary>
    public event Action<string>? ParentPaired;

    /// <summary>親の一覧でチェックが切り替えられた（子の PC でのウィンドウの番号・共有するか）。受け取る側のスレッドから通知。</summary>
    public event Action<long, bool>? ShareRequested;

    // 親へ送るウィンドウの一覧（JSON）。送ったものと違えば送る
    private byte[]? _windowList;
    private byte[]? _sentWindowList;

    /// <summary>この PC のウィンドウの一覧を渡す（UI スレッドから）。変わっていれば親へ送る。</summary>
    public void SubmitWindowList(IReadOnlyList<RemoteWindow> windows)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(windows);
        lock (_gate) _windowList = json;
        _frameReady.Set();
    }

    /// <param name="pinnedParent">ペアリングで覚えた親の公開鍵の指紋（無ければ null＝初めてつなぐ親として許可を待つ）。</param>
    public RemoteChild(string host, int port, string name, RemoteIdentity identity, string? pinnedParent)
    {
        _host = host;
        _port = port;
        _name = name;
        _identity = identity;
        _pinnedParent = pinnedParent;
        // JPEG への変換に WPF の画像機能を使うので STA で動かす
        _thread = new Thread(Run) { IsBackground = true, Name = "RemoteChild" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public string Host => _host;

    /// <summary>新しく合成した絵を渡す（合成のスレッドから）。前の絵がまだ送られていなければ捨てて、こちらを送る。</summary>
    public void SubmitFrame(byte[] pixels, int width, int height)
    {
        lock (_gate) _latest = (pixels, width, height);
        _frameReady.Set();
    }

    private void Run()
    {
        bool loggedFailure = false;
        while (!_stop.IsSet)
        {
            try
            {
                StateChanged?.Invoke(RemoteChildState.Connecting, _host);
                using var client = new TcpClient();
                if (!client.ConnectAsync(_host, _port).Wait(ConnectTimeout))
                    throw new TimeoutException($"{_host}:{_port} につながりませんでした");
                client.NoDelay = true;
                lock (_gate) _client = client;
                // 暗号化する。親の公開鍵はペアリングで覚えたものと比べて確かめる
                using var raw = client.GetStream();
                using var stream = SecureChannel.Connect(raw, _identity);
                byte[] parentKey = stream.PeerPublicKey;
                string parentThumbprint = RemoteIdentity.ThumbprintOf(parentKey);
                if (_pinnedParent is { } pinned && pinned != parentThumbprint)
                {
                    // 覚えた親と違う: つながない（親が設定を消したなら、ペアリングを解除してつなぎ直してもらう）
                    Log.Info($"リモート連携: 親 {_host} の鍵がペアリングしたものと違うので、つなぎません");
                    StateChanged?.Invoke(RemoteChildState.KeyChanged, _host);
                    client.Close();
                    _stop.Wait(RefusedRetryInterval);
                    continue;
                }
                var hello = JsonSerializer.SerializeToUtf8Bytes(new RemoteHello(_name, MainWindow.FullVersion));
                stream.Write(RemoteProtocol.Hello, hello);
                if (_pinnedParent is null)
                {
                    // 初めての親: 両方の画面に同じ番号を出して、親の画面で許可されるのを待つ
                    StateChanged?.Invoke(RemoteChildState.WaitingApproval, RemoteIdentity.PairCode(parentKey, _identity.PublicKey));
                    client.ReceiveTimeout = (int)ApprovalWait.TotalMilliseconds;
                }
                var answer = stream.Read();
                client.ReceiveTimeout = 0;
                if (answer is not { Type: RemoteProtocol.Accepted })
                {
                    Log.Info($"リモート連携: 親 {_host} につなぐのを断られました");
                    StateChanged?.Invoke(RemoteChildState.Rejected, _host);
                    client.Close();
                    _stop.Wait(RefusedRetryInterval);
                    continue;
                }
                if (_pinnedParent is null)
                {
                    _pinnedParent = parentThumbprint;
                    ParentPaired?.Invoke(parentThumbprint);
                    Log.Info($"リモート連携: 親 {_host} とペアリングしました");
                }
                StateChanged?.Invoke(RemoteChildState.Connected, _host);
                Log.Info($"リモート連携: 親 {_host}:{_port} につながりました");
                loggedFailure = false;

                // 親からの「共有する／外す」は別のスレッドで受け取る（こちらは送り続ける）
                var channel = stream;
                var reader = new Thread(() => ReceiveRequests(channel)) { IsBackground = true, Name = "RemoteChild.Receive" };
                reader.Start();
                lock (_gate) _sentWindowList = null; // つなぎ直したら、一覧は必ず送り直す

                byte[]? lastJpeg = null;
                var lastFrameSent = System.Diagnostics.Stopwatch.StartNew();
                while (!_stop.IsSet)
                {
                    _frameReady.WaitOne(KeepAliveInterval);
                    if (_stop.IsSet) break;
                    (byte[] Pixels, int Width, int Height)? frame;
                    byte[]? list = null;
                    lock (_gate)
                    {
                        frame = _latest;
                        _latest = null;
                        if (_windowList is not null && (_sentWindowList is null || !_windowList.AsSpan().SequenceEqual(_sentWindowList)))
                            list = _sentWindowList = _windowList;
                    }
                    if (list is not null) stream.Write(RemoteProtocol.WindowList, list);
                    if (frame is { } f) lastJpeg = JpegCodec.Encode(f.Pixels, f.Width, f.Height, JpegQuality);
                    // 新しい絵は送る。無ければ 2 秒ごとに最後の絵を送り直す（つながっている確認。一覧だけ変わったときは送らない）
                    if (lastJpeg is not null && (frame is not null || lastFrameSent.Elapsed >= KeepAliveInterval))
                    {
                        stream.Write(RemoteProtocol.Frame, lastJpeg);
                        lastFrameSent.Restart();
                    }
                }
            }
            catch (Exception ex) when (!_stop.IsSet)
            {
                // つながらない間は同じエラーでログを埋めない
                if (!loggedFailure) Log.Error($"リモート連携: 親 {_host}:{_port} との通信", ex);
                loggedFailure = true;
                StateChanged?.Invoke(RemoteChildState.Retrying, _host);
                _stop.Wait(RetryInterval);
            }
            catch
            {
                // 止めるときに通信を切ったことで出た例外
            }
            finally
            {
                lock (_gate) _client = null;
            }
        }
    }

    /// <summary>親からの「共有する／外す」を受け取る（つながっている間だけ。切れたら終わる）。</summary>
    private void ReceiveRequests(SecureChannel channel)
    {
        try
        {
            while (!_stop.IsSet && channel.Read() is { } message)
            {
                if (message.Type != RemoteProtocol.SetShared) continue;
                if (JsonSerializer.Deserialize<RemoteShareRequest>(message.Payload) is { } request)
                    ShareRequested?.Invoke(request.Id, request.Shared);
            }
        }
        catch
        {
            // 切れたとき（送る側のループがつなぎ直す）
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _frameReady.Set();
        lock (_gate) _client?.Close();
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// LAN の中の親を探す。各ネットワークの「全員宛て」と、この PC 自身に呼びかけて、返事を少し待つ。
    /// </summary>
    public static async Task<List<FoundParent>> FindParentsAsync(int port, TimeSpan wait)
    {
        var found = new List<FoundParent>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        // 誰もいない宛先（この PC 自身など）への呼びかけに「届かない」と返事が来ると、Windows は次の受信を
        // 「接続が強制的に切断された」エラーにしてしまい、本物の親の返事を待てなくなる。その扱いを切る（SIO_UDP_CONNRESET）
        const int SIO_UDP_CONNRESET = -1744830452;
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
        var request = Encoding.UTF8.GetBytes(RemoteProtocol.FindRequest);
        var targets = new HashSet<IPAddress> { IPAddress.Loopback };
        if (!RemoteProtocol.LoopbackOnly)
        {
            targets.Add(IPAddress.Broadcast);
            foreach (var b in SubnetBroadcasts()) targets.Add(b);
        }
        foreach (var target in targets)
        {
            try { await udp.SendAsync(request, new IPEndPoint(target, port)); }
            catch (SocketException) { /* 送れないネットワークは飛ばす */ }
        }
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(cts.Token); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // 念のため: 上の設定が効かない環境でも、待ち続ける
                }
                string text = Encoding.UTF8.GetString(reply.Buffer);
                if (!text.StartsWith(RemoteProtocol.FoundPrefix, StringComparison.Ordinal)) continue;
                var parts = text[RemoteProtocol.FoundPrefix.Length..].Split('|');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int tcpPort)) continue;
                // 同じ親が「全員宛て」と「この PC 自身」の両方に返事をすると 2 回並ぶので、名前とポートでまとめる。
                // そのときは 127.0.0.1 より LAN の住所を残す（ほかの PC からも同じ住所でつなげるように）
                var parent = new FoundParent(parts[0], reply.RemoteEndPoint.Address.ToString(), tcpPort);
                int same = found.FindIndex(f => f.Name == parent.Name && f.Port == parent.Port);
                if (same < 0) found.Add(parent);
                else if (IPAddress.IsLoopback(IPAddress.Parse(found[same].Address)) && !IPAddress.IsLoopback(reply.RemoteEndPoint.Address)) found[same] = parent;
            }
        }
        catch (OperationCanceledException)
        {
        }
        return found;
    }

    /// <summary>この PC がつながっている各 IPv4 ネットワークの「全員宛て」の住所。</summary>
    private static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null) continue;
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }
}
