using System.Text.RegularExpressions;
using LabFlow.Api.Auth;
using Microsoft.Data.SqlClient;

namespace LabFlow.Api.Tests;

/// <summary>The lab's financial documents, built from a database made by database/*.sql with the demo data.</summary>
public sealed class FinanceSenderTests : IAsyncLifetime
{
    private readonly string _name = $"LabFlow_Test_{Guid.NewGuid():N}_finance";
    private string Cs => new SqlConnectionStringBuilder(SqlFactAttribute.Server) { InitialCatalog = _name }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(SqlFactAttribute.Server) { InitialCatalog = "master", ConnectTimeout = 3 }.ConnectionString))
        {
            try { await master.OpenAsync(); } catch (SqlException) { return; }
            await new SqlCommand($"CREATE DATABASE [{_name}]", master).ExecuteNonQueryAsync();
        }
        await using var db = new SqlConnection(Cs);
        await db.OpenAsync();
        var dir = Path.Combine(AppContext.BaseDirectory, "database");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).Order()
                     .Where(f => Regex.IsMatch(f!, @"^\d{3}_") && !f!.StartsWith("001_") && !f.Contains("_etl_") && !f.Contains("_legacy_")))
            foreach (var batch in Regex.Split(await File.ReadAllTextAsync(Path.Combine(dir, file!)), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                    await new SqlCommand(batch, db) { CommandTimeout = 120 }.ExecuteNonQueryAsync();
    }

    [SqlFact]
    public async Task A_paid_invoice_is_owed_by_patients_and_paid_by_card_and_insured_work_needs_a_fee()
    {
        await using var db = new SqlConnection(Cs);
        await db.OpenAsync();
        var docs = await FinanceSender.BuildAsync(db, "lab");

        var invoice = Assert.Single(docs, d => (string)d["kind"]! == "Invoice" && (string)d["party"]!["type"]! == "Patient");
        Assert.Equal("Lab.Uninsured", (string)invoice["lines"]![0]!["category"]!);
        Assert.Equal("EXEMPT", (string)invoice["lines"]![0]!["taxCode"]!);
        var receipt = Assert.Single(docs, d => (string)d["kind"]! == "Receipt");
        Assert.Equal((string)invoice["reference"]!, (string)receipt["lines"]![0]!["appliesTo"]!);
        Assert.Equal("Credit", (string)receipt["method"]!);
        // The demo's tests have OHIP fee codes but no amounts: nothing insured goes to the accounting yet.
        Assert.DoesNotContain(docs, d => (string)d["party"]?["type"]! == "OHIP");

        await new SqlCommand("UPDATE ref.Test SET OhipFee = 5.50 WHERE IsOhipInsured = 1; UPDATE lab.Requisition SET Status = 'Final'", db).ExecuteNonQueryAsync();
        var insured = (await FinanceSender.BuildAsync(db, "lab")).Where(d => (string)d["party"]?["type"]! == "OHIP").ToList();
        Assert.NotEmpty(insured);
        Assert.All(insured, d => Assert.Equal("Lab.OHIP", (string)d["lines"]![0]!["category"]!));

        // Voided: the invoice is reversed.
        await new SqlCommand("UPDATE billing.Invoice SET Status = 'Void', PaidAt = NULL", db).ExecuteNonQueryAsync();
        var after = await FinanceSender.BuildAsync(db, "lab");
        Assert.Contains(after, d => (string)d["kind"]! == "Reversal" && (string)d["reverses"]! == (string)invoice["externalId"]!);
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(SqlFactAttribute.Server) { InitialCatalog = "master", ConnectTimeout = 3 }.ConnectionString);
        try { await master.OpenAsync(); } catch (SqlException) { return; }
        await new SqlCommand($"IF DB_ID(N'{_name}') IS NOT NULL BEGIN ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]; END", master).ExecuteNonQueryAsync();
    }
}
