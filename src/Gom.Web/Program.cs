using Gom.Web;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Push;
using Gom.Web.Social;
using Gom.Web.Uploads;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

// "--vapid-keys" prints a new key pair for notifications (Push:PublicKey and Push:PrivateKey) and stops; nothing else starts.
if (args.Contains("--vapid-keys"))
{
    var (publicKey, privateKey) = WebPushCrypto.NewVapidKeys();
    Console.WriteLine($"Push__PublicKey={publicKey}");
    Console.WriteLine($"Push__PrivateKey={privateKey}");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// Behind a proxy chain the visitor's address and "https" arrive in X-Forwarded-*. ForwardedHeaders:Hops is how many
// proxies sit in front of the app (staging and production: two, so 2). It is 0 by default, so a copy on a laptop
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

// Everything environment-specific comes from configuration, never from code. ConnectionSettings turns GSS encryption off and
// caps the pool unless the setting names a size.
var connectionString = ConnectionSettings.For(builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set."));

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
builder.Services.AddHostedService<DisappearingJanitor>();
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection("Push"));
builder.Services.Configure<ContactOptions>(builder.Configuration.GetSection("Contact"));
builder.Services.Configure<CallOptions>(builder.Configuration.GetSection("Calls"));
builder.Services.AddSingleton<CallRelay>();
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CallOptions>>().Value;
    return new CallLimiter(new SendLimiter(sp.GetRequiredService<TimeProvider>(), o.CallBurst, 1.0 / Math.Max(1, o.SecondsBetweenCalls)));
});
builder.Services.AddHostedService<CallJanitor>();
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection("Turn"));
(builder.Configuration.GetSection("Turn").Get<TurnOptions>() ?? new TurnOptions()).Check();
builder.Services.AddHttpClient(CloudflareTurnClient.Client, c => c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<ITurnClient, CloudflareTurnClient>();
builder.Services.AddSingleton<TurnLogins>();
builder.Services.AddSingleton<TurnRevoker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TurnRevoker>());
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CallOptions>>().Value;
    return new TurnLimiter(new SendLimiter(sp.GetRequiredService<TimeProvider>(), o.CallBurst, 1.0 / Math.Max(1, o.SecondsBetweenCalls)));
});
builder.Services.AddHostedService<ContactJanitor>();
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddSingleton(FcmAccount.Parse(builder.Configuration["Fcm:ServiceAccount"]));
builder.Services.AddHttpClient(GoogleFcmTransport.Client, c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<IFcmTransport, GoogleFcmTransport>();
builder.Services.AddSingleton<CallPushQueue>();
builder.Services.AddSingleton<CallPusher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CallPusher>());
builder.Services.AddHttpClient(HttpPushTransport.Client)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<IPushTransport, HttpPushTransport>();
builder.Services.AddSingleton<PushSender>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PushSender>());
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<SignInThrottle>(sp => new SignInThrottle(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<HashLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
    var slots = o.MaxConcurrentHashes > 0 ? o.MaxConcurrentHashes : Math.Max(1, Environment.ProcessorCount / 2);
    return new HashLimiter(slots, TimeSpan.FromSeconds(o.HashWaitSeconds));
});
builder.Services.AddAuthentication(GomClaims.Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(GomClaims.Scheme, null);
builder.Services.AddAuthorization(o =>
{
    // Everything signed in is for members: a request to join (a "standing" claim) is refused everywhere but where "SignedIn" says.
    o.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
        .RequireAssertion(c => !c.User.HasClaim(x => x.Type == GomClaims.Standing)).Build();
    o.AddPolicy("SignedIn", p => p.RequireAuthenticatedUser());
    o.AddPolicy("Owner", p => p.RequireAuthenticatedUser().RequireClaim(GomClaims.IsOwner, "true"));
});
builder.Services.AddSingleton(sp => builder.Configuration.GetSection("Owner").Get<OwnerOptions>() ?? new OwnerOptions());
builder.Services.AddSingleton<OwnerSeeder>();
builder.Services.AddHostedService<OwnerSeedingService>();
builder.Services.AddHostedService<Gom.Web.Chat.AnnouncementsSeedingService>();   // after the owner exists

var app = builder.Build();

// "--two-step-off <gomId>": for an owner who lost the phone with their codes (a member gets the owner's reset link instead). Turns
// two-step sign-in off for that account, prints what happened and stops; the server keeps running where it runs.
if (args.Contains("--two-step-off"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var gomIdArg = args.SkipWhile(a => a != "--two-step-off").Skip(1).FirstOrDefault();
    Console.WriteLine(await TwoStepEndpoints.TurnOffFromCommand(scope.ServiceProvider.GetRequiredService<GomDbContext>(), gomIdArg, CancellationToken.None));
    return;
}

if (proxyHops > 0) app.UseForwardedHeaders();
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    // A fingerprinted file never changes under its name: kept for a year. Fonts, stickers and backgrounds are kept a week: one
    // that is redrawn gets a new address (a background ?v=N in Styles/30-thread.css), or browsers show the old one until then.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
        Assets.IsFingerprinted(c.File.Name) ? "public, max-age=31536000, immutable"
        : c.File.Name.EndsWith(".woff2", StringComparison.Ordinal) || c.Context.Request.Path.StartsWithSegments("/stickers") || c.Context.Request.Path.StartsWithSegments("/patterns") ? "public, max-age=604800" : "no-cache",
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
app.UseMiddleware<OriginCheck>();
// A page too old for this server (Pages.PageVersion) is told to reload instead of being answered; the socket checks its own.
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments("/api") && Pages.TooOld(http.Request.Headers[Pages.PageVersionHeader]))
    {
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        await http.Response.WriteAsJsonAsync(new { error = "update_required" });
        return;
    }
    await next(http);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapLinks();
app.MapSignUp();
app.MapContact();
app.MapCalls();
app.MapAccount();
app.MapTwoStep();
app.MapDisappearing();
app.MapStickerPacks();
app.MapFriends();
app.MapConversations();
app.MapGroups();
app.MapStorage();
app.MapPeople();
app.MapMessages();
app.MapAttachments();
app.MapAvatars();
app.MapReceipts();
app.MapPush();
// The fingerprinted names of the files pages load; the page script's fingerprint is the version the socket tells each page.
var fingerprints = Assets.Load(app.Environment.WebRootFileProvider, app.Logger);
app.MapSocket(Assets.Version(fingerprints));

// Every page is one shell with its title, script and limits filled in (see Pages), made once at start-up.
string shell;
using (var reader = new StreamReader(app.Environment.WebRootFileProvider.GetFileInfo("pages/shell.html").CreateReadStream())) shell = await reader.ReadToEndAsync();
var limits = Pages.Limits(
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Gom.Web.Chat.ChatOptions>>().Value,
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Gom.Web.Uploads.UploadOptions>>().Value,
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Gom.Web.Auth.AuthOptions>>().Value,
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContactOptions>>().Value);
var site = app.Configuration.GetSection("Site").Get<SiteOptions>() ?? new SiteOptions();
foreach (var page in Pages.All)
{
    var html = Assets.Apply(Pages.Render(shell, page, limits, site), fingerprints);
    app.MapMethods(page.Route, ["GET", "HEAD"], () => Results.Content(html, "text/html; charset=utf-8"));
}
foreach (var (from, to) in Pages.Moved) app.MapMethods(from, ["GET", "HEAD"], () => Results.Redirect(to));

// 200 only when the database answers; a monitor or a proxy can poll this.
app.MapMethods("/healthz", ["GET", "HEAD"], async (GomDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

await app.RunAsync();

// Lets the test project start the app in memory.
public partial class Program;
