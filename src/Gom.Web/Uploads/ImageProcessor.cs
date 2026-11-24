using SkiaSharp;

namespace Gom.Web.Uploads;

/// <summary>What came out of a picture: the stored version, the small version, and the size of the stored one.</summary>
public sealed record ProcessedImage(byte[] Main, byte[] Thumb, int Width, int Height);

/// <summary>Either the processed picture or the reason it could not be.</summary>
public sealed record ImageResult(ProcessedImage? Image, ImageProblem? Problem);

public enum ImageProblem { NotAnImage, TooManyPixels, Unreadable }

/// <summary>
/// Makes a received picture safe and small. It is decoded, turned upright (phones store the turn in the metadata),
/// scaled down, and written again as WebP, so nothing of the original metadata (location, camera, time) is kept.
/// </summary>
public static class ImageProcessor
{
    // Bytes, not a text literal: a text literal would write 0x89 as two bytes.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The formats that are pictures. Anything else is a file.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF                                            // JPEG
        || head.Length >= 8 && head[..8].SequenceEqual(PngSignature)                                                // PNG
        || head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8);              // WebP

    public static ImageResult Process(byte[] bytes, UploadOptions o)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
                return new ImageResult(null, ImageProblem.NotAnImage);
            // Judged from the header, before any pixel is made: a few kilobytes can describe a picture of many gigabytes.
            if ((long)codec.Info.Width * codec.Info.Height > o.MaxPixels) return new ImageResult(null, ImageProblem.TooManyPixels);

            // All of it or nothing: a picture cut short is refused, not shown half grey.
            using var decoded = new SKBitmap(codec.Info);
            if (codec.GetPixels(codec.Info, decoded.GetPixels()) != SKCodecResult.Success) return new ImageResult(null, ImageProblem.Unreadable);
            using var upright = Orient(decoded, codec.EncodedOrigin);

            using var main = Scale(upright, o.ImageMaxEdge);
            using var thumb = Scale(main, o.ThumbEdge);
            var mainBytes = Encode(main, o.ImageQuality);
            var thumbBytes = Encode(thumb, o.ThumbQuality);
            if (mainBytes is null || thumbBytes is null) return new ImageResult(null, ImageProblem.Unreadable);
            return new ImageResult(new ProcessedImage(mainBytes, thumbBytes, main.Width, main.Height), null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return new ImageResult(null, ImageProblem.Unreadable);
        }
    }

    private static byte[]? Encode(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
        return data?.ToArray();
    }

    /// <summary>A copy no larger than <paramref name="edge"/> on its longer side; the same picture (a copy) when it is already small enough.</summary>
    private static SKBitmap Scale(SKBitmap source, int edge)
    {
        var longer = Math.Max(source.Width, source.Height);
        if (longer <= edge) return source.Copy();
        var factor = (double)edge / longer;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * factor)), Math.Max(1, (int)Math.Round(source.Height * factor)), source.ColorType, source.AlphaType);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new InvalidOperationException("The picture could not be scaled.");
    }

    /// <summary>
    /// Turns the pixels so the picture is upright, following the eight orientations of the metadata standard.
    /// Public so the tests can check every one of them.
    /// </summary>
    public static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source.Copy();
        float w = source.Width, h = source.Height;
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        // x' = a*x + b*y + tx, y' = c*x + d*y + ty
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),        // mirrored left to right
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),    // turned half way
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),      // mirrored top to bottom
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),          // mirrored along the main diagonal
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),        // turned a quarter clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),    // mirrored along the other diagonal
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),      // turned a quarter counter-clockwise
            _ => SKMatrix.Identity,
        };
        var target = new SKBitmap(new SKImageInfo(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }
}
