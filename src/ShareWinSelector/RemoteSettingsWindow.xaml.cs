using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ShareWinSelector.Remote;

namespace ShareWinSelector;

/// <summary>
/// 特殊設定（設定の見出しの右端のボタンから開く）。今はリモート連携だけ。
/// 変えたらその場で保存して効かせる（メイン画面の設定と同じく OK / キャンセルは無い）。
/// </summary>
public partial class RemoteSettingsWindow : Window
{
    private static readonly TimeSpan FindWait = TimeSpan.FromSeconds(1.5);
    private readonly MainWindow _main;
    private bool _loading;

    internal RemoteSettingsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Owner = main;
        Load();
        main.RemoteStatusChanged += ShowStatus;
        Closed += (_, _) => main.RemoteStatusChanged -= ShowStatus;
        ShowStatus();
        // 自分専用の画面なので、モニターごと共有されても映らないようにする
        SourceInitialized += (_, _) => Interop.Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    private void Load()
    {
        _loading = true;
        var s = App.Settings;
        EnabledCheck.IsChecked = s.RemoteEnabled;
        ParentRadio.IsChecked = s.RemoteRole != "Child";
        ChildRadio.IsChecked = s.RemoteRole == "Child";
        PortBox.Text = s.RemotePort.ToString();
        AddressBox.Text = s.RemoteParentAddress ?? "";
        SendListCheck.IsChecked = s.RemoteSendWindowList;
        _loading = false;
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        Body.IsEnabled = EnabledCheck.IsChecked == true;
        ChildPanel.Visibility = ChildRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus()
    {
        StatusText.Text = _main.RemoteStatus;
        PairingText.Text = _main.RemotePairingSummary;
    }

    private void Unpair_Click(object sender, RoutedEventArgs e) => _main.UnpairRemote();

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var s = App.Settings;
        s.RemoteEnabled = EnabledCheck.IsChecked == true;
        s.RemoteRole = ChildRadio.IsChecked == true ? "Child" : "Parent";
        s.Save();
        UpdateEnabledState();
        _main.ApplyRemoteSettings();
    }

    // 子: 親にウィンドウの一覧を送るか（つないでいる最中でもすぐ効く）
    private void SendList_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        App.Settings.RemoteSendWindowList = SendListCheck.IsChecked == true;
        App.Settings.Save();
        _main.ApplyRemoteSendWindowList();
    }

    // --- ポート ---

    private void PortBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitPort();
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e) => CommitPort();

    private void CommitPort()
    {
        // 予約されたポート（1023 以下）は使わない。おかしな値なら元に戻す
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            PortBox.Text = App.Settings.RemotePort.ToString();
            return;
        }
        if (port == App.Settings.RemotePort) return;
        App.Settings.RemotePort = port;
        App.Settings.Save();
        _main.ApplyRemoteSettings();
    }

    // --- 親を見つける ---

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        CommitPort();
        FindButton.IsEnabled = false;
        FindStatus.Text = Loc.T("Remote_Finding");
        FoundList.ItemsSource = null;
        try
        {
            var found = await RemoteChild.FindParentsAsync(App.Settings.RemotePort, FindWait);
            FoundList.ItemsSource = found;
            // 1 台だけでも勝手にはつながない（LAN の中の誰かが親を名乗っているだけかもしれないので、選んでもらう）
            FindStatus.Text = found.Count == 0 ? Loc.T("Remote_FoundNone") : Loc.F("Remote_FoundCount", found.Count);
        }
        catch (Exception ex)
        {
            Log.Error("リモート連携: 親を探せませんでした", ex);
            FindStatus.Text = Loc.F("Remote_StatusError", ex.Message);
        }
        finally
        {
            FindButton.IsEnabled = true;
        }
    }

    private void FoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FoundList.SelectedItem is not FoundParent parent) return;
        AddressBox.Text = parent.Address;
        // 見つけた親のポートが違えば合わせる
        if (parent.Port != App.Settings.RemotePort)
        {
            App.Settings.RemotePort = parent.Port;
            PortBox.Text = parent.Port.ToString();
        }
        ConnectTo(parent.Address);
    }

    // --- IP アドレスを直接入れてつなぐ ---

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConnectTo(AddressBox.Text);
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => ConnectTo(AddressBox.Text);

    private void ConnectTo(string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        App.Settings.RemoteParentAddress = address;
        App.Settings.Save();
        // 自分で［つなぐ］を押した（または見つけた親を選んだ）ので、「つなぎますか？」は聞かない
        _main.ApplyRemoteSettings(userConnect: true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
