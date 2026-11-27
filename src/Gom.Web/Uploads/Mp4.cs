using System.Buffers.Binary;

namespace Gom.Web.Uploads;

/// <summary>What a video says about itself: its size as shown (turned for a phone held upright) and how long it plays.</summary>
public sealed record VideoInfo(int Width, int Height, int DurationMs);

/// <summary>
/// Reads the outline of an MP4 (or QuickTime, the same box format) file: the boxes' headers only, never the pictures or sound, so a
/// file of any size costs a few small reads. Enough to know it is a video, how big to draw it, and how long it is; whether a browser
/// can play what is inside is the browser's to say.
/// </summary>
public static class Mp4
{
    /// <summary>The fewest bytes <see cref="LooksLikeMp4"/> needs.</summary>
    public const int HeadBytes = 12;

    /// <summary>The longest side of a video this server agrees to describe (8K is 7680).</summary>
    public const int MaxEdge = 8192;

    /// <summary>Boxes inside boxes this deep are not looked into (a real file has four or five levels).</summary>
    private const int MaxDepth = 8;

    /// <summary>How many boxes in all are read before giving up on a file: a real one has a few hundred at most.</summary>
    private const int MaxBoxes = 10_000;

    /// <summary>Whether the bytes start as every MP4 does: a first box called "ftyp", of a sensible size.</summary>
    public static bool LooksLikeMp4(ReadOnlySpan<byte> head) =>
        head.Length >= HeadBytes && head[4..8].SequenceEqual("ftyp"u8) && BinaryPrimitives.ReadUInt32BigEndian(head) is >= 8 and <= 4096;

    /// <summary>The video's size and length, or null when the file is not a whole MP4 with a picture track and a length.</summary>
    public static VideoInfo? Read(Stream file)
    {
        try
        {
            var reader = new Reader(file);
            var moov = reader.Children(0, file.Length, 0).FirstOrDefault(b => b.Type == "moov");
            if (moov is null) return null;

            int? durationMs = null;
            (int W, int H)? size = null;
            foreach (var box in reader.Children(moov.Body, moov.End, 1))
            {
                if (box.Type == "mvhd") durationMs = reader.Duration(box);
                else if (box.Type == "trak" && size is null) size = reader.PictureSize(box);
            }
            if (durationMs is not > 0 || size is not { } s || s.W is <= 0 or > MaxEdge || s.H is <= 0 or > MaxEdge) return null;
            return new VideoInfo(s.W, s.H, durationMs.Value);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException) { return null; }
    }

    /// <summary>
    /// Whether the file is sound alone (an M4A): a whole MP4 with a length, a sound track ("soun") and no picture track. Such a file is
    /// kept as a file that may play in the page.
    /// </summary>
    public static bool IsSound(Stream file)
    {
        try
        {
            var reader = new Reader(file);
            var moov = reader.Children(0, file.Length, 0).FirstOrDefault(b => b.Type == "moov");
            if (moov is null) return false;
            int? durationMs = null;
            var handlers = new List<string>();
            foreach (var box in reader.Children(moov.Body, moov.End, 1))
            {
                if (box.Type == "mvhd") durationMs = reader.Duration(box);
                else if (box.Type == "trak" && reader.Handler(box) is { } handler) handlers.Add(handler);
            }
            return durationMs is > 0 && handlers.Contains("soun") && !handlers.Contains("vide");
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException) { return false; }
    }

    private sealed record Box(string Type, long Body, long End);

    private sealed class Reader(Stream file)
    {
        private int _boxes;

        /// <summary>The boxes between two places in the file, each read by its header alone.</summary>
        public IEnumerable<Box> Children(long from, long to, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Boxes too deep.");
            var at = from;
            while (at + 8 <= to)
            {
                if (++_boxes > MaxBoxes) throw new InvalidDataException("Too many boxes.");
                var header = ReadAt(at, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                var body = at + 8;
                if (size == 1) { size = BinaryPrimitives.ReadInt64BigEndian(ReadAt(at + 8, 8)); body = at + 16; }   // a 64-bit size follows
                else if (size == 0) size = to - at;   // runs to the end
                if (size < body - at || at + size > to) throw new InvalidDataException("A box runs past its parent.");
                yield return new Box(type, body, at + size);
                at += size;
            }
        }

        /// <summary>The whole file's length from its "mvhd": a time scale and a duration in it, version 0 (32-bit) or 1 (64-bit).</summary>
        public int? Duration(Box mvhd)
        {
            var head = ReadAt(mvhd.Body, 32);
            var (scale, duration) = head[0] == 1
                ? (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20)), BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(24)))
                : (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16)));
            if (scale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue) return null;
            var ms = duration / (double)scale * 1000;
            return ms is > 0 and < int.MaxValue ? (int)Math.Ceiling(ms) : null;
        }

        /// <summary>What a track holds, as its "mdia/hdlr" names it: "vide", "soun" and so on.</summary>
        public string? Handler(Box trak)
        {
            foreach (var box in Children(trak.Body, trak.End, 2))
                if (box.Type == "mdia")
                    foreach (var inner in Children(box.Body, box.End, 3))
                        if (inner.Type == "hdlr") return System.Text.Encoding.ASCII.GetString(ReadAt(inner.Body + 8, 4));
            return null;
        }

        /// <summary>A track's size as shown, when the track is pictures ("vide" in its "mdia/hdlr"); null for sound and the rest.</summary>
        public (int W, int H)? PictureSize(Box trak)
        {
            Box? tkhd = null;
            var picture = false;
            foreach (var box in Children(trak.Body, trak.End, 2))
            {
                if (box.Type == "tkhd") tkhd = box;
                else if (box.Type == "mdia")
                    foreach (var inner in Children(box.Body, box.End, 3))
                        if (inner.Type == "hdlr") picture = System.Text.Encoding.ASCII.GetString(ReadAt(inner.Body + 8, 4)) == "vide";
            }
            if (!picture || tkhd is null) return null;

            // After the times come a matrix (nine 4-byte numbers) and the width and height in 16.16 fixed point. A matrix whose first
            // number is 0 turns the picture a quarter (a phone held upright), so it is shown the other way round.
            var version = ReadAt(tkhd.Body, 1)[0];
            var matrixAt = tkhd.Body + (version == 1 ? 52 : 40);
            var tail = ReadAt(matrixAt, 44);
            var turned = BinaryPrimitives.ReadInt32BigEndian(tail) == 0 && BinaryPrimitives.ReadInt32BigEndian(tail.AsSpan(16)) == 0;
            var w = (int)(BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(36)) >> 16);
            var h = (int)(BinaryPrimitives.ReadUInt32BigEndian(tail.AsSpan(40)) >> 16);
            return turned ? (h, w) : (w, h);
        }

        private byte[] ReadAt(long at, int count)
        {
            var bytes = new byte[count];
            file.Position = at;
            file.ReadExactly(bytes);
            return bytes;
        }
    }
}
