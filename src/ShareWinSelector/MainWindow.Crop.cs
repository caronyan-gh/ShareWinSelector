using System.Windows;

namespace ShareWinSelector;

/// <summary>
/// 「上をクロップ」と「共有前に確認」。
/// - 上をクロップ（全体のオン・オフ）: ブラウザのタブ名などを隠すため、ウィンドウの上側を隠して共有する。
///   クロップ量はアプリごとに保存し、保存済みのアプリは最初からクロップ、未保存なら調整してから共有する。
/// - 共有前に確認: 選んだウィンドウをすぐ見せず、オーバーレイで確認してから共有する（別のウィンドウを選ぶ事故の防止）。
/// 確認・調整の間、そのウィンドウは相手に見せない。何枚か続けて選んだら 1 枚ずつ順番に確認する。
/// </summary>
public partial class MainWindow
{
    // HWND → 今かけているクロップ量（ウィンドウの DPI 基準の DIP）。無ければクロップなし
    private readonly Dictionary<IntPtr, double> _crop = [];
    // 確認・調整中のウィンドウ（相手には見せていない）
    private IntPtr _cropEditing;
    // 確認・調整のために共有を始めた（＝キャンセルなら共有自体を取りやめる）
    private bool _cropEditStartedSharing;
    // 確認待ちのウィンドウ（選んだ順）。すでに見せない状態で選択済み
    private readonly List<IntPtr> _reviewQueue = [];
    // 確認待ちの処理を予約済みか（その場で呼ぶと調整の開始・キャンセルと入れ子になるので、あとで 1 回だけ回す）
    private bool _reviewQueueScheduled;

    private bool CropMode => App.Settings.CropMode;

    private double GetCropDip(IntPtr hwnd) => _crop.TryGetValue(hwnd, out var dip) ? dip : 0;

    private static string AppKeyOf(WindowItem item) => item.AppKey;

    private static double SavedCropFor(WindowItem item) =>
        App.Settings.CropTop.TryGetValue(AppKeyOf(item), out var dip) ? dip : 0;

    private void SetCropEnabledSilently(WindowItem item, bool value)
    {
        _suppressCheckHandler = true;
        item.CropEnabled = value;
        _suppressCheckHandler = false;
    }

    /// <summary>行で個別に切り替えたクロップの有無（アプリごとに保存。共有をやめても・終了しても残る）。</summary>
    private static bool? CropPreferenceFor(WindowItem item) =>
        App.Settings.CropUse.TryGetValue(AppKeyOf(item), out var use) ? use : null;

    /// <summary>
    /// このウィンドウにクロップを効かせるか。行で個別に切り替えていればそれ、
    /// 無ければ一覧の上の「上をクロップ」（＝選んだときの初期値）。
    /// 親から頼まれた共有（リモート連携の子）では、親とこの PC の「上をクロップ」のどちらかがオンならクロップする。
    /// </summary>
    private bool UsesCrop(WindowItem item) => CropPreferenceFor(item) ?? ((_shareFromParent && _parentCropDefault == true) || CropMode);

    /// <summary>各行の「クロップ」チェックと保存済みアイコンを今の状態に合わせる。</summary>
    private void SyncCropChecks()
    {
        foreach (var item in _items)
        {
            // 子のウィンドウは子の画面でクロップする（親の行には出さない）
            item.CropUiVisible = !item.IsRemote;
            if (item.IsRemote) continue;
            item.HasSavedCrop = SavedCropFor(item) > 0;
            // 共有中は「今クロップしているか」。未共有は自分でチェックしたときだけ（全体の設定は選んだ瞬間にだけ効く）
            bool on = item.IsChecked
                ? _crop.ContainsKey(item.Handle) || (item.Handle == _cropEditing && UsesCrop(item))
                : CropPreferenceFor(item) == true;
            SetCropEnabledSilently(item, on);
        }
    }

    // --- 共有の開始（チェックを付けたとき） ---

    /// <summary>共有に選ばれた。必要なら見せない状態で選択して確認待ちに入れる。</summary>
    private void BeginShare(WindowItem item)
    {
        bool useCrop = UsesCrop(item);
        double saved = useCrop ? SavedCropFor(item) : 0;
        bool needsCropSetup = useCrop && saved <= 0;
        // 親から頼まれた共有（リモート連携の子）は、親の側で確かめるので、ここでは確かめない（クロップの範囲が未設定なら決める）
        bool needsReview = (App.Settings.ConfirmBeforeShare && !_shareFromParent) || needsCropSetup;
        if (!StartSharing(item, saved, hidden: needsReview)) return;
        // クロップの状態はここで決め直す（前に共有していたときの値を残さない）
        if (saved > 0) _crop[item.Handle] = saved;
        else _crop.Remove(item.Handle);
        if (needsReview)
        {
            if (!_reviewQueue.Contains(item.Handle)) _reviewQueue.Add(item.Handle);
            ProcessReviewQueue();
        }
        SyncCropChecks(); // 選んだ結果（クロップしたか）を行のチェックに出す
    }

