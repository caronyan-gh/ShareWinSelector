using System.Diagnostics;
using System.Numerics;
using ShareWinSelector.Capture;
using ShareWinSelector.Interop;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace ShareWinSelector.Rendering;

/// <summary>
/// 選択ウィンドウを「モニターと同じ座標系のキャンバス」に実位置・実 Z 順で合成し、
/// 出力ウィンドウ（子 HWND）の SwapChain に縦横比維持で描く。
/// </summary>
internal sealed class Compositor : IDisposable
{
    private const Format BufferFormat = Format.B8G8R8A8_UNorm;
    private const int TargetFps = 30;

    /// <summary>1 ウィンドウ分のキャプチャと、その最新フレームのコピー。</summary>
    private sealed class Source(WindowIdentity identity, WindowCapture capture, bool isAuto)
    {
        public WindowIdentity Identity { get; } = identity;
        public IntPtr Hwnd => Identity.Handle;
        public WindowCapture Capture { get; } = capture;
        /// <summary>ユーザーが選んだのではなく、ポップアップとして自動追加されたもの。</summary>
        public bool IsAuto { get; } = isAuto;
        /// <summary>キャプチャ対象が閉じられた（WGC の Closed）。以後は描かず、次のフレームで外す。</summary>
        public volatile bool Dead;
        /// <summary>上から隠す量（ウィンドウの DPI 基準の DIP）。タブ名などを相手に見せないため。</summary>
        public volatile float CropTopDip;
        /// <summary>クロップ調整中など、まだ相手に見せないもの（そのポップアップも含めて描かない）。</summary>
        public volatile bool Hidden;
        public ID3D11Texture2D? Texture;
        public ID2D1Bitmap1? Bitmap;
        public int Width, Height;

