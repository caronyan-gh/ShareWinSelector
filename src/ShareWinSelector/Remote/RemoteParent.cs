using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShareWinSelector.Remote;

/// <summary>
/// リモート連携の親: 子からの接続を待ち、届いた絵を渡す。子は一度に 1 台。
/// 通信は暗号化し（SecureChannel）、子の公開鍵がペアリングで許可したものかを確かめる。どちらかが相手を覚えていなければ、
/// ペアリングの番号を出して画面で許可を求める（Approve。子の画面でも同じ番号で許可してもらう）。
/// 新しい子は、確かめが済んで両方が許可してから、今の子と入れ替える（確かめの途中の接続で、今の子を切らない）。
/// 確かめの途中の接続は数本まで、画面で許可を待つものは 1 本だけ。どの段階にも時間の上限がある。
/// 初めての相手に番号を決める乱数を明かすのは、少し間をあけてから（番号がそろうまでつなぎ直して粘られないように）。
/// 接続ごとに番号（セッション番号）を付けて知らせるので、受け取る側は古い接続の知らせを捨てられる。
/// LAN の中で「親いますか」と聞かれたら、名前とポートを返す。
/// </summary>
internal sealed class RemoteParent : IDisposable
{
    // 子は 2 秒ごとに送ってくるので、これだけ何も来なければ切れたとみなす
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);
    // 暗号化の手順と自己紹介が済むまでの時間（全体で。初めての相手を待たせる分も含む）
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(20);
    // 子が受け取らなくなったら、送るのを諦める時間
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    // 画面で許可を押してもらうまで待つ時間（過ぎたら拒否）
    private static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(60);
    // 子の画面で許可を押してもらうまで待つ時間（子の側の待ち時間より少し長く）
    private static readonly TimeSpan PeerAnswerWait = TimeSpan.FromSeconds(75);
    // 初めての相手に番号の乱数を明かす間隔と、それ以上は待たせずに断る長さ
    private static readonly TimeSpan UnknownRevealInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UnknownRevealMaxWait = TimeSpan.FromSeconds(10);
    // 確かめの途中で同時に受ける接続の数
    private const int MaxHandshakes = 4;
    private const int MaxWindows = 500;

    private readonly int _port;
    private readonly string _name;
    private readonly RemoteIdentity _identity;
    private readonly Func<string, bool> _isKnownChild;
    private readonly Func<string, string, string, CancellationToken, Task<bool>> _approve;
    private readonly TcpListener _listener;
    private readonly UdpClient _udp;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _running = true;
    private int _handshakes;
    // 画面で許可を待っている接続（一度に 1 つ）と、許可済みでつながっている子
    private TcpClient? _pending;
    private Session? _current;
    // 接続の番号。親を作り直しても重ならないよう、通しで振る（受け取る側は、今より小さい番号の知らせを古いものとして捨てる）
    private static long s_lastSessionId;
    private volatile bool _thumbnailsWanted;
    private readonly object _revealGate = new();
    private DateTime _nextUnknownReveal = DateTime.MinValue;
    private DateTime _lastHandshakeLog = DateTime.MinValue;

    /// <summary>子と両方の許可がそろい、今の子になった（セッション番号・子の公開鍵の指紋・子の名前）。受け取る側のスレッドから通知。</summary>
    public event Action<long, string, string>? SessionStarted;

    /// <summary>子の最初の絵が届いた（セッション番号・名前・絵の大きさ）。受け取る側のスレッドから通知。</summary>
    public event Action<long, string, int, int>? ChildConnected;

    /// <summary>子が切れた、または別の子に替わった（セッション番号）。受け取る側のスレッドから通知。</summary>
    public event Action<long>? ChildDisconnected;

    /// <summary>子の絵が届いた（セッション番号・BGRA の画素・幅・高さ）。受け取る側のスレッドから通知。</summary>
    public event Action<long, byte[], int, int>? FrameReceived;

    /// <summary>子のウィンドウの一覧が届いた（セッション番号・一覧。数・文字は整えてある）。受け取る側のスレッドから通知。</summary>
    public event Action<long, IReadOnlyList<RemoteWindow>>? WindowListReceived;

    /// <summary>子のウィンドウのサムネイルが届いた（セッション番号・子の PC でのウィンドウの番号・画像）。受け取る側のスレッドから通知。</summary>
    public event Action<long, long, System.Windows.Media.ImageSource>? ThumbnailReceived;

    /// <summary>子にサムネイルを送ってもらうか（親がサムネイル表示の間だけ true）。UI スレッドから。</summary>
    public void SetThumbnailsWanted(bool wanted)
    {
        _thumbnailsWanted = wanted;
        CurrentSession()?.Send(RemoteProtocol.ThumbnailsWanted, [(byte)(wanted ? 1 : 0)]);
    }

    /// <summary>
    /// 子のウィンドウを共有する／外すよう頼む（UI スレッドから。送るのは別のスレッド）。つながっていなければ何もしない。
    /// cropDefault は親の「上をクロップ」（共有するときだけ）。
    /// </summary>
    public void RequestShare(long id, bool shared, bool? cropDefault = null) =>
        CurrentSession()?.Send(RemoteProtocol.SetShared, JsonSerializer.SerializeToUtf8Bytes(new RemoteShareRequest(id, shared, cropDefault)));

    /// <summary>子に「全部外す」を頼む（子の画面の「全部外す」と同じ。元に戻す用に子が覚えておく）。</summary>
    public void RequestUncheckAll() => CurrentSession()?.Send(RemoteProtocol.UncheckAll, []);

    /// <summary>子に「全部外したのを元に戻す」を頼む。</summary>
    public void RequestRestoreAll() => CurrentSession()?.Send(RemoteProtocol.RestoreAll, []);

    /// <param name="isKnownChild">この子（公開鍵の指紋）をペアリングで覚えているか。受け取る側のスレッドから呼ばれる。</param>
    /// <param name="approve">
    /// 子をつないでよいか（子の公開鍵の指紋・子の名前・ペアリングの番号・取り消し）。画面で確かめてから答える
    /// （親と子の両方が相手を覚えていれば呼ばない）。子が切れたり時間が過ぎたりしたら取り消される（窓を閉じる）。
    /// 受け取る側のスレッドから呼ばれる。
    /// </param>
    public RemoteParent(int port, string name, RemoteIdentity identity, Func<string, bool> isKnownChild,
        Func<string, string, string, CancellationToken, Task<bool>> approve)
    {
        _port = port;
        _name = name;
        _identity = identity;
        _isKnownChild = isKnownChild;
        _approve = approve;
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
            // 確かめの途中の接続が多すぎれば、新しいものは断る（今つながっている子はそのまま）
            if (Interlocked.Increment(ref _handshakes) > MaxHandshakes)
            {
                Interlocked.Decrement(ref _handshakes);
                client.Close();
                continue;
            }
            // 絵の変換に WPF の画像機能を使うので STA で受け取る
            var thread = new Thread(() => Receive(client)) { IsBackground = true, Name = "RemoteParent.Receive" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private void Receive(TcpClient client)
    {
        Session? session = null;
        bool handshaking = true;
        string childName = "?";
        using var approval = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        // 段階ごとの時間の上限（1 回の読み取りごとではなく全体で）。過ぎたら接続を閉じて、読み取りの途中でも終わらせる
        using var deadline = new Timer(_ => client.Close(), null, HandshakeDeadline, Timeout.InfiniteTimeSpan);
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = (int)HandshakeDeadline.TotalMilliseconds;
            client.SendTimeout = (int)SendTimeout.TotalMilliseconds;
            // 暗号化する。子の公開鍵は、ペアリングで許可したものかをあとで見る
            using var raw = client.GetStream();
            using var channel = SecureChannel.Accept(raw, _identity, _name, BeforeReveal);
            if (channel.Read(RemoteProtocol.PreAuthMaxMessage) is not { Type: RemoteProtocol.Hello } message) return;
            var hello = JsonSerializer.Deserialize<RemoteHello>(message.Payload);
            childName = RemoteProtocol.CleanText(hello?.Name, 64);
            if (childName.Length == 0) childName = "?";
            string thumbprint = RemoteIdentity.ThumbprintOf(channel.PeerPublicKey);
            bool knowsChild = _isKnownChild(thumbprint);
            // 親がこの子を覚えているかを伝える（どちらかが覚えていなければ、両方の画面で番号を確かめる）
            channel.Write(RemoteProtocol.PeerStatus, [(byte)(knowsChild ? 1 : 0)]);
            bool bothKnown = knowsChild && hello?.KnowsParent == true;
            Log.Info($"リモート連携: 子「{childName}」（{client.Client.RemoteEndPoint}）がつなごうとしています");

            Task<bool> decision;
            if (bothKnown)
            {
                decision = Task.FromResult(true);
            }
            else
            {
                // 画面で許可を待つのは 1 本だけ（別の窓が出ている間は断る）
                lock (_gate)
                {
                    if (_pending is not null) return;
                    _pending = client;
                }
                decision = _approve(thumbprint, childName, channel.PairCode, approval.Token);
            }
            deadline.Change(ApprovalWait + PeerAnswerWait + TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            if (!RemoteProtocol.Agree(channel, client, decision, ApprovalWait, PeerAnswerWait, () => _running, approval))
            {
                Log.Info($"リモート連携: 子「{childName}」とはつながりませんでした（どちらかが断った、または時間切れ）");
                return;
            }
            deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            client.ReceiveTimeout = (int)ReceiveTimeout.TotalMilliseconds;

            // ここで初めて、今の子と入れ替える。番号は入れ替えと同じロックの中で振る（今の子がいつも一番大きい番号になる）
            Session? old;
            lock (_gate)
            {
                if (!_running) return;
                session = new Session(Interlocked.Increment(ref s_lastSessionId), client, channel);
                old = _current;
                _current = session;
                if (ReferenceEquals(_pending, client)) _pending = null;
            }
            handshaking = false;
            Interlocked.Decrement(ref _handshakes);
            if (old is not null)
            {
                old.Close();
                ChildDisconnected?.Invoke(old.Id);
            }
            SessionStarted?.Invoke(session.Id, thumbprint, childName);
            session.Start();
            session.Send(RemoteProtocol.ThumbnailsWanted, [(byte)(_thumbnailsWanted ? 1 : 0)]);

            while (_running && IsCurrent(session))
            {
                if (channel.Read() is not { } m) break;
                switch (m.Type)
                {
                    case RemoteProtocol.Thumbnail when m.Payload.Length > 8:
                        long id = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(m.Payload);
                        // 一覧に無いウィンドウのものは、広げる前に捨てる
                        if (!session.WindowIds.Contains(id)) break;
                        var image = JpegCodec.DecodeImage(m.Payload[8..]);
                        if (IsCurrent(session)) ThumbnailReceived?.Invoke(session.Id, id, image);
                        break;
                    case RemoteProtocol.WindowList:
                        var windows = RemoteProtocol.ReadWindowList(m.Payload, MaxWindows);
                        session.WindowIds = windows.Select(w => w.Id).ToHashSet();
                        if (IsCurrent(session)) WindowListReceived?.Invoke(session.Id, windows);
                        break;
                    case RemoteProtocol.Frame:
                        var (pixels, width, height) = JpegCodec.Decode(m.Payload);
                        if (!IsCurrent(session)) return;
                        if (!session.Announced)
                        {
                            session.Announced = true;
                            ChildConnected?.Invoke(session.Id, childName, width, height);
                        }
                        FrameReceived?.Invoke(session.Id, pixels, width, height);
                        break;
                }
            }
        }
        catch (Exception ex) when (_running && session is not null && IsCurrent(session))
        {
            Log.Error($"リモート連携: 子「{childName}」との通信", ex);
        }
        catch (Exception ex) when (_running && session is null)
        {
            // 確かめの途中で切れた（ポートを調べられた、古い版、時間切れなど）。ログを埋めないよう、ときどき 1 行だけ
            var now = DateTime.UtcNow;
            if (now - _lastHandshakeLog > TimeSpan.FromSeconds(30))
            {
                _lastHandshakeLog = now;
                Log.Info($"リモート連携: 確かめの途中で切れた接続がありました（{ex.GetType().Name}: {ex.Message}）");
            }
        }
        catch
        {
            // 止めたとき・新しい子に替わったときに切ったことで出た例外
        }
        finally
        {
            if (handshaking) Interlocked.Decrement(ref _handshakes);
            approval.Cancel();
            if (session is not null) session.Close();
            else client.Close();
            bool wasCurrent;
            lock (_gate)
            {
                if (ReferenceEquals(_pending, client)) _pending = null;
                wasCurrent = session is not null && ReferenceEquals(_current, session);
                if (wasCurrent) _current = null;
            }
            // 新しい子に替わったときは、入れ替えた側が「切れた」を出している
            if (session is not null && wasCurrent && _running)
            {
                Log.Info($"リモート連携: 子「{childName}」が切れました");
                ChildDisconnected?.Invoke(session.Id);
            }
        }
    }

    /// <summary>
    /// 番号を決める乱数を明かす前に呼ばれる。覚えていない相手には、前の相手から少し間をあけてから明かす
    /// （割り込んだ人が、つないでは番号を見て切る、を繰り返して番号をそろえるのを、1 回の窓の間に数十回までに抑える）。
    /// 覚えている子は待たせない。
    /// </summary>
    private void BeforeReveal(byte[] childKey)
    {
        if (_isKnownChild(RemoteIdentity.ThumbprintOf(childKey))) return;
        TimeSpan wait;
        lock (_revealGate)
        {
            var now = DateTime.UtcNow;
            var at = _nextUnknownReveal > now ? _nextUnknownReveal : now;
            wait = at - now;
            if (wait > UnknownRevealMaxWait) throw new IOException("初めての相手からの接続が続いているので断りました");
            _nextUnknownReveal = at + UnknownRevealInterval;
        }
        if (wait > TimeSpan.Zero) _stopping.Token.WaitHandle.WaitOne(wait);
        if (!_running) throw new OperationCanceledException();
    }

    private Session? CurrentSession()
    {
        lock (_gate) return _current;
    }

    private bool IsCurrent(Session session)
    {
        lock (_gate) return ReferenceEquals(_current, session);
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
        _stopping.Cancel();
        try { _listener.Stop(); } catch { }
        try { _udp.Close(); } catch { }
        Session? current;
        TcpClient? pending;
        lock (_gate)
        {
            current = _current;
            pending = _pending;
            _current = null;
            _pending = null;
        }
        current?.Close();
        pending?.Close();
    }

    /// <summary>
    /// 許可済みでつながっている子 1 台分。親から子へ送るものは、専用のスレッドが順に送る
    /// （UI スレッドで送ると、子が受け取らなくなったときに画面が固まるため）。溜まりすぎたら子が受け取れていないとみなして切る。
    /// </summary>
    private sealed class Session(long id, TcpClient client, SecureChannel channel)
    {
        private const int QueueLimit = 4096;
        private readonly BlockingCollection<(byte Type, byte[] Payload)> _queue = new(QueueLimit);

        public long Id { get; } = id;

        /// <summary>最初の絵が届いて「つながった」を出したか。</summary>
        public volatile bool Announced;

        /// <summary>子から届いた一覧にあるウィンドウの番号（受け取る側のスレッドだけが使う）。</summary>
        public HashSet<long> WindowIds = [];

        public void Start() => new Thread(SendLoop) { IsBackground = true, Name = "RemoteParent.Send" }.Start();

        public void Send(byte type, byte[] payload)
        {
            bool added;
            try { added = _queue.TryAdd((type, payload)); }
            catch (InvalidOperationException) { return; } // もう閉じた
            if (!added)
            {
                Log.Info("リモート連携: 子が受け取らないので切ります");
                Close();
            }
        }

        private void SendLoop()
        {
            try
            {
                foreach (var (type, payload) in _queue.GetConsumingEnumerable())
                    channel.Write(type, payload);
            }
            catch
            {
                // 切れた（受け取る側のスレッドが後始末する）
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            client.Close();
        }
    }
}
