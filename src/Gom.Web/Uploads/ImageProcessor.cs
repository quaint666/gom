using SkiaSharp;

namespace Gom.Web.Uploads;

/// <summary>What came out of a picture: the stored version, the small version, and the size of the stored one.</summary>
public sealed record ProcessedImage(byte[] Main, byte[] Thumb, int Width, int Height);

/// <summary>Either the processed picture or the reason it could not be.</summary>
public sealed record ImageResult(ProcessedImage? Image, ImageProblem? Problem);

/// <summary>A profile picture as WebP bytes, or why there is none.</summary>
public sealed record AvatarResult(byte[]? Bytes, ImageProblem? Problem);

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
        || head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)               // WebP
        || LooksLikeGif(head);                                                                                              // GIF (a still one; a moving one never gets here)

    /// <summary>A GIF, still or moving. A still one is made into WebP like any picture; a moving one is kept as sent.</summary>
    public static bool LooksLikeGif(ReadOnlySpan<byte> head) =>
        head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8));

    /// <summary>A moving picture's size, its number of frames, whether it is a GIF (else WebP), and about how much memory checking it holds.</summary>
    public sealed record AnimatedPlan(int Width, int Height, int Frames, bool Gif, long Cost);

    /// <summary>Not moving (then it is handled as any picture or file), or moving with its plan, or moving but refused.</summary>
    public sealed record AnimatedPlanResult(bool Animated, AnimatedPlan? Plan, ImageProblem? Problem);

    /// <summary>The still first frame of a moving picture, as WebP for its small version, or why it was refused.</summary>
    public sealed record AnimatedResult(byte[]? Still, ImageProblem? Problem);

    /// <summary>
    /// Whether this is a moving picture (a GIF or a WebP with more than one frame) and, if so, whether it is within the limits, judged
    /// from its headers before any pixel is made: a picture's size, the number of frames, and the pixels of all frames together.
    /// </summary>
    public static AnimatedPlanResult PlanAnimated(byte[] bytes, UploadOptions o)
    {
        if (!LooksLikeGif(bytes) && !(bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            return new AnimatedPlanResult(false, null, null);
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp))
                return new AnimatedPlanResult(false, null, null);
            var frames = codec.FrameCount;
            if (frames <= 1) return new AnimatedPlanResult(false, null, null);
            long pixels = (long)codec.Info.Width * codec.Info.Height;
            if (pixels > o.MaxPixels || frames > o.MaxFrames || pixels * frames > o.MaxAnimatedPixels)
                return new AnimatedPlanResult(true, null, ImageProblem.TooManyPixels);
            // One frame at a time, in one bitmap, and the still copy made from the first.
            return new AnimatedPlanResult(true, new AnimatedPlan(codec.Info.Width, codec.Info.Height, frames,
                codec.EncodedFormat == SKEncodedImageFormat.Gif, pixels * 4 * 2 + Overhead), null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            // A GIF that cannot be read is a broken picture; any other header goes on to be judged as a still picture or a file.
            return LooksLikeGif(bytes) ? new AnimatedPlanResult(true, null, ImageProblem.Unreadable) : new AnimatedPlanResult(false, null, null);
        }
    }

    /// <summary>
    /// Decodes every frame once, in order, into one bitmap (a frame drawn on the one before passes that one as already there), so a
    /// moving picture cut short or broken anywhere is refused, as a still one is. The first frame becomes the small version.
    /// </summary>
    public static AnimatedResult CheckAnimated(byte[] bytes, AnimatedPlan plan, UploadOptions o)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec is null || codec.FrameCount != plan.Frames) return new AnimatedResult(null, ImageProblem.Unreadable);
            var info = new SKImageInfo(plan.Width, plan.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var frame = new SKBitmap(info);
            var frames = codec.FrameInfo;
            byte[]? still = null;
            for (var i = 0; i < plan.Frames; i++)
            {
                var required = frames[i].RequiredFrame;
                // The frame before is in the bitmap; any other needed frame the codec decodes again itself.
                var prior = required >= 0 && required == i - 1 ? required : -1;
                if (codec.GetPixels(info, frame.GetPixels(), new SKCodecOptions(i, prior)) != SKCodecResult.Success) return new AnimatedResult(null, ImageProblem.Unreadable);
                if (i == 0)
                {
                    using var small = Scale(frame, o.ThumbEdge);
                    still = Encode(small, o.ThumbQuality);
                    if (still is null) return new AnimatedResult(null, ImageProblem.Unreadable);
                }
            }
            return new AnimatedResult(still, null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return new AnimatedResult(null, ImageProblem.Unreadable);
        }
    }

    /// <summary>How a picture will be handled, worked out from its header alone (no pixel is made yet).</summary>
    /// <param name="DecodeWidth">The size it is decoded at: a big JPEG or WebP is decoded already scaled down (a half, a quarter or an eighth), which takes far less memory and time.</param>
    /// <param name="Cost">About how many bytes of memory processing will hold at its peak.</param>
    public sealed record ImagePlan(int DecodeWidth, int DecodeHeight, SKEncodedOrigin Origin, long Cost);

    public sealed record PlanResult(ImagePlan? Plan, ImageProblem? Problem);

    /// <summary>Memory beyond the decoded pixels: the scaled copy, the small version, the encoders and their output.</summary>
    private const long Overhead = 16L * 1024 * 1024;

    public static PlanResult Plan(byte[] bytes, UploadOptions o)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Gif))
                return new PlanResult(null, ImageProblem.NotAnImage);
            // Judged from the header, before any pixel is made: a few kilobytes can describe a picture of many gigabytes.
            if ((long)codec.Info.Width * codec.Info.Height > o.MaxPixels) return new PlanResult(null, ImageProblem.TooManyPixels);

            // The smallest scale that still has at least the stored size on its longer side (a codec that cannot scale says its own size).
            var width = codec.Info.Width; var height = codec.Info.Height;
            foreach (var scale in new[] { 0.125f, 0.25f, 0.5f })
            {
                var size = codec.GetScaledDimensions(scale);
                if (size.Width > 0 && size.Height > 0 && Math.Max(size.Width, size.Height) >= o.ImageMaxEdge && size.Width * size.Height < width * height)
                {
                    width = size.Width; height = size.Height;
                    break;
                }
            }
            var upright = codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default;
            var decoded = (long)width * height * 4;
            return new PlanResult(new ImagePlan(width, height, codec.EncodedOrigin, decoded * (upright ? 1 : 2) + Overhead), null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return new PlanResult(null, ImageProblem.Unreadable);
        }
    }

    /// <summary>Plans and processes in one go (for callers that need no waiting for memory).</summary>
    public static ImageResult Process(byte[] bytes, UploadOptions o)
    {
        var planned = Plan(bytes, o);
        return planned.Plan is null ? new ImageResult(null, planned.Problem) : Process(bytes, planned.Plan, o);
    }

    /// <summary>The picture decoded and turned upright, or the problem. All of it or nothing: a picture cut short is refused, not shown half grey.</summary>
    private static (SKBitmap? Upright, ImageProblem? Problem) Decode(byte[] bytes, ImagePlan plan)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
        if (codec is null) return (null, ImageProblem.NotAnImage);
        var info = codec.Info.WithSize(plan.DecodeWidth, plan.DecodeHeight);
        var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) { decoded.Dispose(); return (null, ImageProblem.Unreadable); }
        // An upright picture is used as it is; one that needs turning makes a turned copy (the plan counted it).
        if (codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return (decoded, null);
        using (decoded) return (Orient(decoded, codec.EncodedOrigin), null);
    }

    public static ImageResult Process(byte[] bytes, ImagePlan plan, UploadOptions o)
    {
        try
        {
            var (decoded, problem) = Decode(bytes, plan);
            if (decoded is null) return new ImageResult(null, problem);
            using var upright = decoded;

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

    /// <summary>
    /// A profile picture: the largest square from the middle of the picture (turned upright), no larger than <paramref name="edge"/>
    /// on a side, as WebP. Like every picture here it is drawn again, so nothing of the original file (its location, its camera) is kept.
    /// </summary>
    public static AvatarResult Avatar(byte[] bytes, ImagePlan plan, int edge, int quality)
    {
        try
        {
            var (decoded, problem) = Decode(bytes, plan);
            if (decoded is null) return new AvatarResult(null, problem);
            using var upright = decoded;
            var side = Math.Min(upright.Width, upright.Height);
            using var square = new SKBitmap();
            if (!upright.ExtractSubset(square, SKRectI.Create((upright.Width - side) / 2, (upright.Height - side) / 2, side, side))) return new AvatarResult(null, ImageProblem.Unreadable);
            using var small = side <= edge ? square.Copy()
                : square.Resize(new SKImageInfo(edge, edge, square.ColorType, square.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell))
                  ?? throw new InvalidOperationException("The picture could not be scaled.");
            var encoded = Encode(small, quality);
            return encoded is null ? new AvatarResult(null, ImageProblem.Unreadable) : new AvatarResult(encoded, null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return new AvatarResult(null, ImageProblem.Unreadable);
        }
    }

    /// <summary>
    /// The owner's sticker (Chat.StickerPacks): upright, no larger than <paramref name="edge"/> on its longer side, its transparency
    /// kept, as a WebP.
    /// </summary>
    public static AvatarResult Sticker(byte[] bytes, ImagePlan plan, int edge, int quality)
    {
        try
        {
            var (decoded, problem) = Decode(bytes, plan);
            if (decoded is null) return new AvatarResult(null, problem);
            using var upright = decoded;
            using var small = Scale(upright, edge);
            var encoded = Encode(small, quality);
            return encoded is null ? new AvatarResult(null, ImageProblem.Unreadable) : new AvatarResult(encoded, null);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NullReferenceException)
        {
            return new AvatarResult(null, ImageProblem.Unreadable);
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
        // Nearest: the pixels only move (a turn or a mirror), so none is blended with its neighbour.
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return target;
    }
}
