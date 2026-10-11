using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using LabFlow.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LabFlow.Api.Tests;

/// <summary>Runs only where a SQL Server is reachable (LABFLOW_TEST_SQL, or the local default instance); skipped in CI without one.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public static readonly string Server = Environment.GetEnvironmentVariable("LABFLOW_TEST_SQL")
        ?? "Server=.;Trusted_Connection=True;TrustServerCertificate=True";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var c = new SqlConnection(new SqlConnectionStringBuilder(Server) { ConnectTimeout = 3, InitialCatalog = "master" }.ConnectionString);
            c.Open();
            return true;
        }
        catch (SqlException) { return false; }
    });

    public SqlFactAttribute()
    {
        if (!Available.Value)
            Skip = "No SQL Server available for LabFlow's database tests.";
    }
}

/// <summary>
/// LabFlow connected to ClinicHub. ClinicHub is a separate project: its discovery document and token endpoint are
/// stand-ins, and /test/hub-signin plays the sign-in callback with what ClinicHub's id token would carry.
/// </summary>
public abstract class ConnectedFactory : WebApplicationFactory<Program>
{
    protected readonly string Prefix = $"LabFlow_Test_{Guid.NewGuid():N}_";

    /// <summary>ClinicHub receiving results: issues LabFlow's token and records what it is sent.</summary>
    public FakeClinicHub Hub { get; } = new();

    /// <summary>ClinicHub's answer to a refresh; null means "invalid_grant".</summary>
    public Func<HttpResponseMessage>? TokenEndpoint { get; set; } = () => Granted("frontdesk");

    protected string Database(string name) => new SqlConnectionStringBuilder(SqlFactAttribute.Server) { InitialCatalog = Prefix + name }.ConnectionString;

