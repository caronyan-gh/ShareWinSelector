using System.Windows;
using System.Windows.Threading;

namespace ShareWinSelector;

public partial class App : Application
{
    /// <summary>保存された設定（起動時に読み込む）。</summary>
    internal static AppSettings Settings { get; private set; } = new();

    private static SplashScreen? _splash;

    /// <summary>メイン画面の準備ができたら呼ぶ。スプラッシュをフェードアウトして消す。</summary>
    internal static void CloseSplash()
    {
        _splash?.Close(TimeSpan.FromMilliseconds(300));
        _splash = null;
    }

    // 多重起動の防止（Debug 版と Release 版も合わせて 1 つ）。2 つ目は 1 つ目を手前に出してもらって終わる
    // 開発用の別名（AppSettings.Profile）を付けたときは、別名ごとに 1 つ
    private static readonly string InstanceMutexName = @"Local\ShareWinSelector.SingleInstance" + ProfileSuffix;
    private static readonly string ActivateEventName = @"Local\ShareWinSelector.Activate" + ProfileSuffix;
    private static string ProfileSuffix => AppSettings.Profile is { } p ? "." + p : "";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>ほかに起動していなければ true。起動済みなら、そちらに手前へ出るよう頼んで false。</summary>
    private static bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (createdNew) return true;
        // 自分（ユーザーが今起動した側）は手前に出す権利を持っているので、1 つ目に譲ってから知らせる
        AllowSetForegroundWindow(-1); // ASFW_ANY
        _activateEvent.Set();
        return false;
    }

    /// <summary>2 つ目が起動されたら、メイン画面を手前に出す。</summary>
    private void ListenForActivation()
    {
        var handle = _activateEvent!;
        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not { } main) return;
                    Log.Info("2 つ目の起動 → 手前に出す");
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Show();
                    main.Activate();
                });
            }
        }) { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }
        ListenForActivation();
        // 最前面で出したまま、メイン画面の準備ができるまで残す
        _splash = new SplashScreen("Assets/splash.png");
        _splash.Show(autoClose: false, topMost: true);
        Log.Info($"起動 v{ShareWinSelector.MainWindow.FullVersion}（Windows {WindowsBuild()}）");
        // 想定外の例外で会議中に落ちないよう、UI スレッドの例外はログに残して続行する
        // （設定の読み込みより先に登録して、起動途中の失敗もログに残す）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未処理例外（終了）", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未監視のタスク例外", args.Exception);
            args.SetObserved();
        };
        Settings = AppSettings.Load();
        // MainWindow（StartupUri）の生成前に言語を決める
        Loc.Instance.SetLanguage(Settings.Language);
        // ツールチップはすぐ出す（押す邪魔にはならないので、待たせない）
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(150));
        System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(0));
        // 設定で「ツールチップを表示」を外していたら、どの画面でも出さない
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((_, args) => { if (!Settings.ShowTooltips) args.Handled = true; }));
        // 閉じたコンボボックスの上でホイールを回しても選択が変わらないようにする（スクロールに回す）
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox), UIElement.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(OnComboBoxPreviewMouseWheel));
        base.OnStartup(e);
    }

    private static void OnComboBoxPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { IsDropDownOpen: false } combo) return;
        e.Handled = true;
        // 親へ流し直して、外側の ScrollViewer がスクロールできるようにする
        if (System.Windows.Media.VisualTreeHelper.GetParent(combo) is UIElement parent)
        {
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = combo,
            });
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI スレッドの未処理例外", e.Exception);
        e.Handled = true;
        CloseSplash(); // 起動途中で失敗してもスプラッシュが居座らないように
    }

    /// <summary>終了するときに設定とログのフォルダを消す（「設定を削除して終了」）。</summary>
    internal static bool DeleteDataOnExit { get; set; }

    /// <summary>Windows のビルド番号（例: 26100.2894、24H2）。ログ用。取れなければ OS のバージョンだけ。</summary>
    private static string WindowsBuild()
    {
        var v = Environment.OSVersion.Version;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var ubr = key?.GetValue("UBR");
            var display = key?.GetValue("DisplayVersion") as string;
            return $"{v.Build}.{ubr ?? 0}{(string.IsNullOrEmpty(display) ? "" : "、" + display)}";
        }
        catch
        {
            return v.ToString();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 戻ってこなかった取り込みの停止があれば、手がかりとして残す
        if (ShareWinSelector.Rendering.Compositor.DescribePendingStops() is { } pending) Log.Error(pending);
        Log.Info("終了");
        if (DeleteDataOnExit)
        {
            // 保存もログも止めてあるので、ここで消せば何も残らない
            try { System.IO.Directory.Delete(AppSettings.Folder, recursive: true); }
            catch { /* 消せなくても終了はする */ }
        }
        base.OnExit(e);
    }
}
