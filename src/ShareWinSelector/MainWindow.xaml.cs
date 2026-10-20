using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ShareWinSelector.Output;
using ShareWinSelector.Overlay;
using ShareWinSelector.Rendering;
using Windows.Graphics.Capture;

namespace ShareWinSelector;

/// <summary>
/// 一覧を分けるモニターのまとまり。共有するモニターは Order = 0（区切り線なし、一番上）、
/// ほかのモニターは Windows の番号順で、「── DISPLAY2 ──」の区切り線の下に並ぶ。
/// </summary>
public sealed record MonitorGroup(int Order, string Label) : IComparable
{
    public static MonitorGroup Shared { get; } = new(0, "");

    public bool IsShared => Order == 0;

    public int CompareTo(object? obj) => obj is MonitorGroup other ? Order.CompareTo(other.Order) : 1;
}

/// <summary>一覧の 1 行（チェック状態付き）。</summary>
public sealed class WindowItem(WindowInfo info) : INotifyPropertyChanged
{
    private WindowInfo _info = info;
    private bool _isChecked;

    public IntPtr Handle => _info.Handle;
    public string Title => _info.Title;
    public string ProcessName => _info.ProcessName;
    public string Display => _info.Display;

    private string? _appKey;

    /// <summary>クロップ量などを覚えるアプリのキー（問い合わせは 1 回だけ）。</summary>
    internal string AppKey => _appKey ??= AppIdentity.GetKey(Handle, ProcessName);

    /// <summary>タイトル変更などで中身を差し替える。</summary>
    public WindowInfo Info
    {
        get => _info;
        set
        {
            if (_info == value) return;
            _info = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ProcessName));
            OnPropertyChanged(nameof(Display));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    private bool _cropUiVisible;

    /// <summary>行ごとのクロップ操作を見せるか（今は常に表示。将来の切り替え用）。</summary>
    public bool CropUiVisible
    {
        get => _cropUiVisible;
        set
        {
            if (_cropUiVisible == value) return;
            _cropUiVisible = value;
            OnPropertyChanged();
        }
    }

    private bool _hasSavedCrop;

