using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabFlow.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LabFlow.Api.Auth;

/// <summary>
/// Connected mode: LabFlow as one of ClinicHub's applications (https://github.com/ArashKeshtgar/ClinicHub). Staff
/// sign in through ClinicHub, which decides who may use LabFlow and with which permissions. Without this section
/// LabFlow keeps its development sign-in only.
/// </summary>
public class ClinicHubOptions
{
    /// <summary>ClinicHub's address (its OpenID Connect issuer). Empty: not connected.</summary>
    public string? Authority { get; set; }
    public string ClientId { get; set; } = "labflow";
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Empty: one lab database (ConnectionStrings:LabFlow) for every clinic, the usual Ontario arrangement of a lab
    /// as its own business. A connection string containing {clinic} gives each clinic its own LabFlow database,
    /// built from database/*.sql at the clinic's first sign-in.
    /// </summary>
    public string? DatabasePattern { get; set; }

    public int RecheckMinutes { get; set; } = 5;

    /// <summary>How often released results waiting for ClinicHub are sent. 0 turns the worker off (tests send on demand).</summary>
    public int SendSeconds { get; set; } = 5;
    public int OfflineGraceMinutes { get; set; } = 30;
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
    public bool PerClinic => DatabasePattern?.Contains("{clinic}") == true;
}

public static class ClinicHubClaims
{
    /// <summary>ClinicHub's clinic_slug: the clinic the person signed in for.</summary>
    public const string Clinic = "clinic";
    public const string ClinicName = "clinic_name";
    public const string CheckedAt = "checked_at";

    /// <summary>Anonymous booking with a database per clinic names the clinic in this header.</summary>
    public const string ClinicHeader = "X-LabFlow-Clinic";
}

