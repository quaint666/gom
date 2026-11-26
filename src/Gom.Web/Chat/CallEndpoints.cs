using System.Collections.Concurrent;
using Gom.Web.Auth;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gom.Web.Chat;

/// <summary>Voice calls (configuration section "Calls"; docs/voice-calls.md). Off until they work end to end.</summary>
public sealed class CallOptions
{
    /// <summary>The switch for the whole feature: off, every call address answers 404 and /api/me says calls are off.</summary>
    public bool Enabled { get; set; }
    /// <summary>How long a call rings before it is missed.</summary>
    public int RingSeconds { get; set; } = 45;
    /// <summary>How often a phone in a call says it is still there (the app reads it from the server).</summary>
    public int AliveSeconds { get; set; } = 30;
    /// <summary>A side silent this long has gone (the app crashed, the phone died): the call ends.</summary>
    public int SilentSeconds { get; set; } = 90;
    /// <summary>Calls one person may start in a row, then one every <see cref="SecondsBetweenCalls"/>.</summary>
    public int CallBurst { get; set; } = 3;
    public int SecondsBetweenCalls { get; set; } = 20;
    /// <summary>
    /// Whether the Android app takes calls: off, its registration does not make its person callable and calls never wake it
    /// (a high-priority push it would not show lowers FCM's priority for the app). Pages that take calls say so themselves
    /// (docs/voice-calls.md, section 2b).
    /// </summary>
    public bool AppsTakeCalls { get; set; }
}

/// <summary>Who may start a call how often: a bucket per person, apart from the one for messages.</summary>
public sealed class CallLimiter(SendLimiter bucket)
{
    public TimeSpan? TryTake(Guid caller) => bucket.TryTake(caller);
}

/// <summary>
/// What the two phones say to set a call up (their offers and answers, which hold their network addresses), kept in memory only and
/// forgotten when the call ends; and the call frames to their devices. A server restart loses what a ringing call needed, and that
/// call fails; a call already connected goes on phone to phone.
/// Also the relay logins handed out: a device asks for one before it calls or answers, and the call it then starts or answers claims
/// it; each call's logins are taken back when it ends, and one no call claimed soon is taken back by the sweep.
/// And the wake-ups for the person called's apps (FCM), which ring and stop ringing a phone with Gom closed.
/// </summary>
public sealed class CallRelay(ConnectionRegistry registry, TurnRevoker revoker, Push.CallPushQueue pushes, IOptions<CallOptions> options)
{
    /// <summary>Wakes the person's apps, when apps take calls.</summary>
    public void Wake(Push.CallPush job) { if (options.Value.AppsTakeCalls) pushes.Enqueue(job); }

    /// <summary>Whether one of the person's open pages takes calls (its socket said so).</summary>
    public bool OnAPage(Guid person) => registry.For(person).Any(c => c.TakesCalls);

    public sealed record Descriptions(string? Offer, string? Answer, int Rev, Guid? LastFrom, string? LastType, string? LastSdp);

    private readonly ConcurrentDictionary<Guid, Descriptions> _calls = new();

    public void Offer(Guid call, string sdp) => _calls[call] = new Descriptions(sdp, null, 0, null, null, null);
    public void Answer(Guid call, string sdp) => _calls.AddOrUpdate(call, _ => new Descriptions(null, sdp, 0, null, null, null), (_, d) => d with { Answer = sdp });

    /// <summary>A new offer or answer mid-call; returns its number, so a device can tell a newer one from an older.</summary>
    public int Renegotiate(Guid call, Guid from, string type, string sdp) =>
        _calls.AddOrUpdate(call, _ => new Descriptions(null, null, 1, from, type, sdp), (_, d) => d with { Rev = d.Rev + 1, LastFrom = from, LastType = type, LastSdp = sdp }).Rev;

    public Descriptions? Of(Guid call) => _calls.TryGetValue(call, out var d) ? d : null;

    /// <summary>The call is over: what it needed is forgotten, and its relay logins are taken back.</summary>
    public void Forget(Guid call)
    {
        _calls.TryRemove(call, out _);
        List<string>? names;
        lock (_gate) _logins.Remove(call, out names);
        foreach (var name in names ?? []) revoker.Revoke(name);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Person, Guid Session), List<(string Name, DateTimeOffset At)>> _unclaimed = [];
    private readonly Dictionary<Guid, List<string>> _logins = [];

