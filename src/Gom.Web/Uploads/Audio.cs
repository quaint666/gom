namespace Gom.Web.Uploads;

/// <summary>
/// Which kind of sound a file is, from its first bytes, never from its name or what the sender claimed: such a file is kept as sent and
/// may play in the page (<c>?play=true</c>), served with this type and <c>nosniff</c>. Anything else stays a download.
/// </summary>
public static class Audio
{
    /// <summary>The fewest bytes <see cref="TypeOf"/> needs.</summary>
    public const int HeadBytes = 12;

    /// <summary>MP3, AAC, OGG (Opus or Vorbis), WAV or FLAC, as a browser plays it; null for anything else.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> head)
    {
        if (head.Length < HeadBytes) return null;
        if (head.StartsWith("ID3"u8)) return "audio/mpeg";   // an MP3 with its tags first
        if (head.StartsWith("OggS"u8)) return "audio/ogg";
        if (head.StartsWith("fLaC"u8)) return "audio/flac";
        if (head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8)) return "audio/wav";
        if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return "audio/aac";   // an ADTS frame: sync, MPEG-4 or -2, layer 0
        // An MPEG audio frame: 11 bits of sync, a version and a layer that exist, a bitrate that is not "bad" and a rate that exists.
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x18) != 0x08 && (head[1] & 0x06) != 0 && (head[2] & 0xF0) != 0xF0 && (head[2] & 0x0C) != 0x0C)
            return "audio/mpeg";
        return null;
    }
}
