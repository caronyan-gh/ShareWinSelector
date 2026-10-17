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
        }
    }

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>一覧での並び順（小さいほど上）。共有中のものはこれより優先して上に並ぶ。</summary>
    public int Order { get; set; }

    public override string ToString() => Display;
}

public partial class MainWindow : Window
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private GraphicsDevice? _device;
    private OutputWindow? _output;
    private Compositor? _compositor;
    private OverlayWindow? _overlay;
    private ToolbarWindow? _toolbar;
    private readonly ObservableCollection<WindowItem> _items = [];
    // 一覧の並び順の採番（新しいウィンドウは上、最初に見つけたものは見つけた順）
    private int _topOrder;
    private int _bottomOrder;
    // 共有中（キャプチャ中）のウィンドウ。UI スレッド専用
    // HWND → 選んだ時点の本人確認情報（HWND の使い回しで別のウィンドウを共有しないため）
    private readonly Dictionary<IntPtr, WindowIdentity> _selected = [];
    // チェック変更をコードから行うときにハンドラを無視する
    private bool _suppressCheckHandler;
    private readonly DispatcherTimer _listTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _shuttingDown;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        // デバッグビルドは自分の画面もキャプチャに写るので、ひと目で分かるようにする
        Title += Interop.Native.ShouldExcludeFromCapture ? " (Debug)" : " (Debug - capturable)";
