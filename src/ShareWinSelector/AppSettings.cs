using System.IO;
using System.Text.Json;

namespace ShareWinSelector;

/// <summary>ウィンドウの位置とサイズ（WPF の DIP）。</summary>
internal sealed record WindowBounds(double Left, double Top, double Width, double Height);

/// <summary>出力ウィンドウの位置（画面内での DIP）・中身の横幅（物理ピクセル）・最大化していたか。</summary>
internal sealed record OutputWindowState(double Left, double Top, int ClientWidth, bool Maximized);

/// <summary>%LOCALAPPDATA%\ShareWinSelector\settings.json に保存する設定。</summary>
internal sealed class AppSettings
{
    /// <summary>表示言語（"ja" / "en"、null は OS に合わせる）。</summary>
    public string? Language { get; set; }

    /// <summary>メイン画面を常に手前に表示。</summary>
    public bool Topmost { get; set; }

    /// <summary>メイン画面をコンパクト表示にする。</summary>
    public bool Compact { get; set; }

    /// <summary>ウィンドウ一覧をサムネイルで表示する（false は一覧）。</summary>
    public bool ThumbnailView { get; set; } = true;

    /// <summary>一覧には共有するモニター上のウィンドウだけを出す。</summary>
    public bool MonitorOnly { get; set; } = true;

    /// <summary>出力ウィンドウの配置（"Back" / "Normal" / "OffScreen"）。</summary>
    public string Placement { get; set; } = "Back";

    public bool ShowOverlay { get; set; } = true;
    public bool CaptureCursor { get; set; } = true;
    public bool IncludePopups { get; set; } = true;

    /// <summary>ボタンなどにカーソルを乗せたときの説明（ツールチップ）を出す。出すときはすぐ出す。</summary>
    public bool ShowTooltips { get; set; } = true;

    /// <summary>相手に見える画面の、ウィンドウが無い部分の背景（"Solid" / "Default" / "Custom"）。</summary>
    public string Background { get; set; } = "Default";

    /// <summary>Background が "Custom" のときの画像ファイル。</summary>
    public string? BackgroundPath { get; set; }

    /// <summary>Background が "Solid" のときの色（"#RRGGBB"、null は黒）。</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>「上をクロップ」全体のオン・オフ。オンのとき、未保存のアプリは調整してから共有する。</summary>
    public bool CropMode { get; set; }

    /// <summary>選んだウィンドウをすぐ見せず、オーバーレイで確認してから共有する（ねこ事故防止）。</summary>
    public bool ConfirmBeforeShare { get; set; }

    /// <summary>
    /// アプリごとの「上をクロップ」する量（ウィンドウ上端からの DIP。モニターの DPI が変わっても見た目の高さが同じ）。
    /// キーは AppIdentity.GetKey。ここにあるアプリは、共有に選んだ時点で自動的にクロップされる。
    /// </summary>
    public Dictionary<string, double> CropTop { get; set; } = [];

    /// <summary>
    /// アプリごとの「クロップを効かせるか」（行のチェックを切り替えたもの）。ここに無いアプリは CropMode に従う。
    /// 共有をやめても・アプリを終了しても保持する。キーは CropTop と同じ。
    /// </summary>
    public Dictionary<string, bool> CropUse { get; set; } = [];

    /// <summary>共有するモニターのデバイス名（null はメインモニター）。</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>リモート連携を使う（オンなら起動したときも前回の役割で動き始める。オフなら待ち受けもつなぎに行くこともしない）。</summary>
    public bool RemoteEnabled { get; set; }

    /// <summary>リモート連携の役割（"Parent" / "Child"）。親は会議で共有する PC、子は画面を親へ送る PC（RDP でつないでいる会社のノートなど）。</summary>
    public string RemoteRole { get; set; } = "Parent";

    /// <summary>リモート連携で使うポート（親が待ち受け、子がつなぎに行く）。</summary>
    public int RemotePort { get; set; } = DefaultRemotePort;

    public const int DefaultRemotePort = 47810;

    /// <summary>子がつなぎに行く親の IP アドレス（見つけて選んだもの、または直接入れたもの）。</summary>
    public string? RemoteParentAddress { get; set; }

    /// <summary>通常表示・コンパクト表示それぞれの位置とサイズ（null は既定）。</summary>
    public WindowBounds? NormalBounds { get; set; }
    public WindowBounds? CompactBounds { get; set; }

    /// <summary>出力ウィンドウの位置と大きさ（null は既定）。</summary>
    public OutputWindowState? OutputWindow { get; set; }

    /// <summary>
    /// 開発用の別名（Debug 版だけ、環境変数 SWS_PROFILE）。付けると多重起動の判定と設定フォルダが別になり、
    /// 1 台の PC でリモート連携の親と子を同時に動かして確かめられる。リリース版では常に null。
    /// </summary>
    public static string? Profile { get; } =
#if DEBUG
        Environment.GetEnvironmentVariable("SWS_PROFILE") is { Length: > 0 } p ? p : null;
#else
        null;
#endif

    /// <summary>設定とログを置くフォルダ（%LOCALAPPDATA%\ShareWinSelector）。アプリが書き込むのはここだけ。</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Profile is null ? "ShareWinSelector" : "ShareWinSelector-" + Profile);

    private static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    /// <summary>「設定を削除して終了」のあと、終了処理中の保存でファイルを作り直さないように true にする。</summary>
    public static bool SavingDisabled { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()).Normalize();
        }
        catch (Exception ex)
        {
            Log.Error("設定の読み込みに失敗", ex);
        }
        return new AppSettings();
    }

    /// <summary>手で書き換えられて null になった項目などを既定値に戻す。</summary>
    private AppSettings Normalize()
    {
        CropTop ??= [];
        CropUse ??= [];
        Placement ??= "Back";
        Background ??= "Default";
        if (RemoteRole is not ("Parent" or "Child")) RemoteRole = "Parent";
        if (RemotePort is <= 0 or > 65535) RemotePort = DefaultRemotePort;
        // 1.0 の「黒」は「単色（色を選んでいなければ黒）」になった
        if (Background == "Black") Background = "Solid";
        return this;
    }

    public void Save()
    {
        if (SavingDisabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 書きかけで落ちても壊れないよう、一時ファイルに書いてから置き換える
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("設定の保存に失敗", ex);
        }
    }
}
