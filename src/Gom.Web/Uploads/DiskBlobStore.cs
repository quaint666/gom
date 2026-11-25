using System.Text.RegularExpressions;

namespace Gom.Web.Uploads;

/// <summary>Uploads as files in one folder tree: root/ab/ab12... (the first two letters spread them over folders).</summary>
public sealed partial class DiskBlobStore : IBlobStore
{
    private readonly string _root;

    public DiskBlobStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The uploads folder is not set.", nameof(root));
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    // Only what this server makes: a GUID, and optionally ".t". Anything else (a slash, "..", a drive) never reaches the disk.
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(\.t)?$")]
    private static partial Regex Valid();

    private string PathOf(string name)
    {
        if (!Valid().IsMatch(name)) throw new ArgumentException("Not a name this store makes.", nameof(name));
        return Path.Combine(_root, name[..2], name);
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written beside it and then moved into place, so a reader never sees half a file and a crash leaves no broken upload.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<bool> CopyAsync(string from, string to, CancellationToken ct)
    {
        var source = PathOf(from);
        var path = PathOf(to);
        if (!File.Exists(source)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Copied beside it and then moved into place, like a write: a reader never sees half a file.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct);
            }
            catch (FileNotFoundException) { return false; }   // taken back while it was being copied
            File.Move(temp, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Stream? OpenRead(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan) : null;
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
