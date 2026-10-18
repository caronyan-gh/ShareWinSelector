using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShareWinSelector.Interop;

namespace ShareWinSelector.Overlay;

/// <summary>
/// モニター上部に浮かぶ小さな操作バー（自分にだけ見える）。
/// しばらく触らないとアイコン＋共有数だけにたたみ、共有数の上に少しカーソルを置くと広がる（どちらも「みょーん」と動く）。
/// 共有数の文字は画面上で動かず、タイトルは左へ、ボタンは右へ伸びる。
/// </summary>
internal sealed class ToolbarWindow : Window
{
    // アニメーションのはみ出し（広がるときに少し行き過ぎて戻る）用の左右の余白。透明なのでクリックは下へ抜ける
    private const double Slack = 16;

    private static readonly TimeSpan CollapseDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ExpandDelay = TimeSpan.FromMilliseconds(350);
    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(180));

    /// <summary>広がり具合（0 = たたんだ、1 = 広げた。広がるときは少し 1 を超えてから戻る）。</summary>
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ToolbarWindow),
        new PropertyMetadata(1.0, (d, _) => ((ToolbarWindow)d).ApplyProgress()));

    private double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private readonly TextBlock _countText = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xE9, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0),
        Background = Brushes.Transparent, // 文字の隙間でもカーソルを拾う
    };
    private readonly ToggleButton _selectButton;
    private readonly ToggleButton _borderButton;
    private readonly TextBlock _titleText;
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
    // たたむときに幅を縮める入れ物（はみ出した分は切る）
    private readonly Border _titleClip = new() { ClipToBounds = true };
    private readonly Border _buttonsClip = new() { ClipToBounds = true };
    private readonly Border _shell;
    private double _titleWidth, _buttonsWidth;

    private IntPtr _hwnd;
    private Native.RECT _monitor;
    private bool _positioned;

    private readonly System.Windows.Threading.DispatcherTimer _collapseTimer = new() { Interval = CollapseDelay };
    private readonly System.Windows.Threading.DispatcherTimer _expandTimer = new() { Interval = ExpandDelay };
    private bool _collapsed;
    private bool _dragging;
    // クロップ調整中など、たたまずに出しておく
    private bool _hold;

    public event Action<bool>? SelectModeToggled;
    public event Action<bool>? BordersToggled;
    public event Action? SettingsRequested;
    public event Action? BringToFrontRequested;
    /// <summary>ツールバーの上で右クリックされた（「クリックで選択」を抜ける用）。</summary>
    public event Action? RightClicked;

    public ToolbarWindow(Native.RECT monitor)
    {
        _monitor = monitor;
        Title = "ShareWinSelector Toolbar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        // 幅は広げたときの大きさで固定し、中で伸び縮みさせる（ウィンドウ自体は動かさない）
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        // アプリアイコン＋名前の部分をつかんで移動する
        var grip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.SizeAll,
            Background = Brushes.Transparent, // 余白でもつかめるように
        };
        var icon = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grip.Children.Add(icon);
        _titleText = new TextBlock
        {
            Text = "ShareWinSelector",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _titleClip.Child = _titleText;
        grip.Children.Add(_titleClip);
        Bind(grip, ToolTipProperty, "Toolbar_DragTip");
        grip.MouseLeftButtonDown += (_, _) =>
        {
            // 動かしている間は広げない・たたまない
            _dragging = true;
            _expandTimer.Stop();
            DragMove();
            _dragging = false;
            _positioned = true;
        };

        _selectButton = MakeToggle("Toolbar_Select", "Toolbar_SelectTip");
        // Click ではなく状態変化で拾う（UI オートメーション操作やコードからの変更でも同じ動きにする）
        _selectButton.Checked += (_, _) => SelectModeToggled?.Invoke(true);
        _selectButton.Unchecked += (_, _) => SelectModeToggled?.Invoke(false);
        _borderButton = MakeToggle("Toolbar_Border", "Toolbar_BorderTip");
        _borderButton.IsChecked = true;
        _borderButton.Checked += (_, _) => BordersToggled?.Invoke(true);
        _borderButton.Unchecked += (_, _) => BordersToggled?.Invoke(false);
        var front = MakeButton("Toolbar_Front", "BringAllToFrontTip");
        front.Click += (_, _) => BringToFrontRequested?.Invoke();
        var settings = MakeButton("Toolbar_Settings", "Toolbar_SettingsTip");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        _buttons.Children.Add(_selectButton);
        _buttons.Children.Add(front);
        _buttons.Children.Add(_borderButton);
        _buttons.Children.Add(settings);
        _buttonsClip.Child = _buttons;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(grip);
        panel.Children.Add(_countText);
        panel.Children.Add(_buttonsClip);

        _shell = new Border
        {
            // メイン画面と同じ紺色＋シアンの縁
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0B, 0x10, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0xD8, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
        // 余白は透明（背景なし）にして、クリックが下のウィンドウへ抜けるようにする
        Content = new Grid { Children = { _shell } };

        PreviewMouseRightButtonUp += (_, e) => { RightClicked?.Invoke(); e.Handled = true; };
        // 共有数の上に少し置いたら広げる。アイコンの上では広げない（動かそうとしてつかむ所なので）
        _countText.MouseEnter += (_, _) => { if (_collapsed && !_dragging) _expandTimer.Start(); };
        _countText.MouseLeave += (_, _) => _expandTimer.Stop();
        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); SetCollapsed(false); };
        _shell.MouseEnter += (_, _) => _collapseTimer.Stop();
        _shell.MouseLeave += (_, _) => RestartCollapseTimer();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_shell.IsMouseOver && !_dragging && !KeepOpen) SetCollapsed(true);
        };
        // 「クリックで選択」の間は、止めるボタンが要るので広げたまま
        _selectButton.Checked += (_, _) => { _collapseTimer.Stop(); SetCollapsed(false); };
        _selectButton.Unchecked += (_, _) => RestartCollapseTimer();

        SetCount(0);
        Loc.Instance.LanguageChanged += () =>
        {
            SetCount(_count);
            // 文言の長さが変わるので、ドラッグされていなければ中央に置き直す
            Dispatcher.BeginInvoke(PlaceAtTop, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private static ToggleButton MakeToggle(string textKey, string tipKey) => Localized(new ToggleButton(), textKey, tipKey);

    private static Button MakeButton(string textKey, string tipKey) => Localized(new Button(), textKey, tipKey);

    private static T Localized<T>(T button, string textKey, string tipKey) where T : ButtonBase
    {
        button.Padding = new Thickness(8, 2, 8, 2);
        button.Margin = new Thickness(4, 0, 0, 0);
        button.Focusable = false;
        Bind(button, ContentControl.ContentProperty, textKey);
        Bind(button, ToolTipProperty, tipKey);
        return button;
    }

    /// <summary>言語切り替えに追従するよう、文字列プロパティを Loc にバインドする。</summary>
    private static void Bind(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetBinding(property, new System.Windows.Data.Binding($"[{key}]") { Source = Loc.Instance });

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, 0);
        Native.ExcludeFromCapture(_hwnd);
        Relayout();
        ContentRendered += (_, _) =>
        {
            PlaceAtTop();
            RestartCollapseTimer();
        };
    }

    public IntPtr Handle => _hwnd;

    private bool KeepOpen => _hold || _selectButton.IsChecked == true;

    /// <summary>クロップ調整中などは、たたまずに出しておく。</summary>
    public void SetHold(bool hold)
    {
        if (_hold == hold) return;
        _hold = hold;
        if (hold)
        {
            _collapseTimer.Stop();
            SetCollapsed(false);
        }
        else
        {
            RestartCollapseTimer();
        }
    }

    private void RestartCollapseTimer()
    {
        _collapseTimer.Stop();
        if (!KeepOpen) _collapseTimer.Start();
    }

    /// <summary>
    /// 文字の幅を測り直して、広げたときの大きさでウィンドウの幅を決める（文言や共有数が変わったとき）。
    /// </summary>
    private void Relayout()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _titleText.Measure(inf);
        _titleWidth = _titleText.DesiredSize.Width;
        _buttons.Measure(inf);
        _buttonsWidth = _buttons.DesiredSize.Width;
        // 広げたときの全体の幅（入れ物を自然な幅にして測る）
        _titleClip.Width = double.NaN;
        _buttonsClip.Width = double.NaN;
        _shell.Margin = new Thickness(0);
        _shell.Measure(inf);
        Width = _shell.DesiredSize.Width + Slack * 2;
        ApplyProgress();
    }

    /// <summary>広がり具合に合わせて、タイトルとボタンの幅、全体の位置を決める（共有数の文字の位置は変えない）。</summary>
    private void ApplyProgress()
    {
        double p = Progress;
        _titleClip.Width = Math.Max(0, p * _titleWidth);
        _buttonsClip.Width = Math.Max(0, p * _buttonsWidth);
        _titleClip.Opacity = _buttonsClip.Opacity = Math.Clamp(p, 0, 1);
        // タイトルが縮んだ分だけ全体を右へずらす＝共有数は同じ場所のまま
        _shell.Margin = new Thickness(Slack + (1 - p) * _titleWidth, 0, 0, 0);
    }

    /// <summary>たたむ（アイコン＋共有数だけ）／広げる。どちらも短いアニメーションで。</summary>
    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        var animation = new DoubleAnimation(collapsed ? 0 : 1, collapsed ? CollapseDuration : ExpandDuration)
        {
            // 広がるときは少し行き過ぎて戻る（みょーん）、たたむときはすっと
            EasingFunction = collapsed
                ? new CubicEase { EasingMode = EasingMode.EaseIn }
                : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
        };
        BeginAnimation(ProgressProperty, animation);
    }

    private bool _allowClose;

    /// <summary>アプリ終了時だけ閉じる。</summary>
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

    public void SetMonitor(Native.RECT monitor)
    {
        _monitor = monitor;
        _positioned = false;
        PlaceAtTop();
    }

    /// <summary>モニター上端の中央に置く（ユーザーがドラッグした後は動かさない）。</summary>
    private void PlaceAtTop()
    {
        if (_hwnd == IntPtr.Zero || _positioned) return;
        Native.GetWindowRect(_hwnd, out var rc);
        int x = _monitor.Left + (_monitor.Width - rc.Width) / 2;
        int y = _monitor.Top + 8;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private int _count;

    public void SetCount(int count)
    {
        _count = count;
        _countText.Text = count == 0 ? Loc.T("Toolbar_NoneShared") : Loc.F("Toolbar_Sharing", count);
        if (_hwnd != IntPtr.Zero) Relayout();
    }

    public void SetSelectMode(bool on) => _selectButton.IsChecked = on;
}
