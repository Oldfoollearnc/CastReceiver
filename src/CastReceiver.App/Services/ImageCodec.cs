using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CastReceiver.App.Services;

/// <summary>图像编码（全部在内存里完成，任何一步都不落明文文件）。</summary>
internal static class ImageCodec
{
    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] EncodeJpeg(Bitmap bitmap, int quality)
    {
        using var stream = new MemoryStream();
        if (JpegCodec is null) { bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 30, 100));
        bitmap.Save(stream, JpegCodec, parameters);
        return stream.ToArray();
    }

    public static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>缩略图：库列表只解密这几 KB，所以打开库很快。</summary>
    public static byte[] MakeThumbnailJpeg(Bitmap source, int maxWidth = 320, int quality = 72)
    {
        int width = Math.Max(1, Math.Min(maxWidth, source.Width));
        int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / Math.Max(1, source.Width)));
        using var thumb = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(thumb))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        return EncodeJpeg(thumb, quality);
    }

    /// <summary>把已编码的 JPEG/PNG 字节解码成 Bitmap（查看器用）。</summary>
    public static Bitmap? Decode(byte[] encoded)
    {
        try
        {
            using var stream = new MemoryStream(encoded, writable: false);
            return new Bitmap(stream);
        }
        catch { return null; }
    }
}
