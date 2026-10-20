using ShareWinSelector.Interop;
using ShareWinSelector.Rendering;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ShareWinSelector.Capture;

/// <summary>
/// 1 ウィンドウ分の WGC キャプチャ。
/// 届いたフレームはすぐには渡さず「最新の 1 枚」として預かり、描画側が DeliverPending で受け取る。
/// ゲームや動画は 60〜144 回/秒届くが、合成は 30 回/秒なので、使わないフレームのコピーを省ける。
/// 最新の 1 枚は必ず残るので、動きが止まった直後の絵が抜けることはない。
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly GraphicsDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    // 預かっている最新フレーム（まだ描画側に渡していないもの）。_frameLock で守る
    private readonly object _frameLock = new();
    private Direct3D11CaptureFrame? _pending;
    private bool _disposed;

    /// <summary>新しいフレーム。surface は呼び出し中のみ有効。サイズはコンテンツの実サイズ。</summary>
    public event Action<IDXGISurface, int, int>? FrameArrived;

    /// <summary>キャプチャ対象が閉じられた。</summary>
    public event Action? Closed;

    public WindowCapture(GraphicsDevice device, IntPtr hwnd, bool captureCursor)
    {
        _device = device;
        _item = CaptureInterop.CreateItemForWindow(hwnd);
        _item.Closed += OnItemClosed;

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.WinRTDevice, PixelFormat, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        try
        {
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = captureCursor;
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
            {
                try { _session.IsBorderRequired = false; } catch { /* 許可が無ければ黄色枠のまま */ }
            }
            _session.StartCapture();
        }
        catch
        {
            // 管理者権限ウィンドウ等で失敗したら、作りかけのプールとセッションを残さない。
            // セッションを閉じる呼び出しは戻ってこないことがあるので（Dispose を参照）、呼んだスレッドを止めないよう別のスレッドで
            _item.Closed -= OnItemClosed;
            _pool.FrameArrived -= OnFrameArrived;
            _pool.Dispose();
            if (_session is { } session)
                new Thread(() => { try { session.Dispose(); } catch { /* 後片付けなので無視 */ } }) { IsBackground = true, Name = "Capture stop" }.Start();
            throw;
        }
    }

    // 対象が閉じられた知らせが来たか（止めるときのログ用）
    private volatile bool _itemClosed;

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _itemClosed = true;
        Closed?.Invoke();
    }

    public bool CaptureCursor
    {
        get => _session.IsCursorCaptureEnabled;
        set => _session.IsCursorCaptureEnabled = value;
    }

    // 止める処理と、WGC のスレッドでのフレームの受け取りが、今どの手順にいるか（止まったままのときにログに出す）
    private volatile string _disposeStep = "";
    private volatile string _callbackStep = "";

    /// <summary>止める処理と、フレームの受け取りの今の手順（ログ用）。</summary>
    public string DescribeSteps() =>
        $"停止の手順 {(_disposeStep.Length == 0 ? "未開始" : _disposeStep)}、フレームの受け取り {(_callbackStep.Length == 0 ? "なし" : _callbackStep)}"
        + $"、閉じた知らせ {(_itemClosed ? "あり" : "なし")}";

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            ProcessFrame(sender);
        }
        catch (Exception ex)
        {
            // WGC のワーカースレッドで例外を投げるとプロセスごと落ちるので握りつぶす
            if (!_frameErrorLogged) Log.Error("フレーム処理エラー", ex);
            _frameErrorLogged = true;
        }
        finally
        {
            _callbackStep = "";
        }
    }

    private bool _frameErrorLogged;

    private void ProcessFrame(Direct3D11CaptureFramePool sender)
    {
        _callbackStep = "TryGetNextFrame";
        var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        _callbackStep = "FrameLock";
        lock (_frameLock)
        {
            _callbackStep = "Store";
            if (_disposed)
            {
                frame.Dispose();
                return;
            }

            SizeInt32 contentSize = frame.ContentSize;
            if (contentSize.Width == _poolSize.Width && contentSize.Height == _poolSize.Height)
            {
                // 古い方は使わずに返す（プールのバッファを空けて、次のフレームを受け取れるように）
                _pending?.Dispose();
                _pending = frame;
                return;
            }

            // 対象ウィンドウのサイズが変わった: 新しい大きさの絵をその場で渡してから、プールを作り直す
            // （作り直す前に、古いプールのフレームは全部手放す）
            _pending?.Dispose();
            _pending = null;
            _callbackStep = "Deliver";
            using (frame) Deliver(frame);
            _poolSize = contentSize;
            _callbackStep = "Recreate";
            _pool.Recreate(_device.WinRTDevice, PixelFormat, 2, contentSize);
        }
    }

    /// <summary>
    /// 預かっている最新フレームがあれば FrameArrived で渡す（描画スレッドから呼ぶ）。渡したら true。
    /// </summary>
    public bool DeliverPending()
    {
        lock (_frameLock)
        {
            if (_pending is not { } frame) return false;
            _pending = null;
            using (frame) Deliver(frame);
            return true;
        }
    }

    private void Deliver(Direct3D11CaptureFrame frame)
    {
        SizeInt32 contentSize = frame.ContentSize;
        IntPtr ptr = CaptureInterop.GetDxgiInterface(frame.Surface, typeof(IDXGISurface).GUID);
        using var surface = new IDXGISurface(ptr);
        FrameArrived?.Invoke(surface, contentSize.Width, contentSize.Height);
    }

    /// <summary>
    /// 止める。セッションを閉じる呼び出しは、中で取り込みサービスの返事を待つので、リモート デスクトップの中などでは
    /// 戻ってこないことがある。呼ぶ側は、止まってもよいスレッド（使い捨て）から呼ぶこと。
    /// </summary>
    public void Dispose()
    {
        StopReceiving();
        _disposeStep = "Session";
#if DEBUG
        // デバッグ: SWS_SIMULATE_STOP_HANG_SEC=<秒> で、キャプチャを止めるたびに、セッションを閉じる前でその秒数だけ待たせる
        if (int.TryParse(Environment.GetEnvironmentVariable("SWS_SIMULATE_STOP_HANG_SEC"), out int hang))
            Thread.Sleep(TimeSpan.FromSeconds(hang));
#endif
        _session.Dispose();
        _disposeStep = "Done";
    }

    /// <summary>
    /// 止めるが、セッションは閉じずに返す（閉じる呼び出しが戻ってこない環境で、止まったスレッドを増やさないため）。
    /// 返したセッションは、呼んだ側がプロセスの終わりまで持っておくこと（手放すと、後片付けの係がそこで閉じようとして止まる）。
    /// </summary>
    public object DisposeKeepingSession()
    {
        StopReceiving();
        // 知らせの先（合成器など）まで持ち続けないように切り離す
        FrameArrived = null;
        Closed = null;
        _disposeStep = "Kept";
        return (_session, _item);
    }

    /// <summary>フレームの受け取りをやめて、預かっているフレームとフレーム置き場を返す（ここまでは戻ってくる）。</summary>
    private void StopReceiving()
    {
        _disposeStep = "Unsubscribe";
        _item.Closed -= OnItemClosed;
        _pool.FrameArrived -= OnFrameArrived;
        _disposeStep = "FrameLock";
        lock (_frameLock)
        {
            _disposed = true;
            _disposeStep = "DropPending";
            _pending?.Dispose();
            _pending = null;
        }
        // フレーム置き場（重い方）を先に閉じる。このあとのセッションで止まっても、置き場の分は返せている
        _disposeStep = "Pool";
        _pool.Dispose();
    }
}
