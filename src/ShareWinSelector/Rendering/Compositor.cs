using System.Collections.Concurrent;
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
        /// <summary>キャプチャ。カーソルの有無を切り替えたときに作り直す（直前の絵は Texture に残したまま）。</summary>
        public volatile WindowCapture Capture = capture;
        /// <summary>ユーザーが選んだのではなく、ポップアップとして自動追加されたもの。</summary>
        public bool IsAuto { get; } = isAuto;
        /// <summary>ログ用の、ウィンドウのクラス名とアプリ名（止めるときにはウィンドウが消えていて取れないことがあるので、始めたときに取っておく）。</summary>
        public string Label { get; init; } = "";
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
    // キャプチャ作成に失敗したポップアップ（消えるまで再試行しない）。_popupGate で守る
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

    /// <summary>描画スレッドが今している処理（止まったときに、どこで止まったかをログに出す）。</summary>
    public enum RenderStage { Idle, PullFrames, DrawOrder, WaitLock, RemoteFrame, Draw, ReadBack, Present, Sink }
    private volatile RenderStage _stage;
    // 今の処理の中の細かい手順と、そのとき見ていたウィンドウ（描画スレッドだけが書く）
    private volatile string _detail = "";
    private volatile nint _detailHwnd;
    // 今の手順を始めた時刻と、描画の 1 周を最後に始めた時刻（Stopwatch のタイムスタンプ）
    private long _stageStart, _beat = Stopwatch.GetTimestamp();
    // 1 つの手順にこれより長くかかったら、戻ってきたときにログに残す
    private static readonly TimeSpan SlowStage = TimeSpan.FromSeconds(1);
    // 止めるときに描画スレッドを待つ長さ。戻らなければ待たずに進む（終了や作り直しを止めない）
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);
    // 止めるときにポップアップの専用スレッドを待つ長さ（終了全体が 5 秒に収まるように短め）
    private static readonly TimeSpan PopupCloseWait = TimeSpan.FromSeconds(1);

    /// <summary>描画スレッドが今している処理。</summary>
    public RenderStage Stage => _stage;

    /// <summary>描画スレッドが今している処理と手順、見ているウィンドウ（ログ用。例: 「DrawOrder/IsPopup、窓 #32770（mmc）」）。</summary>
    public string StageText => Describe(_stage, _detail, _detailHwnd);

    /// <summary>描画の 1 周を最後に始めてからの時間（ふだんは 1/30 秒ほど。長ければ描画が止まっている）。</summary>
    public TimeSpan StalledFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _beat));

    /// <summary>描画スレッドの今の処理を記録する（前の手順に時間がかかっていたらログに残す）。</summary>
    private void Enter(RenderStage stage) => Mark(stage, "", 0);

    /// <summary>今の処理の中の手順と、見ているウィンドウを記録する（前の手順に時間がかかっていたらログに残す）。</summary>
    private void Mark(string detail, nint hwnd = 0) => Mark(_stage, detail, hwnd);

    private void Mark(RenderStage stage, string detail, nint hwnd)
    {
        long now = Stopwatch.GetTimestamp();
        var took = Stopwatch.GetElapsedTime(_stageStart, now);
        if (_stage != RenderStage.Idle && took >= SlowStage)
            Log.Error($"描画の {Describe(_stage, _detail, _detailHwnd)} に {took.TotalSeconds:F1} 秒かかりました");
        _stage = stage;
        _detail = detail;
        _detailHwnd = hwnd;
        _stageStart = now;
    }

    private static string Describe(RenderStage stage, string detail, nint hwnd) =>
        (detail.Length == 0 ? stage.ToString() : $"{stage}/{detail}") + (hwnd == 0 ? "" : "、窓 " + DescribeWindow(hwnd));

    /// <summary>ウィンドウのクラス名とアプリ名（ログ用。タイトルは書かない）。</summary>
    internal static string DescribeWindow(nint hwnd)
    {
        string cls = Native.GetClassName(hwnd);
        string app = "?";
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            app = p.ProcessName;
        }
        catch { /* 終わったプロセスなど */ }
        return $"{(cls.Length == 0 ? "?" : cls)}（{app}）";
    }

    // 何も変わっていないフレームは描き直さない。ただし会議ソフトが「止まった」と見なさないよう、この間隔では必ず描く
    private static readonly TimeSpan IdleRedrawInterval = TimeSpan.FromMilliseconds(250);
    // 設定やサイズの変更など、次のフレームで必ず描き直してほしいとき true
    private volatile bool _dirty = true;
    private volatile bool _holdOrder;

    // --- リモート連携 ---
    // 親: 子から届いた絵（これを描いている間は、この PC のウィンドウは描かない）
    private volatile bool _remoteMode;
    private ID2D1Bitmap1? _remoteBitmap;
    // 子: 合成した絵を渡す先と、渡す間隔（1 秒あたり 12 コマまで）。変わったときだけ渡す
    private static readonly TimeSpan FrameSinkInterval = TimeSpan.FromMilliseconds(1000.0 / 12);
    private ID3D11Texture2D? _staging;
    private bool _sinkPending;
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

        _popupThread = new Thread(PopupLoop) { IsBackground = true, Name = "Compositor popups" };
        _popupThread.Start();
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
            if (_captureCursor == value) return;
            _captureCursor = value;
            // 設定を変えるだけだと、ウィンドウの中身が変わるまで新しい絵が来ず、止まったカーソルの絵が残る。
            // キャプチャを作り直すと最初の 1 枚がすぐ届くので、それで描き直す（届くまでは直前の絵のまま＝ちらつかない）
            List<Source> snapshot;
            lock (_device.SyncRoot) snapshot = [.. _sources.Values];
            foreach (var s in snapshot)
            {
                if (s.Dead) continue;
                WindowCapture fresh;
                try { fresh = new WindowCapture(_device, s.Hwnd, value); }
                catch
                {
                    // 作り直せなければ、設定だけ変える（次に中身が変わったときに反映される）
                    s.Capture.CaptureCursor = value;
                    continue;
                }
                Hook(s, fresh);
                var old = s.Capture;
                s.Capture = fresh;
                StopCaptureInBackground(s.Label, old);
            }
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
        var source = new Source(identity, capture, isAuto)
        {
            CropTopDip = (float)Math.Max(0, cropTopDip),
            Hidden = hidden,
            Label = DescribeWindow(identity.Handle),
        };
        Hook(source, capture);
        lock (_device.SyncRoot)
        {
            // UI スレッドとポップアップの専用スレッドが同時に追加した場合は先勝ち。止めたあとには足さない
            if (!_disposed && _sources.TryAdd(identity.Handle, source))
            {
                _dirty = true;
                return;
            }
        }
        StopCaptureInBackground(source.Label, capture);
    }

    /// <summary>キャプチャのフレームと「閉じた」通知を、このソースにつなぐ。</summary>
    private void Hook(Source source, WindowCapture capture)
    {
        capture.FrameArrived += (surface, w, h) => OnFrame(source, surface, w, h);
        capture.Closed += () =>
        {
            // 閉じたら即座に描画対象から外す（HWND の使い回しで古い絵や別のウィンドウが映らないように）
            if (source.Dead) return;
            source.Dead = true;
            _dirty = true;
            if (!source.IsAuto) SourceClosed?.Invoke(source.Hwnd);
        };
    }

    /// <summary>キャプチャ対象を外す（UI スレッド・描画スレッド）。すぐ描かれなくなり、キャプチャの停止は別のスレッドで行う。</summary>
    public void RemoveWindow(IntPtr hwnd)
    {
        Source? source;
        lock (_device.SyncRoot)
        {
            if (!_sources.Remove(hwnd, out source)) return;
            // 外したソースには、フレームが届いても写さない（OnFrame が _sources を確かめる）ので、絵はここで捨ててよい
            source.DisposeResources();
        }
        _dirty = true;
        // フレームハンドラがロック待ちの可能性があるので、キャプチャ停止はロックの外で。
        // 閉じる呼び出しが戻らない環境とわかったら、メニュー等のセッションは閉じずに持っておく
        StopCaptureInBackground(source.Label, source.Capture, keepSession: source.IsAuto && _closeHangs);
    }

    // --- キャプチャの停止（1 件ずつ使い捨てのスレッドで） ---
    // 消えたウィンドウ（閉じたメニューなど）のキャプチャを止める呼び出しが、リモート デスクトップの中では戻ってこないことがある。
    // 呼んだスレッド（描画・UI・ポップアップの専用スレッド）ごと止まらないよう、1 件ずつ別のスレッドで止め、戻ってこないものは置いていく
    private sealed class StopJob(string label, WindowCapture capture)
    {
        public string Label { get; } = label;
        public WindowCapture Capture { get; } = capture;
        public long Start { get; } = Stopwatch.GetTimestamp();
        /// <summary>「終わりません」をもうログに出したか。</summary>
        public bool Reported;
    }
    // 終わっていない停止（作り直した合成器の分も含めて、プロセス全体で数える）
    private static readonly ConcurrentDictionary<StopJob, byte> _stops = new();
    // 終わっていない停止がこれだけたまったら、新しいポップアップの取り込みを始めない（止まったスレッドを増やし続けないように。起動し直すまで）
    private const int MaxPendingStops = 20;
    private static int _popupsSuspendedLogged;

    // 一度でもセッションを閉じる呼び出しが戻らなかったか（この起動の間ずっと）。そうなったら、メニュー等は開け閉めのたびに
    // 止まるので、以後メニュー等のセッションは閉じずに持っておく（フレーム置き場は返す）。手放すと後片付けの係が閉じようとして止まる
    private static volatile bool _closeHangs;
    private static readonly ConcurrentQueue<object> _keptSessions = new();
    // 持っておくセッションがこれだけたまったら、メニュー等の新しい取り込みをやめる（起動し直すまで）
    private const int MaxKeptSessions = 500;

    /// <summary>メニュー等の新しい取り込みを、もうやめているか（停止がたまりすぎた）。</summary>
    private static bool PopupsSuspended => _stops.Count >= MaxPendingStops || _keptSessions.Count >= MaxKeptSessions;

    // 終了時に「終わっていない」と数える、止め始めてからの長さ（止め始めたばかりのものは数えない）
    private static readonly TimeSpan PendingStopAge = TimeSpan.FromSeconds(2);

    /// <summary>終わっていない停止と、閉じずに持っているセッションの件数（終了時のログ用）。どちらも無ければ null。</summary>
    public static string? DescribePendingStops()
    {
        var jobs = _stops.Keys.Where(j => Stopwatch.GetElapsedTime(j.Start) >= PendingStopAge).ToList();
        int kept = _keptSessions.Count;
        if (jobs.Count == 0 && kept == 0) return null;
        var windows = jobs.GroupBy(j => j.Label).Select(g => g.Count() == 1 ? g.Key : $"{g.Key}×{g.Count()}");
        string oldest = jobs.Count == 0 ? "" : $"、いちばん古いもので {jobs.Max(j => Stopwatch.GetElapsedTime(j.Start)).TotalSeconds:F0} 秒";
        return $"終わっていない取り込みの停止 {jobs.Count} 件（{string.Join("、", windows)}{oldest}）、閉じずに持っていたセッション {kept} 件";
    }

    private void StopCaptureInBackground(string label, WindowCapture capture, bool keepSession = false)
    {
        if (keepSession)
        {
            // セッションは閉じないので戻ってくる（フレーム置き場を閉じるだけ）。それでも呼んだスレッドは待たせない
            new Thread(() =>
            {
                try { _keptSessions.Enqueue(capture.DisposeKeepingSession()); }
                catch (Exception ex) { Log.Error($"取り込みの停止に失敗（窓 {label}）", ex); }
            }) { IsBackground = true, Name = "Capture stop" }.Start();
            return;
        }
        var job = new StopJob(label, capture);
        _stops[job] = 0;
        new Thread(() =>
        {
            try { capture.Dispose(); }
            catch (Exception ex) { Log.Error($"取り込みの停止に失敗（窓 {label}）", ex); }
            _stops.TryRemove(job, out _);
            var took = Stopwatch.GetElapsedTime(job.Start);
            if (took >= SlowStage) Log.Error($"取り込みの停止に {took.TotalSeconds:F1} 秒かかりました（窓 {label}）");
        }) { IsBackground = true, Name = "Capture stop" }.Start();
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

    /// <summary>
    /// 子として動くとき、合成した絵（BGRA の画素・幅・高さ）を渡す先。null なら渡さない。
    /// 絵が変わったときだけ、1 秒あたり 12 コマまで、描画スレッドから呼ぶ。
    /// </summary>
    /// 渡す先を付けたときは、絵が変わっていなくても次のコマで 1 枚渡す（つないだ直後の親に、すぐ絵が届くように）。
    /// 渡す先が無い間は、絵を取り出さない（GPU から読み戻す手間をかけない）。
    public Action<byte[], int, int>? FrameSink
    {
        get => _frameSink;
        set { _frameSink = value; _dirty = true; }
    }
    private volatile Action<byte[], int, int>? _frameSink;

    private volatile bool _paused;

    /// <summary>一時停止中か（true の間は背景だけを描く。選んだウィンドウやキャプチャはそのまま続ける）。</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; _dirty = true; }
    }

    /// <summary>親として子の画面を共有しているか（true の間は子の絵だけを描く）。</summary>
    public bool RemoteMode
    {
        get => _remoteMode;
        set { _remoteMode = value; _dirty = true; }
    }

    // 子から届いた最新の絵（GPU へ載せるのは描画スレッドだけ。届いた側のスレッドは置いていくだけ）
    private readonly object _remoteGate = new();
    private (byte[] Pixels, int Width, int Height)? _remotePending;
    private bool _remoteClear;
    // 置いてある絵・GPU に載っている絵が、どの接続のものか（切れた接続の後始末で、新しい接続の絵を消さないように）
    private long _remotePendingTag, _remoteBitmapTag;
    // これまでに届いた一番新しい接続の番号（それより古い接続の絵が遅れて届いても受け取らない）
    private long _remoteLatestTag;
    private volatile bool _disposed;

    /// <summary>子から届いた絵を差し替える（tag は接続の番号、BGRA の画素。受け取る側のスレッドから呼ぶ）。GPU へは描画スレッドが載せる。</summary>
    public void SetRemoteFrame(long tag, byte[] pixels, int width, int height)
    {
        if (_disposed) return;
        lock (_remoteGate)
        {
            if (tag < _remoteLatestTag) return;
            _remoteLatestTag = tag;
            _remotePending = (pixels, width, height);
            _remotePendingTag = tag;
        }
        // 子の画面を共有していないときは描き直さない（R を選んだときに、最新の絵を載せる）
        if (_remoteMode) _dirty = true;
    }

    /// <summary>子の絵を捨てる（子が切れたとき）。tag の接続の絵だけ捨てる（0 なら全部）。</summary>
    public void ClearRemoteFrame(long tag)
    {
        lock (_remoteGate)
        {
            if (tag == 0 || _remotePendingTag == tag) _remotePending = null;
            if (tag == 0 || _remoteBitmapTag == tag) _remoteClear = true;
        }
        _dirty = true;
    }

    /// <summary>届いた子の絵を GPU へ載せる・捨てる（描画スレッドで、装置の鍵を持って呼ぶ）。</summary>
    private unsafe void ApplyRemoteFrame()
    {
        (byte[] Pixels, int Width, int Height)? next;
        lock (_remoteGate)
        {
            if (_remoteClear)
            {
                _remoteClear = false;
                _remoteBitmap?.Dispose();
                _remoteBitmap = null;
            }
            if (!_remoteMode) return;
            next = _remotePending;
            _remotePending = null;
            if (next is not null) _remoteBitmapTag = _remotePendingTag;
        }
        if (next is not { } f) return;
        fixed (byte* p = f.Pixels)
        {
            if (_remoteBitmap is null || _remoteBitmap.PixelSize.Width != f.Width || _remoteBitmap.PixelSize.Height != f.Height)
            {
                _remoteBitmap?.Dispose();
                _remoteBitmap = _context.CreateBitmap(new Vortice.Mathematics.SizeI(f.Width, f.Height), (IntPtr)p, (uint)(f.Width * 4),
                    new BitmapProperties1(new D2DPixelFormat(BufferFormat, D2DAlphaMode.Ignore), 96, 96, BitmapOptions.None));
            }
            else
            {
                _remoteBitmap.CopyFromMemory((IntPtr)p, (uint)(f.Width * 4));
            }
        }
    }

    /// <summary>
    /// 重なり順を今のまま止めておく。まとめて最前面では 1 つずつ手前へ出すので、途中の一瞬だけ共有中のウィンドウ同士の
    /// 重なりが変わる（終わると元の並びに戻る）。その途中の並びが相手に映ってちらつかないように、出している間だけ止める。
    /// 中身（各ウィンドウの絵）はその間も更新する。
    /// </summary>
    public bool HoldOrder
    {
        get => _holdOrder;
        set { _holdOrder = value; _dirty = true; }
    }

    private void RenderLoop()
    {
        var frameTime = TimeSpan.FromSeconds(1.0 / TargetFps);
        var sw = Stopwatch.StartNew();
        var lastRender = TimeSpan.Zero;
        var lastSink = TimeSpan.Zero;
        int consecutiveErrors = 0;
        while (_running)
        {
            var start = sw.Elapsed;
            Interlocked.Exchange(ref _beat, Stopwatch.GetTimestamp());
            try
            {
                // 各ウィンドウの最新フレームを受け取る（このときだけコピーするので、30 回/秒より多くは写さない）
                Enter(RenderStage.PullFrames);
                bool fresh = PullFrames();
                // 子の画面を共有している間は、この PC のウィンドウは描かないので調べない。
                // 重なり順を止めている間（まとめて最前面の最中）は、途中の並びを描かないよう前回の並びのまま描く
                Enter(RenderStage.DrawOrder);
                var order = _remoteMode ? [] : _holdOrder && _lastOrder.Count > 0 ? _lastOrder : GetDrawOrder();
                bool changed = fresh || _dirty || !SameOrder(order, _lastOrder);
                // 子として動いているときは、変わった絵を親へ渡す（間隔が来るまでは取っておいて、来たら渡す）
                var sink = FrameSink;
                if (sink is null) _sinkPending = false;
                else if (changed) _sinkPending = true;
                bool sinkDue = _sinkPending && start - lastSink >= FrameSinkInterval;
                // 中身も配置も設定も変わっていなければ描き直さない（GPU を休ませる）
                if (changed || sinkDue || start - lastRender >= IdleRedrawInterval)
                {
                    _dirty = false;
                    (byte[] Pixels, int Width, int Height)? captured = null;
                    Enter(RenderStage.WaitLock);
                    lock (_device.SyncRoot)
                    {
                        Enter(RenderStage.RemoteFrame);
                        ApplyRemoteFrame();
                        captured = RenderCore(order, readBack: sinkDue);
                    }
                    _lastOrder = order;
                    lastRender = start;
                    if (sinkDue && captured is { } c)
                    {
                        _sinkPending = false;
                        lastSink = start;
                        Enter(RenderStage.Sink);
                        try { sink!(c.Pixels, c.Width, c.Height); }
                        catch (Exception ex) { Log.Error("合成した絵を渡せませんでした", ex); }
                    }
                }
                consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                // 同じエラーの連発でログを埋めない
                if (consecutiveErrors++ == 0) Log.Error("描画エラー", ex);
            }
            Enter(RenderStage.Idle);

            if (_device.IsDeviceLost || SimulateDeviceLost(sw.Elapsed))
            {
                Log.Error($"GPU デバイスが失われました: {_device.D3DDevice.DeviceRemovedReason}");
                _running = false;
                if (!_disposed) DeviceLost?.Invoke();
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
        {
            Mark("Deliver", s.Hwnd);
            if (!s.Dead && s.Capture.DeliverPending()) any = true;
        }
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

    // デバッグ: SWS_SIMULATE_STALL_SEC=<秒>[,<止める秒>] で、プロセス内で 1 回だけ、Present の手前で描画を止める（既定 30 秒）
#if DEBUG
    private static int _simulatedStall;
    private static readonly Stopwatch SimulateClock = Stopwatch.StartNew();

    private static void SimulateStall()
    {
        var parts = (Environment.GetEnvironmentVariable("SWS_SIMULATE_STALL_SEC") ?? "").Split(',');
        if (!int.TryParse(parts[0], out int sec) || SimulateClock.Elapsed.TotalSeconds < sec) return;
        if (Interlocked.Exchange(ref _simulatedStall, 1) != 0) return;
        int hold = parts.Length > 1 && int.TryParse(parts[1], out int h) ? h : 30;
        Log.Info($"デバッグ: 描画を {hold} 秒止めます");
        Thread.Sleep(TimeSpan.FromSeconds(hold));
    }
#endif

    /// <summary>
    /// 描画対象（選択ウィンドウ＋そのポップアップ）を「奥 → 手前」の順で、実矩形付きで返す。
    /// ついでにポップアップのキャプチャを追加・削除し、閉じた／使い回されたウィンドウを外す。
    /// </summary>
    private List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> GetDrawOrder()
    {
        List<Source> snapshot;
        lock (_device.SyncRoot) snapshot = [.. _sources.Values];
        // 何も共有していなければ、ウィンドウを調べるまでもない（描くのは背景だけ）
        if (snapshot.Count == 0) return [];

        // 本人確認: 閉じた（Closed 済み）か、HWND が別のプロセス・スレッドに使い回されたものは使わない
        var manual = new HashSet<IntPtr>();
        var cropDip = new Dictionary<IntPtr, float>();
        var auto = new HashSet<IntPtr>();
        var deadAuto = new List<IntPtr>();
        foreach (var s in snapshot)
        {
            Mark("IsAlive", s.Hwnd);
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
        Mark("Shell");
        var context = new PopupContext(manual, ShellProcessId());
        foreach (var h in manual)
        {
            Mark("Selected", h);
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
            Mark("Visible", hWnd);
            if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd)) return true;
            bool isManual = manual.Contains(hWnd);
            if (!isManual && includePopups) Mark("IsPopup", hWnd);
            bool isPopup = !isManual && includePopups && IsPopupOfSelected(hWnd, context);
            if (!isManual && !isPopup) return true;
            // キャプチャを禁止しているポップアップは、黒い四角で下を隠すより描かない方がまし
            Mark("Affinity", hWnd);
            if (isPopup && Native.BlocksCapture(hWnd)) return true;
            // 別の仮想デスクトップなど、自分の画面に出ていないものは相手にも見せない
            Mark("Cloaked", hWnd);
            if (Native.IsCloaked(hWnd)) return true;
            Mark("Bounds", hWnd);
            if (!Native.TryGetFrameBounds(hWnd, out var rc) || rc.Width <= 0 || rc.Height <= 0) return true;
            // クロップで隠している部分（タブ・アドレスバー）から出てきたポップアップは映さない。
            // タブのホバーカード（別のタブの名前と縮小画像）やアドレスバーの入力候補（閲覧履歴）が漏れないように
            if (isPopup && StartsInHiddenBand(rc, context.HiddenBands)) return true;
            if (isPopup) wantedPopups.Add(hWnd);
            Mark("Dpi", hWnd);
            int cropPx = isManual && cropDip.TryGetValue(hWnd, out float dip) ? CropPx(hWnd, dip, rc.Height) : 0;
            order.Add((hWnd, rc, cropPx));
            return true;
        }, IntPtr.Zero);
        order.Reverse();

        // 消えたポップアップを外す
        Mark("Popups");
        foreach (var h in auto)
            if (!wantedPopups.Contains(h)) RemoveWindow(h);

        // 新しく出たポップアップのキャプチャを始める（最初のフレームが来るまでは描かれない）
        bool suspended = PopupsSuspended;
        if (suspended && wantedPopups.Count > 0 && Interlocked.Exchange(ref _popupsSuspendedLogged, 1) == 0)
            Log.Error($"取り込みの停止が {_stops.Count} 件終わらず、閉じずに持っているセッションが {_keptSessions.Count} 件あるので、"
                + "メニュー等の新しい取り込みをやめます（起動し直すと戻ります）");
        lock (_popupGate)
        {
            _failedPopups.IntersectWith(wantedPopups);
            foreach (var h in suspended ? [] : wantedPopups)
            {
                if (auto.Contains(h) || _failedPopups.Contains(h) || !_popupStarting.Add(h)) continue;
                var identity = WindowIdentity.Of(h);
                if (!EnqueuePopupJob("開始", h, () => StartPopup(identity))) _popupStarting.Remove(h);
            }
        }
        return order;
    }

    // --- ポップアップ（選択アプリのメニュー・ダイアログ等）のキャプチャの開始と停止 ---
    // WGC のキャプチャを始める呼び出しは、まれに長く待たされることがある。描画スレッドで待つと共有の映像ごと止まるので、
    // ポップアップの分はこの専用スレッドで順に行う（待たされても、そのポップアップが映るのが遅れるだけで、描画は続く）。
    // 止める方は、戻ってこないことがあるので 1 件ずつ別のスレッドで行う（StopCaptureInBackground）
    private readonly BlockingCollection<(string What, nint Hwnd, Action Run)> _popupJobs = new();
    private readonly Thread _popupThread;
    private readonly object _popupGate = new();
    // 開始を頼んで、まだ終わっていないポップアップ（同じものを何度も頼まない）。_popupGate で守る
    private readonly HashSet<IntPtr> _popupStarting = [];
    // ポップアップの専用スレッドが今している仕事と、そのウィンドウ・始めた時刻（止まったままのときにログに出す）
    private volatile string? _popupJobWhat;
    private volatile nint _popupJobHwnd;
    private long _popupJobStart;

    /// <summary>ポップアップの専用スレッドに仕事を頼む。止めたあとなら頼まずに false。</summary>
    private bool EnqueuePopupJob(string what, nint hwnd, Action run)
    {
        try { return _popupJobs.TryAdd((what, hwnd, run)); }
        catch (InvalidOperationException) { return false; } // 止めたあと（CompleteAdding 済み）
    }

    private void PopupLoop()
    {
        foreach (var job in _popupJobs.GetConsumingEnumerable())
        {
            _popupJobHwnd = job.Hwnd;
            Interlocked.Exchange(ref _popupJobStart, Stopwatch.GetTimestamp());
            _popupJobReported = false;
            _popupJobWhat = job.What;
            try { job.Run(); }
            catch (Exception ex) { Log.Error($"メニュー等の取り込みの{job.What}に失敗", ex); }
            _popupJobWhat = null;
            var took = Stopwatch.GetElapsedTime(Interlocked.Read(ref _popupJobStart));
            if (took >= SlowStage)
                Log.Error($"メニュー等の取り込みの{job.What}に {took.TotalSeconds:F1} 秒かかりました（窓 {DescribeWindow(job.Hwnd)}）");
        }
    }

    /// <summary>ポップアップの専用スレッドの今の仕事の説明（ログ用。していなければ null）。</summary>
    private string? DescribePopupJob()
    {
        var what = _popupJobWhat;
        if (what is null) return null;
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _popupJobStart));
        return $"メニュー等の取り込みの{what}が {elapsed.TotalSeconds:F0} 秒終わりません（窓 {DescribeWindow(_popupJobHwnd)}）";
    }

    /// <summary>
    /// キャプチャの開始・停止のうち、atLeast 以上終わっていないものの説明（ログ用）。同じものは 1 回だけ返す。
    /// 描画は止まらないが、メニュー等が映らない・止まらない手がかりとして残す。
    /// </summary>
    public List<string> TakeSlowCaptureReports(TimeSpan atLeast)
    {
        var reports = new List<string>();
        if (!_popupJobReported && _popupJobWhat is not null
            && Stopwatch.GetElapsedTime(Interlocked.Read(ref _popupJobStart)) >= atLeast
            && DescribePopupJob() is { } popup)
        {
            _popupJobReported = true;
            reports.Add(popup);
        }
        foreach (var job in _stops.Keys)
        {
            var elapsed = Stopwatch.GetElapsedTime(job.Start);
            if (job.Reported || elapsed < atLeast) continue;
            job.Reported = true;
            if (!_closeHangs)
            {
                _closeHangs = true;
                reports.Add("取り込みのセッションを閉じる呼び出しが戻らないので、この起動の間、メニュー等のセッションは閉じずに持っておきます");
            }
            reports.Add($"取り込みの停止が {elapsed.TotalSeconds:F0} 秒終わりません（窓 {job.Label}、{job.Capture.DescribeSteps()}、終わっていない停止 {_stops.Count} 件）");
        }
        return reports;
    }

    // 今のポップアップの仕事の「終わりません」をもうログに出したか
    private volatile bool _popupJobReported;

    /// <summary>ポップアップのキャプチャを始める（専用スレッド）。失敗したら、消えるまで再試行しない。</summary>
    private void StartPopup(WindowIdentity identity)
    {
#if DEBUG
        // デバッグ: SWS_SIMULATE_POPUP_DELAY_SEC=<秒> で、ポップアップのキャプチャの開始を毎回その秒数だけ待たせる
        if (int.TryParse(Environment.GetEnvironmentVariable("SWS_SIMULATE_POPUP_DELAY_SEC"), out int delay))
            Thread.Sleep(TimeSpan.FromSeconds(delay));
#endif
        try
        {
            // 順番を待つ間に消えたメニュー等は、もう始めない（すぐ止めることになり、止める処理を増やすだけなので）
            if (!_disposed && identity.IsAlive() && Native.IsWindowVisible(identity.Handle))
                AddWindowCore(identity, isAuto: true);
        }
        catch { lock (_popupGate) _failedPopups.Add(identity.Handle); }
        finally { lock (_popupGate) _popupStarting.Remove(identity.Handle); }
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
        Mark("Title", hWnd);
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
    /// <summary>描く。readBack なら、出す前の絵を取り出して返す（子として親へ送るため）。</summary>
    private (byte[] Pixels, int Width, int Height)? RenderCore(List<(IntPtr Hwnd, Native.RECT Bounds, int CropPx)> order, bool readBack = false)
    {
        if (_target is null) return null;
        var targetSize = _target.PixelSize;
        int cw = Math.Max(_canvas.Width, 1), ch = Math.Max(_canvas.Height, 1);

        Enter(RenderStage.Draw);
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
        if (_remoteMode && !_paused)
        {
            // 子の画面を共有中: 子から届いた絵をキャンバスいっぱいに描く（届くまでは塗りつぶしのまま）
            if (_remoteBitmap is not null)
            {
                var rs = _remoteBitmap.PixelSize;
                _context.DrawBitmap(_remoteBitmap, canvasRect, 1f, InterpolationMode.Linear, new Rect(0, 0, rs.Width, rs.Height), null);
            }
        }
        else if (_background is not null)
        {
            // キャンバスを覆うように拡大（はみ出す分は中央で切る）
            var bs = _background.PixelSize;
            float fill = Math.Max((float)cw / bs.Width, (float)ch / bs.Height);
            float bw = bs.Width * fill, bh = bs.Height * fill;
            _context.DrawBitmap(_background, new Rect((cw - bw) / 2f, (ch - bh) / 2f, bw, bh), 1f,
                InterpolationMode.HighQualityCubic, new Rect(0, 0, bs.Width, bs.Height), null);
        }

        // 一時停止中は、ウィンドウも子の絵も描かない（背景だけ）
        foreach (var (hwnd, bounds, cropPx) in _remoteMode || _paused ? [] : order)
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
        // 出す（Present）とバックバッファの中身は保証されないので、その前に取り出す
        if (readBack) Enter(RenderStage.ReadBack);
        var captured = readBack ? ReadBackBuffer() : null;
#if DEBUG
        // デバッグ: 共有映像を画像に書き出す（リリース版には入れない）
        if (Environment.GetEnvironmentVariable("SWS_DUMP") is { } dumpPath && Environment.TickCount64 % 1000 < 40)
            DumpBackBuffer(dumpPath);
#endif
        Enter(RenderStage.Present);
#if DEBUG
        SimulateStall();
#endif
        _swapChain.Present(0, PresentFlags.None);
        return captured;
    }

    /// <summary>今のバックバッファの絵を BGRA の画素で取り出す（行の詰め物は除く）。</summary>
    private (byte[] Pixels, int Width, int Height)? ReadBackBuffer()
    {
        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = back.Description;
        if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
        {
            _staging?.Dispose();
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            _staging = _device.D3DDevice.CreateTexture2D(desc);
        }
        _device.D3DContext.CopyResource(_staging, back);
        var map = _device.D3DContext.Map(_staging, 0, MapMode.Read);
        try
        {
            int width = (int)desc.Width, height = (int)desc.Height, row = width * 4;
            var pixels = new byte[row * height];
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(map.DataPointer + (nint)(y * (long)map.RowPitch), pixels, y * row, row);
            return (pixels, width, height);
        }
        finally
        {
            _device.D3DContext.Unmap(_staging, 0);
        }
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

    public void Dispose() => Close(null);

    /// <summary>
    /// 止めて後始末をし、then を呼ぶ。描画スレッドは <see cref="CloseWait"/> まで待つ。
    /// それでも戻らなければ（描画が止まったまま）待たずに false を返し、後始末と then は、描画スレッドが戻ったときに
    /// 別のスレッドで行う。その間、この合成器の描画先（子 HWND）は使えない。
    /// </summary>
    public bool Close(Action? then)
    {
        _disposed = true;
        _running = false;
        // 戻ってきた古い描画から、作り直したあとの UI へ知らせが行かないように
        SourceClosed = null;
        DeviceLost = null;
        if (_renderThread.Join(CloseWait))
        {
            ReleaseAndThen(then);
            return true;
        }
        Log.Error($"描画が止まったままです（{StageText}、{StalledFor.TotalSeconds:F0} 秒）。待たずに進みます");
        new Thread(() =>
        {
            _renderThread.Join();
            Log.Info("止まっていた描画が戻ったので、後始末をします");
            ReleaseAndThen(then);
        }) { IsBackground = true, Name = "Compositor cleanup" }.Start();
        return false;
    }

    private void ReleaseAndThen(Action? then)
    {
        try { Release(); }
        catch (Exception ex) { Log.Error("合成器の破棄に失敗", ex); }
        then?.Invoke();
    }

    private void Release()
    {
        // ポップアップの専用スレッドには、頼んである分を済ませて終わってもらう（待たされていたら、待たずに進む）
        _popupJobs.CompleteAdding();
        if (!_popupThread.Join(PopupCloseWait))
            Log.Error(DescribePopupJob() ?? "メニュー等の取り込みの専用スレッドが終わりません。待たずに進みます");
        RemoveAll();
        lock (_device.SyncRoot)
        {
            _context.Target = null;
            _target?.Dispose();
            _canvasBrush.Dispose();
            _background?.Dispose();
            _remoteBitmap?.Dispose();
            _staging?.Dispose();
            _context.Dispose();
            _swapChain.Dispose();
        }
    }
}