    /// <summary>A login for a device that is about to call or answer.</summary>
    public void Issued(Guid person, Guid session, string name, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_unclaimed.TryGetValue((person, session), out var list)) _unclaimed[(person, session)] = list = [];
            list.Add((name, at));
        }
    }

    /// <summary>A fresh login for a device already in the call (a long call).</summary>
    public void Issued(Guid call, string name)
    {
        lock (_gate)
        {
            if (!_logins.TryGetValue(call, out var list)) _logins[call] = list = [];
            list.Add(name);
        }
    }

    /// <summary>The device started or answered this call: its waiting logins are the call's now.</summary>
    public void Claim(Guid call, Guid person, Guid session)
    {
        lock (_gate)
        {
            if (!_unclaimed.Remove((person, session), out var waiting)) return;
            if (!_logins.TryGetValue(call, out var list)) _logins[call] = list = [];
            list.AddRange(waiting.Select(w => w.Name));
        }
    }

    /// <summary>Takes back logins given out before <paramref name="before"/> that no call claimed (a call never made, an answer too late).</summary>
    public int RevokeUnclaimed(DateTimeOffset before)
    {
        var gone = new List<string>();
        lock (_gate)
            foreach (var key in _unclaimed.Keys.ToList())
            {
                var list = _unclaimed[key];
                gone.AddRange(list.Where(w => w.At < before).Select(w => w.Name));
                list.RemoveAll(w => w.At < before);
                if (list.Count == 0) _unclaimed.Remove(key);
            }
        foreach (var name in gone) revoker.Revoke(name);
        return gone.Count;
    }

    /// <summary>The calls holding logins, so the sweep can take back those of a call that ended without this server hearing of it.</summary>
    public List<Guid> HoldingLogins() { lock (_gate) return [.. _logins.Keys]; }

    /// <summary>To a person's devices: all of them, only one session, or all but one.</summary>
    public void Send(Guid person, byte[] frame, Guid? onlySession = null, Guid? exceptSession = null)
    {
        foreach (var connection in registry.For(person))
            if ((onlySession is null || connection.SessionId == onlySession) && connection.SessionId != exceptSession)
                connection.TryEnqueue(frame);
    }
}

/// <summary>
/// One-to-one voice calls between friends. The steps are REST, as sending is (the socket only carries news): start, accept, decline,
/// hang up, a new offer or answer mid-call, and "still here". The server keeps each call's state and checks the two are still friends
/// at every step; the first device to accept wins, and the person's other devices stop ringing. Each call leaves one line in the chat
/// when it ends. Phones carry the protocol's version, since the app and the server are updated at different times.
/// </summary>
public static class CallEndpoints
{
    /// <summary>The call protocol this server speaks. It also takes the one before, while apps catch up.</summary>
    public const int ProtocolVersion = 1;
    /// <summary>An offer or answer with every address in it is a few kilobytes; this is far above that, and stops a flood.</summary>
    public const int MaxSdpChars = 20_000;

    public static bool Supported(int? version) => version == ProtocolVersion || (version == ProtocolVersion - 1 && version >= 1);

    public sealed record StartRequest(Guid? ConversationId, Guid? ClientId, string? Sdp, int? V);
    public sealed record AnswerRequest(string? Sdp, int? V);
    public sealed record SdpRequest(string? Type, string? Sdp);
    /// <param name="ConversationId">Before calling: the chat with the friend to call.</param>
    /// <param name="CallId">Before answering (the call rings for me), or during a long call for a fresh login.</param>
    public sealed record RelayRequest(Guid? ConversationId, Guid? CallId);

