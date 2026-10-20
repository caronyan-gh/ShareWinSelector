using ShareWinSelector.Interop;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace ShareWinSelector.Rendering;

/// <summary>キャプチャと描画で共有する D3D11 / D2D デバイス一式。</summary>
internal sealed class GraphicsDevice : IDisposable
{
    /// <summary>D3D11 イミディエイトコンテキストと D2D を触るときは必ずこのロックを取る。</summary>
    public object SyncRoot { get; } = new();

    public ID3D11Device D3DDevice { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public IDXGIDevice DxgiDevice { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public IDirect3DDevice WinRTDevice { get; }

    public GraphicsDevice()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? device).CheckError();
        D3DDevice = device!;
        D3DContext = D3DDevice.ImmediateContext;
        try
        {
            // WGC のフレームプールが別スレッドからデバイスを使うため保護を有効化
            using (var mt = D3DDevice.QueryInterface<ID3D11Multithread>())
                mt.SetMultithreadProtected(true);

            DxgiDevice = D3DDevice.QueryInterface<IDXGIDevice>();
            D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
            D2DDevice = D2DFactory.CreateDevice(DxgiDevice);
            WinRTDevice = CaptureInterop.CreateDirect3DDevice(DxgiDevice.NativePointer);
            LogAdapter();
        }
        catch
        {
            // GPU 復旧の再試行で何度も作り直すことがあるので、作りかけを残さない
            D2DDevice?.Dispose();
            D2DFactory?.Dispose();
            DxgiDevice?.Dispose();
            D3DContext.Dispose();
            D3DDevice.Dispose();
            throw;
        }
    }

    /// <summary>使っている GPU の名前をログに残す（リモート デスクトップの中など、描画の不具合を調べるとき用）。</summary>
    private void LogAdapter()
    {
        try
        {
            using var adapter = DxgiDevice.GetAdapter();
            var d = adapter.Description;
            string remote = System.Windows.SystemParameters.IsRemoteSession ? "、リモート デスクトップ" : "";
            Log.Info($"GPU: {d.Description}（{d.VendorId:X4}:{d.DeviceId:X4}、{D3DDevice.FeatureLevel}{remote}）");
        }
        catch (Exception ex)
        {
            Log.Error("GPU の名前を取れませんでした", ex);
        }
    }

    /// <summary>デバイスが削除・リセットされ、作り直しが必要か。</summary>
    public bool IsDeviceLost => D3DDevice.DeviceRemovedReason.Failure;

    public void Dispose()
    {
        (WinRTDevice as IDisposable)?.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        DxgiDevice.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
    }
}
