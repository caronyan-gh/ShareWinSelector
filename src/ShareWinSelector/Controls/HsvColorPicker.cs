using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShareWinSelector.Controls;

/// <summary>
/// 色の面（鮮やかさ × 明るさ）と色相のバーをドラッグして選ぶ色選び（お絵かきソフトによくあるもの）。
/// ドラッグ中は ColorChanged、離したら ColorCommitted を出す。
/// </summary>
internal sealed class HsvColorPicker : Grid
{
    private const double ThumbSize = 12;

    private readonly Border _svArea = new() { CornerRadius = new CornerRadius(4), ClipToBounds = true, Cursor = Cursors.Cross };
    private readonly Canvas _svLayer = new();
    private readonly Ellipse _svThumb = new() { Width = ThumbSize, Height = ThumbSize, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Border _hueBar = new() { CornerRadius = new CornerRadius(4), Cursor = Cursors.SizeNS };
    private readonly Canvas _hueLayer = new();
    private readonly Border _hueThumb = new()
    {
        Height = 6, BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
    };
    private readonly Border _swatch = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBlock _hexText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    // 色相 0〜360、鮮やかさ・明るさ 0〜1
    private double _hue, _saturation, _value;

    /// <summary>ドラッグ中、色が変わるたびに出る（その場でプレビューに反映する用）。</summary>
    public event Action<Color>? ColorChanged;

    /// <summary>ドラッグを離して色が決まったら出る（保存する用）。</summary>
    public event Action<Color>? ColorCommitted;

    public HsvColorPicker()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 色の面: 左が白 → 右が今の色相、上 → 下で黒くなる
        var whiteToClear = new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
        };
        var clearToBlack = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
        };
        var svGrid = new Grid();
        svGrid.Children.Add(whiteToClear);
        svGrid.Children.Add(clearToBlack);
        svGrid.Children.Add(_svLayer);
        _svLayer.Children.Add(_svThumb);
        _svThumb.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 };
        _svArea.Child = svGrid;
        SetColumn(_svArea, 0);
        Children.Add(_svArea);

        // 色相のバー（上から 赤 → 黄 → 緑 → 水色 → 青 → 紫 → 赤）
        var hueStops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) hueStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        _hueBar.Background = new LinearGradientBrush(hueStops, new Point(0, 0), new Point(0, 1));
        _hueBar.Child = _hueLayer;
        _hueLayer.Children.Add(_hueThumb);
        SetColumn(_hueBar, 2);
        Children.Add(_hueBar);

        var info = new StackPanel { Orientation = Orientation.Horizontal };
        info.Children.Add(_swatch);
        info.Children.Add(_hexText);
        SetRow(info, 2);
        SetColumnSpan(info, 3);
        Children.Add(info);

        HookDrag(_svArea, p =>
        {
            _saturation = Math.Clamp(p.X / Math.Max(1, _svArea.ActualWidth), 0, 1);
            _value = 1 - Math.Clamp(p.Y / Math.Max(1, _svArea.ActualHeight), 0, 1);
        });
        HookDrag(_hueBar, p => _hue = Math.Clamp(p.Y / Math.Max(1, _hueBar.ActualHeight), 0, 1) * 359.999);
        SizeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => UpdateVisuals();
    }

    /// <summary>今の色。コードから入れたときはイベントを出さない。</summary>
    public Color Color
    {
        get => FromHsv(_hue, _saturation, _value);
        set
        {
            (_hue, _saturation, _value) = ToHsv(value);
            UpdateVisuals();
        }
    }

    private void HookDrag(FrameworkElement area, Action<Point> apply)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            area.CaptureMouse();
            apply(e.GetPosition(area));
            Changed();
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            apply(e.GetPosition(area));
            Changed();
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            area.ReleaseMouseCapture();
            ColorCommitted?.Invoke(Color);
            e.Handled = true;
        };
    }

    private void Changed()
    {
        UpdateVisuals();
        ColorChanged?.Invoke(Color);
    }

    private void UpdateVisuals()
    {
        var color = Color;
        _svArea.Background = new SolidColorBrush(FromHsv(_hue, 1, 1));
        Canvas.SetLeft(_svThumb, _saturation * _svArea.ActualWidth - ThumbSize / 2);
        Canvas.SetTop(_svThumb, (1 - _value) * _svArea.ActualHeight - ThumbSize / 2);
        _hueThumb.Width = Math.Max(0, _hueBar.ActualWidth + 4);
        Canvas.SetLeft(_hueThumb, -2);
        Canvas.SetTop(_hueThumb, _hue / 360 * _hueBar.ActualHeight - _hueThumb.Height / 2);
        _swatch.Background = new SolidColorBrush(color);
        _swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        _hexText.Text = ToHex(color);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>"#RRGGBB" を色に。読めなければ null。</summary>
    public static Color? ParseHex(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#'
            || !int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return null;
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
