using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using ShareWinSelector.Interop;

namespace ShareWinSelector;

/// <summary>メイン画面の見た目まわり（ピン留め・コンパクト表示・一覧/サムネイル・プレビュー・設定の保存）。</summary>
public partial class MainWindow
{
    private const double DefaultNormalWidth = 1000, DefaultNormalHeight = 660;
    private const double DefaultCompactWidth = 340, DefaultCompactHeight = 440;

    private DwmThumbnails? _thumbnails;
    private bool _compact;
    private bool _applyingSettings;

    /// <summary>コンストラクタから呼ぶ。保存された設定を画面に反映する。</summary>
    private void InitializeLayout()
    {
        _applyingSettings = true;
        var s = App.Settings;
        OverlayCheck.IsChecked = s.ShowOverlay;
        CursorCheck.IsChecked = s.CaptureCursor;
        PopupCheck.IsChecked = s.IncludePopups;
        (s.Placement switch
        {
            "Normal" => PlacementNormal,
            "OffScreen" => PlacementOffScreen,
            _ => PlacementBack,
        }).IsChecked = true;
        PinButton.IsChecked = s.Topmost;
        Topmost = s.Topmost;
        SetThumbnailView(s.ThumbnailView);
        MonitorOnlyButton.IsChecked = s.MonitorOnly;
        CropModeCheck.IsChecked = s.CropMode;
        ConfirmCheck.IsChecked = s.ConfirmBeforeShare;
        _applyingSettings = false;

        // 共有中のウィンドウだけを並べるチップ（チェックが変わると自動で出入りする）
        var shared = new ListCollectionView(_items) { Filter = o => ((WindowItem)o).IsChecked, IsLiveFiltering = true };
        shared.LiveFilteringProperties.Add(nameof(WindowItem.IsChecked));
        SharedChips.ItemsSource = shared;

        SourceInitialized += (_, _) =>
        {
            // 自分専用の操作画面なので、モニターごと共有されても映らないようにする
            Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
            _thumbnails = new DwmThumbnails(this);
            ApplyMode(App.Settings.Compact, restoreBounds: true);
        };
        LayoutUpdated += (_, _) => _thumbnails?.UpdateAll();
    }

    /// <summary>出力ウィンドウができてから呼ぶ。プレビューに合成結果を出す。</summary>
    private void OnPipelineReady()
    {
        if (_output is null || _thumbnails is null) return;
        IntPtr outputHwnd = new WindowInteropHelper(_output).Handle;
        _thumbnails.Register(PreviewHost, outputHwnd, clientAreaOnly: true);
        _thumbnails.Register(CompactPreviewHost, outputHwnd, clientAreaOnly: true);
    }

    private void SaveOptions()
    {
        if (_applyingSettings || !IsInitialized) return;
        var s = App.Settings;
        s.ShowOverlay = OverlayCheck.IsChecked == true;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.IncludePopups = PopupCheck.IsChecked == true;
        s.Placement = PlacementNormal.IsChecked == true ? "Normal" : PlacementOffScreen.IsChecked == true ? "OffScreen" : "Back";
        s.Save();
    }

