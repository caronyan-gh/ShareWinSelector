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
internal enum RemoteChildState { Connecting, Connected, Retrying }

/// <summary>
/// リモート連携の子: 親へつなぎに行き、合成した絵を JPEG にして送り続ける。切れたら数秒おきにつなぎ直す。
/// 絵は合成の側から SubmitFrame で渡される（最新の 1 枚だけ持つので、送るのが追いつかなくても溜まらない）。
/// </summary>
internal sealed class RemoteChild : IDisposable
{
    private const int JpegQuality = 80;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    // 絵が変わらなくても、この間隔で最後の絵を送り直す（つながっていることを親に知らせる）
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);

    private readonly string _host;
    private readonly int _port;
    private readonly string _name;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private (byte[] Pixels, int Width, int Height)? _latest;
    private TcpClient? _client;

    /// <summary>状態が変わった（送る側のスレッドから通知）。</summary>
    public event Action<RemoteChildState, string>? StateChanged;

    public RemoteChild(string host, int port, string name)
    {
        _host = host;
        _port = port;
        _name = name;
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
                using var stream = client.GetStream();
                stream.Write(RemoteProtocol.Magic);
                var hello = JsonSerializer.SerializeToUtf8Bytes(new RemoteHello(_name, MainWindow.FullVersion));
                RemoteProtocol.Write(stream, RemoteProtocol.Hello, hello);
                StateChanged?.Invoke(RemoteChildState.Connected, _host);
                Log.Info($"リモート連携: 親 {_host}:{_port} につながりました");
                loggedFailure = false;

                byte[]? lastJpeg = null;
                while (!_stop.IsSet)
                {
                    _frameReady.WaitOne(KeepAliveInterval);
                    if (_stop.IsSet) break;
                    (byte[] Pixels, int Width, int Height)? frame;
                    lock (_gate)
                    {
                        frame = _latest;
                        _latest = null;
                    }
                    if (frame is { } f) lastJpeg = JpegCodec.Encode(f.Pixels, f.Width, f.Height, JpegQuality);
                    // 新しい絵が無ければ最後の絵を送り直す（2 秒ごと、つながっている確認）
                    if (lastJpeg is not null) RemoteProtocol.Write(stream, RemoteProtocol.Frame, lastJpeg);
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
                var reply = await udp.ReceiveAsync(cts.Token);
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
