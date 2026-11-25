using Gom.Web;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Gom.Web.Uploads;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Behind a proxy chain the visitor's address and "https" arrive in X-Forwarded-*. ForwardedHeaders:Hops is how many
// proxies sit in front of the app (staging: nginx, then cloudflared, so 2). It is 0 by default, so a copy on a laptop
// never believes a header a visitor sends. When it is on, the app must be reachable only through the proxy: callers are
// not checked, the number of hops is what picks the visitor's address out of the header.
var proxyHops = builder.Configuration.GetValue<int?>("ForwardedHeaders:Hops") ?? 0;
if (proxyHops < 0) throw new InvalidOperationException("ForwardedHeaders:Hops must be 0 (no proxy) or a whole number above 0.");
if (proxyHops > 0)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = proxyHops;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
    });

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
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.AddSingleton<IBlobStore>(sp => new DiskBlobStore(sp.GetRequiredService<IOptions<UploadOptions>>().Value.Path));
builder.Services.AddSingleton<UploadLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<UploadOptions>>().Value;
    return new UploadLimiter(new SendLimiter(sp.GetRequiredService<TimeProvider>(), o.UploadBurst, o.UploadPerSecond));
});
builder.Services.AddSingleton<UploadMemory>(sp =>
{
    var o = sp.GetRequiredService<IOptions<UploadOptions>>().Value;
    // The container's own limit when it has one (the runtime reads it): pictures may use 35% of it, uploads being received 25%.
    var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    var most = Math.Max(o.MaxFileBytes, o.MaxImageBytes);
    var images = o.MemoryMegabytes > 0 ? o.MemoryMegabytes * 1024L * 1024 : Math.Max(48L * 1024 * 1024, available * 35 / 100);
    var bodies = o.MemoryMegabytes > 0 ? Math.Max(o.MemoryMegabytes * 1024L * 1024, most) : Math.Max(most, available * 25 / 100);
    return new UploadMemory(new MemoryGate(bodies), new MemoryGate(images));
});
builder.Services.AddSingleton<ImageLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<UploadOptions>>().Value;
    return new ImageLimiter(o.ImageSlots > 0 ? o.ImageSlots : Math.Max(1, Environment.ProcessorCount / 2), TimeSpan.FromSeconds(o.ImageWaitSeconds));
});
builder.Services.AddHostedService<AttachmentJanitor>();
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

if (proxyHops > 0) app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    // A fingerprinted file never changes under its name: kept for a year. Fonts never change under one name either: a week. Every
    // other file is checked with the server (ETag) on each use.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
        Assets.IsFingerprinted(c.File.Name) ? "public, max-age=31536000, immutable"
        : c.File.Name.EndsWith(".woff2", StringComparison.Ordinal) ? "public, max-age=604800" : "no-cache",
});
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
app.MapAttachments();
app.MapReceipts();
app.MapSocket();

// One small HTML file per page; the script in /js/main.js picks the page from data-page. The files a page loads are named by their
// fingerprinted copies (see Assets), so a browser may keep them.
var fingerprints = Assets.Load(app.Environment.WebRootFileProvider, app.Logger);
void MapPage(string route, string file) => app.MapMethods(route, ["GET", "HEAD"], (IWebHostEnvironment env) =>
{
    using var reader = new StreamReader(env.WebRootFileProvider.GetFileInfo($"pages/{file}").CreateReadStream());
    return Results.Content(Assets.Apply(reader.ReadToEnd(), fingerprints), "text/html; charset=utf-8");
});
MapPage("/", "home.html");
MapPage("/c/{id}", "home.html");
MapPage("/sign-in", "sign-in.html");
MapPage("/join/{token}", "join.html");
MapPage("/reset/{token}", "reset.html");
MapPage("/settings", "settings.html");
MapPage("/friends", "friends.html");
MapPage("/groups/new", "newgroup.html");
MapPage("/groups/{id}", "group.html");
MapPage("/add/{gomId}", "add.html");
MapPage("/owner", "owner.html");

// 200 only when the database answers; a monitor or a proxy can poll this.
app.MapMethods("/healthz", ["GET", "HEAD"], async (GomDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

// Lets the test project start the app in memory.
public partial class Program;