    // --- 常に手前 ---

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true || _overlay?.RaiseMainInSelectMode == true;
        PinButton.Content = Topmost ? "" : ""; // 今の状態を出す: ピン留め中 / ピン留めなし（斜線）
        if (_applyingSettings) return;
        App.Settings.Topmost = Topmost;
        App.Settings.Save();
    }

    // --- コンパクト ⇔ 通常 ---

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowBounds();
        ApplyMode(!_compact, restoreBounds: true);
        App.Settings.Compact = _compact;
        App.Settings.Save();
    }

    private void ApplyMode(bool compact, bool restoreBounds)
    {
        _compact = compact;
        // コンパクト表示は小さなパネルなので最大化させない（ボタンもダブルクリックも）
        if (compact && WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        SetMaximizeEnabled(!compact);
        NormalView.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactView.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        LogoImage.Height = compact ? 26 : 44;
        ModeButton.Content = compact ? "" : ""; // 全画面 / ウィンドウに戻す
        ModeButton.SetBinding(ToolTipProperty, new Binding(compact ? "[Header_ExpandTip]" : "[Header_CompactTip]") { Source = Loc.Instance });

        if (!restoreBounds) return;
        var saved = compact ? App.Settings.CompactBounds : App.Settings.NormalBounds;
        Width = saved?.Width ?? (compact ? DefaultCompactWidth : DefaultNormalWidth);
        Height = saved?.Height ?? (compact ? DefaultCompactHeight : DefaultNormalHeight);
        if (saved is not null && IsOnScreen(saved))
        {
            Left = saved.Left;
            Top = saved.Top;
        }
    }

    private void SetMaximizeEnabled(bool enabled)
    {
        const int GWL_STYLE = -16;
        const long WS_MAXIMIZEBOX = 0x00010000;
        const uint SWP_FRAMECHANGED = 0x0020;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long style = Native.GetWindowLongPtr(hwnd, GWL_STYLE);
        style = enabled ? style | WS_MAXIMIZEBOX : style & ~WS_MAXIMIZEBOX;
        Native.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        // タイトルバーのボタンを描き直させる
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>今のモードの位置とサイズを覚えておく。</summary>
    private void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal) return;
        var bounds = new WindowBounds(Left, Top, Width, Height);
        if (_compact) App.Settings.CompactBounds = bounds;
        else App.Settings.NormalBounds = bounds;
    }

    /// <summary>保存位置がモニター構成変更で画面外になっていないか。</summary>
    private static bool IsOnScreen(WindowBounds b) =>
        b.Left + 80 > SystemParameters.VirtualScreenLeft && b.Top + 40 > SystemParameters.VirtualScreenTop
        && b.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
        && b.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void CompactSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null || _toolbar is null) return;
        // 選択モードは自分用オーバーレイで行うので、隠していたら出す
        if (OverlayCheck.IsChecked != true) OverlayCheck.IsChecked = true;
        // ここから始めたときだけ、選択中もメイン画面を膜の上に出す（このボタンでまた止められるように）
        if (!_overlay.IsSelectMode) _selectFromCompact = true;
        _toolbar.SetSelectMode(!_overlay.IsSelectMode);
    }

    /// <summary>選択モードをコンパクトのボタンから始めたか（そのときだけメイン画面を膜の上に出す）。</summary>
    private bool _selectFromCompact;

    /// <summary>
    /// 選択モードの切り替えに合わせて、メイン画面を膜（オーバーレイ）の上に出す・戻す。
    /// コンパクトのボタンから始めたときだけ、もう一度押して止められるよう、そのあいだ常に手前にする。
    /// ツールバーから始めたときは前に出さない（通常表示の大きな画面が選びたいウィンドウを隠さないように）。
    /// </summary>
    private void OnSelectModeChanged(bool on)
    {
        if (!on) _selectFromCompact = false;
        if (_overlay is not null) _overlay.RaiseMainInSelectMode = on && _selectFromCompact;
        Topmost = (on && _selectFromCompact) || PinButton.IsChecked == true;
        // コンパクトのボタンはオンのあいだシアンで押し込んだ見た目にする（ピン止めと同じ色）
        CompactSelectButton.Background = (Brush)FindResource(on ? "AccentFillColorDefaultBrush" : "BrandBlueBrush");
        CompactSelectButton.Foreground = on ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : Brushes.White;
    }

    private void ChipRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowItem item) item.IsChecked = false;
    }

    // --- 最前面に出す ---

    private void BringToFront_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WindowItem item) return;
        IntPtr hwnd = item.Handle;
        if (!item.Info.Identity.IsAlive()) return;
        // 最小化していたら元に戻してから前に出す
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
    }

    // --- 共有するモニターのウィンドウだけ表示 ---

    private void MonitorOnlyButton_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || !IsInitialized) return;
        App.Settings.MonitorOnly = MonitorOnlyButton.IsChecked == true;
        App.Settings.Save();
        RefreshList();
    }

    // --- 一覧 ⇔ サムネイル ---

    private void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        SetThumbnailView(sender == ThumbViewButton);
        App.Settings.ThumbnailView = ThumbViewButton.IsChecked == true;
        App.Settings.Save();
    }

    private void SetThumbnailView(bool thumbnails)
    {
        ThumbViewButton.IsChecked = thumbnails;
        ListViewButton.IsChecked = !thumbnails;
        ThumbScroll.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;
        WindowList.Visibility = thumbnails ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ThumbHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host && host.Tag is IntPtr hwnd)
            _thumbnails?.Register(host, hwnd, viewport: ThumbScroll);
    }

    private void ThumbHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host) _thumbnails?.Unregister(host);
    }

    private void ThumbScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => _thumbnails?.UpdateAll();

    /// <summary>何も共有していないときは、真っ黒なプレビューの代わりに案内を出す。</summary>
    private void UpdateEmptyStates()
    {
        bool empty = VisibleSharedCount == 0;
        // サムネイルは WPF の上に描かれるので、プレースホルダーごと隠して案内を見せる
        PreviewHost.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        CompactPreviewHost.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        PreviewEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CompactPreviewEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CompactSharedEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // 空のときは案内と内容が重なるのでステータス行は出さない
        CompactStatusText.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _thumbnails?.UpdateAll();
    }
}