/// <summary>Which LabFlow database serves a request, and building a clinic's database the first time.</summary>
public class LabDatabases(IConfiguration config, IOptions<ClinicHubOptions> options, ILogger<LabDatabases> logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _prepared = new();

    public bool PerClinic => options.Value.PerClinic;

    public static bool IsValidSlug(string? slug) =>
        slug is { Length: >= 2 and <= 40 } && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public string ConnectionString(string? slug)
    {
        if (!PerClinic)
            return config.GetConnectionString("LabFlow") ?? throw new InvalidOperationException("ConnectionStrings:LabFlow is not configured.");
        if (!IsValidSlug(slug))
            throw new NoClinicException();
        return options.Value.DatabasePattern!.Replace("{clinic}", slug);
    }

    public static LabFlowDbContext Create(string connectionString) =>
        new(new DbContextOptionsBuilder<LabFlowDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>With a database per clinic: creates it and runs the schema and reference scripts once. Otherwise nothing.</summary>
    public Task PrepareAsync(string slug) =>
        PerClinic ? _prepared.GetOrAdd(slug, s => new Lazy<Task>(() => BuildAsync(s))).Value : Task.CompletedTask;

    /// <summary>Anonymous booking may only reach a clinic database that has been built (no database is made for a stranger).</summary>
    public async Task<bool> ExistsAsync(string slug)
    {
        if (_prepared.ContainsKey(slug))
            return true;
        var target = new SqlConnectionStringBuilder(ConnectionString(slug));
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n", master);
        cmd.Parameters.AddWithValue("@n", target.InitialCatalog);
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private async Task BuildAsync(string slug)
    {
        var target = new SqlConnectionStringBuilder(ConnectionString(slug));
        var name = target.InitialCatalog;
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync();
            // Same as database/001_database.sql, which deploy.ps1 runs through sqlcmd variables.
            await Exec(master, $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;");
            await Exec(master, $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;");
        }
        await using var db = new SqlConnection(target.ConnectionString);
        await db.OpenAsync();
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM sys.schemas WHERE name = 'lab'", db))
            if ((int)(await check.ExecuteScalarAsync())! == 1)
            {
                // Built before: only the upgrade scripts, which are safe to run again.
                await RunScriptsAsync(db, name, UpgradeScripts());
                return;
            }
        // Schema, sequences and reference data. Not the database itself (made above), the synthetic patients, or the
        // warehouse and legacy-test mapping, which belong to migrating the original lab's data, not to a new clinic.
        await RunScriptsAsync(db, name, Scripts(f => !f.StartsWith("001_") && !f.Contains("_seed_demo") && !f.Contains("_etl_") && !f.Contains("_legacy_")));
    }

    /// <summary>
    /// Scripts from 050 on change existing databases and are written to be run again safely (IF OBJECT_ID … IS NULL).
    /// deploy.ps1 runs them on a new database like any other; LabFlow runs them on start-up for a database built before.
    /// </summary>
    public static IEnumerable<string> UpgradeScripts() => Scripts(f => string.CompareOrdinal(f, "050") >= 0);

    private static IEnumerable<string> Scripts(Func<string, bool> include) =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "database"), "*.sql")
            .Select(f => Path.GetFileName(f))
            .Where(f => Regex.IsMatch(f, @"^\d{3}_") && include(f))
            .Order()
            .Select(f => Path.Combine(AppContext.BaseDirectory, "database", f));

    public async Task UpgradeAsync(string connectionString)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        await RunScriptsAsync(db, db.Database, UpgradeScripts());
    }

    private async Task RunScriptsAsync(SqlConnection db, string name, IEnumerable<string> scripts)
    {
        foreach (var script in scripts)
        {
            logger.LogInformation("{Database}: {Script}", name, Path.GetFileName(script));
            foreach (var batch in Regex.Split(await File.ReadAllTextAsync(script), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                    await Exec(db, batch);
        }
    }

    private static async Task Exec(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>A database per clinic, and the request does not say which clinic.</summary>
public class NoClinicException() : Exception("Which clinic? Sign in through ClinicHub, or give the clinic for booking.");

public static class ClinicHubConnection
{
    public const string Scheme = "ClinicHub";

    /// <summary>ClinicHub calling LabFlow itself (orders): a bearer token signed by ClinicHub, for this audience.</summary>
    public const string ServiceScheme = "ClinicHubService";
    public const string OrdersPolicy = "clinichub.orders";
    public const string CsrfHeader = "X-LabFlow";

    public static bool IsConnected(this IServiceProvider services) =>
        services.GetRequiredService<IOptions<ClinicHubOptions>>().Value.Enabled;

    /// <summary>Cookie sign-in through ClinicHub, and the database of each request. Returns false when not connected.</summary>
    public static bool AddClinicHubSignIn(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ClinicHubOptions>(config.GetSection("ClinicHub"));
        // Connected or not: it also applies the upgrade scripts to the lab's database.
        services.AddSingleton<LabDatabases>();
        var hub = config.GetSection("ClinicHub").Get<ClinicHubOptions>() ?? new ClinicHubOptions();
        if (!hub.Enabled)
            return false;

        // Results back to ClinicHub, from the lab's (or each clinic's) outbox.
        services.AddHttpClient(ResultSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<ResultSender>();
        services.AddHostedService<ResultSenderWorker>();
        // The request's database: the signed-in person's clinic, or for anonymous booking the clinic it names.
        services.AddScoped(sp =>
        {
            var databases = sp.GetRequiredService<LabDatabases>();
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            // A person's session says "clinic"; ClinicHub's own service token says "clinic_slug"; public booking may name one.
            var slug = http?.User.FindFirstValue(ClinicHubClaims.Clinic) ?? http?.User.FindFirstValue("clinic_slug")
                ?? http?.Request.Headers[ClinicHubClaims.ClinicHeader].FirstOrDefault();
            if (databases.PerClinic && http?.User.Identity?.IsAuthenticated != true)
            {
                if (!LabDatabases.IsValidSlug(slug) || !databases.ExistsAsync(slug!).GetAwaiter().GetResult())
                    throw new NoClinicException();
            }
            return LabDatabases.Create(databases.ConnectionString(slug));
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "labflow.auth";
                o.Cookie.HttpOnly = true;
                // Lax: the browser arrives from ClinicHub's sign-in. Unsafe API calls must also carry X-LabFlow.
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = ValidateAsync;
            })
            .AddJwtBearer(ServiceScheme, o =>
            {
                o.Authority = hub.Authority;
                o.Audience = hub.ClientId;
                o.RequireHttpsMetadata = hub.RequireHttpsMetadata;
                o.MapInboundClaims = false;
            })
            .AddOpenIdConnect(Scheme, "ClinicHub", o =>
            {
                o.Authority = hub.Authority;
                o.ClientId = hub.ClientId;
                o.ClientSecret = hub.ClientSecret;
                o.RequireHttpsMetadata = hub.RequireHttpsMetadata;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.UsePkce = true;
                o.Scope.Clear();
                foreach (var s in new[] { "openid", "profile", "clinic", "offline_access" })
                    o.Scope.Add(s);
                o.SaveTokens = true;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.TokenValidationParameters.NameClaimType = "name";
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.NonceCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // Keep only what is used later: the refresh token (re-checks) and the id token (sign-out hint). The access token
                // is not used here and would only make the cookie bigger (on localhost every app's cookies travel together).
                o.Events.OnTicketReceived = ctx =>
                {
                    ctx.Properties!.StoreTokens(ctx.Properties.GetTokens().Where(t => t.Name is "refresh_token" or "id_token").ToList());
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = async ctx => ctx.Principal = await ToLocalPrincipal(ctx.HttpContext.RequestServices, ctx.Principal!);
                o.Events.OnRemoteFailure = ctx =>
                {
                    ctx.Response.Redirect("/reception?signInError=" + Uri.EscapeDataString(ctx.Failure?.Message is { } m && m.Contains("access_denied")
                        ? "ClinicHub: you have no role that allows LabFlow at the clinic you chose."
                        : "Signing in through ClinicHub did not complete. Try again."));
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorizationBuilder().AddPolicy(OrdersPolicy, p => p
            .AddAuthenticationSchemes(ServiceScheme).RequireAuthenticatedUser()
            .RequireClaim("scope", "orders").RequireAssertion(ctx => LabDatabases.IsValidSlug(ctx.User.FindFirstValue("clinic_slug"))));
        return true;
    }

    /// <summary>
    /// ClinicHub's person becomes (or is found as) a sec.AppUser of the right LabFlow database, linked by ClinicHub's
    /// account id, so the PHIPA access log and specimen receipts name them. Permissions come from ClinicHub.
    /// </summary>
    public static async Task<ClaimsPrincipal> ToLocalPrincipal(IServiceProvider services, ClaimsPrincipal hub)
    {
        var slug = hub.FindFirstValue("clinic_slug");
        if (!LabDatabases.IsValidSlug(slug))
            throw new InvalidOperationException("ClinicHub did not say which clinic.");
        var databases = services.GetRequiredService<LabDatabases>();
        await databases.PrepareAsync(slug!);
        await using var db = LabDatabases.Create(databases.ConnectionString(slug));

        var sub = hub.FindFirstValue("sub")!;
        var permissions = hub.FindAll("permission").Select(c => c.Value).Where(LabPermissions.All.Contains).Distinct().ToList();
        var externalId = $"clinichub:{sub}";
        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.ExternalId == externalId);
        if (user is null)
            // ClinicHub keeps no e-mail address; sec.AppUser needs a unique one, so this placeholder never receives mail.
            db.AppUsers.Add(user = new AppUser { ExternalId = externalId, Email = $"clinichub-{sub}@users.invalid", IsActive = true });
        user.DisplayName = hub.FindFirstValue("name") ?? hub.FindFirstValue("preferred_username") ?? externalId;
        user.Role = LabPermissions.RoleFor(permissions);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sec.AppUser SET LastLoginAt = SYSUTCDATETIME() WHERE UserId = {user.UserId}");

        return Principal(user, slug!, hub.FindFirstValue("clinic_name") ?? slug!, permissions, DateTime.UtcNow);
    }

    private static ClaimsPrincipal Principal(AppUser user, string slug, string clinicName, IEnumerable<string> permissions, DateTime checkedAt)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(ClinicHubClaims.Clinic, slug),
            new(ClinicHubClaims.ClinicName, clinicName),
            new(ClinicHubClaims.CheckedAt, new DateTimeOffset(checkedAt).ToUnixTimeSeconds().ToString()),
        };
        claims.AddRange(permissions.Where(LabPermissions.All.Contains).Distinct().Select(p => new Claim(LabPermissions.ClaimType, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>
    /// Every request: the LabFlow user must still be active. Every few minutes: ClinicHub is asked again with the
    /// refresh token; a refusal (roles removed, signed out everywhere) ends the session, an unreachable ClinicHub is tolerated for a while.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal!;
        var services = ctx.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<ClinicHubOptions>>().Value;
        var slug = principal.FindFirstValue(ClinicHubClaims.Clinic);
        var databases = services.GetRequiredService<LabDatabases>();
        if (!LabDatabases.IsValidSlug(slug))
        {
            await Reject(ctx);
            return;
        }
        await using var db = LabDatabases.Create(databases.ConnectionString(slug));
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == principal.UserId());
        if (user is null || !user.IsActive || !user.ExternalId.StartsWith("clinichub:"))
        {
            await Reject(ctx);
            return;
        }

        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue(ClinicHubClaims.CheckedAt) ?? "0")).UtcDateTime;
        var permissions = principal.FindAll(LabPermissions.ClaimType).Select(c => c.Value).ToList();
        if (DateTime.UtcNow - checkedAt >= TimeSpan.FromMinutes(options.RecheckMinutes))
        {
            switch (await Refresh(ctx, options))
            {
                case RefreshResult.Refused:
                case RefreshResult.Unreachable when DateTime.UtcNow - checkedAt > TimeSpan.FromMinutes(options.OfflineGraceMinutes):
                    await Reject(ctx);
                    return;
                case RefreshResult.Ok(var granted):
                    permissions = granted;
                    checkedAt = DateTime.UtcNow;
                    ctx.ShouldRenew = true;
                    break;
            }
        }
        ctx.ReplacePrincipal(Principal(user, slug!, principal.FindFirstValue(ClinicHubClaims.ClinicName) ?? slug!, permissions, checkedAt));
    }

    private abstract record RefreshResult
    {
        public sealed record Ok(List<string> Permissions) : RefreshResult;
        public sealed record Refused : RefreshResult;
        public sealed record Unreachable : RefreshResult;
    }

    private static async Task<RefreshResult> Refresh(CookieValidatePrincipalContext ctx, ClinicHubOptions options)
    {
        var refreshToken = ctx.Properties.GetTokenValue("refresh_token");
        if (refreshToken is null)
            return new RefreshResult.Refused();
        var services = ctx.HttpContext.RequestServices;
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Scheme);
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
            using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret ?? "",
            }));
            if (!response.IsSuccessStatusCode)
                return (int)response.StatusCode is >= 400 and < 500 ? new RefreshResult.Refused() : new RefreshResult.Unreachable();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Straight from ClinicHub's token endpoint over the authenticated back channel (OpenID Connect Core 3.1.3.7).
            var idToken = json.RootElement.GetProperty("id_token").GetString()!;
            ctx.Properties.UpdateTokenValue("id_token", idToken);
            if (json.RootElement.TryGetProperty("refresh_token", out var rt))
                ctx.Properties.UpdateTokenValue("refresh_token", rt.GetString()!);
            return new RefreshResult.Ok(new JwtSecurityTokenHandler().ReadJwtToken(idToken).Claims.Where(c => c.Type == "permission")
                .Select(c => c.Value).Where(LabPermissions.All.Contains).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicHub").LogWarning(ex, "ClinicHub could not be reached to check a session.");
            return new RefreshResult.Unreachable();
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>Cookie sign-in is ambient, so unsafe /api requests must carry X-LabFlow (a browser can't add it cross-site without CORS).</summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var unsafeMethod = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { title = $"Missing {CsrfHeader} header." });
            return;
        }
        await next();
    });
}
