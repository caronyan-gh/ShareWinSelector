using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ShareWinSelector.Interop;

namespace ShareWinSelector.Overlay;

/// <summary>
/// 対象モニター全体を覆う、自分にだけ見える透明オーバーレイ。
/// 通常はクリック透過で強調枠だけ描き、選択モード中はクリックでウィンドウを選べる。
/// 画面キャプチャからは除外される。
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly Func<IReadOnlyCollection<IntPtr>> _getSelected;
    // 選択ウィンドウごとの「上から隠す量」（ウィンドウの DPI 基準の DIP、0 は無し）
    private readonly Func<IntPtr, double> _getCropDip;
    private readonly OverlaySurface _surface = new();
    // クロップ調整中に出す「確定 / キャンセル」
    private readonly System.Windows.Controls.Canvas _editLayer = new();
    private readonly System.Windows.Controls.Border _editPanel;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private Native.RECT _monitor;
    private bool _selectMode;
    private bool _showBorders = true;
    private IntPtr _hoverTarget;
    // 前回描いた内容。null で次回必ず描き直す
    private string? _lastSignature;

    // 見張る間隔: 操作中・何かが動いている間は細かく（枠がウィンドウに付いてくるように）、落ち着いたら間引く
    private static readonly TimeSpan BusyInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(600);
    private long _lastChangeTick;

    /// <summary>選択モード中にウィンドウがクリックされた（ルートのメインウィンドウ）。</summary>
    public event Action<IntPtr>? WindowClicked;

    /// <summary>選択モード中に Ctrl を押しながらウィンドウがクリックされた（クロップを調整して共有）。</summary>
    public event Action<IntPtr>? WindowCtrlClicked;

    /// <summary>Esc・右クリックで選択モードが終わった。</summary>
    public event Action? SelectModeExited;

    /// <summary>クロップ調整が確定した（HWND, 上から隠す量 DIP）。</summary>
    public event Action<IntPtr, double>? CropConfirmed;

    /// <summary>クロップ調整がキャンセルされた（または対象が閉じた）。</summary>
    public event Action<IntPtr>? CropCancelled;

    /// <summary>枠の重なり判定・クリック判定から除外する自前ウィンドウ（ツールバー等）。</summary>
    public IntPtr ToolbarHandle { get; set; }

    /// <summary>メイン画面。RaiseMainInSelectMode のとき、選択モード中は膜の上に出す。</summary>
    public IntPtr MainHandle { get; set; }

    /// <summary>選択モード中にメイン画面を膜の上に出すか（コンパクトのボタンから始めたときだけ）。</summary>
    public bool RaiseMainInSelectMode { get; set; }

    // クロップ調整の状態
    private IntPtr _cropTarget;
    private double _cropDip;
    // 上辺をドラッグできるか（false は「共有前の確認」だけ）
    private bool _cropAllowDrag;
    private bool _dragging;
    private System.Windows.Controls.Button? _confirmButton;
    // 直近の描画での、調整対象の位置（オーバーレイ DIP）と DPI 比
    private Rect _cropTargetBounds;
    private double _cropTargetScale = 1; // ウィンドウの物理ピクセル / DIP

    public OverlayWindow(Func<IReadOnlyCollection<IntPtr>> getSelected, Func<IntPtr, double> getCropDip, Native.RECT monitor)
    {
        _getSelected = getSelected;
        _getCropDip = getCropDip;
        _monitor = monitor;
        Title = "ShareWinSelector Overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        _editPanel = CreateEditPanel();
        _editLayer.Children.Add(_editPanel);
        _editPanel.Visibility = Visibility.Collapsed;
        var root = new System.Windows.Controls.Grid();
        root.Children.Add(_surface);
        root.Children.Add(_editLayer);
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = BusyInterval };
        _timer.Tick += (_, _) => Refresh();
        // 隠している間（オーバーレイをオフにしたとき）は見張りも止める
        IsVisibleChanged += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            if (IsVisible) { _lastSignature = null; _timer.Start(); }
            else _timer.Stop();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftClick;
        MouseRightButtonUp += (_, _) => ExitSelectMode();
        // 切り取りツールなどから戻って手前に出し直されると、ツールバーより上に来て押せなくなる。そのたびにツールバーを上げ直す
        Activated += (_, _) =>
        {
            if (_selectMode || IsCropEditing) Dispatcher.BeginInvoke(RaiseToolbar, DispatcherPriority.Input);
        };
        KeyDown += (_, e) =>
        {
            if (IsCropEditing)
            {
                if (e.Key == Key.Escape) EndCropEdit(confirm: false);
                else if (e.Key == Key.Enter) EndCropEdit(confirm: true);
                return;
            }
            if (e.Key == Key.Escape) ExitSelectMode();
        };
        Loc.Instance.LanguageChanged += () => { _lastSignature = null; Refresh(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, 0);
        Native.ExcludeFromCapture(_hwnd);
        ApplyBounds();
        _timer.Start();
    }

    private bool _allowClose;

    /// <summary>アプリ終了時だけ閉じる（選択モード中の Alt+F4 などでは閉じない）。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    public IntPtr Handle => _hwnd;

    public void SetMonitor(Native.RECT monitor)
    {
        _monitor = monitor;
        ApplyBounds();
    }

    public bool ShowBorders
    {
        get => _showBorders;
        set { _showBorders = value; _lastSignature = null; Refresh(); }
    }

    public bool IsSelectMode => _selectMode;

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, Native.SWP_NOACTIVATE);
    }

    public void EnterSelectMode()
    {
        if (_selectMode || _hwnd == IntPtr.Zero) return;
        _selectMode = true;
        Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        Activate();
        Focus();
        Cursor = Cursors.Hand;
        RaiseToolbar();
        _lastSignature = null;
        Refresh();
    }

    public void ExitSelectMode()
    {
        if (!_selectMode) return;
        _selectMode = false;
        _hoverTarget = IntPtr.Zero;
        Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE, 0);
        Cursor = null;
        _lastSignature = null;
        Refresh();
        SelectModeExited?.Invoke();
    }

    private void RaiseToolbar()
    {
        // コンパクトのボタンから始めた選択中は、メイン画面も膜の上に出す（また押して止められるように）。その上にツールバー
        if (_selectMode && RaiseMainInSelectMode && MainHandle != IntPtr.Zero && !Native.IsIconic(MainHandle))
            Native.SetWindowPos(MainHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        if (ToolbarHandle != IntPtr.Zero)
            Native.SetWindowPos(ToolbarHandle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // --- クロップ調整 ---

    public bool IsCropEditing => _cropTarget != IntPtr.Zero;

    /// <summary>
    /// 共有前の確認を始める。allowDrag なら上辺を下へドラッグして隠す範囲も決めてもらう。
    /// 確定するまで、このウィンドウは相手に見せない（呼び出し側で隠しておく）。
    /// </summary>
    public void BeginCropEdit(IntPtr hwnd, double initialDip, bool allowDrag = true)
    {
        if (_hwnd == IntPtr.Zero) return;
        ExitSelectMode();
        // 置き換え: 前の調整は呼び出し側で後始末済みなので、キャンセル通知は出さない（入れ子を防ぐ）
        if (IsCropEditing) EndCropEdit(confirm: false, notify: false);
        _cropTarget = hwnd;
        // 対象の位置はすぐ取っておく（次の描画まで前の対象の位置を使わないように）
        _cropTargetBounds = Rect.Empty;
        _cropDip = Math.Max(0, initialDip);
        _cropAllowDrag = allowDrag;
        _confirmButton?.SetBinding(System.Windows.Controls.ContentControl.ContentProperty,
            new System.Windows.Data.Binding(allowDrag ? "[Crop_Confirm]" : "[Review_Share]") { Source = Loc.Instance });
        // 対象を手前に出してから、オーバーレイでマウスを受ける
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        Activate();
        Focus();
        RaiseToolbar();
        _editPanel.Visibility = Visibility.Visible;
        _lastSignature = null;
        Refresh();
    }

    /// <summary>外から調整を打ち切る（キャンセル扱い）。</summary>
    public void CancelCropEdit() => EndCropEdit(confirm: false);

    /// <summary>直前に終わった調整が、上辺をドラッグできるもの（クロップ量を決めるもの）だったか。</summary>
    public bool LastEditAllowedDrag { get; private set; }

    private void EndCropEdit(bool confirm, bool notify = true)
    {
        if (!IsCropEditing) return;
        IntPtr target = _cropTarget;
        double dip = _cropDip;
        LastEditAllowedDrag = _cropAllowDrag;
        _cropTarget = IntPtr.Zero;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = null;
        _editPanel.Visibility = Visibility.Collapsed;
        Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE, 0);
        _lastSignature = null;
        Refresh();
        // 確定・キャンセルのあとは、対象のウィンドウに操作を戻す
        Native.SetForegroundWindow(target);
        if (!notify) return;
        if (confirm) CropConfirmed?.Invoke(target, dip);
        else CropCancelled?.Invoke(target);
    }

    private System.Windows.Controls.Border CreateEditPanel()
    {
        System.Windows.Controls.Button MakeButton(string key, bool accent)
        {
            var b = new System.Windows.Controls.Button { Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0) };
            b.SetBinding(ContentProperty, new System.Windows.Data.Binding($"[{key}]") { Source = Loc.Instance });
            if (accent)
            {
                b.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0xFF));
                b.Foreground = Brushes.White;
            }
            return b;
        }
        var confirm = MakeButton("Crop_Confirm", accent: true);
        _confirmButton = confirm;
        confirm.Click += (_, _) => EndCropEdit(confirm: true);
        var cancel = MakeButton("Crop_Cancel", accent: false);
        cancel.Click += (_, _) => EndCropEdit(confirm: false);
        var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(confirm);
        panel.Children.Add(cancel);
        return new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0B, 0x10, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0xD8, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 6, 10, 6),
            Child = panel,
        };
    }

    /// <summary>調整中の上辺の y（オーバーレイ DIP）。</summary>
    private double CropLineY => _cropTargetBounds.Top + _cropDip * _cropTargetScale / VisualTreeHelper.GetDpi(this).DpiScaleY;

    private bool IsNearCropLine(Point p) =>
        _cropAllowDrag && p.X >= _cropTargetBounds.Left && p.X <= _cropTargetBounds.Right && Math.Abs(p.Y - CropLineY) <= 10;

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsCropEditing || !IsNearCropLine(e.GetPosition(this))) return;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (IsCropEditing)
        {
            var p = e.GetPosition(this);
            if (_dragging)
            {
                // オーバーレイ DIP → 物理ピクセル → 対象ウィンドウの DIP。ウィンドウの高さ（少し残す）まで
                double overlayScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
                double maxPx = Math.Max(0, _cropTargetBounds.Height * overlayScale - 40);
                double px = Math.Clamp((p.Y - _cropTargetBounds.Top) * overlayScale, 0, maxPx);
                _cropDip = px / _cropTargetScale;
            }
            Cursor = _dragging || IsNearCropLine(p) ? Cursors.SizeNS : null;
        }
        Refresh();
    }

    private void OnLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            return;
        }
        if (!_selectMode) return;
        var target = HitTest(GetVisibleWindows());
        if (target != IntPtr.Zero)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) WindowCtrlClicked?.Invoke(target);
            else WindowClicked?.Invoke(target);
        }
        _lastSignature = null;
        Refresh();
    }

    private List<VisibleWindow> GetVisibleWindows()
    {
        // モニター一覧はタスクバーを見つけたときだけ取る（見張りのたびに取ると重い）
        List<MonitorInfo>? monitors = null;
        return WindowEnumerator.GetVisibleWindowsTopDown()
            .Where(w => w.Handle != _hwnd && w.Handle != ToolbarHandle && !w.IsClickThrough
                        && !(IsTaskbar(w.Handle) && IsOutsideAllMonitors(w, monitors ??= MonitorEnumerator.GetMonitors())))
            .ToList();
    }

    private static bool IsTaskbar(IntPtr hwnd)
    {
        string cls = Native.GetClassName(hwnd);
        return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// 1 つのモニターに収まっていないか（自動で隠れたタスクバーの判定用）。隠れたタスクバーはモニター外へずらされるだけなので、
    /// 隣のモニターに食い込んだ状態になる。そこには実際には何も描かれない。
    /// </summary>
    private static bool IsOutsideAllMonitors(VisibleWindow w, List<MonitorInfo> monitors)
    {
        var b = w.Bounds;
        return !monitors.Any(m =>
            b.Left >= m.Bounds.Left && b.Top >= m.Bounds.Top && b.Right <= m.Bounds.Right && b.Bottom <= m.Bounds.Bottom);
    }

    /// <summary>カーソル直下に見えているウィンドウ（所有ダイアログなら本体）を返す。共有できないものなら Zero。</summary>
    private IntPtr HitTest(List<VisibleWindow> windows)
    {
        Native.GetCursorPos(out var pt);
        // ツールバーの上ではウィンドウを選ばない
        if (ToolbarHandle != IntPtr.Zero && Native.GetWindowRect(ToolbarHandle, out var tb)
            && pt.X >= tb.Left && pt.X < tb.Right && pt.Y >= tb.Top && pt.Y < tb.Bottom)
            return IntPtr.Zero;
        foreach (var w in windows)
        {
            if (!w.Contains(pt.X, pt.Y)) continue;
            IntPtr root = WindowEnumerator.GetRootOwner(w.Handle);
            return WindowEnumerator.IsCapturable(root) ? root : IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 見張る間隔を決める。選択モード・調整中・ウィンドウが動いている間は 30 回/秒（枠が遅れて付いてこないように）、
    /// 落ち着いたら 7 回/秒に落とす（動き出したら次の見張りで気づいて細かくなる）。
    /// </summary>
    private void AdjustInterval()
    {
        bool busy = _selectMode || IsCropEditing || _dragging
                    || Environment.TickCount64 - _lastChangeTick < (long)SettleTime.TotalMilliseconds;
        var interval = busy ? BusyInterval : IdleInterval;
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    private static void AppendSignature(System.Text.StringBuilder sig, OverlayItem item)
    {
        sig.Append(item.Bounds).Append('|');
        foreach (var o in item.Occluders) sig.Append(o).Append(',');
        sig.Append(';');
    }

    /// <summary>強調枠（と選択モードのハイライト）を最新のウィンドウ配置で描き直す。</summary>
    private void Refresh()
    {
        if (_hwnd == IntPtr.Zero) return;
        var selected = _getSelected();
        // 枠も選択モードも調整も無いとき（何も共有していないとき）は、ウィンドウを調べずに済ませる
        var windows = selected.Count > 0 || IsCropEditing || _selectMode ? GetVisibleWindows() : [];
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        Rect ToDip(Native.RECT r) => new(
            (r.Left - _monitor.Left) / scale, (r.Top - _monitor.Top) / scale, r.Width / scale, r.Height / scale);

        // windows[index] より手前にあって、bounds（＋枠・タグの余白）にかかるウィンドウ矩形
        List<Rect> OccludersAbove(int index, Rect bounds)
        {
            var inflated = bounds;
            inflated.Inflate(30, 30);
            var list = new List<Rect>();
            for (int j = 0; j < index; j++)
            {
                var o = ToDip(windows[j].Bounds);
                if (o.IntersectsWith(inflated)) list.Add(o);
            }
            return list;
        }

        // 調整中の対象が閉じられたら調整を打ち切る
        if (IsCropEditing && !Native.IsWindow(_cropTarget)) EndCropEdit(confirm: false);
        // 対象が最小化などで見えないあいだは、ドラッグもボタンも出さない
        bool targetVisible = false;

        var items = new List<OverlayItem>();
        // モード自体も含める（空シグネチャ同士で「変化なし」と誤判定しないように）
        var sig = new System.Text.StringBuilder().Append(_selectMode).Append(_showBorders).Append('#');
        if (selected.Count > 0 || IsCropEditing)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                bool editing = w.Handle == _cropTarget;
                if (!editing && !selected.Contains(w.Handle)) continue;
                var bounds = ToDip(w.Bounds);
                // 隠す量: ウィンドウの DIP → 物理ピクセル → オーバーレイ DIP
                uint winDpi = Native.GetDpiForWindow(w.Handle);
                double winScale = (winDpi == 0 ? 96 : winDpi) / 96.0;
                double cropDip = editing ? _cropDip : _getCropDip(w.Handle);
                double cropTop = Math.Min(bounds.Height, cropDip * winScale / scale);
                if (editing)
                {
                    _cropTargetBounds = bounds;
                    _cropTargetScale = winScale;
                    targetVisible = true;
                }
                var item = new OverlayItem(bounds, OccludersAbove(i, bounds), cropTop, editing, editing && _cropAllowDrag);
                items.Add(item);
                AppendSignature(sig, item);
                sig.Append(cropTop.ToString("F1")).Append(editing);
            }
        }

        if (IsCropEditing && !targetVisible) _cropTargetBounds = Rect.Empty;
        _editPanel.Visibility = IsCropEditing && targetVisible ? Visibility.Visible : Visibility.Collapsed;

        // 確定 / キャンセルは上辺の下の案内のさらに下・右寄せに置く（案内と重ならないように）
        if (IsCropEditing && targetVisible)
        {
            _editPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _editPanel.DesiredSize;
            double x = Math.Max(_cropTargetBounds.Left + 8, _cropTargetBounds.Right - size.Width - 12);
            double y = Math.Min(CropLineY + 40, _cropTargetBounds.Bottom - size.Height - 8);
            System.Windows.Controls.Canvas.SetLeft(_editPanel, x);
            System.Windows.Controls.Canvas.SetTop(_editPanel, Math.Max(_cropTargetBounds.Top, y));
        }

        OverlayItem? hover = null;
        bool hoverSelected = false;
        Point cursor = default;
        if (_selectMode)
        {
            _hoverTarget = HitTest(windows);
            int index = windows.FindIndex(w => w.Handle == _hoverTarget);
            if (index >= 0)
            {
                // 手前のウィンドウに隠れた部分には塗らない（枠と同じ扱い）
                var bounds = ToDip(windows[index].Bounds);
                hover = new OverlayItem(bounds, OccludersAbove(index, bounds));
                hoverSelected = selected.Contains(_hoverTarget);
                Native.GetCursorPos(out var pt);
                cursor = ToDip(new Native.RECT { Left = pt.X, Top = pt.Y, Right = pt.X, Bottom = pt.Y }).TopLeft;
            }
            sig.Append('H').Append(hoverSelected).Append(cursor);
            if (hover is { } h) AppendSignature(sig, h);
        }

        // 変化が無ければ再描画しない
        string signature = sig.ToString();
        bool changed = signature != _lastSignature;
        if (changed) _lastChangeTick = Environment.TickCount64;
        AdjustInterval();
        if (!changed) return;
        _lastSignature = signature;
        _surface.Update(items, _showBorders, _selectMode, hover, hoverSelected, cursor);
    }
}
