using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Everything environment-specific comes from configuration, never from code.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");

builder.Services.AddDbContext<GomDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<LiveDelivery>();
builder.Services.AddSingleton<SendLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<ChatOptions>>().Value;
    return new SendLimiter(sp.GetRequiredService<TimeProvider>(), o.SendBurst, o.SendPerSecond);
});
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SignInThrottle>(sp => new SignInThrottle(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<HashLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
    var slots = o.MaxConcurrentHashes > 0 ? o.MaxConcurrentHashes : Math.Max(1, Environment.ProcessorCount / 2);
    return new HashLimiter(slots, TimeSpan.FromSeconds(o.HashWaitSeconds));
});
builder.Services.AddAuthentication(GomClaims.Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(GomClaims.Scheme, null);
builder.Services.AddAuthorization(o => o.AddPolicy("Owner", p => p.RequireAuthenticatedUser().RequireClaim(GomClaims.IsOwner, "true")));
builder.Services.AddSingleton(sp => builder.Configuration.GetSection("Owner").Get<OwnerOptions>() ?? new OwnerOptions());
builder.Services.AddSingleton<OwnerSeeder>();
builder.Services.AddHostedService<OwnerSeedingService>();

var app = builder.Build();

app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
app.UseMiddleware<OriginCheck>();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapLinks();
app.MapAccount();
app.MapFriends();
app.MapConversations();
app.MapGroups();
app.MapMessages();
app.MapSocket();

// One small HTML file per page; the script in /js/main.js picks the page from data-page.
void MapPage(string route, string file) => app.MapGet(route, (IWebHostEnvironment env) =>
    Results.File(env.WebRootFileProvider.GetFileInfo($"pages/{file}").CreateReadStream(), "text/html; charset=utf-8"));
MapPage("/", "home.html");
MapPage("/c/{id}", "home.html");
MapPage("/sign-in", "sign-in.html");
MapPage("/join/{token}", "join.html");
MapPage("/reset/{token}", "reset.html");
MapPage("/settings", "settings.html");
MapPage("/friends", "friends.html");
MapPage("/add/{gomId}", "add.html");
MapPage("/owner", "owner.html");

// 200 only when the database answers; a monitor or a proxy can poll this.
app.MapGet("/healthz", async (GomDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

// Lets the test project start the app in memory.
public partial class Program;
