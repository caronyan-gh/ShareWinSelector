using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ShareWinSelector.Overlay;

/// <summary>
/// オーバーレイに描く 1 ウィンドウ分（座標はオーバーレイ内の DIP）。
/// CropTop は上から隠している高さ、Editing はクロップを調整中か。
/// </summary>
internal readonly record struct OverlayItem(Rect Bounds, IReadOnlyList<Rect> Occluders, double CropTop = 0, bool Editing = false, bool CanDrag = false);

/// <summary>強調枠と選択モードのハイライトを描く面。</summary>
internal sealed class OverlaySurface : FrameworkElement
{
    private const double BorderThickness = 3;

    // ブランドカラー: 共有中はシアン、選択モードのハイライトはブルー
    private static readonly Brush BorderBrush = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x00, 0xD8, 0xFF), Color.FromRgb(0x1E, 0x6B, 0xFF), 45));
    private static readonly Brush TagBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xD8, 0xFF)));
    private static readonly Brush TagTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x14, 0x21)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x6B, 0xFF)));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0xFF)), 3) { DashStyle = DashStyles.Dash });
    private static readonly Brush HoverTagBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0xFF)));
    private static readonly Brush SelectModeTint = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00)));
    // クロップで隠している範囲（自分にだけ見える）。下が見えて操作できるよう、ごく薄い細い斜線だけにする
    private static readonly Brush HiddenAreaBrush = Freeze(new DrawingBrush
    {
        TileMode = TileMode.Tile,
        Viewport = new Rect(0, 0, 18, 18),
        ViewportUnits = BrushMappingMode.Absolute,
        Drawing = new DrawingGroup
        {
            Children =
            {
                // タイル全体を覆う透明な四角（無いと線だけの大きさに縮んでしまう）
                new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 18, 18))),
                new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0xD8, 0xFF)), 1), new LineGeometry(new Point(0, 18), new Point(18, 0))),
            },
        },
    });
    private static readonly Brush HiddenTagBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x18, 0x30)));
    // 隠している範囲との境目
    private static readonly Pen CropBoundaryPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0xD8, 0xFF)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen CropHandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xD8, 0xFF)), 4));
    private static readonly Brush CropGripBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xD8, 0xFF)));
    private static readonly Brush GrabBandBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x02, 0x00, 0x00, 0x00)));
    private static readonly Brush EditTint = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)));

    private IReadOnlyList<OverlayItem> _items = [];
    private OverlayItem? _hover;
    private bool _hoverSelected;
    private Point _cursor;
    private bool _selectMode;
    private bool _showBorders = true;

    public void Update(IReadOnlyList<OverlayItem> items, bool showBorders, bool selectMode, OverlayItem? hover, bool hoverSelected, Point cursor)
    {
        _items = items;
        _showBorders = showBorders;
        _selectMode = selectMode;
        _hover = hover;
        _hoverSelected = hoverSelected;
        _cursor = cursor;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(RenderSize);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 選択モード中はうっすら暗くし、かつクリックを受けられるようにする（完全透明はクリックが抜ける）
        if (_selectMode) dc.DrawRectangle(SelectModeTint, null, full);

        // クロップ調整中は、対象ウィンドウ以外を暗くする（操作はできない＝調整に集中）
        if (_items.FirstOrDefault(i => i.Editing) is { Editing: true } editing)
        {
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(editing.Bounds));
            dc.DrawGeometry(EditTint, null, dim);
        }

        foreach (var item in _items)
        {
            // 手前の別ウィンドウに隠れている部分には描かない（調整中の対象は前に出しているので全部描く）
            dc.PushClip(item.Editing ? new RectangleGeometry(full) : CreateVisibleMask(full, item.Occluders));
            var visible = item.Bounds;
            double crop = Math.Min(item.CropTop, visible.Height);
            if (crop > 0)
            {
                // 隠している範囲: 自分にだけ斜線で見せる
                var hiddenArea = new Rect(visible.Left, visible.Top, visible.Width, crop);
                dc.DrawRectangle(HiddenAreaBrush, null, hiddenArea);
                dc.DrawLine(CropBoundaryPen, new Point(hiddenArea.Left, hiddenArea.Bottom), new Point(hiddenArea.Right, hiddenArea.Bottom));
                // ラベルは境目のすぐ上・右端に小さく（タブ名などの邪魔にならないように）
                DrawSmallTag(dc, Loc.T("Crop_HiddenLabel"), new Point(hiddenArea.Right - 6, hiddenArea.Bottom - 2), pixelsPerDip);
                visible = new Rect(visible.Left, visible.Top + crop, visible.Width, Math.Max(0, visible.Height - crop));
            }

            if (item.Editing && !item.CanDrag)
            {
                // 共有前の確認だけ: 枠を点線で囲み、まだ見せていないことを伝える
                dc.DrawRectangle(null, HoverPen, visible);
                DrawTag(dc, Loc.T("Review_Hint"), new Point(visible.Left + 8, visible.Top + 10), HoverTagBrush, Brushes.White, pixelsPerDip, above: false);
            }
            else if (item.Editing)
            {
                // ドラッグできる上辺（ハンドル）
                double y = item.Bounds.Top + crop;
                // 線の上下をつかみやすくする（完全透明だとクリックが下のウィンドウへ抜けるので、ほぼ透明で塗る）
                dc.DrawRectangle(GrabBandBrush, null, new Rect(item.Bounds.Left, y - 10, item.Bounds.Width, 20));
                dc.DrawLine(CropHandlePen, new Point(item.Bounds.Left, y), new Point(item.Bounds.Right, y));
                var grip = new Rect(item.Bounds.Left + item.Bounds.Width / 2 - 28, y - 7, 56, 14);
                dc.DrawRoundedRectangle(CropGripBrush, null, grip, 7, 7);
                DrawTag(dc, Loc.T("Crop_EditHint"), new Point(item.Bounds.Left + 8, y + 10), HoverTagBrush, Brushes.White, pixelsPerDip, above: false);
            }
            else if (_showBorders)
            {
                // 相手に見えている範囲だけを枠で囲む
                var outer = visible;
                outer.Inflate(BorderThickness, BorderThickness);
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(visible));
                dc.DrawGeometry(BorderBrush, null, ring);
                DrawTag(dc, Loc.T("Overlay_SharedTag"), new Point(outer.Left, outer.Top), TagBrush, TagTextBrush, pixelsPerDip, above: crop <= 0);
            }
            dc.Pop();
        }

        if (_hover is { } hover)
        {
            // 手前のウィンドウに隠れた部分には塗らない
            dc.PushClip(CreateVisibleMask(full, hover.Occluders));
            dc.DrawRectangle(HoverFill, HoverPen, hover.Bounds);
            dc.Pop();
            // 案内はカーソルの右下に出す（隠れた部分に置かれて見えなくならないように）
            DrawTag(dc, _hoverSelected ? Loc.T("Overlay_ClickToRemove") : Loc.T("Overlay_ClickToAdd"),
                new Point(_cursor.X + 18, _cursor.Y + 18), HoverTagBrush, Brushes.White, pixelsPerDip, above: false);
        }
    }

    private static Geometry CreateVisibleMask(Rect full, IReadOnlyList<Rect> occluders)
    {
        if (occluders.Count == 0) return new RectangleGeometry(full);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var r in occluders) group.Children.Add(new RectangleGeometry(r));
        return new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), group);
    }

    private static void DrawTag(DrawingContext dc, string text, Point origin, Brush background, Brush foreground, double pixelsPerDip, bool above)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, foreground, pixelsPerDip);
        var rect = new Rect(origin.X, above ? origin.Y - ft.Height - 4 : origin.Y, ft.Width + 12, ft.Height + 4);
        // 上に出せない（画面上端）ときは内側に置く
        if (rect.Top < 0) rect.Y = origin.Y;
        dc.DrawRoundedRectangle(background, null, rect, 3, 3);
        dc.DrawText(ft, new Point(rect.Left + 6, rect.Top + 2));
    }

    /// <summary>右下を origin に合わせた小さなラベル。</summary>
    private static void DrawSmallTag(DrawingContext dc, string text, Point bottomRight, double pixelsPerDip)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Yu Gothic UI"), 10, Brushes.White, pixelsPerDip);
        var rect = new Rect(bottomRight.X - ft.Width - 8, bottomRight.Y - ft.Height - 2, ft.Width + 8, ft.Height + 2);
        dc.DrawRoundedRectangle(HiddenTagBrush, null, rect, 3, 3);
        dc.DrawText(ft, new Point(rect.Left + 4, rect.Top + 1));
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