    protected abstract void Configure(IWebHostBuilder builder);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ClinicHub:Authority", "https://hub.test/");
        builder.UseSetting("ClinicHub:ClientSecret", "test-secret");
        builder.UseSetting("ClinicHub:RecheckMinutes", "0");
        builder.UseSetting("ClinicHub:SendSeconds", "0");
        Configure(builder);
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<OpenIdConnectOptions>(ClinicHubConnection.Scheme, o =>
            {
                var config = new OpenIdConnectConfiguration
                {
                    Issuer = "https://hub.test/", AuthorizationEndpoint = "https://hub.test/connect/authorize",
                    TokenEndpoint = "https://hub.test/connect/token", EndSessionEndpoint = "https://hub.test/connect/logout",
                };
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.Backchannel = new HttpClient(new Stub(this));
            });
            services.AddSingleton<IStartupFilter>(new HubSignInRoute());
            // Results going to ClinicHub: its token endpoint and /fhir, answered here.
            services.AddHttpClient(ResultSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Hub);
            // ClinicHub's service tokens: signed here with a test key instead of ClinicHub's published one.
            services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(ClinicHubConnection.ServiceScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = "https://hub.test/" };
                config.SigningKeys.Add(ServiceKey);
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
        });
    }

    public static readonly Microsoft.IdentityModel.Tokens.RsaSecurityKey ServiceKey = new(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test" };

    /// <summary>A token like the one ClinicHub sends with an order.</summary>
    public static string ServiceToken(string clinic, string audience = "labflow", string scope = "orders") =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "https://hub.test/", Audience = audience, Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(ServiceKey, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = "clinichub", ["scope"] = scope, ["clinic_slug"] = clinic, ["clinic_name"] = clinic },
        });

    public static HttpResponseMessage Granted(params string[] permissions) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { id_token = IdToken(permissions), access_token = "at", refresh_token = "rt-2", token_type = "Bearer" }),
    };

    private static string IdToken(string[] permissions)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject { ["permission"] = new JsonArray(permissions.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
        return $"{B64("""{"alg":"none"}""")}.{B64(payload.ToJsonString())}.";
    }

    private sealed class Stub(ConnectedFactory f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(f.TokenEndpoint?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>?sub=&name=&clinic=&perm=a,b — as if ClinicHub had just returned.</summary>
    private sealed class HubSignInRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, nextMiddleware) =>
            {
                if (http.Request.Path != "/test/hub-signin")
                {
                    await nextMiddleware();
                    return;
                }
                var q = http.Request.Query;
                var hub = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
                {
                    new("sub", q["sub"]!), new("name", q["name"]!), new("clinic_slug", q["clinic"]!), new("clinic_name", q["clinic"]!),
                }.Concat(q["perm"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p))), "test"));
                var principal = await ClinicHubConnection.ToLocalPrincipal(http.RequestServices, hub);
                var props = new AuthenticationProperties();
                props.StoreTokens([new AuthenticationToken { Name = "refresh_token", Value = "rt-1" }]);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
            });
            next(app);
        };
    }

    public async Task<HttpClient> SignInAsync(string sub, string name, string clinic, params string[] permissions)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-LabFlow", "1");
        (await c.GetAsync($"/test/hub-signin?sub={sub}&name={Uri.EscapeDataString(name)}&clinic={clinic}&perm={string.Join(',', permissions)}")).EnsureSuccessStatusCode();
        return c;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(SqlFactAttribute.Server) { InitialCatalog = "master" }.ConnectionString);
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var list = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @p", conn))
        {
            list.Parameters.AddWithValue("@p", Prefix + "%");
            await using var r = await list.ExecuteReaderAsync();
            while (await r.ReadAsync()) names.Add(r.GetString(0));
        }
        foreach (var n in names)
        {
            await using var drop = new SqlCommand($"ALTER DATABASE [{n}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{n}];", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>A database per clinic: the connection string carries {clinic}.</summary>
public sealed class PerClinicFactory : ConnectedFactory
{
    protected override void Configure(IWebHostBuilder builder) => builder.UseSetting("ClinicHub:DatabasePattern", Database("{clinic}"));
    public string ClinicDatabase(string clinic) => Database(clinic);
}

/// <summary>One lab database for every clinic, built here first from the same scripts.</summary>
public sealed class OneLabFactory : ConnectedFactory
{
    protected override void Configure(IWebHostBuilder builder)
    {
        // Build the lab's database by asking the per-clinic builder for a "clinic" named lab.
        var builderOptions = Options.Create(new ClinicHubOptions { Authority = "x", DatabasePattern = Database("{clinic}") });
        new LabDatabases(new ConfigurationBuilder().Build(), builderOptions, NullLogger<LabDatabases>.Instance).PrepareAsync("lab").GetAwaiter().GetResult();
        builder.UseSetting("ConnectionStrings:LabFlow", Database("lab"));
    }
}

public class PerClinicTests(PerClinicFactory factory) : IClassFixture<PerClinicFactory>
{
    private static readonly object Patient = new
    {
        firstName = "Ana", lastName = "Harbour", dateOfBirth = "1980-02-03", sex = "F", healthCardNumber = (string?)null, healthCardVersion = (string?)null,
        email = (string?)null, mobilePhone = (string?)null, preferredLanguage = "en", consentEmailNotification = false,
    };

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    [SqlFact]
    public async Task Each_clinic_gets_its_own_lab_database_built_at_first_sign_in()
    {
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var harbour = await factory.SignInAsync("7", "Jordan Lee", "harbourview", "frontdesk");
        var me = await Json(await harbour.GetAsync("/api/me"));
        Assert.Equal("harbourview", (string)me["clinic"]!);
        Assert.Equal(["frontdesk"], me["permissions"]!.AsArray().Select(p => (string)p!));
        Assert.True((bool)(await Json(await harbour.GetAsync("/api/auth/settings")))["perClinicDatabase"]!);

        await Json(await harbour.PostAsJsonAsync("/api/reception/patients", Patient));
        Assert.NotEmpty((await Json(await harbour.GetAsync("/api/reception/patients?q=Harbour"))).AsArray());

        var lake = await factory.SignInAsync("7", "Jordan Lee", "lakeshore", "frontdesk");
        Assert.Empty((await Json(await lake.GetAsync("/api/reception/patients?q=Harbour"))).AsArray());

        // The PHIPA access log of each clinic's database names the ClinicHub user.
        await using var conn = new SqlConnection(factory.ClinicDatabase("harbourview"));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM audit.AccessLog l JOIN sec.AppUser u ON u.UserId = l.UserId WHERE u.ExternalId = 'clinichub:7'", conn);
        Assert.True((int)(await cmd.ExecuteScalarAsync())! > 0);
    }

    [SqlFact]
    public async Task Anonymous_booking_must_name_a_clinic_that_exists()
    {
        await factory.SignInAsync("8", "Riley Morgan", "harbourview", "frontdesk");   // builds harbourview's database
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync("/api/locations")).StatusCode);

        anonymous.DefaultRequestHeaders.Add(ClinicHubClaims.ClinicHeader, "nowhere");
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync("/api/locations")).StatusCode);

        var booking = factory.CreateClient();
        booking.DefaultRequestHeaders.Add(ClinicHubClaims.ClinicHeader, "harbourview");
        Assert.Equal(HttpStatusCode.OK, (await booking.GetAsync("/api/tests")).StatusCode);
    }

    [SqlFact]
    public async Task ClinicHub_decides_at_each_check()
    {
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var c = await factory.SignInAsync("9", "Sam Okafor", "harbourview", "frontdesk");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/reception/patients?q=zz")).StatusCode);

        factory.TokenEndpoint = () => ConnectedFactory.Granted("results.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/reception/patients?q=zz")).StatusCode);

        factory.TokenEndpoint = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/reception/patients?q=zz")).StatusCode);
    }

    [SqlFact]
    public async Task Cookie_sessions_need_the_header_on_changes()
    {
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var c = await factory.SignInAsync("10", "Chris Moreau", "harbourview", "frontdesk");
        c.DefaultRequestHeaders.Remove("X-LabFlow");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/reception/patients", Patient)).StatusCode);
    }

    [SqlFact]
    public async Task Sign_in_starts_at_ClinicHub()
    {
        var res = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/api/auth/sso/start");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("https://hub.test/connect/authorize?", res.Headers.Location!.ToString());
        Assert.Contains("client_id=labflow", res.Headers.Location!.ToString());
    }
}

public class OneLabTests(OneLabFactory factory) : IClassFixture<OneLabFactory>
{
    [SqlFact]
    public async Task Staff_from_different_clinics_share_the_labs_database()
    {
        var a = await factory.SignInAsync("11", "Desk A", "harbourview", "frontdesk");
        var created = await a.PostAsJsonAsync("/api/reception/patients", new
        {
            firstName = "Lee", lastName = "Sharedlab", dateOfBirth = "1975-05-06", sex = "M", healthCardNumber = (string?)null, healthCardVersion = (string?)null,
            email = (string?)null, mobilePhone = (string?)null, preferredLanguage = "en", consentEmailNotification = false,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var b = await factory.SignInAsync("12", "Desk B", "lakeshore", "frontdesk");
        var found = await b.GetFromJsonAsync<JsonArray>("/api/reception/patients?q=Sharedlab");
        Assert.NotEmpty(found!);
        Assert.False((bool)(await (await b.GetAsync("/api/auth/settings")).Content.ReadFromJsonAsync<JsonNode>())!["perClinicDatabase"]!);
        // Anonymous booking needs no clinic: it is the lab's.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/tests")).StatusCode);
    }
}

public sealed class FakeClinicHub : HttpMessageHandler
{
    public List<JsonNode> Results { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/connect/token")
        {
            Assert.Contains("grant_type=client_credentials", await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "lab-token", expires_in = 3600 }) };
        }
        Assert.Equal("lab-token", request.Headers.Authorization?.Parameter);
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
        lock (Results)
            Results.Add(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"resourceType":"Bundle","type":"transaction-response","entry":[]}""") };
    }
}
