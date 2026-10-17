using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShareWinSelector.Interop;

namespace ShareWinSelector.Overlay;

/// <summary>モニター上部に浮かぶ小さな操作バー（自分にだけ見える）。</summary>
internal sealed class ToolbarWindow : Window
{
    private readonly TextBlock _countText = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xE9, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0),
    };
    private readonly ToggleButton _selectButton;
    private readonly ToggleButton _borderButton;
    private IntPtr _hwnd;
    private Native.RECT _monitor;
    private bool _positioned;

    public event Action<bool>? SelectModeToggled;
    public event Action<bool>? BordersToggled;
    public event Action? SettingsRequested;

    public ToolbarWindow(Native.RECT monitor)
    {
        _monitor = monitor;
        Title = "ShareWinSelector Toolbar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        // アプリアイコン＋名前の部分をつかんで移動する
        var grip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
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
        grip.Children.Add(new TextBlock
        {
            Text = "ShareWinSelector",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Bind(grip, ToolTipProperty, "Toolbar_DragTip");
        grip.MouseLeftButtonDown += (_, _) => { DragMove(); _positioned = true; };

        _selectButton = MakeToggle("Toolbar_Select", "Toolbar_SelectTip");
        // Click ではなく状態変化で拾う（UI オートメーション操作やコードからの変更でも同じ動きにする）
        _selectButton.Checked += (_, _) => SelectModeToggled?.Invoke(true);
        _selectButton.Unchecked += (_, _) => SelectModeToggled?.Invoke(false);
        _borderButton = MakeToggle("Toolbar_Border", "Toolbar_BorderTip");
        _borderButton.IsChecked = true;
        _borderButton.Checked += (_, _) => BordersToggled?.Invoke(true);
        _borderButton.Unchecked += (_, _) => BordersToggled?.Invoke(false);
        var settings = MakeButton("Toolbar_Settings", "Toolbar_SettingsTip");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(grip);
        panel.Children.Add(_countText);
        panel.Children.Add(_selectButton);
        panel.Children.Add(_borderButton);
        panel.Children.Add(settings);

        Content = new Border
        {
            // メイン画面と同じ紺色＋シアンの縁
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0B, 0x10, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0xD8, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 6, 6),
            Child = panel,
        };
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
        ContentRendered += (_, _) => PlaceAtTop();
    }

    public IntPtr Handle => _hwnd;

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
    }

    public void SetSelectMode(bool on) => _selectButton.IsChecked = on;
}
