using System.Security.Claims;
using System.Threading.RateLimiting;
using LabFlow.Api.Auth;
using LabFlow.Api.Data;
using LabFlow.Api.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LabFlow")
    ?? throw new InvalidOperationException("ConnectionStrings:LabFlow is not configured.");
builder.Services.AddDbContext<LabFlowDbContext>(o => o.UseSqlServer(connectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Demo sign-in exists only in Development. Production will add Entra ID here instead,
// and without it every protected endpoint simply returns 401.
var auth = builder.Services.AddAuthentication(DemoAuthHandler.SchemeName);
if (builder.Environment.IsDevelopment())
    auth.AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuthHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.FrontDesk, p => p.RequireRole("Reception", "Collector", "Admin"));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(BookingEndpoints.BookingRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/me", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
    ? Results.Ok(new { userId = user.UserId(), name = user.Identity.Name, role = user.FindFirstValue(ClaimTypes.Role) })
    : Results.Ok(new { userId = (int?)null, name = (string?)null, role = (string?)null }));

if (app.Environment.IsDevelopment())
{
    // Feeds the role switcher in the React app.
    app.MapGet("/api/dev/users", async (LabFlowDbContext db) =>
        await db.AppUsers.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UserId)
            .Select(u => new { u.ExternalId, u.DisplayName, u.Role }).ToListAsync());
}

app.MapBookingEndpoints();
app.MapReceptionEndpoints();

app.Run();

public partial class Program;