    public static void MapCalls(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/calls").RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IOptions<CallOptions>>().Value.Enabled ? await next(context) : Results.NotFound());
        api.MapPost("", Start);
        api.MapPost("/relay", Relay);
        api.MapGet("/{id:guid}", Get);
        api.MapPost("/{id:guid}/accept", Accept);
        api.MapPost("/{id:guid}/decline", Decline);
        api.MapPost("/{id:guid}/hangup", HangUp);
        api.MapPost("/{id:guid}/sdp", Renegotiate);
        api.MapPost("/{id:guid}/alive", Alive);
    }

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);

    private static IResult TooMany(HttpContext http, TimeSpan wait)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, "too_many_calls");
    }

    public static string Name(CallState state) => state switch { CallState.Ringing => "ringing", CallState.Active => "active", _ => "ended" };

    public static string Name(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Completed => "completed", CallOutcome.Missed => "missed", CallOutcome.Declined => "declined", CallOutcome.Busy => "busy", _ => "failed",
    };

    /// <summary>An offer or answer: some text, not too long, as WebRTC writes it (lines of printable ASCII). Null when it is not.</summary>
    private static string? CleanSdp(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp) || sdp.Length > MaxSdpChars || !sdp.StartsWith("v=0", StringComparison.Ordinal)) return null;
        foreach (var c in sdp) if (c is not ('\r' or '\n') && (c < ' ' || c > '~')) return null;
        return sdp;
    }

    private static Task<bool> Friends(GomDbContext db, Guid a, Guid b, CancellationToken ct)
    {
        var (low, high) = Friendship.Order(a, b);
        return db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted, ct);
    }

    /// <summary>
    /// The friend I may call in this chat, or why there is none: not my chat, not one-to-one, no longer friends, or nothing of theirs can
    /// take a call now: an open page that takes calls, or the Android app signed in once apps take calls (docs/voice-calls.md, 2b).
    /// </summary>
    private static async Task<(Guid Peer, IResult? Refused)> Callee(GomDbContext db, CallRelay relay, bool apps, Guid conversation, Guid me, DateTimeOffset now, CancellationToken ct)
    {
        var access = await Access.Of(db, conversation, me, ct);
        if (access is null) return (default, Results.NotFound());
        if (access.Kind != ConversationKind.Direct) return (default, Error(StatusCodes.Status400BadRequest, "invalid_call"));
        var peer = await db.Members.Where(m => m.ConversationId == conversation && m.UserId != me).Select(m => m.UserId).SingleAsync(ct);
        if (!await Friends(db, me, peer, ct)) return (default, Error(StatusCodes.Status403Forbidden, "not_friends"));
        var reachable = relay.OnAPage(peer) || (apps && await (
            from s in db.Sessions
            where s.UserId == peer && s.RevokedAt == null && s.ExpiresAt > now
            join d in db.FcmDevices on s.Id equals d.SessionId
            select d.Id).AnyAsync(ct));
        if (!reachable) return (default, Error(StatusCodes.Status409Conflict, "cannot_take_calls"));
        return (peer, null);
    }

    /// <summary>What a device of one of the two may know of a call. The offer and answer go only to the devices that need them.</summary>
    private static object Describe(Call k, Guid me, Guid session, CallRelay relay, CallOptions o, string? callerName)
    {
        var d = relay.Of(k.Id);
        var callee = me == k.CalleeId;
        var inIt = session == k.CallerSessionId || session == k.AnswerSessionId;
        return new
        {
            id = k.Id, conversationId = k.ConversationId, state = Name(k.State), outcome = k.Outcome is { } x ? Name(x) : null,
            callerId = k.CallerId, calleeId = k.CalleeId, caller = new { id = k.CallerId, displayName = callerName },
            createdAt = k.CreatedAt, answeredAt = k.AnsweredAt, endedAt = k.EndedAt, v = k.Version,
            // The person called needs the offer to answer (any of their devices while it rings); afterwards only the two devices in it.
            offer = callee && (k.State == CallState.Ringing || (k.State == CallState.Active && inIt)) ? d?.Offer : null,
            answer = !callee && k.State == CallState.Active && inIt ? d?.Answer : null,
            // The newest mid-call offer or answer from the other side, for a device that missed its frame.
            sdp = k.State == CallState.Active && inIt && d?.LastFrom is { } from && from != me ? new { type = d.LastType, sdp = d.LastSdp, rev = d.Rev } : null,
            ringSeconds = o.RingSeconds, aliveSeconds = o.AliveSeconds,
        };
    }

    private static async Task<IResult> Described(GomDbContext db, Call k, Guid me, Guid session, CallRelay relay, CallOptions o, CancellationToken ct)
    {
        var name = await db.Users.Where(u => u.Id == k.CallerId).Select(u => u.DisplayName).SingleAsync(ct);
        return Results.Ok(Describe(k, me, session, relay, o, name));
    }

    private static async Task<IResult> Start(StartRequest request, HttpContext http, GomDbContext db, CallLimiter limiter, CallRelay relay, LiveDelivery live, Push.PushQueue push,
        TimeProvider clock, IOptions<CallOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        var o = options.Value;
        if (!Supported(request.V)) return Error(StatusCodes.Status400BadRequest, "unsupported_version");
        if (request.ConversationId is not { } conversation || request.ClientId is not { } clientId || clientId == Guid.Empty)
            return Error(StatusCodes.Status400BadRequest, "missing_fields");
        if (CleanSdp(request.Sdp) is not { } sdp) return Error(StatusCodes.Status400BadRequest, "invalid_sdp");

        // The same start sent again (a retry after a lost answer) is the same call.
        if (await db.Calls.AsNoTracking().SingleOrDefaultAsync(k => k.CallerId == me && k.ClientId == clientId, ct) is { } again)
            return await Described(db, again, me, session, relay, o, ct);

        var (peer, refused) = await Callee(db, relay, o.AppsTakeCalls, conversation, me, clock.GetUtcNow(), ct);
        if (refused is not null) return refused;

        // They are calling me at this moment: that call is the one, so this device shows it as incoming.
        var crossing = await db.Calls.AsNoTracking().Where(k => k.CallerId == peer && k.CalleeId == me && k.State == CallState.Ringing).Select(k => (Guid?)k.Id).FirstOrDefaultAsync(ct);
        if (crossing is { } theirs) return Results.Json(new { error = "call_waiting", id = theirs }, statusCode: StatusCodes.Status409Conflict);

        if (limiter.TryTake(me) is { } wait) return TooMany(http, wait);

        var now = clock.GetUtcNow();
        var call = new Call
        {
            Id = Guid.NewGuid(), ConversationId = conversation, CallerId = me, CalleeId = peer, CallerSessionId = session, ClientId = clientId,
            Version = request.V!.Value, State = CallState.Ringing, CreatedAt = now,
        };
        // One row per person in a call: the key refuses a second call for either of them, atomically.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.Calls.Add(call);
            db.CallParties.AddRange(new CallParty { UserId = me, CallId = call.Id }, new CallParty { UserId = peer, CallId = call.Id });
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (await db.Calls.AsNoTracking().SingleOrDefaultAsync(k => k.CallerId == me && k.ClientId == clientId, ct) is { } raced)
                    return await Described(db, raced, me, session, relay, o, ct);
                if (await db.CallParties.AnyAsync(p => p.UserId == me, ct)) return Error(StatusCodes.Status409Conflict, "in_call");
                // The person called is in another call: the caller hears "busy", and for them it is a missed call.
                call.State = CallState.Ringing;
                db.Calls.Add(call);
                await db.SaveChangesAsync(ct);
                relay.Claim(call.Id, me, session);
                await EndAsync(db, relay, live, push, call.Id, CallOutcome.Busy, now, ct);
                var busy = await db.Calls.AsNoTracking().SingleAsync(k => k.Id == call.Id, ct);
                return await Described(db, busy, me, session, relay, o, ct);
            }
        }
        relay.Offer(call.Id, sdp);
        relay.Claim(call.Id, me, session);
        var caller = await db.Users.Where(u => u.Id == me).Select(u => new MessageEndpoints.PersonDto(u.Id, u.DisplayName)).SingleAsync(ct);
        relay.Send(peer, Frames.CallIncoming(call.Id, conversation, caller));
        relay.Wake(new Push.CallPush(call.Id, peer, Ring: true, Until: now.AddSeconds(o.RingSeconds)));
        return Results.Ok(Describe(call, me, session, relay, o, caller.DisplayName));
    }

    /// <summary>
    /// The servers for one device's connection, asked for before it builds its offer or answer (every address goes in it at once), and
    /// again before the login runs out in a long call (<c>seconds</c>). <c>relay</c> false: no relay this time, a direct connection only.
    /// Only for a call I may make, one ringing for me, or one I am in; counted as calls are.
    /// </summary>
    private static async Task<IResult> Relay(RelayRequest request, HttpContext http, GomDbContext db, CallRelay relay, TurnLogins turn, TurnLimiter limiter,
        TimeProvider clock, IOptions<CallOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        Guid? inCall = null;
        if (request.CallId is { } id)
        {
            var call = await Mine(db, id, me, ct);
            if (call is null) return Results.NotFound();
            if (call.State == CallState.Ended) return Error(StatusCodes.Status409Conflict, "call_ended");
            if (InIt(call, session)) inCall = id;
            else if (!(call.State == CallState.Ringing && call.CalleeId == me)) return Error(StatusCodes.Status403Forbidden, "not_in_call");
        }
        else if (request.ConversationId is { } conversation)
        {
            if ((await Callee(db, relay, options.Value.AppsTakeCalls, conversation, me, clock.GetUtcNow(), ct)).Refused is { } refused) return refused;
        }
        else return Error(StatusCodes.Status400BadRequest, "missing_fields");

        if (limiter.TryTake(me) is { } wait) return TooMany(http, wait);
        var login = await turn.IssueAsync(me, ct);
        if (login.Username is { } name)
        {
            if (inCall is { } call) relay.Issued(call, name);
            else relay.Issued(me, session, name, clock.GetUtcNow());
        }
        return Results.Ok(new { iceServers = login.IceServers, relay = login.Username is not null, seconds = login.Seconds });
    }

    /// <summary>The call, for one of its two people (anyone else: not found).</summary>
    private static Task<Call?> Mine(GomDbContext db, Guid id, Guid me, CancellationToken ct) =>
        db.Calls.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id && (k.CallerId == me || k.CalleeId == me), ct);

    private static async Task<IResult> Get(Guid id, HttpContext http, GomDbContext db, CallRelay relay, IOptions<CallOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var call = await Mine(db, id, me, ct);
        return call is null ? Results.NotFound() : await Described(db, call, me, GomClaims.Session(http.User), relay, options.Value, ct);
    }

    /// <summary>A call that rang past its time is missed, whatever arrives after; the janitor would end it within seconds anyway.</summary>
    private static bool RangOut(Call k, DateTimeOffset now, CallOptions o) => k.State == CallState.Ringing && now - k.CreatedAt >= TimeSpan.FromSeconds(o.RingSeconds);

    private static async Task<IResult> Accept(Guid id, AnswerRequest request, HttpContext http, GomDbContext db, CallRelay relay, LiveDelivery live, Push.PushQueue push, TimeProvider clock,
        IOptions<CallOptions> options, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        var o = options.Value;
        if (!Supported(request.V)) return Error(StatusCodes.Status400BadRequest, "unsupported_version");
        if (CleanSdp(request.Sdp) is not { } sdp) return Error(StatusCodes.Status400BadRequest, "invalid_sdp");
        var call = await Mine(db, id, me, ct);
        if (call is null || call.CalleeId != me) return Results.NotFound();
        var now = clock.GetUtcNow();
        if (call.State == CallState.Ringing && RangOut(call, now, o)) { await EndAsync(db, relay, live, push, id, CallOutcome.Missed, now, ct); return Error(StatusCodes.Status409Conflict, "call_ended"); }
        if (call.State == CallState.Ringing && !await Friends(db, call.CallerId, me, ct)) { await EndAsync(db, relay, live, push, id, CallOutcome.Failed, now, ct); return Error(StatusCodes.Status403Forbidden, "not_friends"); }
        // Without the caller's offer (the server restarted while it rang) there is nothing to answer: the call fails, and is tried again.
        if (call.State == CallState.Ringing && relay.Of(id)?.Offer is null) { await EndAsync(db, relay, live, push, id, CallOutcome.Failed, now, ct); return Error(StatusCodes.Status409Conflict, "call_ended"); }

        // The first device to answer wins; the others hear it was answered elsewhere.
        var won = await db.Calls.Where(k => k.Id == id && k.State == CallState.Ringing).ExecuteUpdateAsync(s => s
            .SetProperty(k => k.State, CallState.Active).SetProperty(k => k.AnswerSessionId, session).SetProperty(k => k.AnsweredAt, now)
            .SetProperty(k => k.CallerSeenAt, now).SetProperty(k => k.CalleeSeenAt, now), ct);
        if (won == 0)
        {
            var state = await db.Calls.Where(k => k.Id == id).Select(k => k.State).SingleAsync(ct);
            return Error(StatusCodes.Status409Conflict, state == CallState.Active ? "call_answered" : "call_ended");
        }
        relay.Answer(id, sdp);
        relay.Claim(id, me, session);
        relay.Send(call.CallerId, Frames.CallAccepted(id, call.ConversationId, sdp), onlySession: call.CallerSessionId);
        relay.Send(me, Frames.CallAnsweredElsewhere(id, call.ConversationId), exceptSession: session);
        relay.Wake(new Push.CallPush(id, me, Ring: false, Until: now, Outcome: "answered", ExceptSession: session));
        var now2 = await db.Calls.AsNoTracking().SingleAsync(k => k.Id == id, ct);
        return await Described(db, now2, me, session, relay, o, ct);
    }

    private static async Task<IResult> Decline(Guid id, HttpContext http, GomDbContext db, CallRelay relay, LiveDelivery live, Push.PushQueue push, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var call = await Mine(db, id, me, ct);
        if (call is null || call.CalleeId != me) return Results.NotFound();
        if (call.State == CallState.Ringing) await EndAsync(db, relay, live, push, id, CallOutcome.Declined, clock.GetUtcNow(), ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Either side ends it. While it rings: the caller giving up makes it a missed call; the person called hanging up declines it.
    /// Once answered: only a device in the call can end it (another of the callee's devices has nothing to hang up). Ended already: nothing.
    /// </summary>
    private static async Task<IResult> HangUp(Guid id, HttpContext http, GomDbContext db, CallRelay relay, LiveDelivery live, Push.PushQueue push, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        var call = await Mine(db, id, me, ct);
        if (call is null) return Results.NotFound();
        var now = clock.GetUtcNow();
        switch (call.State)
        {
            case CallState.Ringing:
                await EndAsync(db, relay, live, push, id, me == call.CallerId ? CallOutcome.Missed : CallOutcome.Declined, now, ct);
                break;
            case CallState.Active when session == call.CallerSessionId || session == call.AnswerSessionId:
                await EndAsync(db, relay, live, push, id, CallOutcome.Completed, now, ct);
                break;
            case CallState.Active:
                return Error(StatusCodes.Status403Forbidden, "not_in_call");
        }
        return Results.NoContent();
    }

    /// <summary>The two devices in an answered call, the only ones that may speak for it.</summary>
    private static bool InIt(Call k, Guid session) => k.State == CallState.Active && (session == k.CallerSessionId || session == k.AnswerSessionId);

    /// <summary>A new offer or answer mid-call (an ICE restart after a network change, or fresh relay logins): passed to the other device.</summary>
    private static async Task<IResult> Renegotiate(Guid id, SdpRequest request, HttpContext http, GomDbContext db, CallRelay relay, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        if (request.Type is not ("offer" or "answer")) return Error(StatusCodes.Status400BadRequest, "invalid_sdp");
        if (CleanSdp(request.Sdp) is not { } sdp) return Error(StatusCodes.Status400BadRequest, "invalid_sdp");
        var call = await Mine(db, id, me, ct);
        if (call is null) return Results.NotFound();
        if (call.State == CallState.Ended) return Error(StatusCodes.Status409Conflict, "call_ended");
        if (!InIt(call, session)) return Error(StatusCodes.Status403Forbidden, "not_in_call");
        var rev = relay.Renegotiate(id, me, request.Type, sdp);
        var (other, otherSession) = me == call.CallerId ? (call.CalleeId, call.AnswerSessionId!.Value) : (call.CallerId, call.CallerSessionId);
        relay.Send(other, Frames.CallSdp(id, call.ConversationId, request.Type, sdp, rev), onlySession: otherSession);
        return Results.Ok(new { rev });
    }

    /// <summary>"Still here", from a device in the call, every <see cref="CallOptions.AliveSeconds"/>. Ended already: it is told so.</summary>
    private static async Task<IResult> Alive(Guid id, HttpContext http, GomDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        var session = GomClaims.Session(http.User);
        var call = await Mine(db, id, me, ct);
        if (call is null) return Results.NotFound();
        if (call.State == CallState.Ended) return Error(StatusCodes.Status409Conflict, "call_ended");
        if (!InIt(call, session)) return Error(StatusCodes.Status403Forbidden, "not_in_call");
        var now = clock.GetUtcNow();
        if (me == call.CallerId) await db.Calls.Where(k => k.Id == id && k.State == CallState.Active).ExecuteUpdateAsync(s => s.SetProperty(k => k.CallerSeenAt, now), ct);
        else await db.Calls.Where(k => k.Id == id && k.State == CallState.Active).ExecuteUpdateAsync(s => s.SetProperty(k => k.CalleeSeenAt, now), ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Ends a call once, however many ask at the same moment: its state, who is in a call, and the line in the chat in one transaction
    /// (the conversation locked as a send locks it); then, after the commit, both people's devices hear it and the line goes out as any
    /// notice does. <paramref name="at"/> is when it ended (for a call that went silent, when it was last heard from). False when it had
    /// ended already.
    /// </summary>
    public static async Task<bool> EndAsync(GomDbContext db, CallRelay relay, LiveDelivery live, Push.PushQueue push, Guid id, CallOutcome outcome, DateTimeOffset at, CancellationToken ct)
    {
        Message notice;
        Call call;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var conversation = await db.Calls.Where(k => k.Id == id).Select(k => (Guid?)k.ConversationId).SingleOrDefaultAsync(ct);
            if (conversation is null) return false;
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM conversations WHERE id = {conversation.Value} FOR UPDATE", ct);
            var ended = await db.Calls.Where(k => k.Id == id && k.State != CallState.Ended).ExecuteUpdateAsync(s => s
                .SetProperty(k => k.State, CallState.Ended).SetProperty(k => k.Outcome, outcome).SetProperty(k => k.EndedAt, at), ct);
            if (ended == 0) return false;
            await db.CallParties.Where(p => p.CallId == id).ExecuteDeleteAsync(ct);
            call = await db.Calls.AsNoTracking().SingleAsync(k => k.Id == id, ct);
            notice = await Notices.Write(db, call.ConversationId, call.CallerId, NoticeKind.Call, null, "", at, ct);
            await db.Calls.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.MessageId, notice.Id), ct);
            // Unlike other notices, a call's line is what last happened here: the conversation rises in the list.
            await db.Conversations.Where(c => c.Id == call.ConversationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageAt, at), ct);
            await tx.CommitAsync(ct);
        }
        relay.Forget(id);
        var frame = Frames.CallEnded(id, call.ConversationId, Name(outcome));
        relay.Send(call.CallerId, frame);
        relay.Send(call.CalleeId, frame);
        // A phone still ringing from a push stops (and says "missed" when it was); an answered call's other devices were told at the answer.
        if (call.AnsweredAt is null) relay.Wake(new Push.CallPush(id, call.CalleeId, Ring: false, Until: at, Outcome: Name(outcome)));
        await Notices.Publish(db, live, notice, ct);
        if (outcome is CallOutcome.Missed or CallOutcome.Busy)
        {
            var caller = await db.Users.Where(u => u.Id == call.CallerId).Select(u => u.DisplayName).SingleAsync(ct);
            push.Enqueue(new Push.PushJob(call.ConversationId, notice.Seq, call.CallerId, caller, 0, 0, MessageId: notice.Id, MissedCall: true));
        }
        return true;
    }

    /// <summary>
    /// Takes away call records with their lines in the chat (an account deleted: docs/voice-calls.md, decision 16). Inside the caller's
    /// transaction; returns the calls that had not ended, so their other side can be told after the commit.
    /// </summary>
    public static async Task<List<(Guid Id, Guid Conversation, Guid Caller, Guid Callee)>> DeleteRecords(GomDbContext db, Guid person, CancellationToken ct)
    {
        var calls = await db.Calls.Where(k => k.CallerId == person || k.CalleeId == person)
            .Select(k => new { k.Id, k.ConversationId, k.CallerId, k.CalleeId, k.State, k.MessageId }).ToListAsync(ct);
        if (calls.Count == 0) return [];
        var ids = calls.Select(k => k.Id).ToList();
        var lines = calls.Where(k => k.MessageId != null).Select(k => k.MessageId!.Value).ToList();
        await db.CallParties.Where(p => ids.Contains(p.CallId)).ExecuteDeleteAsync(ct);
        await db.Calls.Where(k => ids.Contains(k.Id)).ExecuteDeleteAsync(ct);
        await db.Hides.Where(h => lines.Contains(h.MessageId)).ExecuteDeleteAsync(ct);
        await db.Events.Where(e => lines.Contains(e.MessageId)).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => lines.Contains(m.Id)).ExecuteDeleteAsync(ct);
        return [.. calls.Where(k => k.State != CallState.Ended).Select(k => (k.Id, k.ConversationId, k.CallerId, k.CalleeId))];
    }
}

