namespace Gom.Web.Uploads;

/// <summary>Where the bytes of uploads live. A name is made by the server (a random id, with ".t" for the small version), never by a client.</summary>
public interface IBlobStore
{
    /// <summary>Stores the bytes under the name; a name already there is replaced whole or not at all.</summary>
    Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>
    /// Stores what is read from the stream (after <paramref name="head"/>, bytes already read from it) without holding it in memory.
    /// The number of bytes stored, or null, with nothing stored, when there are more than <paramref name="limit"/>.
    /// </summary>
    Task<long?> WriteStreamAsync(string name, ReadOnlyMemory<byte> head, Stream rest, long limit, CancellationToken ct);

    /// <summary>Makes a second, independent copy under a new name, without holding the file in memory. False when there is nothing under the first name.</summary>
    Task<bool> CopyAsync(string from, string to, CancellationToken ct);

    /// <summary>The bytes to read, or null when there is nothing under that name.</summary>
    Stream? OpenRead(string name);

    /// <summary>Removes it; nothing there is not an error.</summary>
    void Delete(string name);
}