    /// <summary>待っている次のウィンドウの確認を始める（あとで 1 回だけ）。</summary>
    private void ProcessReviewQueue()
    {
        if (_reviewQueueScheduled) return;
        _reviewQueueScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _reviewQueueScheduled = false;
            ProcessReviewQueueNow();
        });
    }

    private void ProcessReviewQueueNow()
    {
        if (_cropEditing != IntPtr.Zero || _overlay is null || _shuttingDown) return;
        while (_reviewQueue.Count > 0)
        {
            IntPtr hwnd = _reviewQueue[0];
            _reviewQueue.RemoveAt(0);
            var item = _items.FirstOrDefault(i => i.Handle == hwnd);
            // 待っている間に外された・閉じたものは飛ばす
            if (item is null || !_selected.ContainsKey(hwnd)) continue;
            StartEdit(item, startedSharing: true);
            return;
        }
    }

    // --- 各行の「クロップ」チェック（このウィンドウにクロップを効かせるか。保存値は消さない） ---

    private void OnCropEnabledChanged(WindowItem item)
    {
        IntPtr hwnd = item.Handle;
        // アプリごとに覚えておく（共有をやめても・終了しても、次も同じ状態で）
        App.Settings.CropUse[AppKeyOf(item)] = item.CropEnabled;
        App.Settings.Save();
        if (item.CropEnabled)
        {
            double saved = SavedCropFor(item);
            if (saved <= 0)
            {
                // 保存が無い: 調整して作る（共有していなければ見せない状態で選択してから）
                BeginAdjust(item);
                return;
            }
            if (item.IsChecked)
            {
                // 共有中なら保存済みのクロップをすぐ効かせる（確認待ちなら見せないまま）
                _crop[hwnd] = saved;
                _compositor?.SetCrop(hwnd, saved, hidden: IsWaitingForReview(hwnd));
            }
            SyncCropChecks();
            return;
        }

        // このウィンドウだけクロップなしにする（保存値は残すので、ほかのウィンドウや次回には影響しない）
        _crop.Remove(hwnd);
        if (_cropEditing == hwnd)
        {
            // 確認・調整中: 上辺の調整だけやめて、確認は続ける（まだ見せない）
            StartEdit(item, _cropEditStartedSharing, forceDrag: false);
            return;
        }
        _compositor?.SetCrop(hwnd, 0, hidden: IsWaitingForReview(hwnd));
        SyncCropChecks();
    }

    /// <summary>親: 子のウィンドウ（R の行）のうち、子で共有中のものの数（子の画面で確認・クロップの範囲を決める待ちのものは数えない）。</summary>
    private int RemoteSharedCount => _items.Count(i => i.IsRemote && i.IsChecked && !i.RemotePending);

    /// <summary>相手に実際に見えている（確認・調整待ちでない）共有中のウィンドウの数。</summary>
    private int VisibleSharedCount => _selected.Keys.Count(h => !IsWaitingForReview(h));

    /// <summary>確認待ち（まだ相手に見せていない）か。</summary>
    private bool IsWaitingForReview(IntPtr hwnd) => hwnd == _cropEditing || _reviewQueue.Contains(hwnd);

    private void CropAdjust_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WindowItem item) BeginAdjust(item);
    }

    /// <summary>隠す範囲を（作り直して）調整する。</summary>
    private void BeginAdjust(WindowItem item)
    {
        if (_overlay is null || item.IsRemote) return;
        IntPtr hwnd = item.Handle;

        if (_cropEditing == hwnd)
        {
            // 同じウィンドウを確認・調整中: 上辺をドラッグできるようにするだけ（まだ見せない・取りやめ時の扱いも変えない）
            StartEdit(item, _cropEditStartedSharing, forceDrag: true);
            return;
        }
        if (_cropEditing != IntPtr.Zero)
        {
            // 別のウィンドウを確認・調整中なら、それはキャンセル扱いにしてから（通知を経由せずに直接）
            IntPtr previous = _cropEditing;
            bool previousStartedSharing = _cropEditStartedSharing;
            _cropEditing = IntPtr.Zero;
            CancelEdit(previous, previousStartedSharing);
        }

        // 確認待ちの列にいたなら、今ここで確認する（取りやめなら共有自体をやめる）
        bool startedSharing = _reviewQueue.Remove(hwnd);
        if (!item.IsChecked)
        {
            // まだ共有していない: 見せない状態で選択してから調整する（タブ名が一瞬も映らないように）
            _suppressCheckHandler = true;
            item.IsChecked = true;
            _suppressCheckHandler = false;
            if (!StartSharing(item, SavedCropFor(item), hidden: true))
            {
                SyncCropChecks();
                return;
            }
            startedSharing = true;
            UpdateStatus();
        }
        StartEdit(item, startedSharing, forceDrag: true);
    }

    /// <summary>オーバーレイで確認（と、クロップがオンなら上辺の調整）を始める。</summary>
    private void StartEdit(WindowItem item, bool startedSharing, bool forceDrag = false)
    {
        bool allowDrag = forceDrag || UsesCrop(item);
        double initial = _crop.TryGetValue(item.Handle, out var current) ? current : (allowDrag ? SavedCropFor(item) : 0);
        // 先に見せない状態にしてから、調整の状態を記録してオーバーレイを開く
        // （オーバーレイは置き換え時にキャンセル通知を出さないので、ここで入れ子にならない）
        _compositor?.SetCrop(item.Handle, initial, hidden: true);
        _cropEditing = item.Handle;
        _cropEditStartedSharing = startedSharing;
        // 確認・調整は自分用オーバーレイで行うので、隠していたら出す
        if (OverlayCheck.IsChecked != true) OverlayCheck.IsChecked = true;
        _overlay!.BeginCropEdit(item.Handle, initial, allowDrag);
        SyncCropChecks();
        UpdateStatus();
    }

    private void OnCropConfirmed(IntPtr hwnd, double dip)
    {
        if (hwnd != _cropEditing) return;
        _cropEditing = IntPtr.Zero;
        var item = _items.FirstOrDefault(i => i.Handle == hwnd);
        // 上辺を調整したならその値を保存（ほぼ 0 ならクロップなし）。確認だけなら今の値のまま
        if (item is not null && _overlayCropWasDraggable)
        {
            if (dip < 1)
            {
                _crop.Remove(hwnd);
                App.Settings.CropTop.Remove(AppKeyOf(item));
            }
            else
            {
                _crop[hwnd] = dip;
                App.Settings.CropTop[AppKeyOf(item)] = dip;
            }
            App.Settings.Save();
        }
        // ここで初めて相手に見せる
        _compositor?.SetCrop(hwnd, GetCropDip(hwnd), hidden: false);
        SyncCropChecks();
        UpdateStatus();
        ProcessReviewQueue();
        ResumeSelectAfterEdit();
    }

    // 確定したときの調整が、上辺をドラッグできるもの（＝クロップ量を決めるもの）だったか
    private bool _overlayCropWasDraggable => _overlay?.LastEditAllowedDrag == true;

    private void OnCropCancelled(IntPtr hwnd)
    {
        if (hwnd != _cropEditing) return;
        bool startedSharing = _cropEditStartedSharing;
        _cropEditing = IntPtr.Zero;
        CancelEdit(hwnd, startedSharing);
        SyncCropChecks();
        UpdateStatus();
        ProcessReviewQueue();
        ResumeSelectAfterEdit();
    }

    /// <summary>確認・調整をやめたときの後始末（_cropEditing は呼び出し側で外しておく）。</summary>
    private void CancelEdit(IntPtr hwnd, bool startedSharing)
    {
        if (startedSharing)
        {
            // 確認・調整のために共有を始めただけなので、共有自体を取りやめる
            RemoveSelected(hwnd);
        }
        else
        {
            // 共有中のものの調整をやめた: 前の状態に戻して見せる
            _compositor?.SetCrop(hwnd, GetCropDip(hwnd), hidden: false);
        }
    }
    // --- 一覧の上のチェック ---

    private void CropModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || !IsInitialized) return;
        // 選んだときの初期値だけを変える（共有中のウィンドウのクロップはそのまま）
        bool on = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;
        App.Settings.CropMode = on;
        App.Settings.Save();
        // 通常表示とコンパクト表示のチェックをそろえる
        _applyingSettings = true;
        CropModeCheck.IsChecked = CompactCropModeCheck.IsChecked = on;
        _applyingSettings = false;
        SyncCropChecks();
    }

    private void ConfirmCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings || !IsInitialized) return;
        App.Settings.ConfirmBeforeShare = ConfirmCheck.IsChecked == true;
        App.Settings.Save();
    }
}