#endif
        // 共有中のものを上にまとめる（チェックした瞬間に並び替わる）。それ以外は見つけた順
        var sorted = new System.Windows.Data.ListCollectionView(_items) { IsLiveSorting = true };
        sorted.SortDescriptions.Add(new SortDescription(nameof(WindowItem.IsChecked), ListSortDirection.Descending));
        sorted.SortDescriptions.Add(new SortDescription(nameof(WindowItem.Order), ListSortDirection.Ascending));
        sorted.LiveSortingProperties.Add(nameof(WindowItem.IsChecked));
        WindowList.ItemsSource = sorted;
        ThumbList.ItemsSource = sorted;
        InitializeLayout();
        PopulateLanguages();
        PopulateBackgrounds();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Loaded += OnLoaded;
        Closed += (_, _) => Shutdown();
        _listTimer.Tick += (_, _) => RefreshList();
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
        UpdateStatus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            StatusText.Text = Loc.T("Status_NotSupported");
            App.CloseSplash();
            IsEnabled = false;
            return;
        }

        // 黄色いキャプチャ枠を消す許可（Win11）。失敗しても続行
        try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch { }

        var monitors = MonitorEnumerator.GetMonitors();
        MonitorCombo.ItemsSource = monitors;
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
        App.CloseSplash();
        _listTimer.Start();
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
        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0) return;
        string? current = CurrentMonitor?.DeviceName;
        var next = monitors.FirstOrDefault(m => m.DeviceName == current)
                   ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        Log.Info($"表示設定の変更: {string.Join(", ", monitors.Select(m => m.Display))} → {next.Display}");
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem = next; // SelectionChanged でキャンバス・オーバーレイを更新
        ApplyMonitor(next);
        _output?.ReapplyPlacement();
    }

    private void CreateOverlay(MonitorInfo monitor)
    {
        // 確認待ち（まだ見せていない）のものには「共有中」の枠を付けない
        _overlay = new OverlayWindow(() => _selected.Keys.Where(h => !_reviewQueue.Contains(h)).ToList(), GetCropDip, monitor.Bounds);
        _overlay.CropConfirmed += OnCropConfirmed;
        _overlay.CropCancelled += OnCropCancelled;
        _overlay.WindowClicked += ToggleSelection;
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
        _toolbar.BordersToggled += on => _overlay.ShowBorders = on;
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

    private void SetOverlayVisible(bool visible)
    {
        if (_overlay is null || _toolbar is null) return;
        if (!visible) _overlay.ExitSelectMode();
        _overlay.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        _toolbar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>一覧を最新化する。既存の行は残したまま、増減とタイトル変更だけ反映する。</summary>
    private void RefreshList()
    {
        // 閉じた通知を取りこぼしていても、2 秒ごとの本人確認で共有から外す
        foreach (var dead in _selected.Values.Where(id => !id.IsAlive()).ToList())
            RemoveSelected(dead.Handle);

        var current = WindowEnumerator.GetCapturableWindows();
        // 共有するモニター上のものだけにする（共有中のものは下の処理で残る）
        if (MonitorOnlyButton.IsChecked == true && CurrentMonitor is { } monitor)
            current = current.Where(w => WindowEnumerator.IsOnMonitor(w.Handle, monitor.Bounds)).ToList();
        var byHandle = current.ToDictionary(w => w.Handle);

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
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
            item.Order = order++;
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Insert(initial ? _items.Count : insertAt++, item);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void UncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var hwnd in _selected.Keys.ToList()) RemoveSelected(hwnd);
        _compositor?.RemoveAll();
        SetCheckedSilently(_items, false);
        UpdateStatus();
    }

    /// <summary>オーバーレイの選択モードでクリックされたウィンドウの共有を切り替える。</summary>
    private void ToggleSelection(IntPtr hwnd)
    {
        var item = _items.FirstOrDefault(i => i.Handle == hwnd);
        if (item is null)
        {
            RefreshList();
            item = _items.FirstOrDefault(i => i.Handle == hwnd);
        }
        if (item is null) return;
        item.IsChecked = !item.IsChecked;
        WindowList.ScrollIntoView(item);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCheckHandler || sender is not WindowItem item) return;
        if (e.PropertyName == nameof(WindowItem.CropEnabled))
        {
            OnCropEnabledChanged(item);
            return;
        }
        if (e.PropertyName != nameof(WindowItem.IsChecked)) return;

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
        _toolbar?.SetCount(count);
        string summary = count == 0 ? Loc.T("Status_NoneShared") : Loc.F("Status_Sharing", count);
        if (pending > 0) summary += Loc.F("Status_Pending", pending);
        StatusText.Text = summary + "\n" + Loc.F("Status_ShareHint", Loc.T("Output_Title"));
        CompactStatusText.Text = summary;
        UpdateEmptyStates();
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentMonitor is not { } monitor) return;
        ApplyMonitor(monitor);
        if (_compositor is not null) RefreshList(); // 一覧をこのモニターのものに
        App.Settings.MonitorDevice = monitor.DeviceName;
        App.Settings.Save();
    }

    private void ApplyMonitor(MonitorInfo monitor)
    {
        if (_compositor is not null) _compositor.Canvas = monitor.Bounds;
        // 出力の中身をモニターと同じ縦横比にして、共有映像に帯が出ないようにする
        _output?.SetCanvasSize(monitor.Bounds.Width, monitor.Bounds.Height);
        _overlay?.SetMonitor(monitor.Bounds);
        _toolbar?.SetMonitor(monitor.Bounds);
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

    private void Placement_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 読み込み中（IsChecked="True" の適用時）は他の部品がまだ無い
        if (!IsInitialized) return;
        var placement =
            PlacementNormal.IsChecked == true ? OutputPlacement.Normal :
            PlacementOffScreen.IsChecked == true ? OutputPlacement.OffScreen :
            OutputPlacement.Back;
        if (_output is not null) _output.Placement = placement;
        SaveOptions();
    }

    private void Shutdown()
    {
        _shuttingDown = true;
        SaveWindowBounds();
        if (_output is not null) App.Settings.OutputWindow = _output.CaptureState();
        App.Settings.Save();
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _listTimer.Stop();
        _displayChangeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChangedRaw;
        _thumbnails?.Dispose();
        _overlay?.CloseForShutdown();
        _toolbar?.CloseForShutdown();
        DisposePipeline();
        _output?.CloseForShutdown();
        Application.Current.Shutdown();
    }
}
