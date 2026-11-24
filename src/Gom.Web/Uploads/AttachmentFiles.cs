namespace Gom.Web.Uploads;

public static class AttachmentFiles
{
    /// <summary>Removes the bytes of uploads whose rows are gone. Called after the database commit, so a failed commit never loses a file.</summary>
    public static void Forget(IBlobStore store, IEnumerable<Guid> ids)
    {
        foreach (var id in ids) { store.Delete(id.ToString()); store.Delete(id + ".t"); }
    }
}
