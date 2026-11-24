using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat.Live;

/// <summary>Tells connected members about a new message. Runs only after the commit, and never fails the send.</summary>
public sealed class LiveDelivery(ConnectionRegistry registry, ILogger<LiveDelivery> logger)
{
    public async Task PublishCreatedAsync(GomDbContext db, MessageEndpoints.MessageDto message)
    {
        try
        {
            // Membership is read now, not remembered: whoever is a member at this moment gets it, nobody else.
            var members = await db.Members.AsNoTracking().Where(m => m.ConversationId == message.ConversationId).Select(m => m.UserId).ToListAsync();
            var frame = Frames.Live(message.ConversationId, new EventDto(message.Seq, "created", message));
            foreach (var user in members)
                foreach (var connection in registry.For(user))
                    connection.TryEnqueue(frame);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "live delivery failed; devices will catch up from their cursor");
        }
    }
}
