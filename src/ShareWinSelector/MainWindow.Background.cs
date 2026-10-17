using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareWinSelector;

/// <summary>相手に見える画面の背景（単色 / 標準画像 / 好きな画像）。</summary>
public partial class MainWindow
{
    private const string BackgroundSolid = "Solid", BackgroundDefault = "Default", BackgroundCustom = "Custom";
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
            new(BackgroundSolid, "Background_Solid"),
            new(BackgroundDefault, "Background_Default"),
            new(BackgroundCustom, "Background_Custom"),
        };
        BackgroundCombo.ItemsSource = options;
        BackgroundCombo.SelectedItem = options.FirstOrDefault(o => o.Key == App.Settings.Background) ?? options[1];
        UpdateBackgroundTooltip();
        UpdateSolidColorRow(SolidBackgroundColor);
        _suppressBackgroundHandler = false;
    }

    /// <summary>単色の背景の色（選んでいなければ黒）。</summary>
    private static Color SolidBackgroundColor =>
        Controls.HsvColorPicker.ParseHex(App.Settings.BackgroundColor) ?? Colors.Black;

    /// <summary>[色を選ぶ] ■ #RRGGBB の行。「単色」以外では押せないようにして、色も薄く見せる。</summary>
    private void UpdateSolidColorRow(Color color)
    {
        bool solid = App.Settings.Background == BackgroundSolid;
        PickColorButton.IsEnabled = solid;
        SolidColorSwatch.Background = new SolidColorBrush(color);
        SolidColorText.Text = Controls.HsvColorPicker.ToHex(color);
        SolidColorSwatch.Opacity = SolidColorText.Opacity = solid ? 1 : 0.4;
        if (!solid) ColorPopup.IsOpen = false;
    }

    /// <summary>色選びをつなぐ。ドラッグ中はその場で相手の画面（とプレビュー）に反映し、離したら保存する。</summary>
    private void InitializeColorPicker()
    {
        BackgroundColorPicker.ColorChanged += c =>
        {
            _compositor?.SetCanvasColor(c.R, c.G, c.B);
            UpdateSolidColorRow(c);
        };
        BackgroundColorPicker.ColorCommitted += c =>
        {
            App.Settings.BackgroundColor = Controls.HsvColorPicker.ToHex(c);
            App.Settings.Save();
            UpdateSolidColorRow(c);
        };
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        BackgroundColorPicker.Color = SolidBackgroundColor;
        ColorPopup.IsOpen = true;
    }

    /// <summary>ポップアップは別ウィンドウなので、メイン画面と同じく画面共有に映らないようにする。</summary>
    private void ColorPopup_Opened(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(ColorPopup.Child) is System.Windows.Interop.HwndSource source)
            Interop.Native.ExcludeFromCapture(source.Handle);
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
        UpdateSolidColorRow(SolidBackgroundColor);
    }

    private void UpdateBackgroundTooltip() =>
        BackgroundCombo.ToolTip = App.Settings.Background == BackgroundCustom ? App.Settings.BackgroundPath : null;

    /// <summary>設定どおりの背景を合成器に渡す。画像が読めなければ標準に戻す。</summary>
    private void ApplyBackground()
    {
        if (_compositor is null) return;
        var s = App.Settings;
        // 画像の下地（画像なしのときはこの色がそのまま見える）
        var solid = s.Background == BackgroundSolid ? SolidBackgroundColor : Colors.Black;
        _compositor.SetCanvasColor(solid.R, solid.G, solid.B);
        if (s.Background == BackgroundSolid)
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
