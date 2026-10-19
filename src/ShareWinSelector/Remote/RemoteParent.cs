using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携の親: 子からの接続を待ち、届いた絵を渡す。子は一度に 1 台（新しくつないできたら前の子は切る）。
/// LAN の中で「親いますか」と聞かれたら、名前とポートを返す。
/// </summary>
internal sealed class RemoteParent : IDisposable
{
    // 子は 2 秒ごとに送ってくるので、これだけ何も来なければ切れたとみなす
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private readonly int _port;
    private readonly string _name;
    private readonly TcpListener _listener;
    private readonly UdpClient _udp;
    private readonly object _gate = new();
    private volatile bool _running = true;
    private TcpClient? _current;

    /// <summary>子がつながって最初の絵が届いた（名前・絵の大きさ）。受け取る側のスレッドから通知。</summary>
    public event Action<string, int, int>? ChildConnected;

    /// <summary>子が切れた。受け取る側のスレッドから通知。</summary>
    public event Action? ChildDisconnected;

    /// <summary>子の絵が届いた（BGRA の画素・幅・高さ）。受け取る側のスレッドから通知。</summary>
    public event Action<byte[], int, int>? FrameReceived;

    public RemoteParent(int port, string name)
    {
        _port = port;
        _name = name;
        var address = RemoteProtocol.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        _listener = new TcpListener(address, port);
        _listener.Start();
        try
        {
            _udp = new UdpClient(new IPEndPoint(address, port));
            // 返事の宛先がもう閉じていたとき、次の受信がエラーにならないようにする（SIO_UDP_CONNRESET を切る）
            const int SIO_UDP_CONNRESET = -1744830452;
            try { _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
        }
        catch
        {
            _listener.Stop();
            throw;
        }
        new Thread(AcceptLoop) { IsBackground = true, Name = "RemoteParent.Accept" }.Start();
        new Thread(DiscoveryLoop) { IsBackground = true, Name = "RemoteParent.Discovery" }.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (_running) Thread.Sleep(500); continue; }
            // 新しい子が来たら前の子は切る（一度に 1 台）
            TcpClient? old;
            lock (_gate)
            {
                old = _current;
                _current = client;
            }
            old?.Close();
            // 絵の変換に WPF の画像機能を使うので STA で受け取る
            var thread = new Thread(() => Receive(client)) { IsBackground = true, Name = "RemoteParent.Receive" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private void Receive(TcpClient client)
    {
        bool announced = false;
        string childName = "?";
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds;
            using var stream = client.GetStream();
            var magic = new byte[RemoteProtocol.Magic.Length];
            if (!RemoteProtocol.ReadExactly(stream, magic) || !magic.AsSpan().SequenceEqual(RemoteProtocol.Magic)) return;
            while (_running && IsCurrent(client))
            {
                if (RemoteProtocol.Read(stream) is not { } message) break;
                switch (message.Type)
                {
                    case RemoteProtocol.Hello:
                        childName = JsonSerializer.Deserialize<RemoteHello>(message.Payload)?.Name ?? "?";
                        Log.Info($"リモート連携: 子「{childName}」（{client.Client.RemoteEndPoint}）がつながりました");
                        break;
                    case RemoteProtocol.Frame:
                        var (pixels, width, height) = JpegCodec.Decode(message.Payload);
                        if (!IsCurrent(client)) return;
                        if (!announced)
                        {
                            announced = true;
                            ChildConnected?.Invoke(childName, width, height);
                        }
                        FrameReceived?.Invoke(pixels, width, height);
                        break;
                }
            }
        }
        catch (Exception ex) when (_running && IsCurrent(client))
        {
            Log.Error($"リモート連携: 子「{childName}」との通信", ex);
        }
        catch
        {
            // 止めたとき・新しい子に替わったときに切ったことで出た例外
        }
        finally
        {
            client.Close();
            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = ReferenceEquals(_current, client);
                if (wasCurrent) _current = null;
            }
            // 新しい子に替わったときは「切れた」を出さない（すぐ次の子がつながる）
            if (announced && wasCurrent && _running)
            {
                Log.Info($"リモート連携: 子「{childName}」が切れました");
                ChildDisconnected?.Invoke();
            }
        }
    }

    private bool IsCurrent(TcpClient client)
    {
        lock (_gate) return ReferenceEquals(_current, client);
    }

    private void DiscoveryLoop()
    {
        var reply = Encoding.UTF8.GetBytes($"{RemoteProtocol.FoundPrefix}{_name.Replace('|', '/')}|{_port}");
        while (_running)
        {
            try
            {
                IPEndPoint? from = null;
                var data = _udp.Receive(ref from);
                if (Encoding.UTF8.GetString(data) == RemoteProtocol.FindRequest && from is not null)
                    _udp.Send(reply, reply.Length, from);
            }
            catch
            {
                if (_running) Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _udp.Close(); } catch { }
        TcpClient? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }
        current?.Close();
    }
}
