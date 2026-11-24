using SkiaSharp;

namespace Gom.Tests;

/// <summary>Makes pictures for tests, including the metadata a phone writes.</summary>
public static class TestImages
{
    /// <summary>A photo-like picture: busy, so it does not shrink to nothing.</summary>
    public static SKBitmap Busy(int width, int height, bool transparentCorner = false)
    {
        var bitmap = new SKBitmap(width, height);
        // A small fixed sequence: the same picture every run (and Random is not allowed here: CA5394).
        var state = 12345u;
        int Next(int max) { state = state * 1664525u + 1013904223u; return (int)((state >> 8) % (uint)max); }
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SteelBlue);
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 600; i++)
        {
            paint.Color = new SKColor((byte)Next(256), (byte)Next(256), (byte)Next(256));
            canvas.DrawCircle(Next(width), Next(height), 4 + Next(Math.Max(1, width / 12)), paint);
        }
        if (transparentCorner) canvas.DrawRect(0, 0, width / 4, height / 4, new SKPaint { BlendMode = SKBlendMode.Src, Color = SKColors.Transparent });
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, quality).ToArray();
    }

    public static byte[] Jpeg(int width, int height) { using var b = Busy(width, height); return Encode(b, SKEncodedImageFormat.Jpeg); }
    public static byte[] Png(int width, int height, bool transparentCorner = false) { using var b = Busy(width, height, transparentCorner); return Encode(b, SKEncodedImageFormat.Png); }

    /// <summary>One colour all over: a large picture in a few hundred bytes.</summary>
    public static byte[] PlainPng(int width, int height)
    {
        using var b = new SKBitmap(width, height);
        b.Erase(SKColors.Teal);
        return Encode(b, SKEncodedImageFormat.Png);
    }

    public const string GpsMarker = "GPS-LAT-10.7769N-LON-106.7009E";

    /// <summary>The JPEG with an Exif block holding an orientation (1 to 8) and a text standing for a location.</summary>
    public static byte[] WithExif(byte[] jpeg, int orientation)
    {
        // Big-endian TIFF: one entry (Orientation, a short), then the marker text after it, standing for the GPS block.
        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0 };
        tiff.AddRange(System.Text.Encoding.ASCII.GetBytes(GpsMarker));
        var exif = System.Text.Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var segment = new List<byte> { 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)((exif.Length + 2) & 0xFF) };
        segment.AddRange(exif);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }

    public static bool Contains(byte[] haystack, string text) => haystack.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(text)) >= 0;
}
