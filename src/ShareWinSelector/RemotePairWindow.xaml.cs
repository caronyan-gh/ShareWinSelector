using System.Windows;
using System.Windows.Interop;

namespace ShareWinSelector;

/// <summary>
/// リモート連携のペアリング: 初めての相手とつなぐときに、親と子の両方の画面に同じ番号を出して、許可するかを聞く。
/// 答え（許可＝true）は Result で受け取る。閉じたり、時間が過ぎたり、相手が切れたり（cancel）したら拒否。
/// </summary>
public partial class RemotePairWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly CancellationTokenRegistration _cancelRegistration;
    private bool _closed;

    /// <summary>許可されたら true、拒否・閉じたら false。</summary>
    public Task<bool> Result => _result.Task;

    internal RemotePairWindow(string message, string code, CancellationToken cancel)
    {
        InitializeComponent();
        MessageText.Text = message;
        // 6 桁は 3 桁ずつに分けて見比べやすく
        CodeText.Text = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        // 待つのは 60 秒まで。それを過ぎたら答えても届かないので閉じる
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(58) };
        timeout.Tick += (_, _) => { timeout.Stop(); Close(); };
        timeout.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            timeout.Stop();
            _cancelRegistration.Dispose();
            _result.TrySetResult(false);
        };
        // 相手が切れた・リモート連携を止めたときも閉じる
        _cancelRegistration = cancel.Register(() => Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }));
        // 自分専用の画面なので、モニターごと共有されても映らないようにする
        SourceInitialized += (_, _) => Interop.Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => Close();
}
