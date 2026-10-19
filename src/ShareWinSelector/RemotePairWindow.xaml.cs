using System.Windows;
using System.Windows.Interop;

namespace ShareWinSelector;

/// <summary>
/// リモート連携のペアリング: 初めての子がつなごうとしたときに、親の画面に番号を出して許可するかを聞く。
/// 答え（許可＝true）は Result で受け取る。閉じたり時間が過ぎたりしたら拒否。
/// </summary>
public partial class RemotePairWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    /// <summary>許可されたら true、拒否・閉じたら false。</summary>
    public Task<bool> Result => _result.Task;

    internal RemotePairWindow(string childName, string code)
    {
        InitializeComponent();
        MessageText.Text = Loc.F("Pair_Message", childName);
        CodeText.Text = code;
        Closed += (_, _) => _result.TrySetResult(false);
        // 親の側が待つのは 60 秒まで。それを過ぎたら答えても届かないので閉じる
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(58) };
        timeout.Tick += (_, _) => { timeout.Stop(); Close(); };
        timeout.Start();
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
