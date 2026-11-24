using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Everything environment-specific comes from configuration, never from code.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");

builder.Services.AddDbContext<GomDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<PasswordService>();

var app = builder.Build();

// 200 only when the database answers; a monitor or a proxy can poll this.
app.MapGet("/healthz", async (GomDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

// Lets the test project start the app in memory.
public partial class Program;
