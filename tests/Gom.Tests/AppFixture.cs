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