        public void DisposeResources()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Texture?.Dispose();
            Texture = null;
        }
    }

    private readonly GraphicsDevice _device;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1SolidColorBrush _canvasBrush;
    private readonly Dictionary<IntPtr, Source> _sources = [];
    private ID2D1Bitmap1? _target;
    // ウィンドウが無い部分に敷く背景（null は黒）
    private ID2D1Bitmap1? _background;
    private Native.RECT _canvas;
    private bool _captureCursor = true;
    private volatile bool _includePopups = true;
    // キャプチャ作成に失敗したポップアップ（消えるまで再試行しない）。描画スレッド専用
    private readonly HashSet<IntPtr> _failedPopups = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    // 選択アプリと同じスレッドでも絶対に拾わないウィンドウ（シェル、タスク切り替え・サムネイル系、メニューの影）
    private static readonly HashSet<string> ExcludedPopupClasses =
    [
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow", "SysShadow",
        "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland",
    ];

    // 所有者なしでも拾ってよい、典型的なポップアップのクラス（メニュー・ツールチップ・コンボの一覧・XAML のポップアップ）
    private static readonly HashSet<string> AllowedPopupClasses =
        ["#32768", "tooltips_class32", "ComboLBox", "Xaml_WindowedPopupClass"];

    private readonly Thread _renderThread;
    private volatile bool _running = true;

    // 何も変わっていないフレームは描き直さない。ただし会議ソフトが「止まった」と見なさないよう、この間隔では必ず描く
    private static readonly TimeSpan IdleRedrawInterval = TimeSpan.FromMilliseconds(250);
    // 設定やサイズの変更など、次のフレームで必ず描き直してほしいとき true
    private volatile bool _dirty = true;
    // 前回描いた配置（描画スレッド専用）
    private List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> _lastOrder = [];

    /// <summary>キャプチャ対象が閉じられた（ワーカースレッドから通知）。</summary>
    public event Action<IntPtr>? SourceClosed;

    /// <summary>
    /// GPU デバイスが失われた（ドライバ更新・TDR・スリープ復帰など）。描画は停止済み。
    /// 受け取った側でデバイスごと作り直す（描画スレッドから通知）。
    /// </summary>
    public event Action? DeviceLost;

    public Compositor(GraphicsDevice device, IntPtr hwnd, int width, int height, Native.RECT canvas)
    {
        _device = device;
        _canvas = canvas;
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            Format = BufferFormat,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForHwnd(device.D3DDevice, hwnd, desc);
        try
        {
            _context = device.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
            _canvasBrush = _context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 1f));
            CreateTarget();
        }
        catch
        {
            // GPU 復旧の再試行で何度も作り直すことがあるので、作りかけを残さない
            _target?.Dispose();
            _canvasBrush?.Dispose();
            _context?.Dispose();
            _swapChain.Dispose();
            throw;
        }

        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Compositor" };
        _renderThread.Start();
    }

    private void CreateTarget()
    {
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(
            new D2DPixelFormat(BufferFormat, D2DAlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _context.CreateBitmapFromDxgiSurface(surface, props);
        _context.Target = _target;
    }

    /// <summary>合成キャンバスの範囲（＝共有するモニターの矩形、物理ピクセル）。</summary>
    public Native.RECT Canvas
    {
        get { lock (_device.SyncRoot) return _canvas; }
        set
        {
            lock (_device.SyncRoot) _canvas = value;
            _dirty = true;
        }
    }

    public bool CaptureCursor
    {
        get => _captureCursor;
        set
        {
            _captureCursor = value;
            lock (_device.SyncRoot)
                foreach (var s in _sources.Values) s.Capture.CaptureCursor = value;
        }
    }

    /// <summary>選択アプリの右クリックメニュー・ダイアログ・ツールチップ等も自動で映すか。</summary>
    public bool IncludePopups
    {
        get => _includePopups;
        set
        {
            _includePopups = value;
            _dirty = true;
        }
    }

    /// <summary>ウィンドウが無い部分を塗る色（背景画像が無いときに見える）。UI スレッドから呼ぶ。</summary>
    public void SetCanvasColor(byte r, byte g, byte b)
    {
        lock (_device.SyncRoot) _canvasBrush.Color = new Color4(r / 255f, g / 255f, b / 255f, 1f);
        _dirty = true;
    }

    /// <summary>背景画像を設定する（BGRA・乗算済みアルファの画素、null で画像なし＝単色）。UI スレッドから呼ぶ。</summary>
    public unsafe void SetBackground(byte[]? pixels, int width, int height)
    {
        _dirty = true;
        lock (_device.SyncRoot)
        {
            _background?.Dispose();
            _background = null;
            if (pixels is null || width <= 0 || height <= 0) return;
            fixed (byte* p = pixels)
            {
                _background = _context.CreateBitmap(new Vortice.Mathematics.SizeI(width, height), (IntPtr)p, (uint)(width * 4),
                    new BitmapProperties1(new D2DPixelFormat(BufferFormat, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
        }
    }

    public bool Contains(IntPtr hwnd)
    {
        lock (_device.SyncRoot) return _sources.ContainsKey(hwnd);
    }

    /// <summary>
    /// キャプチャ対象を追加する（UI スレッド）。identity は選んだ時点の本人確認情報で、
    /// HWND が別のウィンドウに使い回されていたら追加しない（例外）。
    /// </summary>
    public void AddWindow(WindowIdentity identity, double cropTopDip = 0, bool hidden = false)
    {
        if (!identity.IsAlive())
            throw new InvalidOperationException("ウィンドウはすでに閉じられています");
        Source? existing;
        lock (_device.SyncRoot) _sources.TryGetValue(identity.Handle, out existing);
        if (existing is { IsAuto: false, Dead: false } && existing.Identity == identity)
        {
            SetCrop(identity.Handle, cropTopDip, hidden);
            return;
        }
        // ポップアップとして自動追加済み・別の持ち主・閉じたもの は作り直す
        if (existing is not null) RemoveWindow(identity.Handle);
        AddWindowCore(identity, isAuto: false, cropTopDip, hidden);
    }

    /// <summary>上から隠す量と、相手に見せないかどうかを変える（UI スレッド）。</summary>
    public void SetCrop(IntPtr hwnd, double cropTopDip, bool hidden)
    {
        lock (_device.SyncRoot)
        {
            if (!_sources.TryGetValue(hwnd, out var s) || s.IsAuto) return;
            s.CropTopDip = (float)Math.Max(0, cropTopDip);
            s.Hidden = hidden;
        }
        _dirty = true;
    }

    private void AddWindowCore(WindowIdentity identity, bool isAuto, double cropTopDip = 0, bool hidden = false)
    {
        var capture = new WindowCapture(_device, identity.Handle, _captureCursor);
        // クロップは描き始める前に決めておく（最初のフレームでタブ名が映らないように）
        var source = new Source(identity, capture, isAuto) { CropTopDip = (float)Math.Max(0, cropTopDip), Hidden = hidden };
        capture.FrameArrived += (surface, w, h) => OnFrame(source, surface, w, h);
        capture.Closed += () =>
        {
            // 閉じたら即座に描画対象から外す（HWND の使い回しで古い絵や別のウィンドウが映らないように）
            source.Dead = true;
            _dirty = true;
            if (!isAuto) SourceClosed?.Invoke(identity.Handle);
        };
        lock (_device.SyncRoot)
        {
            // UI スレッドと描画スレッドが同時に追加した場合は先勝ち
            if (_sources.TryAdd(identity.Handle, source))
            {
                _dirty = true;
                return;
            }
        }
        capture.Dispose();
    }

    /// <summary>キャプチャ対象を外す（UI スレッド）。</summary>
    public void RemoveWindow(IntPtr hwnd)
    {
        Source? source;
        lock (_device.SyncRoot)
        {
            if (!_sources.Remove(hwnd, out source)) return;
        }
        _dirty = true;
        // フレームハンドラがロック待ちの可能性があるので、キャプチャ停止はロックの外で
        source.Capture.Dispose();
        lock (_device.SyncRoot) source.DisposeResources();
    }

    public void RemoveAll()
    {
        IntPtr[] all;
        lock (_device.SyncRoot) all = [.. _sources.Keys];
        foreach (var h in all) RemoveWindow(h);
    }

    /// <summary>出力先のサイズ変更（UI スレッド）。</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_device.SyncRoot)
        {
            _context.Target = null;
            _target?.Dispose();
            _target = null;
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, BufferFormat, SwapChainFlags.None).CheckError();
            CreateTarget();
        }
        _dirty = true;
    }

    private void OnFrame(Source source, IDXGISurface surface, int contentWidth, int contentHeight)
    {
        lock (_device.SyncRoot)
        {
            // 外された（または同じ HWND で作り直された）ソースのフレームは捨てる
            if (source.Dead || !_sources.TryGetValue(source.Hwnd, out var current) || !ReferenceEquals(current, source)) return;

            using var tex = surface.QueryInterface<ID3D11Texture2D>();
            var desc = tex.Description;
            if (source.Texture is null || source.Texture.Description.Width != desc.Width || source.Texture.Description.Height != desc.Height)
            {
                source.DisposeResources();
                source.Texture = _device.D3DDevice.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                using var copySurface = source.Texture.QueryInterface<IDXGISurface>();
                source.Bitmap = _context.CreateBitmapFromDxgiSurface(copySurface,
                    new BitmapProperties1(new D2DPixelFormat(BufferFormat, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
            _device.D3DContext.CopyResource(source.Texture, tex);
            source.Width = contentWidth;
            source.Height = contentHeight;
        }
    }

    private void RenderLoop()
    {
        var frameTime = TimeSpan.FromSeconds(1.0 / TargetFps);
        var sw = Stopwatch.StartNew();
        var lastRender = TimeSpan.Zero;
        int consecutiveErrors = 0;
        while (_running)
        {
            var start = sw.Elapsed;
            try
            {
                // 各ウィンドウの最新フレームを受け取る（このときだけコピーするので、30 回/秒より多くは写さない）
                bool fresh = PullFrames();
                var order = GetDrawOrder();
                // 中身も配置も設定も変わっていなければ描き直さない（GPU を休ませる）
                if (fresh || _dirty || !SameOrder(order, _lastOrder) || start - lastRender >= IdleRedrawInterval)
                {
                    _dirty = false;
                    lock (_device.SyncRoot) RenderCore(order);
                    _lastOrder = order;
                    lastRender = start;
                }
                consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // 同じエラーの連発でログを埋めない
                if (consecutiveErrors++ == 0) Log.Error("描画エラー", ex);
            }

            if (_device.IsDeviceLost || SimulateDeviceLost(sw.Elapsed))
            {
                Log.Error($"GPU デバイスが失われました: {_device.D3DDevice.DeviceRemovedReason}");
                _running = false;
                DeviceLost?.Invoke();
                return;
            }
            var remain = frameTime - (sw.Elapsed - start);
            if (remain > TimeSpan.Zero) Thread.Sleep(remain);
        }
    }

    /// <summary>預かっている最新フレームを全ウィンドウ分受け取ってコピーする。1 つでも新しい絵があれば true。</summary>
    private bool PullFrames()
    {
        List<Source> snapshot;
        lock (_device.SyncRoot) snapshot = [.. _sources.Values];
        bool any = false;
        foreach (var s in snapshot)
            if (!s.Dead && s.Capture.DeliverPending()) any = true;
        return any;
    }

    private static bool SameOrder(List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> a, List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Hwnd != y.Hwnd || x.CropPx != y.CropPx
                || x.Bounds.Left != y.Bounds.Left || x.Bounds.Top != y.Bounds.Top
                || x.Bounds.Right != y.Bounds.Right || x.Bounds.Bottom != y.Bounds.Bottom) return false;
        }
        return true;
    }

    // デバッグ: SWS_SIMULATE_DEVICE_LOST_SEC=<秒> で、プロセス内で 1 回だけデバイス喪失を模擬する
#if DEBUG
    private static int _simulatedDeviceLost;
#endif

#if DEBUG
    private static bool SimulateDeviceLost(TimeSpan elapsed) =>
        int.TryParse(Environment.GetEnvironmentVariable("SWS_SIMULATE_DEVICE_LOST_SEC"), out int sec)
        && elapsed.TotalSeconds >= sec
        && Interlocked.Exchange(ref _simulatedDeviceLost, 1) == 0;
#else
    private static bool SimulateDeviceLost(TimeSpan elapsed) => false;
#endif

    /// <summary>
    /// 描画対象（選択ウィンドウ＋そのポップアップ）を「奥 → 手前」の順で、実矩形付きで返す。
    /// ついでにポップアップのキャプチャを追加・削除し、閉じた／使い回されたウィンドウを外す。
    /// </summary>
    private List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> GetDrawOrder()
    {
        List<Source> snapshot;
        lock (_device.SyncRoot) snapshot = [.. _sources.Values];

        // 本人確認: 閉じた（Closed 済み）か、HWND が別のプロセス・スレッドに使い回されたものは使わない
        var manual = new HashSet<IntPtr>();
        var cropDip = new Dictionary<IntPtr, float>();
        var auto = new HashSet<IntPtr>();
        var deadAuto = new List<IntPtr>();
        foreach (var s in snapshot)
        {
            bool alive = !s.Dead && s.Identity.IsAlive();
            if (s.IsAuto)
            {
                if (alive) auto.Add(s.Hwnd);
                else deadAuto.Add(s.Hwnd);
            }
            else if (alive)
            {
                // 調整中など相手に見せないものは、選択扱いにもしない（そのメニュー等も拾わない）
                if (!s.Hidden) manual.Add(s.Hwnd);
                cropDip[s.Hwnd] = s.CropTopDip;
            }
            else if (!s.Dead)
            {
                // Closed が届かないまま使い回された: 即座に描画をやめ、UI に外してもらう
                s.Dead = true;
                SourceClosed?.Invoke(s.Hwnd);
            }
        }
        foreach (var h in deadAuto) RemoveWindow(h);

        var order = new List<(IntPtr, Native.RECT, int)>();
        var wantedPopups = new HashSet<IntPtr>();

        // 選択ウィンドウの UI スレッド（右クリックメニュー等はここで作られる）と、画面上の位置
        var context = new PopupContext(manual, ShellProcessId());
        foreach (var h in manual)
        {
            context.Threads.Add(Native.GetWindowThreadProcessId(h, out _));
            if (!Native.TryGetFrameBounds(h, out var r)) continue;
            context.Bounds.Add(r);
            // 上をクロップして隠している帯（物理ピクセル）
            int px = cropDip.TryGetValue(h, out float d) ? CropPx(h, d, r.Height) : 0;
            if (px > 0) context.HiddenBands.Add(new Native.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Top + px });
        }
        bool includePopups = _includePopups && manual.Count > 0;

        // EnumWindows は手前 → 奥の順
        Native.EnumWindows((hWnd, _) =>
        {
            // 見えていないもの（数百ある）は、重いポップアップ判定より先に外す
            if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd)) return true;
            bool isManual = manual.Contains(hWnd);
            bool isPopup = !isManual && includePopups && IsPopupOfSelected(hWnd, context);
            if (!isManual && !isPopup) return true;
            // キャプチャを禁止しているポップアップは、黒い四角で下を隠すより描かない方がまし
            if (isPopup && Native.BlocksCapture(hWnd)) return true;
            // 別の仮想デスクトップなど、自分の画面に出ていないものは相手にも見せない
            if (Native.IsCloaked(hWnd)) return true;
            if (!Native.TryGetFrameBounds(hWnd, out var rc) || rc.Width <= 0 || rc.Height <= 0) return true;
            // クロップで隠している部分（タブ・アドレスバー）から出てきたポップアップは映さない。
            // タブのホバーカード（別のタブの名前と縮小画像）やアドレスバーの入力候補（閲覧履歴）が漏れないように
            if (isPopup && StartsInHiddenBand(rc, context.HiddenBands)) return true;
            if (isPopup) wantedPopups.Add(hWnd);
            int cropPx = isManual && cropDip.TryGetValue(hWnd, out float dip) ? CropPx(hWnd, dip, rc.Height) : 0;
            order.Add((hWnd, rc, cropPx));
            return true;
        }, IntPtr.Zero);
        order.Reverse();

        // 消えたポップアップを外す
        foreach (var h in auto)
            if (!wantedPopups.Contains(h)) RemoveWindow(h);
        _failedPopups.IntersectWith(wantedPopups);

        // 新しく出たポップアップのキャプチャを始める（最初のフレームが来るまでは描かれない）
        foreach (var h in wantedPopups)
        {
            if (auto.Contains(h) || _failedPopups.Contains(h)) continue;
            try { AddWindowCore(WindowIdentity.Of(h), isAuto: true); }
            catch { _failedPopups.Add(h); }
        }
        return order;
    }

    /// <summary>ポップアップ判定に使う、そのフレームの選択ウィンドウの情報。</summary>
    private sealed class PopupContext(HashSet<IntPtr> manual, uint shellPid)
    {
        public HashSet<IntPtr> Manual { get; } = manual;
        public HashSet<uint> Threads { get; } = [];
        public List<Native.RECT> Bounds { get; } = [];
        /// <summary>選択ウィンドウの、上をクロップして隠している帯。</summary>
        public List<Native.RECT> HiddenBands { get; } = [];
        public uint ShellPid { get; } = shellPid;
    }

    /// <summary>上から隠す量（DIP）を、今いるモニターの DPI で物理ピクセルに直す。</summary>
    private static int CropPx(IntPtr hwnd, float dip, int height)
    {
        if (dip <= 0) return 0;
        uint dpi = Native.GetDpiForWindow(hwnd);
        return Math.Min(height, (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0));
    }

    // 隠している帯の下端から少しはみ出して出るもの（タブの下辺に付いて出るホバーカードなど）も含める
    private const int HiddenBandMargin = 6;

    /// <summary>ポップアップの上端が、隠している帯の中（下端の少し下まで）にあるか。</summary>
    private static bool StartsInHiddenBand(Native.RECT popup, List<Native.RECT> bands)
    {
        foreach (var b in bands)
        {
            bool overlapsX = popup.Left < b.Right && popup.Right > b.Left;
            if (overlapsX && popup.Top >= b.Top - HiddenBandMargin && popup.Top < b.Bottom + HiddenBandMargin) return true;
        }
        return false;
    }

    /// <summary>エクスプローラー（タスクバー・デスクトップを持つシェル）のプロセス ID。</summary>
    private static uint ShellProcessId()
    {
        IntPtr shell = Native.GetShellWindow();
        if (shell == IntPtr.Zero) return 0;
        Native.GetWindowThreadProcessId(shell, out uint pid);
        return pid;
    }

    /// <summary>
    /// 選択ウィンドウに付随するポップアップ（所有ダイアログ・メニュー・ツールチップ等）か。
    /// 選んでいないものが映るのが一番まずいので、迷ったら拾わない（許可リスト方式）。
    /// </summary>
    private bool IsPopupOfSelected(IntPtr hWnd, PopupContext ctx)
    {
        // 毎フレーム全ウィンドウに対して呼ばれるので、安い判定で先にふるい落とし、クラス名は候補だけ調べる

        // 選択ウィンドウが所有するダイアログ等。GA_ROOTOWNER は WS_POPUP 以外の所有ウィンドウを
        // たどれないので、GW_OWNER を自前でたどる
        IntPtr owner = Native.GetWindow(hWnd, Native.GW_OWNER);
        if (owner != IntPtr.Zero)
        {
            for (int depth = 0; owner != IntPtr.Zero && depth < 32; depth++)
            {
                if (ctx.Manual.Contains(owner)) return !ExcludedPopupClasses.Contains(Native.GetClassName(hWnd));
                owner = Native.GetWindow(owner, Native.GW_OWNER);
            }
            return false;
        }

        // 所有者なし: 選択ウィンドウと同じ UI スレッドのものに限る
        uint tid = Native.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == _ownPid || !ctx.Threads.Contains(tid)) return false;
        string cls = Native.GetClassName(hWnd);
        if (ExcludedPopupClasses.Contains(cls)) return false;
        if (IsMainWindowLike(hWnd)) return false; // 同じスレッドの別のメインウィンドウは映さない

        // シェル（エクスプローラー）はタスクバーやタスク切り替えなど他アプリの中身を出すウィンドウを
        // 同じプロセスで持つので、標準のメニューだけ許可する
        if (pid == ctx.ShellPid) return cls == "#32768";

        // 典型的なポップアップのクラスはそのまま許可
        if (AllowedPopupClasses.Contains(cls)) return true;

        // それ以外は「WS_POPUP で、タスクバーに出さず、選択ウィンドウに重なっている」ものだけ
        long style = Native.GetWindowLongPtr(hWnd, Native.GWL_STYLE);
        long exStyle = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE);
        if ((style & Native.WS_POPUP) == 0 || (exStyle & Native.WS_EX_APPWINDOW) != 0) return false;
        if (!Native.TryGetFrameBounds(hWnd, out var r)) return false;
        foreach (var b in ctx.Bounds)
            if (r.Left < b.Right && r.Right > b.Left && r.Top < b.Bottom && r.Bottom > b.Top) return true;
        return false;
    }

    /// <summary>Alt+Tab に出るような通常のトップレベルウィンドウか。</summary>
    private static bool IsMainWindowLike(IntPtr hWnd)
    {
        long exStyle = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE);
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
        return !string.IsNullOrWhiteSpace(Native.GetTitle(hWnd));
    }
    private void RenderCore(List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> order)
    {
        if (_target is null) return;
        var targetSize = _target.PixelSize;
        int cw = Math.Max(_canvas.Width, 1), ch = Math.Max(_canvas.Height, 1);

        _context.BeginDraw();
        _context.Transform = Matrix3x2.Identity;
        // キャンバス外（レターボックス部分）は濃いグレーで区別
        _context.Clear(new Color4(0.12f, 0.12f, 0.12f, 1f));

        float scale = Math.Min((float)targetSize.Width / cw, (float)targetSize.Height / ch);
        float ox = (targetSize.Width - cw * scale) / 2f, oy = (targetSize.Height - ch * scale) / 2f;
        _context.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, oy);

        var canvasRect = new Rect(0, 0, cw, ch);
        _context.PushAxisAlignedClip(canvasRect, AntialiasMode.Aliased);
        _context.FillRectangle(canvasRect, _canvasBrush);
        if (_background is not null)
        {
            // キャンバスを覆うように拡大（はみ出す分は中央で切る）
            var bs = _background.PixelSize;
            float fill = Math.Max((float)cw / bs.Width, (float)ch / bs.Height);
            float bw = bs.Width * fill, bh = bs.Height * fill;
            _context.DrawBitmap(_background, new Rect((cw - bw) / 2f, (ch - bh) / 2f, bw, bh), 1f,
                InterpolationMode.HighQualityCubic, new Rect(0, 0, bs.Width, bs.Height), null);
        }

        foreach (var (hwnd, bounds, cropPx) in order)
        {
            if (!_sources.TryGetValue(hwnd, out var s) || s.Dead || s.Hidden || s.Bitmap is null || s.Width <= 0 || s.Height <= 0) continue;
            // キャプチャ内容は物理ピクセル等倍で、実ウィンドウの左上に置く。上をクロップしていればその分を描かない
            int crop = Math.Min(cropPx, s.Height);
            if (crop >= s.Height) continue;
            var dest = new Rect(bounds.Left - _canvas.Left, bounds.Top - _canvas.Top + crop, s.Width, s.Height - crop);
            _context.DrawBitmap(s.Bitmap, dest, 1f, InterpolationMode.Linear, new Rect(0, crop, s.Width, s.Height - crop), null);
        }

        _context.PopAxisAlignedClip();
        _context.EndDraw();
