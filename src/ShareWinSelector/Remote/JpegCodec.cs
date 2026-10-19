using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareWinSelector.Remote;

/// <summary>
/// 送る絵の JPEG への変換（WPF の画像機能＝WIC を使う）。
/// WPF の画像オブジェクトは作ったスレッドでしか使えないので、変換も同じスレッド（STA）の中で済ませる。
/// </summary>
internal static class JpegCodec
{
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

    /// <summary>JPEG を BGRA（アルファは使わない）の画素に戻す。</summary>
    public static (byte[] Pixels, int Width, int Height) Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgr32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        int width = frame.PixelWidth, height = frame.PixelHeight;
        var pixels = new byte[width * height * 4];
        frame.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }
}
