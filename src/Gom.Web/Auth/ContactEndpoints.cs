using System.Globalization;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gom.Web.Auth;

/// <summary>The contact form on the privacy page (configuration section "Contact").</summary>
public sealed class ContactOptions
{
    /// <summary>How long a message is kept before it is deleted by itself; the privacy page says this number.</summary>
    public int KeepDays { get; set; } = 180;

    /// <summary>At most this many messages from everyone together in any 24 hours; past it the form says to try later.</summary>
    public int DailyMax { get; set; } = 50;
}

/// <summary>
/// "Ask the operator": anyone, signed in or not, can write to the owner from the privacy page (Google Play wants a way to ask about
/// privacy). The owner reads and deletes them on their page and is told by a notification; nothing is shown to anyone else.
/// </summary>
public static class ContactEndpoints
{
    public const int MaxChars = 2000;
    public const int ReplyMaxChars = 200;

    /// <param name="Website">Hidden on the page: a person never fills it in, a bot that fills every field does.</param>
    public sealed record ContactRequest(string? Body, string? ReplyTo, string? Website);

    public static void MapContact(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/contact", Send);
        var owner = app.MapGroup("/api").RequireAuthorization("Owner");
        owner.MapGet("/contact", List);
        owner.MapDelete("/contact/{id:guid}", Delete);
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);

    /// <summary>Trimmed, 1 to <paramref name="max"/> characters, no control or invisible formatting characters (new lines only where allowed).</summary>
    public static string? Clean(string? input, int max, bool lines)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || new StringInfo(text).LengthInTextElements > max) return null;
        foreach (var c in text)
        {
            if (lines && c is '\n') continue;
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
        }
        return text;
    }

    private static async Task<IResult> Send(ContactRequest request, HttpContext http, GomDbContext db, SignInThrottle throttle, Push.PushQueue push,
        TimeProvider clock, IOptions<ContactOptions> options, CancellationToken ct)
    {
        // Slowed per address as sign-up is: every message counts.
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var wait = throttle.Begin(ip, "+contact " + ip);
        if (wait > TimeSpan.Zero)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts");
        }
        // A bot: told it went, so it has no reason to try again; nothing is kept.
        if (!string.IsNullOrEmpty(request.Website)) return Results.NoContent();

        if (Clean(request.Body, MaxChars, lines: true) is not { } body) return Error(StatusCodes.Status400BadRequest, "invalid_contact");
        string? replyTo = null;
        if (!string.IsNullOrWhiteSpace(request.ReplyTo) && (replyTo = Clean(request.ReplyTo, ReplyMaxChars, lines: false)) is null)
            return Error(StatusCodes.Status400BadRequest, "invalid_contact");

        var now = clock.GetUtcNow();
        var since = now.AddDays(-1);
        if (await db.ContactMessages.CountAsync(m => m.CreatedAt > since, ct) >= options.Value.DailyMax)
            return Error(StatusCodes.Status429TooManyRequests, "contact_full");

        db.ContactMessages.Add(new ContactMessage { Id = Guid.NewGuid(), Body = body, ReplyTo = replyTo, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        push.Enqueue(new Push.PushJob(Guid.Empty, 0, Guid.Empty, "", 0, 0, Contact: true));
        return Results.NoContent();
    }

    /// <summary>The owner's list, newest first. Old ones are gone already (<see cref="ContactCleaner"/>); this skips any not yet taken.</summary>
    private static async Task<IResult> List(GomDbContext db, TimeProvider clock, IOptions<ContactOptions> options, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.KeepDays);
        var messages = await db.ContactMessages.AsNoTracking().Where(m => m.CreatedAt >= cutoff).OrderByDescending(m => m.CreatedAt)
            .Select(m => new { id = m.Id, body = m.Body, replyTo = m.ReplyTo, createdAt = m.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { messages });
    }

    private static async Task<IResult> Delete(Guid id, GomDbContext db, CancellationToken ct)
    {
        await db.ContactMessages.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>Deletes contact messages older than <see cref="ContactOptions.KeepDays"/>.</summary>
public static class ContactCleaner
{
    public static Task<int> RunOnce(GomDbContext db, DateTimeOffset now, int keepDays, CancellationToken ct)
    {
        var cutoff = now.AddDays(-keepDays);
        return db.ContactMessages.Where(m => m.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Runs <see cref="ContactCleaner"/> at start and every hour, so a message is never kept much past its time.</summary>
public sealed class ContactJanitor(IServiceScopeFactory scopes, IOptions<ContactOptions> options, TimeProvider clock, ILogger<ContactJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await ContactCleaner.RunOnce(scope.ServiceProvider.GetRequiredService<GomDbContext>(), clock.GetUtcNow(), options.Value.KeepDays, stop);
                    if (removed > 0) log.LogInformation("Deleted {Count} contact messages past their time", removed);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Deleting old contact messages failed; it will be tried again"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}
