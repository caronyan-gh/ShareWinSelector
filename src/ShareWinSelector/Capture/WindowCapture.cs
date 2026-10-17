using ShareWinSelector.Interop;
using ShareWinSelector.Rendering;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ShareWinSelector.Capture;

/// <summary>1 ウィンドウ分の WGC キャプチャ。フレームはワーカースレッドで通知される。</summary>
internal sealed class WindowCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly GraphicsDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;

    /// <summary>新しいフレーム。surface は呼び出し中のみ有効。サイズはコンテンツの実サイズ。</summary>
    public event Action<IDXGISurface, int, int>? FrameArrived;

    /// <summary>キャプチャ対象が閉じられた。</summary>
    public event Action? Closed;

    public WindowCapture(GraphicsDevice device, IntPtr hwnd, bool captureCursor)
    {
        _device = device;
        _item = CaptureInterop.CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => Closed?.Invoke();

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
            // 管理者権限ウィンドウ等で失敗したら、作りかけのプールとセッションを残さない
            _pool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _pool.Dispose();
            throw;
        }
    }

    public bool CaptureCursor
    {
        get => _session.IsCursorCaptureEnabled;
        set => _session.IsCursorCaptureEnabled = value;
    }

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
    }

    private bool _frameErrorLogged;

    private void ProcessFrame(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        SizeInt32 contentSize = frame.ContentSize;
        IntPtr ptr = CaptureInterop.GetDxgiInterface(frame.Surface, typeof(IDXGISurface).GUID);
        using (var surface = new IDXGISurface(ptr))
        {
            FrameArrived?.Invoke(surface, contentSize.Width, contentSize.Height);
        }

        // 対象ウィンドウのサイズが変わったらプールを作り直す
        if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
        {
            _poolSize = contentSize;
            _pool.Recreate(_device.WinRTDevice, PixelFormat, 2, contentSize);
        }
    }

    public void Dispose()
    {
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }
}
