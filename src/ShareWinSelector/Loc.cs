using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace ShareWinSelector;

/// <summary>
/// UI 文字列の多言語化。Resources/Strings*.resx から引き、実行中の言語切り替えに対応する。
/// XAML からは <c>{Binding [Key], Source={x:Static local:Loc.Instance}}</c> で参照する。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly ResourceManager Resources =
        new("ShareWinSelector.Resources.Strings", typeof(Loc).Assembly);

    // 起動時の OS の表示言語（「自動」のときに使う）
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    /// <summary>選べる言語（コード → 表示名）。null は OS に合わせる。</summary>
    public static IReadOnlyList<(string? Code, string NativeName)> SupportedLanguages { get; } =
    [
        (null, ""), // 表示名は Language_Auto を現在の言語で出す
        ("ja", "日本語"),
        ("en", "English"),
    ];

    public CultureInfo Culture { get; private set; } = SystemCulture;

    /// <summary>現在の設定（null = 自動）。</summary>
    public string? LanguageCode { get; private set; }

    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public static string T(string key) => Instance[key];

    public static string F(string key, params object[] args) => string.Format(Instance.Culture, T(key), args);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>言語が変わった（コードで組み立てている文字列を更新する合図）。</summary>
    public event Action? LanguageChanged;

    public void SetLanguage(string? code)
    {
        LanguageCode = string.IsNullOrEmpty(code) ? null : code;
        try
        {
            Culture = LanguageCode is null ? SystemCulture : CultureInfo.GetCultureInfo(LanguageCode);
        }
        catch (CultureNotFoundException)
        {
            // 設定ファイルの言語コードが壊れていたら OS に合わせる
            LanguageCode = null;
            Culture = SystemCulture;
        }
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        // インデクサのバインドをすべて更新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }
}
