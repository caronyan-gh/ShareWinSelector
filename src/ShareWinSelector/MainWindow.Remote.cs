using System.Windows;
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

    /// <summary>
    /// 親: 子をつないでよいか（受け取る側のスレッドから呼ばれる）。許可済みの子ならすぐ許可。
    /// 初めての子なら、ペアリングの番号を出した窓で聞き、許可されたら覚える。
    /// </summary>
    private Task<bool> ApproveChild(string thumbprint, string name, string code)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (App.Settings.RemotePairedChildren.ContainsKey(thumbprint))
            {
                App.Settings.RemotePairedChildren[thumbprint] = name;
                return Task.FromResult(true);
            }
            SetRemoteStatus(Loc.F("Remote_StatusParentWaiting", name, code));
            var window = new RemotePairWindow(name, code);
            window.Show();
            window.Activate();
            return window.Result.ContinueWith(t =>
            {
                bool allowed = t.Result;
                Dispatcher.BeginInvoke(() =>
                {
                    if (allowed)
                    {
                        App.Settings.RemotePairedChildren[thumbprint] = name;
                        App.Settings.Save();
                        Log.Info($"リモート連携: 子「{name}」とペアリングしました");
                    }
                    if (_remoteParent is not null) SetRemoteStatus(Loc.F("Remote_StatusListening", App.Settings.RemotePort));
                });
                return allowed;
            });
        }).Task.Unwrap();
    }

    private void StartParent()
    {
        int port = App.Settings.RemotePort;
        try
        {
            var parent = new RemoteParent(port, LocalName, _remoteIdentity!, ApproveChild);
            parent.ChildConnected += (name, w, h) => Dispatcher.BeginInvoke(() => OnChildConnected(parent, name, w, h));
            parent.ChildDisconnected += () => Dispatcher.BeginInvoke(() => OnChildDisconnected(parent));
            parent.FrameReceived += (pixels, w, h) => OnRemoteFrame(parent, pixels, w, h);
            parent.WindowListReceived += windows => Dispatcher.BeginInvoke(() => UpdateRemoteItems(parent, windows));
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
        var child = new RemoteChild(address, App.Settings.RemotePort, LocalName, _remoteIdentity!, App.Settings.RemotePairedParent);
        child.StateChanged += (state, detail) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
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
        child.ShareRequested += (id, shared) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            var item = _items.FirstOrDefault(i => !i.IsRemote && (long)i.Handle == id);
            if (item is not null && item.IsChecked != shared) item.IsChecked = shared;
        });
        // 初めての親に許可されたら、その親を覚える（次からはこの親だけにつなぐ）
        child.ParentPaired += thumbprint => Dispatcher.BeginInvoke(() =>
        {
            App.Settings.RemotePairedParent = thumbprint;
            App.Settings.Save();
        });
        _remoteChild = child;
        // 合成した絵を親へ渡す（変わったときだけ）
        if (_compositor is not null) _compositor.FrameSink = child.SubmitFrame;
        SetChildOutputOffScreen(true);
        PublishRemoteWindowList();
        Log.Info($"リモート連携: 子として {address}:{App.Settings.RemotePort} へ");
    }

    private void StopRemote()
    {
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
            if (_remoteMonitor is not null) DropRemoteMonitor();
        }
    }

    // --- 親 ---

    private void OnChildConnected(RemoteParent parent, string name, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent)) return;
        _remoteMonitor = new MonitorInfo("REMOTE", new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height }, false)
        {
            IsRemote = true,
            RemoteName = name,
        };
        RefreshMonitorList();
        SetRemoteStatus(Loc.F("Remote_StatusChildConnected", name));
    }

    private void OnChildDisconnected(RemoteParent parent)
    {
        if (!ReferenceEquals(_remoteParent, parent)) return;
        DropRemoteMonitor();
        SetRemoteStatus(Loc.F("Remote_StatusListening", App.Settings.RemotePort));
    }

    /// <summary>子の絵が届いた（受け取る側のスレッド）。合成にそのまま渡し、大きさが変わったら一覧の R も直す。</summary>
    private void OnRemoteFrame(RemoteParent parent, byte[] pixels, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent)) return;
        _compositor?.SetRemoteFrame(pixels, width, height);
        var known = _remoteMonitor;
        if (known is not null && (known.Bounds.Width != width || known.Bounds.Height != height))
            Dispatcher.BeginInvoke(() => OnRemoteSizeChanged(parent, width, height));
    }

    private void OnRemoteSizeChanged(RemoteParent parent, int width, int height)
    {
        if (!ReferenceEquals(_remoteParent, parent) || _remoteMonitor is not { } m) return;
        if (m.Bounds.Width == width && m.Bounds.Height == height) return;
        _remoteMonitor = m with { Bounds = new Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height } };
        RefreshMonitorList();
    }

    /// <summary>子の画面（R）を一覧から外す。共有していたら、この PC のモニターに戻す。</summary>
    private void DropRemoteMonitor()
    {
        _remoteMonitor = null;
        _compositor?.ClearRemoteFrame();
        RemoveRemoteItems();
        RefreshMonitorList();
    }

    // --- 子のウィンドウを親の一覧に並べる ---

    /// <summary>子: この PC のウィンドウの一覧（共有中かどうか付き）を親へ送る（変わっていなければ送られない）。</summary>
    private void PublishRemoteWindowList()
    {
        if (_remoteChild is not { } child) return;
        child.SubmitWindowList(_items.Where(i => !i.IsRemote)
            .Select(i => new RemoteWindow((long)i.Handle, i.Title, i.ProcessName, i.IsChecked)).ToList());
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
        for (int index = 0; index < windows.Count; index++)
        {
            var w = windows[index];
            var info = new WindowInfo(IntPtr.Zero, w.Title, w.Process);
            if (existing.Remove(w.Id, out var item))
            {
                item.Info = info;
                item.Order = index;
                if (item.IsChecked != w.Shared) SetCheckedSilently([item], w.Shared);
                continue;
            }
            item = new WindowItem(info) { IsRemote = true, RemoteId = w.Id, IsChecked = w.Shared, Order = index, Group = group };
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }
        // 子で閉じられたウィンドウは消す
        foreach (var gone in existing.Values)
        {
            gone.PropertyChanged -= OnItemPropertyChanged;
            _items.Remove(gone);
        }
    }

    /// <summary>親: 子が切れたら、子のウィンドウの行を全部消す。</summary>
    private void RemoveRemoteItems()
    {
        foreach (var item in _items.Where(i => i.IsRemote).ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            _items.Remove(item);
        }
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
