using System.Net.Http.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gom.Tests;

/// <summary>Moves only when a test says so.</summary>
public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>The whole app on a throwaway database, with a fake clock and a cheap password hash.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string Origin = "https://gom.test";
    public const string Password = "correct horse battery";

    public TestDatabase Db { get; } = new();
    public FakeClock Clock { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public PasswordService Passwords { get; } = new(1_000);

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", Db.ConnectionString);
            b.UseSetting("Auth:PasswordIterations", "1000");
            b.UseSetting("Auth:AllowedOrigins:0", Origin);
            b.UseSetting("Chat:MaxGroupMembers", "5");   // small, so a test can fill a group
            b.ConfigureServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IStartupFilter, TestIpFilter>();
            });
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    private int _nextIp;

    /// <summary>A client with an address of its own, so one test's failed sign-ins never count against another's.</summary>
    public HttpClient Client()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(Origin) });
        var n = Interlocked.Increment(ref _nextIp);
        client.DefaultRequestHeaders.Add("X-Test-Ip", $"10.1.{n / 250}.{n % 250 + 1}");
        return client;
    }

    public async Task<User> AddUser(string? gomId = null, string password = Password, bool owner = false, bool disabled = false)
    {
        gomId ??= "u_" + Guid.NewGuid().ToString("N")[..10];
        await using var db = Db.NewContext();
        var user = new User
        {
            Id = Guid.NewGuid(), GomId = gomId, DisplayName = gomId, PasswordHash = Passwords.Hash(password),
            IsOwner = owner, CreatedAt = Clock.Now, DisabledAt = disabled ? Clock.Now : null,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Signs in and returns the cookie value, or fails the test.</summary>
    public async Task<string> SignIn(string gomId, string password = Password, string userAgent = "TestAgent")
    {
        using var client = Client();
        using var response = await Send(client, HttpMethod.Post, "/api/session", new { gomId, password }, userAgent: userAgent);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return CookieValue(response)!;
    }

    public static string? CookieValue(HttpResponseMessage response)
    {
        var header = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "=")) : null;
        return header?.Split(';')[0][(SessionCookie.Name.Length + 1)..];
    }

    public static string? SetCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "=")) : null;

    public static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? body = null,
        string? cookie = null, string? origin = Origin, string userAgent = "TestAgent")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (cookie is not null) request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return client.SendAsync(request);
    }

    private (User User, string Cookie)? _owner;

    /// <summary>The one owner of this database, signed in. Made on first use.</summary>
    public async Task<(User User, string Cookie)> Owner()
    {
        if (_owner is null)
        {
            var user = await AddUser("the_owner", owner: true);
            _owner = (user, await SignIn(user.GomId));
        }
        return _owner.Value;
    }

    public async Task<HttpResponseMessage> Call(HttpMethod method, string path, object? body = null, string? cookie = null, string? origin = Origin)
    {
        using var client = Client();
        return await Send(client, method, path, body, cookie, origin);
    }

    public async Task<string> NewInviteToken()
    {
        var (_, cookie) = await Owner();
        using var response = await Call(HttpMethod.Post, "/api/invites", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString()!;
    }

    /// <summary>A new member, signed in.</summary>
    public async Task<(User User, string Cookie)> Member(string? gomId = null)
    {
        var user = await AddUser(gomId);
        return (user, await SignIn(user.GomId));
    }

    /// <summary>Makes two people friends directly in the database (the friend endpoints have their own tests).</summary>
    public async Task Befriend(User a, User b)
    {
        var (low, high) = Friendship.Order(a.Id, b.Id);
        await using var db = Db.NewContext();
        db.Friendships.Add(new Friendship { UserLow = low, UserHigh = high, RequestedBy = a.Id, Status = FriendStatus.Accepted, CreatedAt = Clock.Now, AcceptedAt = Clock.Now });
        await db.SaveChangesAsync();
    }

    /// <summary>Opens the one-to-one conversation between the signed-in person and a friend; returns its id.</summary>
    public async Task<Guid> OpenChat(string cookie, string friendGomId)
    {
        using var r = await Call(HttpMethod.Post, "/api/conversations/direct", new { gomId = friendGomId }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    public static async Task<System.Text.Json.JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

    public Task<List<Session>> SessionsOf(Guid userId) => ReadSessions(userId);

    private async Task<List<Session>> ReadSessions(Guid userId)
    {
        await using var db = Db.NewContext();
        return await db.Sessions.Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt).ToListAsync();
    }
}

/// <summary>Tests only: lets a request say which address it came from (a test server has none).</summary>
public sealed class TestIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip)) context.Connection.RemoteIpAddress = IPAddress.Parse(ip!);
            return nextMiddleware();
        });
        next(app);
    };
}
