using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ShareWinSelector.Remote;

namespace ShareWinSelector;

/// <summary>
/// リモート連携の子: ペアリング済みの親につなぐ前に「つなぎますか？」と聞く窓。
/// 答え（つなぐ・ペアリングを解除・キャンセル）と「この親には確認せずにつなぐ」は Result で受け取る。
/// 閉じたり、時間が過ぎたり、親が切れたり（cancel）したらキャンセル。
/// </summary>
internal sealed class RemoteConnectWindow : Window
{
    private readonly TaskCompletionSource<(RemoteConnectChoice Choice, bool SkipNextTime)> _result = new();
    private readonly CheckBox _skip;
    private bool _closed;

    public Task<(RemoteConnectChoice Choice, bool SkipNextTime)> Result => _result.Task;

    /// <param name="anchor">この窓の上に重ねて出す窓（特殊設定かメイン画面）。無ければ画面の中央。</param>
    /// <param name="sendsList">親にウィンドウの一覧を送る設定か（送らないなら「親から選んで共有できる」の一文を出さない）。</param>
    public RemoteConnectWindow(string parentName, bool sendsList, CancellationToken cancel, Window? anchor)
    {
        Title = Loc.T("Connect_Title");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = anchor is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = true;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrandWindowBackgroundBrush");

        var message = new TextBlock { Text = Loc.F(sendsList ? "Connect_Message" : "Connect_MessageNoList", parentName), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _skip = new CheckBox { Content = Loc.T("Connect_SkipNext"), Margin = new Thickness(0, 0, 0, 14) };
        var connect = new Button
        {
            Content = Loc.T("Connect_Connect"), Padding = new Thickness(18, 4, 18, 4), IsDefault = true,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BrandBlueBrush"), Foreground = System.Windows.Media.Brushes.White,
        };
        var unpair = new Button { Content = Loc.T("Connect_Unpair"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        var cancelButton = new Button { Content = Loc.T("Connect_Cancel"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        connect.Click += (_, _) => Answer(RemoteConnectChoice.Connect);
        unpair.Click += (_, _) => Answer(RemoteConnectChoice.Unpair);
        cancelButton.Click += (_, _) => Answer(RemoteConnectChoice.Cancel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(connect);
        buttons.Children.Add(unpair);
        buttons.Children.Add(cancelButton);
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        panel.Children.Add(message);
        panel.Children.Add(_skip);
        panel.Children.Add(buttons);
        Content = panel;

        if (anchor is not null) Loaded += (_, _) => RemotePairWindow.PlaceOver(this, anchor);
        // 親の側が待つのは 60 秒ほど。それを過ぎたら答えても届かないので閉じる（キャンセル扱い）
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(58) };
        timeout.Tick += (_, _) => { timeout.Stop(); Close(); };
        timeout.Start();
        var registration = cancel.Register(() => Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }));
        Closed += (_, _) =>
        {
            _closed = true;
            timeout.Stop();
            registration.Dispose();
            _result.TrySetResult((RemoteConnectChoice.Cancel, false));
        };
        // 自分専用の画面なので、モニターごと共有されても映らないようにする
        SourceInitialized += (_, _) => Interop.Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    private void Answer(RemoteConnectChoice choice)
    {
        _result.TrySetResult((choice, choice == RemoteConnectChoice.Connect && _skip.IsChecked == true));
        Close();
    }
}
