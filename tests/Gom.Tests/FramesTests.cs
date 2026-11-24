using System.Text;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;

namespace Gom.Tests;

public class FramesTests
{
    [Fact]
    public void Vietnamese_text_goes_out_as_itself_not_as_escapes()
    {
        var chat = Guid.NewGuid();
        var message = new MessageEndpoints.MessageDto(Guid.NewGuid(), 1, chat, Guid.NewGuid(), new(Guid.NewGuid(), "Chị Hai"), "xin chào, ăn cơm chưa?", DateTimeOffset.UnixEpoch);

        var text = Encoding.UTF8.GetString(Frames.Live(chat, new EventDto(1, "created", message)));

        Assert.Contains("xin chào, ăn cơm chưa?", text);
        Assert.Contains("Chị Hai", text);
        Assert.DoesNotContain("\\u", text);
    }
}
