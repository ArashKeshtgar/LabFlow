using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace LabFlow.Api.Auth;

/// <summary>
/// The lab's money to FinancePowerApp, the group's accounting, as financial documents built from the billing tables
/// (each with an id that never changes, marked in billing.FinanceExport once accepted). No health information leaves:
/// no patient, no test names; patients are one party and invoices go by number.
///
/// <list type="bullet">
/// <item>A patient invoice for uninsured tests: owed by "Patients", Lab.Uninsured (exempt: a health care service). Paid
/// online: a receipt by card. Voided or refunded: reversed.</item>
/// <item>A finished requisition's insured tests, where ref.Test has its OHIP fee: owed by OHIP, Lab.OHIP.</item>
/// </list>
/// LabFlow authenticates as itself: a client-credentials token from ClinicHub, scope "finance".
/// </summary>
public class FinanceSender(ResultSender databases, IOptions<ClinicHubOptions> options, IOptionsMonitor<OpenIdConnectOptions> oidc,
    IHttpClientFactory http, ILogger<FinanceSender> logger)
{
    public const string HttpClientName = "finance";
    private string? _token;
    private DateTime _tokenExpires;

    public bool Connected => !string.IsNullOrWhiteSpace(options.Value.FinanceUrl);

    public async Task<int> SendAsync(CancellationToken ct = default)
    {
        if (!Connected)
            return 0;
        var accepted = 0;
        foreach (var cs in await databases.DatabasesAsync(ct))
        {
            await using var db = new SqlConnection(cs);
            await db.OpenAsync(ct);
            // A clinic's own collection room is the clinic's business; the lab's single database is the lab's.
            var key = options.Value.PerClinic ? Slug(db.Database) : "lab";
            List<JsonObject> pending;
            try
            {
                var sent = await SentAsync(db, ct);
                pending = (await BuildAsync(db, key, ct)).Where(d => !sent.Contains((string)d["externalId"]!)).ToList();
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                continue;   // not upgraded yet (no billing.FinanceExport)
            }
            foreach (var chunk in pending.Chunk(100))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.FinanceUrl!.TrimEnd('/') + "/api/integration/documents")
                    {
                        Content = JsonContent.Create(new JsonArray(chunk.Select(d => (JsonNode)d.DeepClone()).ToArray())),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                    using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 401) _token = null;
                        logger.LogWarning("FinancePowerApp refused {Count} documents: {Status} {Body}", chunk.Length, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                        break;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    logger.LogWarning("FinancePowerApp could not be reached: {Error}", ex.Message);
                    break;
                }
                foreach (var d in chunk)
                {
                    await using var mark = new SqlCommand("INSERT billing.FinanceExport (ExternalId) VALUES (@id)", db);
                    mark.Parameters.AddWithValue("@id", (string)d["externalId"]!);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                accepted += chunk.Length;
            }
        }
        if (accepted > 0)
            logger.LogInformation("Sent {Count} financial documents to FinancePowerApp", accepted);
        return accepted;
    }

    private string Slug(string database)
    {
        var name = new SqlConnectionStringBuilder(options.Value.DatabasePattern).InitialCatalog;
        var at = name.IndexOf("{clinic}", StringComparison.Ordinal);
        return database[at..^(name.Length - at - "{clinic}".Length)];
    }

    private static async Task<HashSet<string>> SentAsync(SqlConnection db, CancellationToken ct)
    {
        var set = new HashSet<string>();
        await using var cmd = new SqlCommand("SELECT ExternalId FROM billing.FinanceExport", db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>Every financial document the lab database's billing implies today.</summary>
    public static async Task<List<JsonObject>> BuildAsync(SqlConnection db, string sourceKey, CancellationToken ct = default)
    {
        var docs = new List<JsonObject>();
        await using (var cmd = new SqlCommand(
            "SELECT InvoiceId, Subtotal, Tax, Status, PaymentProvider, PaidAt, CreatedAt FROM billing.Invoice ORDER BY InvoiceId", db))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                var (subtotal, tax, status) = (r.GetDecimal(1), r.GetDecimal(2), r.GetString(3));
                var paidAt = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5);
                var created = Local(r.GetDateTime(6));
                var reference = $"LAB-INV-{id:D6}";
                if (subtotal <= 0) continue;
                docs.Add(Doc(sourceKey, $"invoice:{id}", "Invoice", created, reference, Party("Patient", "PATIENT"),
                    [Line("Lab.Uninsured", subtotal, tax > 0 ? "HST13" : "EXEMPT", tax > 0 ? tax : null)], memo: $"Lab invoice {reference}"));
                if (paidAt is { } paid && status is "Paid" or "Refunded")
                    docs.Add(Doc(sourceKey, $"invoice:{id}:paid", "Receipt", Local(paid), reference, Party("Patient", "PATIENT"),
                        [Line(null, subtotal + tax, null, null, appliesTo: reference)], method: "Credit", memo: $"Paid online ({(r.IsDBNull(4) ? "card" : r.GetString(4))})"));
                if (status == "Refunded")
                    docs.Add(Doc(sourceKey, $"invoice:{id}:refund", "Reversal", Local(paidAt ?? r.GetDateTime(6)), reference, null, [], reverses: $"invoice:{id}:paid"));
                if (status is "Void" or "Refunded")
                    docs.Add(Doc(sourceKey, $"invoice:{id}:void", "Reversal", created, reference, null, [], reverses: $"invoice:{id}"));
            }

        await using (var cmd = new SqlCommand("""
            SELECT q.RequisitionId, q.AccessionNumber, q.ReceivedAt, SUM(t.OhipFee) AS Fee
            FROM lab.Requisition q
            JOIN lab.RequisitionItem i ON i.RequisitionId = q.RequisitionId
            JOIN ref.Test t ON t.TestId = i.TestId
            WHERE q.Status = 'Final' AND i.IsInsured = 1 AND i.Status <> 'Cancelled' AND t.OhipFee IS NOT NULL
            GROUP BY q.RequisitionId, q.AccessionNumber, q.ReceivedAt
            ORDER BY q.RequisitionId
            """, db))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (r.GetDecimal(3) > 0)
                    docs.Add(Doc(sourceKey, $"requisition:{r.GetInt32(0)}:ohip", "Invoice", Local(r.GetDateTime(2)), r.GetString(1), Party("OHIP", "OHIP"),
                        [Line("Lab.OHIP", r.GetDecimal(3), "EXEMPT", null)], memo: $"Insured tests, requisition {r.GetString(1)}"));
        return docs;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires)
            return _token;
        var configuration = await oidc.Get(ClinicHubConnection.Scheme).ConfigurationManager!.GetConfigurationAsync(ct);
        using var response = await http.CreateClient(HttpClientName).PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = options.Value.ClientId, ["client_secret"] = options.Value.ClientSecret ?? "", ["scope"] = "finance",
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ClinicHub refused LabFlow's credentials for finance ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = json.RootElement.GetProperty("access_token").GetString();
        var seconds = json.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
        _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 60));
        return _token!;
    }

    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
    private static DateOnly Local(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Toronto));
    private static JsonObject Party(string type, string code) => new() { ["type"] = type, ["code"] = code };
    private static JsonObject Line(string? category, decimal amount, string? taxCode, decimal? tax, string? appliesTo = null) => new()
    {
        ["category"] = category, ["amount"] = amount, ["taxCode"] = taxCode, ["tax"] = tax, ["department"] = "Lab", ["appliesTo"] = appliesTo,
    };
    private static JsonObject Doc(string sourceKey, string externalId, string kind, DateOnly date, string reference, JsonObject? party, JsonObject[] lines,
        string? method = null, string? memo = null, string? reverses = null) => new()
    {
        ["sourceKey"] = sourceKey, ["externalId"] = externalId, ["kind"] = kind, ["date"] = date.ToString("yyyy-MM-dd"), ["reference"] = reference,
        ["party"] = party, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()), ["method"] = method, ["memo"] = memo, ["reverses"] = reverses,
    };
}

public class FinanceSenderWorker(FinanceSender sender, IOptions<ClinicHubOptions> options, ILogger<FinanceSenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && options.Value.SendSeconds > 0 && sender.Connected)
        {
            try { await sender.SendAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Sending financial documents failed; trying again shortly."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.Value.SendSeconds, 30)), stoppingToken);
        }
    }
}
