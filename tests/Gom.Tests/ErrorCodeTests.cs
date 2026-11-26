using System.Text.RegularExpressions;

namespace Gom.Tests;

/// <summary>
/// The page keeps a list of the error codes the server sends (api.ts), and a sentence for each (text.ts). A code the server sends
/// that is missing there reaches the person as "Có lỗi xảy ra"; five did, until this test.
/// </summary>
public class ErrorCodeTests
{
    /// <summary>Codes the server sends that no page acts on: a request no page makes that way.</summary>
    /// The call codes are the Android app's until calls come to the web pages (docs/voice-calls.md, PR 8).
    private static readonly HashSet<string> NotForPages = ["bad_request", "websocket_required",
        "unsupported_version", "invalid_sdp", "invalid_call", "call_waiting", "too_many_calls", "in_call", "call_ended", "call_answered", "not_in_call",
        "cannot_take_calls", "invalid_token"];

    private static string Web()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gom.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Gom.Web");
    }

    private static HashSet<string> SentByServer()
    {
        var web = Web();
        var codes = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("Migrations", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"error = ""([a-z_]+)""|(?:Error|Fail)\((?:StatusCodes\.\w+, )?""([a-z_]+)""\)|=> ""(password_[a-z_]+)"""))
                codes.Add(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
        }
        return codes;
    }

    [Fact]
    public void Every_code_the_server_sends_is_known_to_the_page_and_has_its_own_sentence()
    {
        var sent = SentByServer();
        Assert.Contains("pending_approval", sent);   // the scan finds codes at all
        var api = File.ReadAllText(Path.Combine(Web(), "Scripts", "api.ts"));
        var listed = Regex.Matches(Regex.Match(api, @"SERVER_CODE_LIST = \[(.*?)\] as const", RegexOptions.Singleline).Groups[1].Value, "'([a-z_]+)'")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var text = File.ReadAllText(Path.Combine(Web(), "Scripts", "text.ts"));

        Assert.Empty(sent.Except(NotForPages).Except(listed).Order());
        Assert.Empty(listed.Where(c => !text.Contains($"case '{c}'", StringComparison.Ordinal)).Order());
    }
}
