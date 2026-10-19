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
        if (s.RemoteRole == "Child") StartChild();
        else StartParent();
    }

    private void StartParent()
    {
        int port = App.Settings.RemotePort;
        try
        {
            var parent = new RemoteParent(port, LocalName);
            parent.ChildConnected += (name, w, h) => Dispatcher.BeginInvoke(() => OnChildConnected(parent, name, w, h));
            parent.ChildDisconnected += () => Dispatcher.BeginInvoke(() => OnChildDisconnected(parent));
            parent.FrameReceived += (pixels, w, h) => OnRemoteFrame(parent, pixels, w, h);
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

    private void StartChild()
    {
        string? address = App.Settings.RemoteParentAddress?.Trim();
        if (string.IsNullOrEmpty(address))
        {
            SetRemoteStatus(Loc.T("Remote_StatusNoParent"));
            return;
        }
        var child = new RemoteChild(address, App.Settings.RemotePort, LocalName);
        child.StateChanged += (state, host) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_remoteChild, child)) return;
            SetRemoteStatus(state switch
            {
                RemoteChildState.Connected => Loc.F("Remote_StatusConnected", host),
                RemoteChildState.Retrying => Loc.F("Remote_StatusRetry", host),
                _ => Loc.F("Remote_StatusConnecting", host),
            });
        });
        _remoteChild = child;
        // 合成した絵を親へ渡す（変わったときだけ）
        if (_compositor is not null) _compositor.FrameSink = child.SubmitFrame;
        Log.Info($"リモート連携: 子として {address}:{App.Settings.RemotePort} へ");
    }

    private void StopRemote()
    {
        if (_compositor is not null) _compositor.FrameSink = null;
        _remoteChild?.Dispose();
        _remoteChild = null;
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
        RefreshMonitorList();
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
