using System.Security.Claims;
using System.Threading.RateLimiting;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();

// Connected to ClinicHub: staff sign in there, and the database is the lab's (or, configured so, the clinic's own).
var connected = builder.Services.AddClinicHubSignIn(builder.Configuration);
if (!connected)
{
    var connectionString = builder.Configuration.GetConnectionString("LabFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:LabFlow is not configured.");
    builder.Services.AddDbContext<LabFlowDbContext>(o => o.UseSqlServer(connectionString));
}

builder.Services.AddScoped<AuditLogger>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConfidentialityGate>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Not connected, the demo sign-in exists only in Development; elsewhere every protected endpoint returns 401.
if (!connected)
{
    var auth = builder.Services.AddAuthentication(DemoAuthHandler.SchemeName);
    if (builder.Environment.IsDevelopment())
        auth.AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuthHandler.SchemeName, null);
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.FrontDesk, p => p.RequireClaim(LabPermissions.ClaimType, LabPermissions.FrontDesk, LabPermissions.Admin))
    .AddPolicy(Policies.ResultsEntry, p => p.RequireClaim(LabPermissions.ClaimType, LabPermissions.ResultsEnter, LabPermissions.Admin));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(BookingEndpoints.BookingRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// One lab database: apply the upgrade scripts (050 on) it may not have yet. A database per clinic is upgraded when it
// is first opened.
if (!app.Services.GetRequiredService<LabDatabases>().PerClinic)
    await app.Services.GetRequiredService<LabDatabases>().UpgradeAsync(builder.Configuration.GetConnectionString("LabFlow")!);

app.UseExceptionHandler();
app.UseStatusCodePages();
// Inside the exception handler, so this answers before it turns the exception into a 500.
// A database per clinic and no clinic named: a clear answer instead of a server error.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (NoClinicException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { title = ex.Message });
    }
});
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
if (connected)
    app.UseCsrfHeaderCheck();
app.UseRateLimiter();

app.MapGet("/api/me", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
    ? Results.Ok(new
    {
        userId = user.UserId(), name = user.Identity.Name, role = user.FindFirstValue(ClaimTypes.Role),
        permissions = user.FindAll(LabPermissions.ClaimType).Select(c => c.Value).ToArray(),
        clinic = user.FindFirstValue(ClinicHubClaims.ClinicName),
    })
    : Results.Ok(new { userId = (int?)null, name = (string?)null, role = (string?)null, permissions = Array.Empty<string>(), clinic = (string?)null }));

// How people sign in, for the React app: "clinichub" or the development "demo" menu.
app.MapGet("/api/auth/settings", (IServiceProvider services) => new
{
    signIn = connected ? "clinichub" : "demo",
    perClinicDatabase = connected && services.GetRequiredService<LabDatabases>().PerClinic,
});

if (connected)
{
    app.MapGet("/api/auth/sso/start", (string? returnUrl) => Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/reception" },
        [ClinicHubConnection.Scheme]));
    // One sign-out: here and at ClinicHub.
    app.MapGet("/api/auth/sso/signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, ClinicHubConnection.Scheme]));
}

if (app.Environment.IsDevelopment() && !connected)
{
    // Feeds the role switcher in the React app.
    app.MapGet("/api/dev/users", async (LabFlowDbContext db) =>
        await db.AppUsers.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UserId)
            .Select(u => new { u.ExternalId, u.DisplayName, u.Role }).ToListAsync());
}

app.MapBookingEndpoints();
app.MapReceptionEndpoints();
app.MapOrderEndpoints();
app.MapResultEndpoints();

app.Run();

public partial class Program;
