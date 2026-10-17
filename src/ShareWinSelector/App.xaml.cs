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
    private const string InstanceMutexName = @"Local\ShareWinSelector.SingleInstance";
    private const string ActivateEventName = @"Local\ShareWinSelector.Activate";
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
        Log.Info("起動");
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

    protected override void OnExit(ExitEventArgs e)
    {
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
