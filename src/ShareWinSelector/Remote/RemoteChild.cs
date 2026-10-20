using System.IO;
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
    /// <summary>初めての親: 親とこの PC の画面で許可されるのを待っている（詳細はペアリングの番号）。</summary>
    WaitingApproval,
    /// <summary>親かこの PC の画面で拒否された、または時間切れ。</summary>
    Rejected,
    /// <summary>ペアリングで覚えた親と鍵が違う（親が設定を消した、または別の PC）。</summary>
    KeyChanged,
    /// <summary>この PC の画面で、親につなぐか確かめている。</summary>
    Confirming,
    /// <summary>この PC の画面でつなぐのをやめた（キャンセル・ペアリングを解除）。特殊設定の［つなぐ］か次の起動まで、自分からはつながない。</summary>
    Paused,
    /// <summary>ペアリングを解除した（この PC の特殊設定で、または親から知らされた）。特殊設定の［つなぐ］か次の起動まで、自分からはつながない。</summary>
    Unpaired,
}

/// <summary>「親につなぎますか？」の答え。</summary>
internal enum RemoteConnectChoice
{
    Connect,
    Unpair,
    Cancel,
}

/// <summary>
/// リモート連携の子: 親へつなぎに行き、合成した絵を JPEG にして送り続ける。切れたら数秒おきにつなぎ直す。
/// 通信は暗号化し（SecureChannel）、親の公開鍵がペアリングで覚えたものと同じかを確かめる。どちらかが相手を覚えていなければ、
/// ペアリングの番号を両方の画面に出して、親とこの PC の両方で許可されるのを待ち、そろったらその親を覚える。
/// 両方の許可がそろうまでは、絵・一覧・サムネイルのどれも送らない。
/// 絵は合成の側から SubmitFrame で渡される（最新の 1 枚だけ持つので、送るのが追いつかなくても溜まらない）。
/// </summary>
internal sealed class RemoteChild : IDisposable
{
    private const int JpegQuality = 80;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    // 暗号化の手順と親の返事が済むまでの時間（全体で。親が初めての相手を少し待たせる分も含む）
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(20);
    // 親が受け取らなくなったら、送るのを諦める時間
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    // 絵が変わらなくても、この間隔で最後の絵を送り直す（つながっていることを親に知らせる）
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    // この PC の画面で許可を押してもらうまで待つ時間（窓は 58 秒で閉じる）
    private static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(60);
    // 親の画面で許可を押してもらうまで待つ時間（親の側の待ち時間より少し長く）
    private static readonly TimeSpan PeerAnswerWait = TimeSpan.FromSeconds(75);
    // 拒否された・鍵が違うときは、すぐにはつなぎ直さない
    private static readonly TimeSpan RefusedRetryInterval = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _port;
    private readonly string _name;
    private readonly RemoteIdentity _identity;
    private readonly Func<string, string, CancellationToken, Task<bool>> _confirm;
    private readonly Func<string, CancellationToken, Task<RemoteConnectChoice>>? _askConnect;
    private readonly bool _startStopped;
    // 親でペアリングが解除された（このあと切れても、自分からはつなぎ直さない。つなぎ直すと両方の画面に番号の窓が出てしまう）
    private volatile bool _unpairedByParent;
    // この起動の中で一度つないだか（つなぎ直しでは「つなぎますか？」を聞かない）
    private bool _connectedOnce;
    private string? _pinnedParent;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private (byte[] Pixels, int Width, int Height)? _latest;
    private TcpClient? _client;

    /// <summary>状態が変わった（送る側のスレッドから通知）。詳細は親の住所、待っているときはペアリングの番号。</summary>
    public event Action<RemoteChildState, string>? StateChanged;

    /// <summary>初めての親と両方の許可がそろった（親の公開鍵の指紋）。覚えておいて、次からはこの親だけにつなぐ。</summary>
    public event Action<string>? ParentPaired;

