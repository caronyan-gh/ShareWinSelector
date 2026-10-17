using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareWinSelector;

/// <summary>相手に見える画面の背景（黒 / 標準画像 / 好きな画像）。</summary>
public partial class MainWindow
{
    private const string BackgroundBlack = "Black", BackgroundDefault = "Default", BackgroundCustom = "Custom";
    private const int MaxBackgroundWidth = 3840; // 4K を超える画像は縮めて GPU メモリを節約

    private static readonly Uri DefaultBackgroundUri = new("pack://application:,,,/Assets/background.jpg");

    private sealed record BackgroundOption(string Key, string LocKey)
    {
        public override string ToString() => Loc.T(LocKey);
    }

    private bool _suppressBackgroundHandler;

    private void PopulateBackgrounds()
    {
        _suppressBackgroundHandler = true;
        var options = new List<BackgroundOption>
        {
            new(BackgroundBlack, "Background_Black"),
            new(BackgroundDefault, "Background_Default"),
            new(BackgroundCustom, "Background_Custom"),
        };
        BackgroundCombo.ItemsSource = options;
        BackgroundCombo.SelectedItem = options.FirstOrDefault(o => o.Key == App.Settings.Background) ?? options[1];
        UpdateBackgroundTooltip();
        _suppressBackgroundHandler = false;
    }

    private void BackgroundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBackgroundHandler || BackgroundCombo.SelectedItem is not BackgroundOption option) return;

        if (option.Key == BackgroundCustom)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Background_DialogTitle"),
                Filter = Loc.T("Background_DialogFilter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp",
            };
            if (App.Settings.BackgroundPath is { } last && File.Exists(last))
                dialog.InitialDirectory = Path.GetDirectoryName(last);
            if (dialog.ShowDialog(this) != true)
            {
                // キャンセルされたら元の選択に戻す
                PopulateBackgrounds();
                return;
            }
            App.Settings.BackgroundPath = dialog.FileName;
        }
        App.Settings.Background = option.Key;
        App.Settings.Save();
        ApplyBackground();
        UpdateBackgroundTooltip();
    }

    private void UpdateBackgroundTooltip() =>
        BackgroundCombo.ToolTip = App.Settings.Background == BackgroundCustom ? App.Settings.BackgroundPath : null;

    /// <summary>設定どおりの背景を合成器に渡す。画像が読めなければ標準に戻す。</summary>
    private void ApplyBackground()
    {
        if (_compositor is null) return;
        var s = App.Settings;
        if (s.Background == BackgroundBlack)
        {
            _compositor.SetBackground(null, 0, 0);
            return;
        }

        Uri uri = DefaultBackgroundUri;
        if (s.Background == BackgroundCustom)
        {
            // 設定ファイルが手で書き換えられて変なパスになっていても落ちないように
            if (s.BackgroundPath is not { } path || !Uri.TryCreate(path, UriKind.Absolute, out var custom) || !custom.IsFile)
            {
                FallBackToDefaultBackground(s.BackgroundPath ?? "", null);
                return;
            }
            uri = custom;
        }
        try
        {
            var (pixels, w, h) = LoadPixels(uri);
            _compositor.SetBackground(pixels, w, h);
        }
        catch (Exception ex)
        {
            if (uri == DefaultBackgroundUri)
            {
                Log.Error($"標準の背景画像の読み込みに失敗: {uri}", ex);
                _compositor.SetBackground(null, 0, 0);
                return;
            }
            FallBackToDefaultBackground(uri.LocalPath, ex);
        }
    }

    /// <summary>好きな画像が使えなかったので標準の背景に戻す。</summary>
    private void FallBackToDefaultBackground(string path, Exception? ex)
    {
        Log.Error($"背景画像の読み込みに失敗: {path}", ex);
        StatusText.Text = Loc.F("Status_BackgroundFailed", Path.GetFileName(path));
        App.Settings.Background = BackgroundDefault;
        App.Settings.Save();
        PopulateBackgrounds();
        ApplyBackground();
    }

    /// <summary>画像を BGRA（乗算済みアルファ）の画素に展開する。大きすぎる画像は縮める。</summary>
    private static (byte[] Pixels, int Width, int Height) LoadPixels(Uri uri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.CacheOption = BitmapCacheOption.OnLoad; // ファイルをロックしない
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();

        BitmapSource source = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        if (source.PixelWidth > MaxBackgroundWidth)
        {
            double scale = (double)MaxBackgroundWidth / source.PixelWidth;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);
        return (pixels, w, h);
    }
}
