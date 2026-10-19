using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ShareWinSelector.Interop;
using ShareWinSelector.Remote;

namespace ShareWinSelector;

/// <summary>
/// リモート連携（親・子）。
/// 子: 合成した絵を親へ送る（RDP でつないでいる会社のノートなど）。選ぶ・クロップ・枠などは今までどおり子の画面で行う。
/// 親: 子からの接続を待ち、つながったら「共有するモニター」に子の画面を R として出す。R を選ぶと出力ウィンドウに子の絵だけを映す。
/// </summary>
public partial class MainWindow
{
    private RemoteParent? _remoteParent;
    private RemoteChild? _remoteChild;
    // この PC の鍵（初めて使うときに作って設定フォルダに置く。Windows の鍵の保管庫には置かない）
    private RemoteIdentity? _remoteIdentity;
    // 親: つながっている子の画面（共有するモニターの一覧に R として出す）
    private MonitorInfo? _remoteMonitor;
    private string _remoteStatus = "";

    /// <summary>リモート連携の今の状態の一文（特殊設定のダイアログに出す）。</summary>
    internal string RemoteStatus => _remoteStatus;

    /// <summary>リモート連携の状態が変わった（UI スレッド）。</summary>
    internal event Action? RemoteStatusChanged;

    /// <summary>この PC の名前（親を探したときの一覧や、親の R に出る）。開発用の別名を付けているときはそれも。</summary>
    private static string LocalName => Environment.MachineName + (AppSettings.Profile is { } p ? $" [{p}]" : "");

    private void SetRemoteStatus(string text)
    {
        _remoteStatus = text;
        RemoteStatusChanged?.Invoke();
    }

    /// <summary>設定どおりにリモート連携を始め直す（今の接続や待ち受けは止めてから）。</summary>
    internal void ApplyRemoteSettings()
    {
        StopRemote();
        var s = App.Settings;
        if (!s.RemoteEnabled)
        {
            SetRemoteStatus(Loc.T("Remote_StatusOff"));
            return;
        }
        try
        {
            _remoteIdentity ??= RemoteIdentity.LoadOrCreate();
        }
        catch (Exception ex)
        {
            Log.Error("リモート連携: 鍵を用意できませんでした", ex);
            SetRemoteStatus(Loc.F("Remote_StatusError", ex.Message));
            return;
        }
        if (s.RemoteRole == "Child") StartChild();
        else StartParent();
    }

    /// <summary>ペアリングで覚えた親・子を忘れる（次につなぐときに、もう一度番号を確かめる）。</summary>
    internal void UnpairRemote()
    {
        App.Settings.RemotePairedParent = null;
        App.Settings.RemotePairedChildren.Clear();
        App.Settings.Save();
        Log.Info("リモート連携: ペアリングを解除しました");
        ApplyRemoteSettings();
    }

    /// <summary>ペアリングの今の状態の一文（特殊設定のダイアログに出す）。</summary>
    internal string RemotePairingSummary => App.Settings.RemoteRole == "Child"
        ? (App.Settings.RemotePairedParent is null ? Loc.T("Remote_PairedParentNone") : Loc.T("Remote_PairedParentYes"))
        : Loc.F("Remote_PairedChildren", App.Settings.RemotePairedChildren.Count);

    /// <summary>番号の窓を重ねて出す先: 特殊設定が開いていればそれ、無ければメイン画面（最小化中は画面の中央）。</summary>
    private Window? PairWindowAnchor()
    {
        if (_advancedWindow is { IsVisible: true } advanced && advanced.WindowState != WindowState.Minimized) return advanced;
        return IsVisible && WindowState != WindowState.Minimized ? this : null;
    }

    /// <summary>親: この子（公開鍵の指紋）をペアリングで覚えているか（受け取る側のスレッドから呼ばれる）。</summary>
    private bool IsKnownChild(string thumbprint)
    {
        try { return Dispatcher.Invoke(() => App.Settings.RemotePairedChildren.ContainsKey(thumbprint)); }
        catch { return false; } // 終了中など
    }