    /// <summary>このアプリのクロップ量が保存済みか（行に小さなアイコンで示す）。</summary>
    public bool HasSavedCrop
    {
        get => _hasSavedCrop;
        set
        {
            if (_hasSavedCrop == value) return;
            _hasSavedCrop = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggleCrop));
        }
    }

    /// <summary>
    /// 行の「クロップ」を押せるか。量が未保存のアプリでは押せない（押すといきなり調整画面になって驚くので）。
    /// 量は「クリックで選択」中の Ctrl＋クリックか、一覧の上の「上をクロップ」から作る。
    /// </summary>
    public bool CanToggleCrop => _hasSavedCrop || _cropEnabled;

    private bool _cropEnabled;

    /// <summary>このウィンドウにクロップを効かせるか（外すとクロップなしで共有。保存値は消さない）。</summary>
    public bool CropEnabled
    {
        get => _cropEnabled;
        set
        {
            if (_cropEnabled == value) return;
            _cropEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanToggleCrop));
        }
    }

    private MonitorGroup _group = MonitorGroup.Shared;

    /// <summary>
    /// どのモニターのまとまりに入るか。共有するモニターに一部でも重なっていれば共有するモニターの側。
    /// 一覧では共有するモニターのものを上に並べ、ほかのモニターのものは区切り線の下に薄く出す。ウィンドウをモニター間で動かすと追いかけて変わる。
    /// </summary>
    public MonitorGroup Group
    {
        get => _group;
        set
        {
            if (_group == value) return;
            bool wasShared = _group.IsShared;
            _group = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupOrder));
            if (wasShared != value.IsShared) OnPropertyChanged(nameof(OnSharedMonitor));
        }
    }

    /// <summary>並べ替え用のまとまりの順（共有するモニターが 0、ほかは Windows の番号）。</summary>
    public int GroupOrder => _group.Order;

    /// <summary>共有するモニターの上にあるか（ほかのモニターのものは薄く出す）。</summary>
    public bool OnSharedMonitor => _group.IsShared;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// リモート連携の子のウィンドウ（親の一覧に並べたもの）。チェックすると子に「共有して」と頼む。
    /// この PC のウィンドウではないので Handle は 0、クロップや最前面に出す操作は出さない。
    /// </summary>
    public bool IsRemote { get; init; }

    /// <summary>子の PC でのウィンドウの番号（IsRemote のとき）。</summary>
    public long RemoteId { get; init; }

    /// <summary>子で共有に選ばれているが、子の画面で確認・クロップの範囲を決めるのを待っている（IsRemote のとき）。</summary>
    public bool RemotePending { get; set; }

    private System.Windows.Media.ImageSource? _remoteThumbnail;

    /// <summary>子のウィンドウのサムネイル（子から数秒おきに届く静止画。この PC のウィンドウでは null）。</summary>
    public System.Windows.Media.ImageSource? RemoteThumbnail
    {
        get => _remoteThumbnail;
        set
        {
            if (_remoteThumbnail == value) return;
            _remoteThumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>この PC のウィンドウか（子のウィンドウでは出さないボタン用）。</summary>
    public bool IsLocal => !IsRemote;

    private int _order;

    /// <summary>
    /// 一覧での並び順（小さいほど上）。共有するモニターのもの・共有中のものはこれより優先して上に並ぶ。
    /// 子のウィンドウの行は、子から一覧が届くたびに子の並び順で振り直すので、変わったら知らせて並べ替えてもらう
    /// （知らせないと、画面の並びと番号が食い違ったまま残り、そのあとチェックが変わった行が途中に差し込まれる）。
    /// </summary>
    public int Order
    {
        get => _order;
        set
        {
            if (_order == value) return;
            _order = value;
            OnPropertyChanged();
        }
    }

    public override string ToString() => Display;
}

public partial class MainWindow : Window
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private GraphicsDevice? _device;
    private OutputWindow? _output;
    private Compositor? _compositor;
    private OverlayWindow? _overlay;
    // 共有していないモニターごとの膜（そこにある選択中のウィンドウに、くすんだ水色の枠を付けるだけ）
    private readonly List<OverlayWindow> _otherOverlays = [];
    private string? _otherOverlaysKey;
    private ToolbarWindow? _toolbar;
    private readonly ObservableCollection<WindowItem> _items = [];
    // 一覧・サムネイルに出す並べ替え済みの見え方（「共有するモニター上のものだけ表示」の切り替えで絞り直す）
    private System.Windows.Data.ListCollectionView _windowView = null!;
    // 一覧の並び順の採番（新しいウィンドウは上、最初に見つけたものは見つけた順）
    private int _topOrder;
    private int _bottomOrder;
    // 共有中（キャプチャ中）のウィンドウ。UI スレッド専用
    // HWND → 選んだ時点の本人確認情報（HWND の使い回しで別のウィンドウを共有しないため）
    private readonly Dictionary<IntPtr, WindowIdentity> _selected = [];
    // チェック変更をコードから行うときにハンドラを無視する
    private bool _suppressCheckHandler;
    private readonly DispatcherTimer _listTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _monitorFollowTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        // デバッグビルドは自分の画面もキャプチャに写るので、ひと目で分かるようにする
        Title += Interop.Native.ShouldExcludeFromCapture ? " (Debug)" : " (Debug - capturable)";
        if (AppSettings.Profile is { } profile) Title += $" [{profile}]";
#endif
        // 共有するモニターのものを上に、その中で共有中のものを上にまとめる（チェックした瞬間・モニターをまたいだ瞬間に並び替わる）。
        // ほかのモニターのものは後ろ（共有中でも）に、モニターごとの区切り線で分けて並べる。それぞれの中は見つけた順
        var sorted = new System.Windows.Data.ListCollectionView(_items) { IsLiveSorting = true, IsLiveFiltering = true, IsLiveGrouping = true };
        sorted.SortDescriptions.Add(new SortDescription(nameof(WindowItem.GroupOrder), ListSortDirection.Ascending));
        sorted.SortDescriptions.Add(new SortDescription(nameof(WindowItem.IsChecked), ListSortDirection.Descending));
        sorted.SortDescriptions.Add(new SortDescription(nameof(WindowItem.Order), ListSortDirection.Ascending));
        sorted.LiveSortingProperties.Add(nameof(WindowItem.GroupOrder));
        sorted.LiveSortingProperties.Add(nameof(WindowItem.IsChecked));
        sorted.LiveSortingProperties.Add(nameof(WindowItem.Order));
        sorted.GroupDescriptions.Add(MonitorGrouping());
        sorted.LiveGroupingProperties.Add(nameof(WindowItem.Group));
        // 「共有するモニター上のものだけ表示」のときは、ほかのモニターのものを（共有中でも）出さない
        sorted.Filter = o => MonitorOnlyButton.IsChecked != true || ((WindowItem)o).OnSharedMonitor;
        sorted.LiveFilteringProperties.Add(nameof(WindowItem.OnSharedMonitor));
        _windowView = sorted;
        WindowList.ItemsSource = sorted;
        ThumbList.ItemsSource = sorted;
        InitializeLayout();
        PopulateLanguages();
        InitializeColorPicker();
        PopulateBackgrounds();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Loaded += OnLoaded;
        Closed += (_, _) => Shutdown();
        // 「クリックで選択」は右クリックで抜けられる。膜より手前にあるメイン画面の上で右クリックしても抜けるように
        PreviewMouseRightButtonUp += (_, e) =>
        {
            if (_overlay?.IsSelectMode != true) return;
            _overlay.ExitSelectMode();
            e.Handled = true;
        };
        _listTimer.Tick += (_, _) => RefreshList();
        _monitorFollowTimer.Tick += (_, _) => UpdateMonitorFlags();
        _undoTimer.Tick += (_, _) => HideUndo();
        // 表示設定の変更は連続で来るので落ち着いてから 1 回だけ処理する
        _displayChangeTimer.Tick += (_, _) => { _displayChangeTimer.Stop(); OnDisplaySettingsChanged(); };
    }

    private MonitorInfo? CurrentMonitor => MonitorCombo.SelectedItem as MonitorInfo;

    /// <summary>言語コンボの 1 項目（「自動」は今の言語で表示）。</summary>
    private sealed record LanguageOption(string? Code, string NativeName)
    {
        public override string ToString() => Code is null ? Loc.T("Language_Auto") : NativeName;
    }

    private bool _suppressLanguageHandler;

    private void PopulateLanguages()
    {
        _suppressLanguageHandler = true;
        var options = Loc.SupportedLanguages.Select(l => new LanguageOption(l.Code, l.NativeName)).ToList();
        LanguageCombo.ItemsSource = options;
        LanguageCombo.SelectedItem = options.FirstOrDefault(o => o.Code == Loc.Instance.LanguageCode) ?? options[0];
        _suppressLanguageHandler = false;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageHandler || LanguageCombo.SelectedItem is not LanguageOption option) return;
        Loc.Instance.SetLanguage(option.Code);
        App.Settings.Language = option.Code;
        App.Settings.Save();
    }

    /// <summary>コードで組み立てている文字列を新しい言語で作り直す（XAML のバインドは自動で更新される）。</summary>
    private void OnLanguageChanged()
    {
        LanguageCombo.Items.Refresh(); // 「自動」の表示名を更新
        // 選択中の表示はキャッシュされるので、選び直して「(メイン)」表記を更新する
        var monitor = CurrentMonitor;
        MonitorCombo.Items.Refresh();
        MonitorCombo.SelectedItem = null;
        MonitorCombo.SelectedItem = monitor;
        PopulateBackgrounds(); // 選択中の表示を新しい言語に
        // コンパクトのモニターボタンの説明（「(メイン)」表記など）も新しい言語で作り直す
        if (MonitorCombo.ItemsSource is IReadOnlyList<MonitorInfo> monitors) RebuildCompactMonitorButtons(monitors);
        UpdateStatus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            StatusText.Text = Loc.T("Status_NotSupported");
            CloseSplashAndApplyPin();
            IsEnabled = false;
            return;
        }

        // 黄色いキャプチャ枠を消す許可（Win11）。失敗しても続行
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch { }

        var monitors = MonitorEnumerator.GetMonitors();
        MonitorCombo.ItemsSource = monitors;
        RebuildCompactMonitorButtons(monitors);
        var monitor = monitors.FirstOrDefault(m => m.DeviceName == App.Settings.MonitorDevice)
                      ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        // 前回の位置・大きさ・配置で出す
        var placement = App.Settings.Placement switch
        {
            "Normal" => OutputPlacement.Normal,
            "OffScreen" => OutputPlacement.OffScreen,
            _ => OutputPlacement.Back,
        };
        _output = new OutputWindow(App.Settings.OutputWindow, placement);
        _output.LayoutChangedByUser += () =>
        {
            if (_shuttingDown || _output is null) return;
            App.Settings.OutputWindow = _output.CaptureState();
            App.Settings.Save();
        };
        _output.Show();
        _output.Host.PixelSizeChanged += (pw, ph) => _compositor?.Resize(pw, ph);
        Placement_Checked(this, e); // 保存されていた配置を適用
        CreatePipeline(monitor);
        CreateOverlay(monitor);

        MonitorCombo.SelectedItem = monitor;
        RefreshList();
        UpdateStatus();
        OnPipelineReady();
        CloseSplashAndApplyPin();
        _listTimer.Start();
        _monitorFollowTimer.Start();
        ApplyRemoteSettings();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChangedRaw;
    }

    /// <summary>GPU デバイスと合成器を作る。</summary>
    private void CreatePipeline(MonitorInfo monitor)
    {
        _device = new GraphicsDevice();
        var (w, h) = _output!.Host.GetPixelSize();
        _compositor = new Compositor(_device, _output.Host.ChildHandle, w, h, monitor.Bounds)
        {
            CaptureCursor = CursorCheck.IsChecked == true,
            IncludePopups = PopupCheck.IsChecked == true,
        };
        _compositor.SourceClosed += hwnd => Dispatcher.BeginInvoke(() => OnSourceClosed(hwnd));
        _compositor.RemoteMode = monitor.IsRemote;
        _compositor.FrameSink = _remoteChild is { } child && _remoteChildConnected ? child.SubmitFrame : null;
        _compositor.Paused = _paused;
        _compositor.DeviceLost += () => Dispatcher.BeginInvoke(RecoverFromDeviceLost);
        ApplyBackground(); // デバイスを作り直したときも背景を載せ直す
    }

    private void DisposePipeline()
    {
        try { _compositor?.Dispose(); } catch (Exception ex) { Log.Error("合成器の破棄に失敗", ex); }
        try { _device?.Dispose(); } catch (Exception ex) { Log.Error("デバイスの破棄に失敗", ex); }
        _compositor = null;
        _device = null;
    }

    /// <summary>GPU デバイス喪失（ドライバ更新・スリープ復帰等）から、選択状態を保ったまま復旧する。</summary>
    private void RecoverFromDeviceLost()
    {
        if (_shuttingDown) return;
        StatusText.Text = Loc.T("Status_Recovering");
        var selected = _selected.Values.ToList();
        DisposePipeline();
        try
        {
            CreatePipeline(CurrentMonitor ?? MonitorEnumerator.GetMonitors()[0]);
        }
        catch (Exception ex)
        {
            // GPU がまだ戻っていない。少し待って再試行
            Log.Error("パイプライン再作成に失敗。再試行します", ex);
            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            retry.Tick += (_, _) => { retry.Stop(); RecoverFromDeviceLost(); };
            retry.Start();
            return;
        }

        foreach (var identity in selected)
        {
            // 復旧を待つ間に閉じられ、HWND が別のウィンドウに使い回されているかもしれないので本人確認してから
            // クロップと「確認・調整中・確認待ちは見せない」も引き継ぐ
            try { _compositor!.AddWindow(identity, GetCropDip(identity.Handle), hidden: IsWaitingForReview(identity.Handle)); }
            catch (Exception ex)
            {
                Log.Error($"復旧時のキャプチャ再開に失敗: {identity.Handle}", ex);
                // クロップや確認待ちの状態も含めて外す（選び直したときに古い状態が残らないように）
                RemoveSelected(identity.Handle);
            }
        }
        Log.Info($"GPU デバイス喪失から復旧（{_selected.Count} 個再開）");
        UpdateStatus();
    }

    private void OnDisplaySettingsChangedRaw(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _displayChangeTimer.Stop(); _displayChangeTimer.Start(); });

    /// <summary>モニターの抜き差し・解像度・配置変更に追従する。</summary>
    private void OnDisplaySettingsChanged()
    {
        if (_shuttingDown) return;
        if (MonitorEnumerator.GetMonitors().Count == 0) return;
        RefreshMonitorList(); // SelectionChanged でキャンバス・オーバーレイを更新
        if (CurrentMonitor is { } next)
        {
            Log.Info($"表示設定の変更 → {next.Display}");
            ApplyMonitor(next);
        }
        _output?.ReapplyPlacement();
    }

    private void CreateOverlay(MonitorInfo monitor)
    {
        _overlay = new OverlayWindow(BorderTargets, GetCropDip, monitor.Bounds);
        _overlay.CropConfirmed += OnCropConfirmed;
        _overlay.CropCancelled += OnCropCancelled;
        _overlay.WindowClicked += ToggleSelection;
        _overlay.WindowCtrlClicked += CropSelection;
        _overlay.SelectModeExited += () =>
        {
            _toolbar?.SetSelectMode(false);
            OnSelectModeChanged(false);
        };

        _toolbar = new ToolbarWindow(monitor.Bounds);
        _toolbar.SelectModeToggled += on =>
        {
            // 膜の上に出すため、選択モードに入る前にメイン画面を常に手前にしておく
            if (on) OnSelectModeChanged(true);
            if (on) _overlay.EnterSelectMode();
            else _overlay.ExitSelectMode();
        };
        _toolbar.BordersToggled += on =>
        {
            _overlay.ShowBorders = on;
            foreach (var other in _otherOverlays) other.ShowBorders = on;
        };
        // ツールバーの上で右クリックしても「クリックで選択」を抜ける
        _toolbar.RightClicked += () => _overlay.ExitSelectMode();
        _toolbar.BringToFrontRequested += BringSharedToFront;
        _toolbar.PauseToggled += TogglePause;
        _toolbar.SettingsRequested += () =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };

        _overlay.Show();
        _toolbar.Show();
        _overlay.ToolbarHandle = _toolbar.Handle;
        _overlay.MainHandle = new WindowInteropHelper(this).Handle;
        SetOverlayVisible(OverlayCheck.IsChecked == true);
    }

    /// <summary>枠を付けるウィンドウ。確認待ち（まだ見せていない）のものには付けない。</summary>
    private IReadOnlyCollection<IntPtr> BorderTargets() => _selected.Keys.Where(h => !_reviewQueue.Contains(h)).ToList();

    /// <summary>
    /// 共有していないモニターに膜を 1 枚ずつ置く（そこへ動かした選択中のウィンドウに、相手には見えていないことが分かる、くすんだ水色の枠を付ける）。
    /// モニターの構成か共有するモニターが変わったときだけ作り直す（言語の切り替えでの選び直しなどでは何もしない）。
    /// </summary>
    private void UpdateOtherOverlays(MonitorInfo shared)
    {
        if (_overlay is null || _toolbar is null) return;
        var others = MonitorEnumerator.GetMonitors().Where(m => m.DeviceName != shared.DeviceName).ToList();
        string key = shared.DeviceName + "|" + string.Join(";", others.Select(m => $"{m.DeviceName}:{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width},{m.Bounds.Height}"));
        if (key == _otherOverlaysKey) return;
        _otherOverlaysKey = key;
        foreach (var old in _otherOverlays) old.CloseForShutdown();
        _otherOverlays.Clear();
        foreach (var m in others)
        {
            var overlay = new OverlayWindow(BorderTargets, GetCropDip, m.Bounds, secondary: true) { ToolbarHandle = _toolbar.Handle };
            overlay.IgnoredHandles.Add(_overlay.Handle);
            overlay.ShowBorders = _overlay.ShowBorders;
            overlay.Show();
            overlay.Visibility = _overlay.Visibility;
            _otherOverlays.Add(overlay);
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        if (_overlay is null || _toolbar is null) return;
        if (!visible) _overlay.ExitSelectMode();
        _overlay.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        _toolbar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        foreach (var other in _otherOverlays) other.Visibility = _overlay.Visibility;
    }

    /// <summary>一覧を最新化する。既存の行は残したまま、増減とタイトル変更だけ反映する。</summary>
    private void RefreshList()
    {
        // 閉じた通知を取りこぼしていても、2 秒ごとの本人確認で共有から外す
        foreach (var dead in _selected.Values.Where(id => !id.IsAlive()).ToList())
            RemoveSelected(dead.Handle);

        // ほかのモニターのものも一覧には持っておく（並べ方と「共有するモニター上のものだけ表示」は見え方の側で決める）
        var current = WindowEnumerator.GetCapturableWindows();
        var byHandle = current.ToDictionary(w => w.Handle);

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.IsRemote) continue;
            if (byHandle.TryGetValue(item.Handle, out var info))
            {
                item.Info = info;
            }
            else if (!_selected.ContainsKey(item.Handle))
            {
                // 共有中のものは閉じられた通知で消すので、ここでは消さない（最小化中など一時的に見えないだけの場合もある）
                item.PropertyChanged -= OnItemPropertyChanged;
                _items.RemoveAt(i);
            }
        }

        // 新しく開いたウィンドウは目に付くよう（共有中のものの次の）先頭に入れる（current は手前 → 奥の順）
        var existing = _items.Select(i => i.Handle).ToHashSet();
        var added = current.Where(w => !existing.Contains(w.Handle)).ToList();
        var groups = new Dictionary<IntPtr, MonitorGroup>();
        bool initial = existing.Count == 0;
        int order = initial ? _bottomOrder : _topOrder - added.Count;
        if (initial) _bottomOrder += added.Count;
        else _topOrder -= added.Count;
        int insertAt = 0;
        foreach (var info in added)
        {
            var item = new WindowItem(info) { IsChecked = _selected.ContainsKey(info.Handle) };
            item.CropUiVisible = true;
            item.HasSavedCrop = SavedCropFor(item) > 0;
            item.CropEnabled = CropPreferenceFor(item) == true;
            item.Group = GroupOf(item.Handle, groups);
            item.Order = order++;
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Insert(initial ? _items.Count : insertAt++, item);
        }
        UpdateMonitorFlags();
        PublishRemoteWindowList();
    }

    // 子の画面（R）を共有している間は、この PC のウィンドウはどれも共有するモニターの上にない
    private bool IsOnSharedMonitor(IntPtr hwnd) =>
        CurrentMonitor is not { } monitor || (!monitor.IsRemote && WindowEnumerator.IsOnMonitor(hwnd, monitor.Bounds));

    /// <summary>モニターごとのまとまりで分ける（まとまりは共有するモニター → Windows の番号順）。</summary>
    internal static System.Windows.Data.PropertyGroupDescription MonitorGrouping()
    {
        var grouping = new System.Windows.Data.PropertyGroupDescription(nameof(WindowItem.Group));
        grouping.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        return grouping;
    }

    /// <summary>ウィンドウが入るモニターのまとまり。共有するモニターに一部でも重なっていればそちら、ほかは一番大きく重なっているモニター。</summary>
    private MonitorGroup GroupOf(IntPtr hwnd, Dictionary<IntPtr, MonitorGroup> cache)
    {
        if (IsOnSharedMonitor(hwnd)) return MonitorGroup.Shared;
        // 最小化中は元に戻したときの位置で決まる
        IntPtr hMon = Interop.Native.MonitorFromWindow(hwnd, Interop.Native.MONITOR_DEFAULTTONEAREST);
        if (cache.TryGetValue(hMon, out var group)) return group;
        var info = new Interop.Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Interop.Native.MONITORINFOEX>() };
        string device = Interop.Native.GetMonitorInfo(hMon, ref info) ? info.szDevice : "?";
        // どのモニターにも映っていない（画面の外に置かれたなど）もので、一番近いのが共有するモニターなら、共有するモニターの側に入れる
        if (device == CurrentMonitor?.DeviceName)
        {
            cache[hMon] = MonitorGroup.Shared;
            return MonitorGroup.Shared;
        }
        int number = MonitorNumber(device);
        // 番号が分からないものは最後（共有するモニターの 0 と重ならないよう 1 以上）
        group = new MonitorGroup(number == int.MaxValue ? 9999 : Math.Max(1, number), device.TrimStart('\\', '.'));
        cache[hMon] = group;
        return group;
    }

    /// <summary>
    /// 各行が共有するモニターの上にあるかを付け直す（一覧の並びと薄い表示がモニター間の移動に付いてくる）。
    /// 一覧の見直し（2 秒ごと）より細かく、0.5 秒ごとに呼ぶ。ウィンドウの位置を聞くだけなので軽い。
    /// </summary>
    private void UpdateMonitorFlags()
    {
        bool changed = false;
        var groups = new Dictionary<IntPtr, MonitorGroup>();
        foreach (var item in _items)
        {
            var group = item.IsRemote ? RemoteItemGroup() : GroupOf(item.Handle, groups);
            if (item.Group == group) continue;
            item.Group = group;
            changed = true;
        }
        // サムネイルの薄さは DWM に送り直す（WPF の不透明度は DWM のサムネイルには効かないので）
        if (changed) _thumbnails?.UpdateAll();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    /// <summary>
    /// 全部外す。「やばい、全部隠したい」の緊急ボタンなので確認は出さず、代わりに数秒だけ「元に戻す」を出す。
    /// </summary>
    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        // 子のウィンドウ（R の行）を共有していれば、子にも「全部外す」を 1 通で頼む（戻す分は子が覚える）
        bool remote = _remoteParent is not null && _items.Any(i => i.IsRemote && i.IsChecked);
        if (remote) _remoteParent!.RequestUncheckAll();
        var snapshot = UncheckAllCore();
        if (snapshot.Count > 0 || remote) ShowUndo(snapshot, remote);
    }

    /// <summary>この PC の共有を全部外す。元に戻す用に、相手に見えていたもの（確認待ちでないもの）とクロップ量を返す。</summary>
    private List<(WindowIdentity Identity, double Crop)> UncheckAllCore()
    {
        var snapshot = _selected.Values.Where(id => !IsWaitingForReview(id.Handle))
            .Select(id => (Identity: id, Crop: GetCropDip(id.Handle))).ToList();
        foreach (var hwnd in _selected.Keys.ToList()) RemoveSelected(hwnd);
        _compositor?.RemoveAll();
        SetCheckedSilently(_items, false);
        UpdateStatus();
        return snapshot;
    }

    // 一時停止中か（相手には背景だけを映す。選んだもの・クロップ・確認待ちはそのまま）。起動し直したら解ける
    private bool _paused;

    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();

    /// <summary>一時停止・再開を切り替える。</summary>
    private void TogglePause()
    {
        _paused = !_paused;
        if (_compositor is not null) _compositor.Paused = _paused;
        Log.Info(_paused ? "一時停止しました" : "再開しました");
        UpdateOverlayMuted();
        UpdateStatus();
    }

    /// <summary>一時停止のボタン（一覧・コンパクト・ツールバー）を今の状態に合わせる。</summary>
    private void UpdatePauseUi()
    {
        string icon = _paused ? "\uE768" : "\uE769"; // 再開（▶）／一時停止（⏸）
        string tip = Loc.T(_paused ? "Resume_Tip" : "Pause_Tip");
        foreach (var button in new[] { PauseButton, CompactPauseButton })
        {
            button.Content = icon;
            button.ToolTip = tip;
        }
        _toolbar?.SetPaused(_paused);
    }

    /// <summary>
    /// 自分の画面の枠をくすんだ水色にするか。一時停止中と、R（子の画面）を共有している間は、選んだウィンドウが相手に見えていないので。
    /// </summary>
    private void UpdateOverlayMuted()
    {
        if (_overlay is not null) _overlay.Muted = _paused || CurrentMonitor?.IsRemote == true;
    }

    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(6);
    private readonly DispatcherTimer _undoTimer = new() { Interval = UndoDuration };
    private List<(WindowIdentity Identity, double Crop)> _undoSnapshot = [];
    // 「全部外す」で子にも外してもらったか（元に戻すときに、子にも戻してもらう）
    private bool _undoRemote;

    private void ShowUndo(List<(WindowIdentity Identity, double Crop)> snapshot, bool remote = false)
    {
        _undoSnapshot = snapshot;
        _undoRemote = remote;
        UndoBar.Visibility = CompactUndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        _undoSnapshot = [];
        _undoRemote = false;
        UndoBar.Visibility = CompactUndoBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>全部外したのを取り消す: 同じウィンドウを同じクロップ量で、確認なしでそのまま共有し直す。</summary>
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _undoSnapshot;
        bool remote = _undoRemote;
        HideUndo();
        if (remote) _remoteParent?.RequestRestoreAll();
        RestoreSharing(snapshot);
    }

    /// <summary>全部外したものを、同じクロップ量で、確認なしでそのまま共有し直す（閉じたものは飛ばす）。</summary>
    private void RestoreSharing(List<(WindowIdentity Identity, double Crop)> snapshot)
    {
        foreach (var (identity, crop) in snapshot)
        {
            if (!identity.IsAlive() || _selected.ContainsKey(identity.Handle)) continue;
            var item = _items.FirstOrDefault(i => i.Handle == identity.Handle);
            if (item is null) continue;
            SetCheckedSilently([item], true);
            if (!StartSharing(item, crop, hidden: false)) continue;
            if (crop > 0) _crop[item.Handle] = crop;
        }
        SyncCropChecks();
        UpdateStatus();
    }

    /// <summary>オーバーレイの選択モードでクリックされたウィンドウの共有を切り替える。</summary>
    private void ToggleSelection(IntPtr hwnd)
    {
        var item = FindItem(hwnd);
        if (item is null) return;
        item.IsChecked = !item.IsChecked;
        WindowList.ScrollIntoView(item);
    }

    /// <summary>
    /// 選択モードで Ctrl＋クリック: いつも「上辺を調整してクロップ」に入る（知っている人向けの隠し操作）。
    /// 共有していなければクロップをオンにして調整 → 確定で共有、共有中なら外さずにクロップ量を決め直す。
    /// </summary>
    private void CropSelection(IntPtr hwnd)
    {
        var item = FindItem(hwnd);
        if (item is null) return;
        App.Settings.CropUse[AppKeyOf(item)] = true;
        App.Settings.Save();
        // 調整が終わったら「クリックで選択」に戻る（続けて選べるように）。調整を始めると選択モードは一度抜ける
        _resumeSelectAfterEdit = _overlay?.IsSelectMode == true;
        _resumeSelectFromCompact = _selectFromCompact;
        BeginAdjust(item);
        WindowList.ScrollIntoView(item);
    }

    // Ctrl＋クリックでクロップを調整したあと、「クリックで選択」に戻るか（とコンパクトのボタンから始めていたか）
    private bool _resumeSelectAfterEdit, _resumeSelectFromCompact;

    /// <summary>Ctrl＋クリックで始めた調整が終わったら、「クリックで選択」に戻す。</summary>
    private void ResumeSelectAfterEdit()
    {
        if (!_resumeSelectAfterEdit || _cropEditing != IntPtr.Zero || _toolbar is null) return;
        _resumeSelectAfterEdit = false;
        bool fromCompact = _resumeSelectFromCompact;
        // 調整の後片付け（対象を前に出す等）が済んでから戻す
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown || _cropEditing != IntPtr.Zero) return;
            _selectFromCompact = fromCompact;
            _toolbar?.SetSelectMode(true);
        }, DispatcherPriority.Background);
    }

    /// <summary>一覧の行を探す（まだ一覧に無ければ取り直してから）。</summary>
    private WindowItem? FindItem(IntPtr hwnd)
    {
        var item = _items.FirstOrDefault(i => i.Handle == hwnd);
        if (item is null)
        {
            RefreshList();
            item = _items.FirstOrDefault(i => i.Handle == hwnd);
        }
        return item;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCheckHandler || sender is not WindowItem item) return;
        if (e.PropertyName == nameof(WindowItem.CropEnabled))
        {
            if (!item.IsRemote) OnCropEnabledChanged(item);
            return;
        }
        if (e.PropertyName != nameof(WindowItem.IsChecked)) return;
        if (item.IsRemote)
        {
            // 子のウィンドウ: 子に頼むだけ（子が共有に入ったら、届く一覧でチェックが確定する）。
            // 「共有前に確認」がオンなら、頼む前にカーソルのそばで確かめる（子の画面では確かめない）
            if (item.IsChecked && App.Settings.ConfirmBeforeShare) ConfirmRemoteShare(item);
            else RequestRemoteShare(item, item.IsChecked);
            return;
        }

        if (item.IsChecked)
        {
            // クロップ・共有前の確認の設定に応じて、すぐ見せるか確認してから見せるかを決める
            BeginShare(item);
        }
        else
        {
            RemoveSelected(item.Handle);
        }
        UpdateStatus();
    }

    /// <summary>キャプチャを始めて共有中にする。hidden は「選択はするが、まだ相手に見せない」（クロップ調整中）。</summary>
    private bool StartSharing(WindowItem item, double cropDip, bool hidden)
    {
        IntPtr hwnd = item.Handle;
        var identity = item.Info.Identity;
        if (!identity.IsAlive())
        {
            // 一覧に出してから選ぶまでの間に閉じられた（HWND が別のウィンドウに使い回されたかも）
            SetCheckedSilently([item], false);
            RefreshList();
            return false;
        }
        if (_compositor is null)
        {
            // GPU 復旧中: 選択だけ覚えておき、復旧したらキャプチャを始める
            _selected[hwnd] = identity;
            return true;
        }
        try
        {
            _compositor.AddWindow(identity, cropDip, hidden);
            _selected[hwnd] = identity;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"キャプチャ開始に失敗: {item.Display}", ex);
            SetCheckedSilently([item], false);
            StatusText.Text = ex.HResult == E_ACCESSDENIED || ex is UnauthorizedAccessException
                ? Loc.F("Status_ElevatedWindow", item.Title)
                : Loc.F("Status_CaptureFailed", item.Title, ex.Message);
            return false;
        }
    }

    /// <summary>共有をやめてチェックを外す（閉じた・使い回された・別アプリになった等）。</summary>
    private void RemoveSelected(IntPtr hwnd)
    {
        if (_cropEditing == hwnd)
        {
            // 調整中なら調整を打ち切る（キャンセル通知は無視させる）
            _cropEditing = IntPtr.Zero;
            _overlay?.CancelCropEdit();
        }
        // 確認待ちの列からも外す（あとで同じ HWND を選び直したときに古い状態が残らないように）
        _reviewQueue.Remove(hwnd);
        _compositor?.RemoveWindow(hwnd);
        _selected.Remove(hwnd);
        _crop.Remove(hwnd);
        SetCheckedSilently(_items.Where(i => i.Handle == hwnd), false);
        SyncCropChecks();
        // 確認中のものを外したら、待っている次の確認へ進む
        ProcessReviewQueue();
    }

    private void SetCheckedSilently(IEnumerable<WindowItem> items, bool value)
    {
        _suppressCheckHandler = true;
        foreach (var item in items) item.IsChecked = value;
        _suppressCheckHandler = false;
    }

    private void OnSourceClosed(IntPtr hwnd)
    {
        RemoveSelected(hwnd);
        RefreshList();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        // 確認・調整待ちのものはまだ相手に見えていないので数えない
        int count = VisibleSharedCount;
        int pending = _selected.Count - count;
        bool remote = CurrentMonitor?.IsRemote == true;
        _toolbar?.SetCount(count, remote);
        // クロップの調整・確認の間は、ツールバーをたたまない
        _toolbar?.SetHold(_cropEditing != IntPtr.Zero);
        int remoteShared = RemoteSharedCount;
        string summary = remote ? Loc.F("Status_SharingRemote", CurrentMonitor!.RemoteName)
            : count == 0 && remoteShared > 0 ? Loc.F("Status_SharingOnRemote", _remoteMonitor?.RemoteName ?? "", remoteShared)
            : count == 0 ? Loc.T("Status_NoneShared") : Loc.F("Status_Sharing", count);
        if (pending > 0) summary += Loc.F("Status_Pending", pending);
        if (_paused) summary = Loc.T("Status_Paused") + " " + summary;
        // 子の画面で確認・クロップの範囲を決めるのを待っているものがあれば知らせる（親からは「頼んだのに映らない」ように見えるので）
        int remotePending = _items.Count(i => i.IsRemote && i.IsChecked && i.RemotePending);
        if (remotePending > 0) summary += "\n" + Loc.F("Status_RemotePending", _remoteMonitor?.RemoteName ?? "", remotePending);
        StatusText.Text = summary + "\n" + Loc.F("Status_ShareHint", Loc.T("Output_Title"));
        CompactStatusText.Text = summary;
        // 共有中が無いときは「まとめて最前面」「全部外す」を押せない
        BringAllButton.IsEnabled = CompactBringAllButton.IsEnabled = count > 0;
        // 子のウィンドウ（親の一覧の R の行）を共有中のときも押せる
        UncheckAllButton.IsEnabled = CompactUncheckAllButton.IsEnabled = _selected.Count > 0 || _items.Any(i => i.IsRemote && i.IsChecked);
        // 何か選び直したら「元に戻す」は引っ込める（戻すと選び直した分とぶつかる）
        if (_selected.Count > 0 && _undoSnapshot.Count > 0) HideUndo();
        UpdatePauseUi();
        // 子: 親の「全部外す」のあとにこの PC で何か共有したら、親の「元に戻す」で戻す分は古いので捨てる
        if (_selected.Count > 0) _parentUndoSnapshot = [];
        PublishRemoteWindowList();
        UpdateEmptyStates();
        UpdateRemoteBadge();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentMonitor is not { } monitor) return;
        ApplyMonitor(monitor);
        SyncCompactMonitorButtons();
        if (_compositor is not null) RefreshList(); // 一覧をこのモニターのものに
        UpdateStatus();
        // 子の画面（R）はつながっているときだけあるので、次の起動用には覚えない
        if (monitor.IsRemote) return;
        App.Settings.MonitorDevice = monitor.DeviceName;
        App.Settings.Save();
    }

    // 最後に選んでいたこの PC のモニター（R を選んでいる間の、膜とツールバーの置き場所）
    private string? _lastLocalMonitor;

    private void ApplyMonitor(MonitorInfo monitor)
    {
        if (monitor.IsRemote)
        {
            // 子の画面を共有: 出力は子の絵だけ。この PC の膜とツールバーは今のモニターに置いたまま、枠はくすんだ水色にする
            // （この PC のウィンドウはどれも相手に見えていないので）
            if (_compositor is not null)
            {
                _compositor.Canvas = monitor.Bounds;
                _compositor.RemoteMode = true;
            }
            _output?.SetCanvasSize(monitor.Bounds.Width, monitor.Bounds.Height);
            UpdateOverlayMuted();
            // 膜とツールバーは、最後に選んでいたこの PC のモニターに置く。抜かれていればメインモニターへ
            // （R を選んでいる間にモニターを抜き差ししても、無くなったモニターの位置に残らないように）
            var local = MonitorEnumerator.GetMonitors();
            if ((local.FirstOrDefault(m => m.DeviceName == _lastLocalMonitor) ?? local.FirstOrDefault(m => m.IsPrimary) ?? local.FirstOrDefault()) is { } place)
            {
                _overlay?.SetMonitor(place.Bounds);
                _toolbar?.SetMonitor(place.Bounds);
                UpdateOtherOverlays(place);
            }
            return;
        }
        _lastLocalMonitor = monitor.DeviceName;
        UpdateOverlayMuted();
        if (_compositor is not null) _compositor.RemoteMode = false;
        if (_compositor is not null) _compositor.Canvas = monitor.Bounds;
        // 出力の中身をモニターと同じ縦横比にして、共有映像に帯が出ないようにする
        _output?.SetCanvasSize(monitor.Bounds.Width, monitor.Bounds.Height);
        _overlay?.SetMonitor(monitor.Bounds);
        _toolbar?.SetMonitor(monitor.Bounds);
        UpdateOtherOverlays(monitor);
    }

    private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        SetOverlayVisible(OverlayCheck.IsChecked == true);
        SaveOptions();
    }

    private void CursorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_compositor is not null) _compositor.CaptureCursor = CursorCheck.IsChecked == true;
        SaveOptions();
    }

    private void PopupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_compositor is not null) _compositor.IncludePopups = PopupCheck.IsChecked == true;
        SaveOptions();
    }

    /// <summary>設定で選んでいる出力ウィンドウの配置。</summary>
    private OutputPlacement SelectedPlacement =>
        PlacementNormal.IsChecked == true ? OutputPlacement.Normal :
        PlacementOffScreen.IsChecked == true ? OutputPlacement.OffScreen :
        OutputPlacement.Back;

    private void Placement_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 読み込み中（IsChecked="True" の適用時）は他の部品がまだ無い
        if (!IsInitialized) return;
        // リモート連携の子として動いている間は画面外に置いたまま（設定だけ覚えて、子をやめたら戻す）
        if (_output is not null && !_childOutputOffScreen) _output.Placement = SelectedPlacement;
        SaveOptions();
    }

    /// <summary>
    /// 設定とログ（%LOCALAPPDATA%\ShareWinSelector）を消して終了する。アプリを消す前の後片付け用。
    /// 終了処理中の保存やログでフォルダが作り直されないよう、先に書き込みを止めてから閉じ、最後にフォルダごと消す。
    /// </summary>
    private void DeleteData_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, Loc.F("Settings_DeleteDataConfirm", AppSettings.Folder), Loc.T("Settings_DeleteDataTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        Log.Info("設定を削除して終了");
        AppSettings.SavingDisabled = true;
        Log.Disabled = true;
        App.DeleteDataOnExit = true;
        Close();
    }

    private void Shutdown()
    {
        _shuttingDown = true;
        SaveWindowBounds();
        if (_output is not null) App.Settings.OutputWindow = _output.CaptureState();
        App.Settings.Save();
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        StopRemote();
        _remoteIdentity?.Dispose();
        _listTimer.Stop();
        _monitorFollowTimer.Stop();
        _displayChangeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChangedRaw;
        _thumbnails?.Dispose();
        _overlay?.CloseForShutdown();
        foreach (var other in _otherOverlays) other.CloseForShutdown();
        _toolbar?.CloseForShutdown();
        DisposePipeline();
        _output?.CloseForShutdown();
        Application.Current.Shutdown();
    }
}