#if DEBUG
        // デバッグ: 共有映像を画像に書き出す（リリース版には入れない）
        if (Environment.GetEnvironmentVariable("SWS_DUMP") is { } dumpPath && Environment.TickCount64 % 1000 < 40)
            DumpBackBuffer(dumpPath);
#endif
        _swapChain.Present(0, PresentFlags.None);
    }

#if DEBUG
    /// <summary>デバッグ用: 現在のバックバッファを PNG に書き出す。</summary>
    private unsafe void DumpBackBuffer(string path)
    {
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = back.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.D3DDevice.CreateTexture2D(desc);
        _device.D3DContext.CopyResource(staging, back);
        var map = _device.D3DContext.Map(staging, 0, MapMode.Read);
        try
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create((int)desc.Width, (int)desc.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, map.DataPointer, (int)(map.RowPitch * desc.Height), (int)map.RowPitch);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
        }
        finally
        {
            _device.D3DContext.Unmap(staging, 0);
        }
    }
#endif

    public void Dispose()
    {
        _running = false;
        _renderThread.Join();
        RemoveAll();
        lock (_device.SyncRoot)
        {
            _context.Target = null;
            _target?.Dispose();
            _canvasBrush.Dispose();
            _background?.Dispose();
            _context.Dispose();
            _swapChain.Dispose();
        }
    }
}
