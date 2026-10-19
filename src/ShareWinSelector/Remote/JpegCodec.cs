using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareWinSelector.Remote;

/// <summary>
/// 送る絵の JPEG への変換（WPF の画像機能＝WIC を使う）。
/// WPF の画像オブジェクトは作ったスレッドでしか使えないので、変換も同じスレッド（STA）の中で済ませる。
/// 受け取った JPEG は、中身を広げる前に縦横の大きさを確かめる（壊れた・悪意のあるデータで巨大な配列を作らない）。
/// </summary>
internal static class JpegCodec
{
    /// <summary>子の画面の絵の上限（8K）。</summary>
    public const int MaxFrameWidth = 7680, MaxFrameHeight = 4320;

    /// <summary>サムネイルの上限（子は 320×180 以内で送る）。</summary>
    public const int MaxThumbnailWidth = 640, MaxThumbnailHeight = 360;

    /// <summary>BGRA（アルファは使わない）の画素を JPEG にする。</summary>
    public static byte[] Encode(byte[] bgra, int width, int height, int quality)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>JPEG をそのまま画面に出せる画像にする（どのスレッドからも使えるよう固めて返す）。大きすぎれば例外。</summary>
    public static BitmapSource DecodeImage(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        CheckSize(stream, MaxThumbnailWidth, MaxThumbnailHeight);
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>JPEG を BGRA（アルファは使わない）の画素に戻す。大きすぎれば例外。</summary>
    public static (byte[] Pixels, int Width, int Height) Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        // 広げるのは CopyPixels のとき（ここでは見出しだけ読む）
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        BitmapSource frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        CheckSize(width, height, MaxFrameWidth, MaxFrameHeight);
        if (frame.Format != PixelFormats.Bgr32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[checked(width * height * 4)];
        frame.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static void CheckSize(Stream stream, int maxWidth, int maxHeight)
    {
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        CheckSize(frame.PixelWidth, frame.PixelHeight, maxWidth, maxHeight);
    }

    private static void CheckSize(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0 || width > maxWidth || height > maxHeight)
            throw new InvalidDataException($"大きさがおかしな絵: {width}×{height}");
    }
}
