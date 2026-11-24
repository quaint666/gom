namespace Gom.Web.Uploads;

/// <summary>Where the bytes of uploads live. A name is made by the server (a random id, with ".t" for the small version), never by a client.</summary>
public interface IBlobStore
{
    /// <summary>Stores the bytes under the name; a name already there is replaced whole or not at all.</summary>
    Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>The bytes to read, or null when there is nothing under that name.</summary>
    Stream? OpenRead(string name);

    /// <summary>Removes it; nothing there is not an error.</summary>
    void Delete(string name);
}