    /// <summary>
    /// 親: 子をつないでよいか（受け取る側のスレッドから呼ばれる。親と子のどちらかが相手を覚えていないときだけ）。
    /// ペアリングの番号を出した窓で聞く（覚えるのは、子の側の許可もそろってから＝SessionStarted）。
    /// 子が切れたり時間が過ぎたりしたら cancel で窓を閉じる。
    /// </summary>
    private Task<bool> ApproveChild(string thumbprint, string name, string code, CancellationToken cancel)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (cancel.IsCancellationRequested) return Task.FromResult(false);
            var parent = _remoteParent;
            SetRemoteStatus(Loc.F("Remote_StatusParentWaiting", name, code));
            var window = new RemotePairWindow(Loc.F("Pair_Message", name), code, cancel, PairWindowAnchor());
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_remoteParent, parent) && _remoteMonitor is null)
                    SetRemoteStatus(Loc.F("Remote_StatusListening", App.Settings.RemotePort));
            };
            window.Show();
            window.Activate();
            return window.Result;
        }).Task.Unwrap();
    }

    // 親: 今の子の接続の番号（0 = つながっていない）。古い接続から遅れて届いた知らせは、これと比べて捨てる
    private long _remoteSessionId;

    /// <summary>
    /// 親: 子と両方の許可がそろって、今の子になった。前の子の分が残っていれば片付ける。
    /// 子を覚える（次からはすぐ許可する）。名前が変わっていれば直す。
    /// </summary>
    private void OnSessionStarted(RemoteParent parent, long session, string thumbprint, string name)
    {
        // 知らせが前後して、古い接続の「始まった」があとから届いても戻らない（番号は新しい接続ほど大きい）
        if (!ReferenceEquals(_remoteParent, parent) || session <= _remoteSessionId) return;
        if (_remoteSessionId != 0 && _remoteSessionId != session) DropRemoteMonitor(_remoteSessionId);
        _remoteSessionId = session;
        bool known = App.Settings.RemotePairedChildren.TryGetValue(thumbprint, out var oldName);
        if (known && oldName == name) return;
        App.Settings.RemotePairedChildren[thumbprint] = name;
        App.Settings.Save();
        if (!known) Log.Info($"リモート連携: 子「{name}」とペアリングしました");
    }

    /// <summary>
    /// 子: 初めての親につないでよいか（送る側のスレッドから呼ばれる）。ペアリングの番号を出した窓で聞く。
    /// 親が切れたり時間が過ぎたりしたら cancel で窓を閉じる。
    /// </summary>
    private Task<bool> ConfirmParent(string parentName, string code, CancellationToken cancel)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (cancel.IsCancellationRequested) return Task.FromResult(false);
            var window = new RemotePairWindow(Loc.F("Pair_MessageChild", parentName), code, cancel, PairWindowAnchor());
            window.Show();
            window.Activate();
            return window.Result;
        }).Task.Unwrap();
    }

    private void StartParent()
    {
        int port = App.Settings.RemotePort;
        try
        {
            var parent = new RemoteParent(port, LocalName, _remoteIdentity!, IsKnownChild, ApproveChild);
            // どの知らせにも接続の番号が付いている（古い接続から遅れて届いたものは捨てる）
            parent.SessionStarted += (session, thumbprint, name) => Dispatcher.BeginInvoke(() => OnSessionStarted(parent, session, thumbprint, name));
            parent.ChildConnected += (session, name, w, h) => Dispatcher.BeginInvoke(() => OnChildConnected(parent, session, name, w, h));
            parent.ChildDisconnected += session => Dispatcher.BeginInvoke(() => OnChildDisconnected(parent, session));
            parent.FrameReceived += (session, pixels, w, h) => OnRemoteFrame(parent, session, pixels, w, h);
            parent.WindowListReceived += (session, windows) => QueueRemoteWindowList(session, windows);
            parent.ThumbnailReceived += (session, id, image) => QueueRemoteThumbnail(session, id, image);
            // サムネイル表示の間だけ、子にサムネイルを送ってもらう
            parent.SetThumbnailsWanted(ThumbViewButton.IsChecked == true);
            _remoteParent = parent;
            SetRemoteStatus(Loc.F("Remote_StatusListening", port));
            Log.Info($"リモート連携: 親として待ち受け開始（ポート {port}）");
        }
        catch (Exception ex)
        {
            Log.Error("リモート連携: 親として待ち受けを始められませんでした", ex);
            SetRemoteStatus(Loc.F("Remote_StatusError", ex.Message));
        }
    }

    // 子として動いている間は、出力ウィンドウを画面外に置く（見る人はいない。描く先として要るだけ）
    private bool _childOutputOffScreen;

    /// <summary>出力ウィンドウを子用に画面外へ置く・設定どおりの配置に戻す。子の間は配置の設定を押せなくする。</summary>
    private void SetChildOutputOffScreen(bool on)
    {
        _childOutputOffScreen = on;
        PlacementBack.IsEnabled = PlacementNormal.IsEnabled = PlacementOffScreen.IsEnabled = !on;
        PlacementChildNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_output is not null) _output.Placement = on ? Output.OutputPlacement.OffScreen : SelectedPlacement;
    }

    private void StartChild()
    {
        string? address = App.Settings.RemoteParentAddress?.Trim();
        if (string.IsNullOrEmpty(address))
        {
            SetRemoteStatus(Loc.T("Remote_StatusNoParent"));
            return;
        }
        var child = new RemoteChild(address, App.Settings.RemotePort, LocalName, _remoteIdentity!, App.Settings.RemotePairedParent, ConfirmParent);
        child.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            // 合成した絵を親へ渡すのは、つながっている間だけ（渡す先を付けると、変わっていなくても 1 枚すぐ渡る）
            _remoteChildConnected = state == RemoteChildState.Connected;
            // 切れたら、親の「全部外す」で覚えた戻す分は捨てる（つなぎ直した親から「元に戻す」が来ても、古いものは戻さない）
            if (!_remoteChildConnected) _parentUndoSnapshot = [];
            if (_compositor is not null) _compositor.FrameSink = _remoteChildConnected ? child.SubmitFrame : null;
            SetRemoteStatus(state switch
            {
                RemoteChildState.Connected => Loc.F("Remote_StatusConnected", detail),
                RemoteChildState.Retrying => Loc.F("Remote_StatusRetry", detail),
                RemoteChildState.WaitingApproval => Loc.F("Remote_StatusWaitApproval", detail),
                RemoteChildState.Rejected => Loc.F("Remote_StatusRejected", detail),
                RemoteChildState.KeyChanged => Loc.F("Remote_StatusCertChanged", detail),
                _ => Loc.F("Remote_StatusConnecting", detail),
            });
        });
        // 親の一覧でチェックが切り替えられたら、この PC でいつもどおり共有する・外す
        child.ShareRequested += (id, shared, cropDefault) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            var item = _items.FirstOrDefault(i => !i.IsRemote && (long)i.Handle == id);
            if (item is null || item.IsChecked == shared) return;
            // 親で確認済み（または親で確認しない設定）なので、この PC の「共有前に確認」は使わない（クロップの範囲が未設定なら、ここで決める）
            // クロップは、親かこの PC の「上をクロップ」のどちらかがオンならする（このアプリだけ個別に決めてあれば、そちらが先）
            _shareFromParent = true;
            _parentCropDefault = cropDefault;
            try { item.IsChecked = shared; }
            finally
            {
                _shareFromParent = false;
                _parentCropDefault = null;
            }
        });
        // 親で「全部外す」「元に戻す」が押されたら、この PC の「全部外す」「元に戻す」と同じことをする（戻す分はこちらで覚える）
        child.UncheckAllRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            _parentUndoSnapshot = UncheckAllCore();
        });
        child.RestoreAllRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            var snapshot = _parentUndoSnapshot;
            _parentUndoSnapshot = [];
            RestoreSharing(snapshot);
        });
        // 初めての親と両方の許可がそろったら、その親を覚える（次からはこの親だけにつなぐ）
        child.ParentPaired += thumbprint => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            App.Settings.RemotePairedParent = thumbprint;
            App.Settings.Save();
        });
        _remoteChild = child;
        _remoteChildConnected = false;
        SetChildOutputOffScreen(true);
        PublishRemoteWindowList();
        Log.Info($"リモート連携: 子として {address}:{App.Settings.RemotePort} へ");
    }

    // 子: 親とつながっているか（合成した絵を渡すのは、その間だけ）
    private bool _remoteChildConnected;

    private void StopRemote()
    {
        _remoteChildConnected = false;
        if (_compositor is not null) _compositor.FrameSink = null;
        if (_remoteChild is not null)
        {
            _remoteChild.Dispose();
            _remoteChild = null;
            SetChildOutputOffScreen(false);
        }
        if (_remoteParent is not null)
        {
            _remoteParent.Dispose();
            _remoteParent = null;
            DropRemoteMonitor(0);
            _remoteSessionId = 0;
        }
    }

    // 子: 親から頼まれた共有を始めている最中か（この PC の「共有前に確認」を使わない）と、そのときの親の「上をクロップ」
    private bool _shareFromParent;
    private bool? _parentCropDefault;

    // 子: 親の「全部外す」で外したもの（親の「元に戻す」で戻す）
    private List<(WindowIdentity Identity, double Crop)> _parentUndoSnapshot = [];

    // --- 親 ---

    private void OnChildConnected(RemoteParent parent, long session, string name, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent) || session != _remoteSessionId) return;
        _remoteMonitor = new MonitorInfo("REMOTE", new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height }, false)
        {
            IsRemote = true,
            RemoteName = name,
        };
        RefreshMonitorList();
        SetRemoteStatus(Loc.F("Remote_StatusChildConnected", name));
    }

    private void OnChildDisconnected(RemoteParent parent, long session)
    {
        if (!ReferenceEquals(_remoteParent, parent) || session != _remoteSessionId) return;
        DropRemoteMonitor(session);
        _remoteSessionId = 0;
        SetRemoteStatus(Loc.F("Remote_StatusListening", App.Settings.RemotePort));
    }

    /// <summary>子の絵が届いた（受け取る側のスレッド）。合成にそのまま渡し、大きさが変わったら一覧の R も直す。</summary>
    private void OnRemoteFrame(RemoteParent parent, long session, byte[] pixels, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent)) return;
        _compositor?.SetRemoteFrame(session, pixels, width, height);
        var known = _remoteMonitor;
        if (known is not null && (known.Bounds.Width != width || known.Bounds.Height != height))
            Dispatcher.BeginInvoke(() => OnRemoteSizeChanged(parent, session, width, height));
    }

    private void OnRemoteSizeChanged(RemoteParent parent, long session, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent) || session != _remoteSessionId || _remoteMonitor is not { } m) return;
        if (m.Bounds.Width == width && m.Bounds.Height == height) return;
        _remoteMonitor = m with { Bounds = new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height } };
        RefreshMonitorList();
    }

    /// <summary>
    /// 子の画面（R）を一覧から外す。共有していたら、この PC のモニターに戻す。
    /// 合成に渡した子の絵は、その接続（session）のものだけ捨てる（0 なら全部。新しい子の絵は消さない）。
    /// </summary>
    private void DropRemoteMonitor(long session)
    {
        _remoteMonitor = null;
        _compositor?.ClearRemoteFrame(session);
        lock (_remoteQueueGate)
        {
            // その接続の分だけ捨てる（もう新しい接続の一覧が届いていれば、それは残す）
            if (session == 0 || _pendingRemoteSession <= session)
            {
                _pendingRemoteList = null;
                _pendingRemoteThumbnails.Clear();
            }
        }
        RemoveRemoteItems();
        RefreshMonitorList();
    }

    // --- 子のウィンドウを親の一覧に並べる ---

    /// <summary>子: この PC のウィンドウの一覧（共有中かどうか付き）を親へ送る（変わっていなければ送られない）。</summary>
    private void PublishRemoteWindowList()
    {
        if (_remoteChild is not { } child) return;
        child.SubmitWindowList(_items.Where(i => !i.IsRemote)
            .Select(i => new RemoteWindow((long)i.Handle, i.Title, i.ProcessName, i.IsChecked, i.IsChecked && IsWaitingForReview(i.Handle))).ToList());
    }

    /// <summary>
    /// 親: 子のウィンドウの行が入るまとまり。R を共有している間は一番上（共有するモニターの側）、
    /// そうでなければ「── R 子の名前 ──」の区切り線の下に薄く。
    /// </summary>
    private MonitorGroup RemoteItemGroup() =>
        CurrentMonitor?.IsRemote == true ? MonitorGroup.Shared : new MonitorGroup(10000, "R  " + (_remoteMonitor?.RemoteName ?? ""));

    /// <summary>親: 子から届いた一覧で、子のウィンドウの行を出し入れし、チェックを子の今の状態に合わせる。</summary>
    private void UpdateRemoteItems(RemoteParent parent, IReadOnlyList<RemoteWindow> windows)
    {
        if (!ReferenceEquals(_remoteParent, parent)) return;
        var existing = _items.Where(i => i.IsRemote).ToDictionary(i => i.RemoteId);
        var group = RemoteItemGroup();
        int newlyWaiting = 0;
        for (int index = 0; index < windows.Count; index++)
        {
            var w = windows[index];
            var info = new WindowInfo(IntPtr.Zero, w.Title, w.Process);
            // 親から共有を頼んだウィンドウが、子の画面で決める待ちになった（カーソルのそばで知らせる）
            if (w.Pending && _remoteShareRequested.Remove(w.Id, out var requestedAt) && DateTime.UtcNow - requestedAt < TimeSpan.FromSeconds(30))
                newlyWaiting++;
            else if (w.Shared && !w.Pending) _remoteShareRequested.Remove(w.Id);
            if (existing.Remove(w.Id, out var item))
            {
                item.Info = info;
                item.Order = index;
                item.RemotePending = w.Pending;
                if (item.IsChecked != w.Shared) SetCheckedSilently([item], w.Shared);
                continue;
            }
            item = new WindowItem(info) { IsRemote = true, RemoteId = w.Id, IsChecked = w.Shared, RemotePending = w.Pending, Order = index, Group = group };
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }
        // 子で閉じられたウィンドウは消す
        foreach (var gone in existing.Values)
        {
            gone.PropertyChanged -= OnItemPropertyChanged;
            _items.Remove(gone);
        }
        // 子のウィンドウの共有の有無で「全部外す」を押せるかが変わる
        UpdateStatus();
        // その場での並べ替えが済んでから確かめる
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FixRemoteOrder);
        if (newlyWaiting > 0) ShowRemoteWaitingHint();
        // 子の画面で決め終わったら（待ちが無くなったら）、知らせは引っ込める
        else if (_remoteWaitingHint is { IsOpen: true } hint && !_items.Any(i => i.IsRemote && i.IsChecked && i.RemotePending)) hint.IsOpen = false;
    }

    /// <summary>
    /// 親: 子のウィンドウのまとまりが「共有中が先 → 子の並び順」になっていなければ、一覧を並べ直す。
    /// 子から一覧が届くたびに、子のウィンドウの行の共有中かどうかと並び順がまとめて変わる。WPF のその場での並べ替えは、
    /// まとまり分けと組み合わせると、そのとき行を途中に差し込んでしまうことがある（共有中なのに先頭に来ない）。
    /// 崩れているときだけ並べ直すので、普段は何もしない。
    /// </summary>
    private void FixRemoteOrder()
    {
        var group = RemoteItemGroup();
        if (_windowView.Groups?.OfType<System.Windows.Data.CollectionViewGroup>().FirstOrDefault(g => Equals(g.Name, group)) is not { } viewGroup) return;
        var shown = viewGroup.Items.OfType<WindowItem>().Where(i => i.IsRemote).ToList();
        var expected = shown.OrderByDescending(i => i.IsChecked).ThenBy(i => i.Order).ToList();
        if (!shown.SequenceEqual(expected)) _windowView.Refresh();
    }

    // 親: 子に共有を頼んだ時刻（子の画面で決める待ちになったら、カーソルのそばで知らせるため）
    private readonly Dictionary<long, DateTime> _remoteShareRequested = [];
    private Popup? _remoteWaitingHint;
    private System.Windows.Threading.DispatcherTimer? _remoteWaitingHintTimer;

    /// <summary>親: 子のウィンドウを共有する／外すよう頼む。共有するときは親の「上をクロップ」も伝え、頼んだ時刻を覚える。</summary>
    private void RequestRemoteShare(WindowItem item, bool shared)
    {
        if (_remoteParent is not { } parent) return;
        if (shared) _remoteShareRequested[item.RemoteId] = DateTime.UtcNow;
        else _remoteShareRequested.Remove(item.RemoteId);
        parent.RequestShare(item.RemoteId, shared, shared ? CropMode : null);
    }

    /// <summary>
    /// 親: 「子の画面でクロップの範囲を決めてください」をカーソルのそばに出す（ステータス行だけだと気づきにくいので）。
    /// 子で決め終わるか、数秒たつか、クリックすると消える。
    /// </summary>
    private void ShowRemoteWaitingHint()
    {
        if (_remoteWaitingHint is { IsOpen: true } previous) previous.IsOpen = false;
        var popup = new Popup
        {
            Child = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrandWindowBackgroundBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BrandCyanBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Child = new TextBlock
                {
                    Text = Loc.F("RemoteWaiting_Hint", _remoteMonitor?.RemoteName ?? ""),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                },
            },
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 12,
            VerticalOffset = 12,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        popup.Opened += (_, _) =>
        {
            // 自分専用の画面なので、モニターごと共有されても映らないようにする
            if (PresentationSource.FromVisual(popup.Child) is HwndSource source) Native.ExcludeFromCapture(source.Handle);
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_remoteWaitingHint, popup)) _remoteWaitingHint = null;
        };
        _remoteWaitingHint = popup;
        popup.IsOpen = true;
        _remoteWaitingHintTimer?.Stop();
        _remoteWaitingHintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _remoteWaitingHintTimer.Tick += (_, _) =>
        {
            _remoteWaitingHintTimer?.Stop();
            popup.IsOpen = false;
        };
        _remoteWaitingHintTimer.Start();
    }

    // 子から届いた一覧・サムネイルは、UI スレッドへまとめて渡す（届くたびに 1 つずつ頼むと、UI が追いつかないことがある）。
    // 溜めるのは 1 つの接続の分だけ（別の接続のものが来たら、前の分は捨てる）。サムネイルは数に上限
    private const int MaxPendingThumbnails = 512;
    private readonly object _remoteQueueGate = new();
    private long _pendingRemoteSession;
    private IReadOnlyList<RemoteWindow>? _pendingRemoteList;
    private readonly Dictionary<long, System.Windows.Media.ImageSource> _pendingRemoteThumbnails = [];
    private bool _remoteFlushPosted;

    private void QueueRemoteWindowList(long session, IReadOnlyList<RemoteWindow> windows)
    {
        lock (_remoteQueueGate)
        {
            if (!SwitchPendingSession(session)) return;
            _pendingRemoteList = windows;
        }
        PostRemoteFlush();
    }

    private void QueueRemoteThumbnail(long session, long id, System.Windows.Media.ImageSource image)
    {
        lock (_remoteQueueGate)
        {
            if (!SwitchPendingSession(session)) return;
            if (_pendingRemoteThumbnails.Count >= MaxPendingThumbnails && !_pendingRemoteThumbnails.ContainsKey(id)) return;
            _pendingRemoteThumbnails[id] = image;
        }
        PostRemoteFlush();
    }

    /// <summary>
    /// 溜めている分が古い接続のものなら捨てて、この接続の分に切り替える。この接続より新しい分を溜めていれば false（届いたものは捨てる）。
    /// _remoteQueueGate を持って呼ぶ。
    /// </summary>
    private bool SwitchPendingSession(long session)
    {
        if (session < _pendingRemoteSession) return false;
        if (session == _pendingRemoteSession) return true;
        _pendingRemoteSession = session;
        _pendingRemoteList = null;
        _pendingRemoteThumbnails.Clear();
        return true;
    }

    private void PostRemoteFlush()
    {
        lock (_remoteQueueGate)
        {
            if (_remoteFlushPosted) return;
            _remoteFlushPosted = true;
        }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, FlushRemoteQueue);
    }

    private void FlushRemoteQueue()
    {
        IReadOnlyList<RemoteWindow>? list;
        List<KeyValuePair<long, System.Windows.Media.ImageSource>> thumbnails;
        long session;
        lock (_remoteQueueGate)
        {
            session = _pendingRemoteSession;
            list = _pendingRemoteList;
            _pendingRemoteList = null;
            thumbnails = [.. _pendingRemoteThumbnails];
            _pendingRemoteThumbnails.Clear();
            _remoteFlushPosted = false;
        }
        // 切れた・替わった接続の分は捨てる（消した子の行を復活させない）
        if (_remoteParent is not { } parent || session != _remoteSessionId) return;
        if (list is not null) UpdateRemoteItems(parent, list);
        if (thumbnails.Count == 0) return;
        var byId = _items.Where(i => i.IsRemote).ToDictionary(i => i.RemoteId);
        foreach (var (id, image) in thumbnails)
            if (byId.TryGetValue(id, out var item)) item.RemoteThumbnail = image;
    }

    // 親: R の行を共有する前の確認（カーソルのそばに出す小窓）
    private Popup? _remoteConfirm;

    /// <summary>
    /// 親: 子のウィンドウを共有する前に、カーソルのそばで確かめる。［共有する］で子に頼み、［やめる］・外をクリックでチェックを戻す。
    /// 子のウィンドウは子の画面にあって、親の画面では確認待ちの枠を出せないので、小窓で聞く。
    /// </summary>
    private void ConfirmRemoteShare(WindowItem item)
    {
        if (_remoteConfirm is { IsOpen: true } previous) previous.IsOpen = false;
        var message = new TextBlock
        {
            Text = Loc.F("RemoteConfirm_Message", _remoteMonitor?.RemoteName ?? "", item.Title),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var share = new Button
        {
            Content = Loc.T("RemoteConfirm_Share"),
            Padding = new Thickness(14, 3, 14, 3),
            IsDefault = true,
            Background = (System.Windows.Media.Brush)FindResource("BrandBlueBrush"),
            Foreground = System.Windows.Media.Brushes.White,
        };
        var cancel = new Button { Content = Loc.T("RemoteConfirm_Cancel"), Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(share);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(message);
        panel.Children.Add(buttons);
        var popup = new Popup
        {
            Child = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("BrandWindowBackgroundBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BrandBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Child = panel,
            },
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 12,
            VerticalOffset = 12,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        bool accepted = false;
        share.Click += (_, _) =>
        {
            accepted = true;
            popup.IsOpen = false;
        };
        cancel.Click += (_, _) => popup.IsOpen = false;
        popup.Opened += (_, _) =>
        {
            // 自分専用の画面なので、モニターごと共有されても映らないようにする
            if (PresentationSource.FromVisual(popup.Child) is HwndSource source) Native.ExcludeFromCapture(source.Handle);
            share.Focus();
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_remoteConfirm, popup)) _remoteConfirm = null;
            // 子が切れた・行が消えたあとなら何もしない
            if (!_items.Contains(item)) return;
            if (accepted) RequestRemoteShare(item, true);
            else SetCheckedSilently([item], false);
            UpdateStatus();
        };
        _remoteConfirm = popup;
        popup.IsOpen = true;
    }

    /// <summary>親: 子が切れたら、子のウィンドウの行を全部消す。</summary>
    private void RemoveRemoteItems()
    {
        _remoteShareRequested.Clear();
        if (_remoteWaitingHint is { IsOpen: true } hint) hint.IsOpen = false;
        foreach (var item in _items.Where(i => i.IsRemote).ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            _items.Remove(item);
        }
        UpdateStatus();
    }

    /// <summary>
    /// 共有するモニターの一覧を作り直す（この PC のモニター＋つながっている子の R）。
    /// 選んでいたものがまだあればそのまま、無くなっていれば前回のモニター → メインモニターの順に選び直す。
    /// </summary>
    private void RefreshMonitorList()
    {
        var all = new List<MonitorInfo>(MonitorEnumerator.GetMonitors());
        if (_remoteMonitor is not null) all.Add(_remoteMonitor);
        if (all.Count == 0) return;
        string? current = CurrentMonitor?.DeviceName;
        var next = all.FirstOrDefault(m => m.DeviceName == current)
                   ?? all.FirstOrDefault(m => m.DeviceName == App.Settings.MonitorDevice)
                   ?? all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        MonitorCombo.ItemsSource = all;
        RebuildCompactMonitorButtons(all);
        MonitorCombo.SelectedItem = next; // SelectionChanged でキャンバス・オーバーレイを更新
    }

    // --- 特殊設定 ---

    private RemoteSettingsWindow? _advancedWindow;

    private void AdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedWindow is { IsLoaded: true })
        {
            _advancedWindow.Activate();
            return;
        }
        _advancedWindow = new RemoteSettingsWindow(this);
        _advancedWindow.Closed += (_, _) => _advancedWindow = null;
        _advancedWindow.Show();
    }
}
