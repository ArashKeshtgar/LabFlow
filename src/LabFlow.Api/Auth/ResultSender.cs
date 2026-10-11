using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabFlow.Api.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LabFlow.Api.Auth;

/// <summary>
/// Sends released results to ClinicHub (connected mode) from the lab's database, or from every clinic's, retrying
/// with growing pauses. LabFlow authenticates as itself: a client-credentials token from ClinicHub, scope "results".
/// </summary>
public class ResultSender(LabDatabases databases, IConfiguration config, IOptions<ClinicHubOptions> options, IOptionsMonitor<OpenIdConnectOptions> oidc,
    IHttpClientFactory http, ILogger<ResultSender> logger)
{
    public const string HttpClientName = "clinichub";
    public const int MaxAttempts = 12;
    private string? _token;
    private DateTime _tokenExpires;

    /// <summary>Connection strings of every LabFlow database: the lab's, or each clinic's that exists on the server.</summary>
    public async Task<List<string>> DatabasesAsync(CancellationToken ct)
    {
        if (!databases.PerClinic)
            return [databases.ConnectionString(null)];
        var pattern = new SqlConnectionStringBuilder(options.Value.DatabasePattern);
        var name = pattern.InitialCatalog;
        var at = name.IndexOf("{clinic}", StringComparison.Ordinal);
        var (prefix, suffix) = (name[..at], name[(at + "{clinic}".Length)..]);
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(pattern.ConnectionString.Replace("{clinic}", "x")) { InitialCatalog = "master" }.ConnectionString);
        await master.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE @p", master);
        cmd.Parameters.AddWithValue("@p", prefix.Replace("_", "[_]") + "%" + suffix.Replace("_", "[_]"));
        var found = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var slug = r.GetString(0)[prefix.Length..^suffix.Length];
                if (LabDatabases.IsValidSlug(slug))
                    found.Add(databases.ConnectionString(slug));
            }
        return found;
    }

    public async Task<int> SendAsync(CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var cs in await DatabasesAsync(ct))
        {
            await using var db = LabDatabases.Create(cs);
            var now = DateTime.UtcNow;
            List<OutboundMessage> due;
            try
            {
                due = await db.OutboundMessages.Where(m => m.SentAt == null && m.Attempts < MaxAttempts && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
                    .OrderBy(m => m.OutboundMessageId).Take(20).ToListAsync(ct);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                continue;   // a database not upgraded yet (no lab.OutboundMessage): nothing to send from it
            }
            foreach (var m in due)
            {
                m.Attempts++;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.Authority!), "fhir"))
                    {
                        Content = new StringContent(m.PayloadJson, Encoding.UTF8, "application/fhir+json"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                    using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                    m.SentAt = now;
                    m.LastError = null;
                    sent++;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
                {
                    m.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    m.NextAttemptAt = now.AddSeconds(Math.Min(10 * Math.Pow(2, m.Attempts - 1), 1800));
                    if (ex.Message.StartsWith("401"))
                        _token = null;
                    logger.LogWarning("Results message {Id} not delivered to ClinicHub (attempt {Attempt}): {Error}", m.OutboundMessageId, m.Attempts, m.LastError);
                }
                await db.SaveChangesAsync(ct);
            }
        }
        return sent;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires)
            return _token;
        var configuration = await oidc.Get(ClinicHubConnection.Scheme).ConfigurationManager!.GetConfigurationAsync(ct);
        using var response = await http.CreateClient(HttpClientName).PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = options.Value.ClientId, ["client_secret"] = options.Value.ClientSecret ?? "",
            ["scope"] = "results",
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ClinicHub refused LabFlow's credentials ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
        _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
        return _token!;
    }
}

public class ResultSenderWorker(ResultSender sender, IOptions<ClinicHubOptions> options, ILogger<ResultSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.Value.SendSeconds > 0)
        {
            try { await sender.SendAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Sending results failed; trying again shortly."); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.SendSeconds), stoppingToken);
        }
    }
}