    /// <summary>
    /// 親の一覧でチェックが切り替えられた（子の PC でのウィンドウの番号・共有するか・親の「上をクロップ」）。受け取る側のスレッドから通知。
    /// </summary>
    public event Action<long, bool, bool?>? ShareRequested;

    /// <summary>ペアリングを解除した（この PC の「つなぎますか？」で、または親から知らせが来た）。親を忘れる。通知はどちらかのスレッドから。</summary>
    public event Action? ParentUnpaired;

    /// <summary>今の親に「ペアリングを解除した」を伝える（UI スレッドから。このあとすぐ止めるので、その場で送る）。</summary>
    public void SendUnpair()
    {
        SecureChannel? channel;
        lock (_gate) channel = _channel;
        try { channel?.Write(RemoteProtocol.Unpair, []); }
        catch { }
    }

    /// <summary>親から「共有する／外す」「全部外す」「元に戻す」を受け付けるか（一覧を送らない設定ではオフ）。</summary>
    public volatile bool AcceptParentRequests = true;

    /// <summary>親で「全部外す」が押された。受け取る側のスレッドから通知。</summary>
    public event Action? UncheckAllRequested;

    /// <summary>親で「元に戻す」が押された。受け取る側のスレッドから通知。</summary>
    public event Action? RestoreAllRequested;

    // 親へ送るウィンドウの一覧（JSON）。送ったものと違えば送る
    private byte[]? _windowList;
    private byte[]? _sentWindowList;
    private long[] _windowIds = [];

    // サムネイル: 親がサムネイル表示の間だけ、数秒おきに変わったものを送る。1 回に撮るのは数枚まで（順番に回す）
    private static readonly TimeSpan ThumbnailInterval = TimeSpan.FromSeconds(5);
    // 撮っていないウィンドウが残っているときの間隔（つないだ直後に、全部そろうまで）
    private static readonly TimeSpan ThumbnailCatchUpInterval = TimeSpan.FromSeconds(1.5);
    private const int ThumbnailsPerRound = 8;
    private const int ThumbnailWidth = 320, ThumbnailHeight = 180, ThumbnailQuality = 70;
    private volatile bool _thumbnailsWanted;
    private volatile bool _thumbnailsReset = true;
    private readonly AutoResetEvent _thumbnailWake = new(false);
    private SecureChannel? _channel;

