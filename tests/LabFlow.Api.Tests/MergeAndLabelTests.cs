using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LabFlow.Api.Auth;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace LabFlow.Api.Tests;

/// <summary>
/// IntegrationHub's flows 6 and 7 at the lab: an order's confidentiality label is kept and goes back on its results, and
/// a merge at the clinic moves the clinic's MRN on the lab's orders.
/// </summary>
public class MergeAndLabelTests(PerClinicFactory factory) : IClassFixture<PerClinicFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private Task<HttpResponseMessage> Send(string bundle) => OrderTests.Send(factory, bundle, ConnectedFactory.ServiceToken("harbourview"));

    /// <summary>OrderTests' Bundle for another patient (MRN and card), with a label on each ServiceRequest when given.</summary>
    private static string Order(string requisition, string mrn, string card, string family, string? label, params (string, string)[] tests)
    {
        var bundle = JsonNode.Parse(OrderTests.Bundle(requisition, tests))!;
        var entries = bundle["entry"]!.AsArray().Select(e => e!["resource"]!).ToList();
        var patient = entries.Single(r => (string)r["resourceType"]! == "Patient");
        patient["identifier"]![0]!["value"] = mrn;
        patient["identifier"]![1]!["value"] = card;
        patient["name"]![0]!["family"] = family;
        if (label is not null)
            foreach (var sr in entries.Where(r => (string)r["resourceType"]! == "ServiceRequest"))
                sr["meta"] = new JsonObject
                {
                    ["security"] = new JsonArray(new JsonObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-Confidentiality", ["code"] = label }),
                };
        return bundle.ToJsonString();
    }

    private static string Merge(string merged, string survivor) => new JsonObject
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["resource"] = new JsonObject
            {
                ["resourceType"] = "Patient", ["active"] = false,
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:mrn", ["value"] = merged }),
                ["link"] = new JsonArray(new JsonObject
                {
                    ["type"] = "replaced-by",
                    ["other"] = new JsonObject { ["identifier"] = new JsonObject { ["system"] = "urn:clinichub:harbourview:mrn", ["value"] = survivor } },
                }),
            },
            ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = $"Patient?identifier=urn:clinichub:harbourview:mrn|{merged}" },
        }),
    }.ToJsonString();

    private async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = new SqlConnection(factory.ClinicDatabase("harbourview"));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [SqlFact]
    public async Task A_labelled_order_shows_its_label_and_its_results_carry_it_back()
    {
        await Json(await Send(Order("R960", "HV000960", "9876543217", "Varga", "V", ("http://loinc.org", "4548-4"))));
        await Json(await Send(Order("R961", "HV000961", "1286844020", "Okafor", "Q", ("http://loinc.org", "4548-4"))));   // unknown code: R
        await Scalar<int>("IF NOT EXISTS (SELECT 1 FROM core.Location) INSERT core.Location (Name, AddressLine1, City, PostalCode) VALUES (N'Site', N'1 St', N'Toronto', 'M5V 0A1'); SELECT 1");

        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk", "results.enter");
        var lab = await factory.SignInAsync("41", "Lab Tech", "harbourview", "frontdesk", "results.enter");
        var waiting = (await Json(await lab.GetAsync("/api/reception/electronic-orders"))).AsArray();
        var labelled = waiting.Single(o => (string)o!["patient"]! == "Varga, Mei")!;
        Assert.Equal("V", (string)labelled["confidentiality"]!);
        Assert.Equal("R", (string)waiting.Single(o => (string)o!["patient"]! == "Okafor, Mei")!["confidentiality"]!);

        var location = (int)(await Json(await lab.GetAsync("/api/locations")))[0]!["locationId"]!;
        var orderId = (int)labelled["electronicOrderId"]!;

        // Closed until someone says why; the unlabelled order is not.
        var closed = await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{orderId}/requisition", new { locationId = location });
        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        var refusal = JsonNode.Parse(await closed.Content.ReadAsStringAsync())!;
        Assert.Equal("restricted", (string)refusal["code"]!);
        Assert.Equal("V", (string)refusal["label"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{orderId}/override", new { reason = "short" })).StatusCode);
        (await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{orderId}/override", new { reason = "Patient is at the desk for the draw" })).EnsureSuccessStatusCode();
        var requisition = (int)(await Json(await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{orderId}/requisition", new { locationId = location })))["requisitionId"]!;
        Assert.Equal(HttpStatusCode.OK, (await lab.GetAsync($"/api/lab/requisitions/{requisition}")).StatusCode);   // the opener carries on

        // Someone else is shut out of the results until they say why, and the override is on the record.
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk", "results.enter");
        var other = await factory.SignInAsync("42", "Second Tech", "harbourview", "frontdesk", "results.enter");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/lab/requisitions/{requisition}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/lab/requisitions/{requisition}/release", null)).StatusCode);
        (await other.PostAsJsonAsync($"/api/lab/requisitions/{requisition}/override", new { reason = "Covering the bench this afternoon" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/lab/requisitions/{requisition}")).StatusCode);
        Assert.Equal(2, await Scalar<int>("SELECT COUNT(*) FROM audit.AccessLog WHERE Action = 'RestrictedOverride'"));

        // The worklist keeps it listed, but names and values are hidden from someone who has not opened it.
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk", "results.enter");
        var third = await factory.SignInAsync("43", "Third Tech", "harbourview", "frontdesk", "results.enter");
        var hidden = (await Json(await third.GetAsync("/api/lab/worklist"))).AsArray().Single(r => (int)r!["requisitionId"]! == requisition)!;
        Assert.Equal("Restricted", (string)hidden["patient"]!);
        Assert.Equal("V", (string)hidden["confidentiality"]!);
        Assert.All(hidden["items"]!.AsArray().SelectMany(i => i!["analytes"]!.AsArray()), a => Assert.Null(a!["value"]));
        var shown = (await Json(await other.GetAsync("/api/lab/worklist"))).AsArray().Single(r => (int)r!["requisitionId"]! == requisition)!;
        Assert.Equal("Varga, Mei", (string)shown["patient"]!);
        Assert.Contains("Covering the bench", await Scalar<string>("SELECT TOP 1 Details FROM audit.AccessLog WHERE Action = 'RestrictedOverride' ORDER BY AuditId DESC") ?? "");
        lab = other;
        var sheet = await Json(await lab.GetAsync($"/api/lab/requisitions/{requisition}"));
        var item = sheet["items"]!.AsArray().Single()!;
        await Json(await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new
        {
            results = new[] { new { requisitionItemId = (int)item["requisitionItemId"]!, testId = (int)item["analytes"]![0]!["testId"]!, value = "5.6" } },
        }));
        await Json(await lab.PostAsync($"/api/lab/requisitions/{requisition}/release", null));
        Assert.Equal(1, await factory.Services.GetRequiredService<ResultSender>().SendAsync());
        var report = factory.Hub.Results.Last()["entry"]!.AsArray().Select(e => e!["resource"]!).Single(r => (string)r["resourceType"]! == "DiagnosticReport");
        Assert.Equal("R960-0", (string)report["basedOn"]![0]!["identifier"]!["value"]!);
        Assert.Equal("V", (string)report["meta"]!["security"]![0]!["code"]!);
        Assert.Equal("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", (string)report["meta"]!["security"]![0]!["system"]!);
    }

    [SqlFact]
    public async Task A_merge_moves_the_clinics_mrn_and_notes_two_lab_patients_as_a_possible_duplicate()
    {
        await Json(await Send(Order("R970", "HV000970", "4532015112", "Lindqvist", null, ("http://loinc.org", "4548-4"))));
        await Json(await Send(Order("R971", "HV000971", "2345678904", "Lindqvist-Ross", null, ("http://loinc.org", "4548-4"))));
        Assert.Equal("HV000970", await Scalar<string>("SELECT SourceMrn FROM lab.ElectronicOrder WHERE ExternalId = 'R970'"));

        await Json(await Send(Merge("HV000970", "HV000971")));
        Assert.Equal("HV000971", await Scalar<string>("SELECT SourceMrn FROM lab.ElectronicOrder WHERE ExternalId = 'R970'"));
        Assert.Equal(0, await Scalar<int>("SELECT COUNT(*) FROM lab.ElectronicOrder WHERE SourceMrn = 'HV000970'"));
        // Two different lab patients now carry one clinic MRN: written down, not merged here.
        Assert.Contains("possibleDuplicate", await Scalar<string>(
            "SELECT TOP 1 Details FROM audit.AccessLog WHERE EntityType = 'ElectronicOrder' AND Details LIKE '%mergedAtClinic%' ORDER BY 1 DESC") ?? "");

        // A record the lab never had: nothing to do, still 200. A merge into itself: 400.
        await Json(await Send(Merge("HV000999", "HV000998")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Merge("HV000971", "HV000971"))).StatusCode);
    }
}
