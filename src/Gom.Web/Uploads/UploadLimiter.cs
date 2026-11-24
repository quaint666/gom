using Gom.Web.Chat;

namespace Gom.Web.Uploads;

/// <summary>How fast one person may upload: a burst, then a steady pace (the same bucket as sending messages, with its own numbers).</summary>
public sealed class UploadLimiter(SendLimiter bucket)
{
    public TimeSpan? TryTake(Guid person) => bucket.TryTake(person);
}