    /// <summary>この PC のウィンドウの一覧を渡す（UI スレッドから）。変わっていれば親へ送る。</summary>
    public void SubmitWindowList(IReadOnlyList<RemoteWindow> windows)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(windows);
        lock (_gate)
        {
            _windowList = json;
            _windowIds = windows.Select(w => w.Id).ToArray();
        }
        _frameReady.Set();
    }

    /// <summary>
    /// サムネイルを撮って送り続ける（親がサムネイル表示にしている間だけ）。前に送った絵と同じなら送らない。
    /// 撮る処理（PrintWindow）は相手のアプリに描かせるので、送る側とは別のスレッドで、1 回に数枚ずつ順番に行う。
    /// </summary>
    private void ThumbnailLoop()
    {
        var sent = new Dictionary<long, string>();
        int cursor = 0;
        var wait = ThumbnailInterval;
        while (!_stop.IsSet)
        {
            _thumbnailWake.WaitOne(wait);
            wait = ThumbnailInterval;
            if (_stop.IsSet) break;
            SecureChannel? channel;
            long[] ids;
            lock (_gate)
            {
                channel = _channel;
                ids = _windowIds;
            }
            if (_thumbnailsReset)
            {
                // つなぎ直したとき・サムネイル表示になったときは、全部送り直す
                sent.Clear();
                _thumbnailsReset = false;
            }
            if (channel is null || !_thumbnailsWanted || ids.Length == 0) continue;
            try
            {
                // まだ送っていないものを先に、そのあとは順番に。1 回に撮るのは ThumbnailsPerRound 枚まで
                var unsent = ids.Where(id => !sent.ContainsKey(id)).ToList();
                var round = unsent.Take(ThumbnailsPerRound).ToList();
                for (int i = 0; round.Count < Math.Min(ThumbnailsPerRound, ids.Length) && i < ids.Length; i++)
                {
                    long id = ids[(cursor + i) % ids.Length];
                    if (!round.Contains(id)) round.Add(id);
                }
                cursor = (cursor + ThumbnailsPerRound) % ids.Length;
                foreach (long id in round)
                {
                    if (_stop.IsSet || !_thumbnailsWanted) break;
                    if (WindowSnapshot.Capture((IntPtr)id, ThumbnailWidth, ThumbnailHeight) is not { } snap)
                    {
                        sent[id] = ""; // 撮れない（最小化など）。次の順番でまた見る
                        continue;
                    }
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snap.Pixels));
                    if (sent.TryGetValue(id, out var last) && last == hash) continue;
                    var jpeg = JpegCodec.Encode(snap.Pixels, snap.Width, snap.Height, ThumbnailQuality);
                    var payload = new byte[8 + jpeg.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload, id);
                    jpeg.CopyTo(payload, 8);
                    channel.Write(RemoteProtocol.Thumbnail, payload);
                    sent[id] = hash;
                }
                // 閉じたウィンドウの分は忘れる
                foreach (var gone in sent.Keys.Except(ids).ToList()) sent.Remove(gone);
                if (unsent.Count > ThumbnailsPerRound) wait = ThumbnailCatchUpInterval;
            }
            catch (Exception ex)
            {
                // 通信が切れたなど（送る側のループがつなぎ直す）
                Log.Error("リモート連携: サムネイルを送れませんでした", ex);
                _thumbnailsReset = true;
            }
        }
    }

    /// <param name="pinnedParent">ペアリングで覚えた親の公開鍵の指紋（無ければ null＝初めてつなぐ親として、両方の画面で番号を確かめる）。</param>
    /// <param name="confirm">
    /// 初めての親につないでよいか（親の名前・ペアリングの番号・取り消し）。この PC の画面で確かめてから答える。
    /// 親が切れたり時間が過ぎたりしたら取り消される（窓を閉じる）。送る側のスレッドから呼ばれる。
    /// </param>
    /// <param name="startStopped">
    /// 初めからつながない（ペアリングを解除した直後）。特殊設定の［つなぐ］で作り直されるまで、Unpaired のまま待つ。
    /// </param>
    /// <param name="askConnect">
    /// ペアリング済みの親につなぐ前に、この PC の画面で「つなぎますか？」と聞く（親の名前・取り消し）。null なら聞かない
    /// （「この親には確認せずにつなぐ」）。1 回の起動の中では、一度つないだらそのあとのつなぎ直しでは聞かない。
    /// </param>
    public RemoteChild(string host, int port, string name, RemoteIdentity identity, string? pinnedParent,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Func<string, CancellationToken, Task<RemoteConnectChoice>>? askConnect = null, bool startStopped = false)
    {
        _startStopped = startStopped;
        _host = host;
        _port = port;
        _name = name;
        _identity = identity;
        _pinnedParent = pinnedParent;
        _confirm = confirm;
        _askConnect = askConnect;
        // JPEG への変換に WPF の画像機能を使うので STA で動かす（動かし始めるのは Start で）
        _thread = new Thread(Run) { IsBackground = true, Name = "RemoteChild" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thumbnailThread = new Thread(ThumbnailLoop) { IsBackground = true, Name = "RemoteChild.Thumbnails" };
        _thumbnailThread.SetApartmentState(ApartmentState.STA);
    }

    private readonly Thread _thumbnailThread;

    /// <summary>動かし始める。知らせ（StateChanged など）を受け取る準備が済んでから呼ぶ（先に動かすと、最初の知らせを取りこぼす）。</summary>
    public void Start()
    {
        _thread.Start();
        _thumbnailThread.Start();
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
        if (_startStopped)
        {
            StateChanged?.Invoke(RemoteChildState.Unpaired, _host);
            _stop.Wait();
            return;
        }
        while (!_stop.IsSet)
        {
            var client = new TcpClient();
            // つなぐ前に覚えておく（止めるときに、つないでいる途中でも切れるように）
            lock (_gate) _client = client;
            using var approval = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            // 段階ごとの時間の上限（1 回の読み取りごとではなく全体で）。過ぎたら接続を閉じて、読み取りの途中でも終わらせる
            using var deadline = new Timer(_ => client.Close(), null, HandshakeDeadline, Timeout.InfiniteTimeSpan);
            // 親からの受け取りが終わった（切れた）。送る側のループもこれを見て抜ける
            using var readerDone = new ManualResetEventSlim(false);
            try
            {
                StateChanged?.Invoke(RemoteChildState.Connecting, _host);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                {
                    connect.CancelAfter(ConnectTimeout);
                    try { client.ConnectAsync(_host, _port, connect.Token).AsTask().GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (!_stop.IsSet)
                    {
                        throw new TimeoutException($"{_host}:{_port} につながりませんでした");
                    }
                }
                client.NoDelay = true;
                client.ReceiveTimeout = (int)HandshakeDeadline.TotalMilliseconds;
                client.SendTimeout = (int)SendTimeout.TotalMilliseconds;
                // 暗号化する。親の公開鍵はペアリングで覚えたものと比べて確かめる
                using var raw = client.GetStream();
                using var stream = SecureChannel.Connect(raw, _identity);
                string parentThumbprint = RemoteIdentity.ThumbprintOf(stream.PeerPublicKey);
                if (_pinnedParent is { } pinned && pinned != parentThumbprint)
                {
                    // 覚えた親と違う: つながない（親が設定を消したなら、ペアリングを解除してつなぎ直してもらう）
                    Log.Info($"リモート連携: 親 {_host} の鍵がペアリングしたものと違うので、つなぎません");
                    StateChanged?.Invoke(RemoteChildState.KeyChanged, _host);
                    client.Close();
                    _stop.Wait(RefusedRetryInterval);
                    continue;
                }
                var hello = JsonSerializer.SerializeToUtf8Bytes(new RemoteHello(_name, MainWindow.FullVersion, _pinnedParent is not null));
                stream.Write(RemoteProtocol.Hello, hello);
                // 親がこの PC を覚えているか
                if (stream.Read(RemoteProtocol.PreAuthMaxMessage) is not { Type: RemoteProtocol.PeerStatus } status)
                    throw new InvalidDataException("親から返事がありません");
                bool parentKnowsMe = status.Payload is [1, ..];

                // お互いに覚えていれば、番号の確認は要らない（「つなぎますか？」は、聞く設定で、この起動でまだつないでいなければ聞く）。
                // どちらかが覚えていなければ、両方の画面に同じ番号を出して、この PC でも許可してもらう
                Task<bool> decision;
                Task<RemoteConnectChoice>? ask = null;
                string peerName = stream.PeerName.Length > 0 ? stream.PeerName : _host;
                if (_pinnedParent is not null && parentKnowsMe)
                {
                    if (_askConnect is not null && !_connectedOnce)
                    {
                        StateChanged?.Invoke(RemoteChildState.Confirming, _host);
                        ask = _askConnect(peerName, approval.Token);
                        decision = ask.ContinueWith(t => t.IsCompletedSuccessfully && t.Result == RemoteConnectChoice.Connect, TaskScheduler.Default);
                    }
                    else decision = Task.FromResult(true);
                }
                else
                {
                    StateChanged?.Invoke(RemoteChildState.WaitingApproval, stream.PairCode);
                    decision = _confirm(peerName, stream.PairCode, approval.Token);
                }
                deadline.Change(ApprovalWait + PeerAnswerWait + TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                bool unpairChosen() => ask is { IsCompletedSuccessfully: true } && ask.Result == RemoteConnectChoice.Unpair;
                if (!RemoteProtocol.Agree(stream, client, decision, ApprovalWait, PeerAnswerWait, () => !_stop.IsSet, approval,
                        refusalType: () => unpairChosen() ? RemoteProtocol.Unpair : RemoteProtocol.Rejected))
                {
                    if (_stop.IsSet) break;
                    // この PC で［つなぐ］以外を選んだ（キャンセル・ペアリングを解除・時間切れ）: 自分からはつなぎ直さない
                    if (ask is not null && !(ask.IsCompletedSuccessfully && ask.Result == RemoteConnectChoice.Connect))
                    {
                        if (unpairChosen())
                        {
                            _pinnedParent = null;
                            ParentUnpaired?.Invoke();
                            Log.Info($"リモート連携: 親 {_host} とのペアリングを解除しました（親にも知らせた）");
                        }
                        else Log.Info($"リモート連携: 親 {_host} につなぐのをやめました");
                        StateChanged?.Invoke(unpairChosen() ? RemoteChildState.Unpaired : RemoteChildState.Paused, _host);
                        client.Close();
                        _stop.Wait();
                        break;
                    }
                    Log.Info($"リモート連携: 親 {_host} とはつながりませんでした（どちらかが断った、または時間切れ）");
                    StateChanged?.Invoke(RemoteChildState.Rejected, _host);
                    client.Close();
                    _stop.Wait(RefusedRetryInterval);
                    continue;
                }
                deadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                // 親からは、切り替えを頼まれたときしか届かないので、受け取りの期限は付けない（切れたら送る側で分かる）
                client.ReceiveTimeout = 0;
                if (_pinnedParent is null)
                {
                    _pinnedParent = parentThumbprint;
                    ParentPaired?.Invoke(parentThumbprint);
                    Log.Info($"リモート連携: 親 {_host} とペアリングしました");
                }
                // 切れる前・切れている間に合成の側から届いて、まだ送っていない絵は捨てる（外したウィンドウが映っているかもしれない）。
                // つながったと知らせると、合成の側が今の絵を 1 枚すぐ渡してくる
                lock (_gate) _latest = null;
                _connectedOnce = true;
                StateChanged?.Invoke(RemoteChildState.Connected, _host);
                Log.Info($"リモート連携: 親 {_host}:{_port} につながりました");
                loggedFailure = false;

                // 親からの「共有する／外す」は別のスレッドで受け取る（こちらは送り続ける）
                var channel = stream;
                var reader = new Thread(() => ReceiveRequests(channel, client, readerDone)) { IsBackground = true, Name = "RemoteChild.Receive" };
                reader.Start();
                lock (_gate)
                {
                    _sentWindowList = null; // つなぎ直したら、一覧とサムネイルは必ず送り直す
                    _channel = stream;
                }
                _thumbnailsReset = true;

                // 絵は、つないだあとに合成の側から届いたものだけ送る（切れている間に外したウィンドウが映った古い絵を送らない。
                // つながると合成の側が、変わっていなくても 1 枚すぐ渡してくる）
                byte[]? lastJpeg = null;
                var lastFrameSent = System.Diagnostics.Stopwatch.StartNew();
                while (!_stop.IsSet)
                {
                    _frameReady.WaitOne(KeepAliveInterval);
                    if (_stop.IsSet) break;
                    if (readerDone.IsSet) throw new IOException("親との接続が切れました");
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
            catch (Exception) when (!_stop.IsSet && _unpairedByParent)
            {
                // 親でペアリングが解除されて切れた: つなぎ直さずに止まる
                StateChanged?.Invoke(RemoteChildState.Unpaired, _host);
                _stop.Wait();
                break;
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
                approval.Cancel();
                client.Close();
                lock (_gate)
                {
                    _client = null;
                    _channel = null;
                }
            }
        }
    }

    /// <summary>
    /// 親からの「共有する／外す」を受け取る（つながっている間だけ）。切れた・壊れたデータが来たら、接続を閉じる
    /// （送る側のループが次の送信でそれに気づき、つなぎ直す）。
    /// </summary>
    private void ReceiveRequests(SecureChannel channel, TcpClient client, ManualResetEventSlim done)
    {
        try
        {
            while (!_stop.IsSet && channel.Read() is { } message)
            {
                switch (message.Type)
                {
                    case RemoteProtocol.SetShared when AcceptParentRequests:
                        if (JsonSerializer.Deserialize<RemoteShareRequest>(message.Payload) is { } request)
                            ShareRequested?.Invoke(request.Id, request.Shared, request.CropDefault);
                        break;
                    case RemoteProtocol.UncheckAll when AcceptParentRequests:
                        UncheckAllRequested?.Invoke();
                        break;
                    case RemoteProtocol.Unpair:
                        // 親でペアリングが解除された: この PC も親を忘れる（次につなぐときは番号の確認から）。切れたあとはつなぎ直さない
                        _unpairedByParent = true;
                        _pinnedParent = null;
                        ParentUnpaired?.Invoke();
                        Log.Info($"リモート連携: 親 {_host} でペアリングが解除されました");
                        break;
                    case RemoteProtocol.RestoreAll when AcceptParentRequests:
                        RestoreAllRequested?.Invoke();
                        break;
                    case RemoteProtocol.ThumbnailsWanted:
                        _thumbnailsWanted = message.Payload is [1, ..];
                        if (_thumbnailsWanted)
                        {
                            _thumbnailsReset = true;
                            _thumbnailWake.Set();
                        }
                        break;
                }
            }
        }
        catch
        {
            // 切れたとき
        }
        client.Close();
        try { done.Set(); } catch (ObjectDisposedException) { }
        _frameReady.Set();
    }

    public void Dispose()
    {
        _stop.Set();
        _stopping.Cancel();
        _frameReady.Set();
        _thumbnailWake.Set();
        lock (_gate) _client?.Close();
        if ((_thread.ThreadState & ThreadState.Unstarted) == 0) _thread.Join(TimeSpan.FromSeconds(1));
    }

    // 親を探したときに並べる数の上限（偽の返事を大量に送られても一覧が膨らまないように）
    private const int MaxFoundParents = 32;

    /// <summary>
    /// LAN の中の親を探す。各ネットワークの「全員宛て」と、この PC 自身に呼びかけて、返事を少し待つ。
    /// 返事の処理は UI スレッドの外で行い、結果だけを返す。
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
            try { await udp.SendAsync(request, new IPEndPoint(target, port)).ConfigureAwait(false); }
            catch (SocketException) { /* 送れないネットワークは飛ばす */ }
        }
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // 念のため: 上の設定が効かない環境でも、待ち続ける
                }
                if (reply.Buffer.Length > 512) continue;
                string text = Encoding.UTF8.GetString(reply.Buffer);
                if (!text.StartsWith(RemoteProtocol.FoundPrefix, StringComparison.Ordinal)) continue;
                var parts = text[RemoteProtocol.FoundPrefix.Length..].Split('|');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int tcpPort) || tcpPort is < 1024 or > 65535) continue;
                // 同じ親が「全員宛て」と「この PC 自身」の両方に返事をすると 2 回並ぶので、名前とポートでまとめる。
                // そのときは 127.0.0.1 より LAN の住所を残す（ほかの PC からも同じ住所でつなげるように）
                var parent = new FoundParent(RemoteProtocol.CleanText(parts[0], 64), reply.RemoteEndPoint.Address.ToString(), tcpPort);
                int same = found.FindIndex(f => f.Name == parent.Name && f.Port == parent.Port);
                if (same < 0)
                {
                    if (found.Count < MaxFoundParents) found.Add(parent);
                }
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