/// <summary>
/// Every few seconds: a call that rang out is missed; an answered call a side has gone silent on ends (its length up to when that side
/// was last heard); and a call that may no longer go on ends: the two are no longer friends (an unfriend, a block), an account was
/// locked or deleted, or a device in it was signed out (docs/voice-calls.md, decision 16). Each step also checks what it needs, so this
/// is for what happens while nobody is asking.
/// </summary>
public static class CallSweeper
{
    public static async Task<int> RunOnce(GomDbContext db, CallRelay relay, LiveDelivery live, Push.PushQueue push, DateTimeOffset now, CallOptions o, CancellationToken ct)
    {
        var count = 0;
        // A login is asked for just before calling or answering; one still unclaimed after a whole ring (and some slack) never will be.
        relay.RevokeUnclaimed(now.AddSeconds(-(o.RingSeconds + 15)));
        // Logins of a call that ended without this server knowing (a race with a fresh login, a call deleted with an account).
        var holding = relay.HoldingLogins();
        if (holding.Count > 0)
        {
            var going = await db.Calls.Where(k => holding.Contains(k.Id) && k.State != CallState.Ended).Select(k => k.Id).ToListAsync(ct);
            foreach (var id in holding.Except(going)) relay.Forget(id);
        }
        var rangOut = now.AddSeconds(-o.RingSeconds);
        foreach (var id in await db.Calls.Where(k => k.State == CallState.Ringing && k.CreatedAt <= rangOut).Select(k => k.Id).ToListAsync(ct))
            if (await CallEndpoints.EndAsync(db, relay, live, push, id, CallOutcome.Missed, now, ct)) count++;

        var silent = now.AddSeconds(-o.SilentSeconds);
        foreach (var k in await db.Calls.Where(k => k.State == CallState.Active && (k.CallerSeenAt <= silent || k.CalleeSeenAt <= silent))
                     .Select(k => new { k.Id, k.CallerSeenAt, k.CalleeSeenAt }).ToListAsync(ct))
        {
            var last = k.CallerSeenAt < k.CalleeSeenAt ? k.CallerSeenAt!.Value : k.CalleeSeenAt!.Value;
            if (await CallEndpoints.EndAsync(db, relay, live, push, k.Id, CallOutcome.Completed, last, ct)) count++;
        }

        var broken = await (
            from k in db.Calls
            where k.State != CallState.Ended
            let friends = db.Friendships.Any(f => f.Status == FriendStatus.Accepted
                && ((f.UserLow == k.CallerId && f.UserHigh == k.CalleeId) || (f.UserLow == k.CalleeId && f.UserHigh == k.CallerId)))
            let people = db.Users.Count(u => (u.Id == k.CallerId || u.Id == k.CalleeId) && u.DisabledAt == null) == 2
            let callerDevice = db.Sessions.Any(s => s.Id == k.CallerSessionId && s.RevokedAt == null && s.ExpiresAt > now)
            let calleeDevice = k.AnswerSessionId == null || db.Sessions.Any(s => s.Id == k.AnswerSessionId && s.RevokedAt == null && s.ExpiresAt > now)
            where !friends || !people || !callerDevice || !calleeDevice
            select new { k.Id, k.State }).ToListAsync(ct);
        foreach (var k in broken)
            if (await CallEndpoints.EndAsync(db, relay, live, push, k.Id, k.State == CallState.Active ? CallOutcome.Completed : CallOutcome.Failed, now, ct)) count++;
        return count;
    }
}

public sealed class CallJanitor(IServiceScopeFactory scopes, IOptions<CallOptions> options, CallRelay relay, TimeProvider clock, ILogger<CallJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await CallSweeper.RunOnce(scope.ServiceProvider.GetRequiredService<GomDbContext>(), relay, scope.ServiceProvider.GetRequiredService<LiveDelivery>(),
                        scope.ServiceProvider.GetRequiredService<Push.PushQueue>(),
                        clock.GetUtcNow(), options.Value, stop);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Ending stale calls failed; it will be tried again"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}
