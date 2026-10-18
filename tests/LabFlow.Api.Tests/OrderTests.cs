using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LabFlow.Api.Auth;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace LabFlow.Api.Tests;

/// <summary>Electronic orders from ClinicHub: a FHIR transaction Bundle in, a waiting order, a requisition at collection.</summary>
public class OrderTests(PerClinicFactory factory) : IClassFixture<PerClinicFactory>
{
    /// <summary>What ClinicHub's outbox sends (see ClinicHub's FhirOrderBundle).</summary>
    internal static string Bundle(string requisition, params (string System, string Code)[] tests) => new JsonObject
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["identifier"] = new JsonObject { ["system"] = "urn:clinichub:harbourview:requisition", ["value"] = requisition },
        ["entry"] = new JsonArray([
            Entry(new JsonObject
            {
                ["resourceType"] = "Patient",
                ["identifier"] = new JsonArray(
                    new JsonObject { ["system"] = "urn:clinichub:harbourview:mrn", ["value"] = "HV000123" },
                    new JsonObject { ["system"] = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-patient-hcn", ["value"] = "9876543225" }),
                ["name"] = new JsonArray(new JsonObject { ["family"] = "Wilson", ["given"] = new JsonArray("Mei") }),
                ["gender"] = "female", ["birthDate"] = "1986-02-06",
            }),
            Entry(new JsonObject
            {
                ["resourceType"] = "Practitioner",
                ["identifier"] = new JsonArray(
                    new JsonObject { ["system"] = "https://fhir.infoway-inforoute.ca/NamingSystem/ca-on-license-physician", ["value"] = "91234" },
                    new JsonObject { ["system"] = "urn:clinichub:ohip-billing-number", ["value"] = "123456" }),
                ["name"] = new JsonArray(new JsonObject { ["family"] = "Patel", ["given"] = new JsonArray("Nadia") }),
            }),
            .. tests.Select((t, i) => Entry(new JsonObject
            {
                ["resourceType"] = "ServiceRequest",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:service-request", ["value"] = $"{requisition}-{i}" }),
                ["status"] = "active", ["intent"] = "order", ["priority"] = "routine",
                ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["system"] = t.System, ["code"] = t.Code, ["display"] = t.Code }) },
                ["note"] = new JsonArray(new JsonObject { ["text"] = "Fatigue, ?anemia" }),
            })),
        ]),
    }.ToJsonString();

    private static JsonObject Entry(JsonObject r) => new() { ["resource"] = r, ["request"] = new JsonObject { ["method"] = "POST", ["url"] = (string)r["resourceType"]! } };

    internal static async Task<HttpResponseMessage> Send(ConnectedFactory f, string bundle, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/fhir") { Content = new StringContent(bundle, Encoding.UTF8, "application/fhir+json") };
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await f.CreateClient().SendAsync(req);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    [SqlFact]
    public async Task An_order_waits_for_the_patient_and_becomes_a_requisition()
    {
        // Arrives before anyone from the clinic has signed in here: the clinic's database is built for it.
        var res = await Json(await Send(factory, Bundle("R900", ("urn:labflow:test-code", "CBC"), ("http://loinc.org", "4548-4")), ConnectedFactory.ServiceToken("harbourview")));
        var location = (string)res["entry"]![2]!["response"]!["location"]!;
        Assert.Equal(location, (string)res["entry"]![3]!["response"]!["location"]!);
        await Json(await Send(factory, Bundle("R900", ("urn:labflow:test-code", "CBC"), ("http://loinc.org", "4548-4")), ConnectedFactory.ServiceToken("harbourview")));

        // The clinic's collection site.
        await using (var conn = new SqlConnection(factory.ClinicDatabase("harbourview")))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("INSERT core.Location (Name, AddressLine1, City, PostalCode) VALUES (N'Harbourview collection', N'100 Example St', N'Toronto', 'M5V 0A1')", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var desk = await factory.SignInAsync("30", "Jordan Lee", "harbourview", "frontdesk");
        var waiting = (await Json(await desk.GetAsync("/api/reception/electronic-orders"))).AsArray();
        var order = Assert.Single(waiting)!;
        Assert.Equal("Wilson, Mei", (string)order["patient"]!);
        Assert.Equal("Dr. Nadia Patel", (string)order["practitioner"]!);
        Assert.Equal(["Complete blood count", "Hemoglobin A1c"], order["tests"]!.AsArray().Select(t => (string)t!).Order());

        var locations = (await Json(await desk.GetAsync("/api/locations"))).AsArray();
        var requisition = await Json(await desk.PostAsJsonAsync($"/api/reception/electronic-orders/{(int)order["electronicOrderId"]!}/requisition",
            new { locationId = (int)locations[0]!["locationId"]! }));
        Assert.Equal("OHIP", (string)requisition["payerType"]!);
        Assert.Equal(2, requisition["items"]!.AsArray().Count);
        Assert.Empty((await Json(await desk.GetAsync("/api/reception/electronic-orders"))).AsArray());
    }

    [SqlFact]
    public async Task A_test_the_lab_does_not_do_is_refused()
    {
        var res = await Send(factory, Bundle("R901", ("http://loinc.org", "99999-9")), ConnectedFactory.ServiceToken("harbourview"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("OperationOutcome", await res.Content.ReadAsStringAsync());
    }

    [SqlFact]
    public async Task Only_ClinicHub_with_the_orders_scope_may_send()
    {
        var bundle = Bundle("R902", ("urn:labflow:test-code", "CBC"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(factory, bundle, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(factory, bundle, ConnectedFactory.ServiceToken("harbourview", audience: "clinicreport"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(factory, bundle, ConnectedFactory.ServiceToken("harbourview", scope: "profile"))).StatusCode);
    }
}

public class ResultTests(PerClinicFactory factory) : IClassFixture<PerClinicFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    [SqlFact]
    public async Task Released_results_go_back_to_ClinicHub_and_a_correction_follows()
    {
        await Json(await OrderTests.Send(factory, OrderTests.Bundle("R950", ("urn:labflow:test-code", "CBC"), ("http://loinc.org", "4548-4")), ConnectedFactory.ServiceToken("harbourview")));
        await using (var conn = new SqlConnection(factory.ClinicDatabase("harbourview")))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM core.Location) INSERT core.Location (Name, AddressLine1, City, PostalCode) VALUES (N'Site', N'1 St', N'Toronto', 'M5V 0A1')", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk", "results.enter");
        var lab = await factory.SignInAsync("40", "Lab Tech", "harbourview", "frontdesk", "results.enter");
        var order = (await Json(await lab.GetAsync("/api/reception/electronic-orders"))).AsArray().Single(o => (string)o!["clinicalNotes"]! != null)!;
        var location = (int)(await Json(await lab.GetAsync("/api/locations")))[0]!["locationId"]!;
        var requisition = (int)(await Json(await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{(int)order["electronicOrderId"]!}/requisition", new { locationId = location })))["requisitionId"]!;

        var sheet = await Json(await lab.GetAsync($"/api/lab/requisitions/{requisition}"));
        var cbc = sheet["items"]!.AsArray().Single(i => (string)i!["code"]! == "CBC")!;
        Assert.Equal(["HGB", "PLT", "WBC"], cbc["analytes"]!.AsArray().Select(a => (string)a!["code"]!).Order());
        int Item(string code) => (int)sheet["items"]!.AsArray().Single(i => (string)i!["code"]! == code)!["requisitionItemId"]!;
        int Test(string item, string code) => (int)sheet["items"]!.AsArray().Single(i => (string)i!["code"]! == item)!["analytes"]!.AsArray().Single(a => (string)a!["code"]! == code)!["testId"]!;
        object Value(string item, string code, string value) => new { requisitionItemId = Item(item), testId = Test(item, code), value };

        // Not everything entered yet: no release.
        await Json(await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new { results = new[] { Value("CBC", "HGB", "98") } }));
        Assert.Equal(HttpStatusCode.Conflict, (await lab.PostAsync($"/api/lab/requisitions/{requisition}/release", null)).StatusCode);

        var entered = await Json(await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new
        {
            results = new[] { Value("CBC", "WBC", "6.2"), Value("CBC", "PLT", "250"), Value("A1C", "A1C", "7.4") },
        }));
        var hgb = entered["items"]!.AsArray().Single(i => (string)i!["code"]! == "CBC")!["analytes"]!.AsArray().Single(a => (string)a!["code"]! == "HGB")!;
        Assert.Equal("L", (string)hgb["flag"]!);
        Assert.NotNull(hgb["range"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new { results = new[] { Value("CBC", "WBC", "lots") } })).StatusCode);

        var released = await Json(await lab.PostAsync($"/api/lab/requisitions/{requisition}/release", null));
        Assert.Equal("Final", (string)released["status"]!);
        Assert.Equal(1, (int)released["reportVersion"]!);

        var sender = factory.Services.GetRequiredService<ResultSender>();
        Assert.Equal(1, await sender.SendAsync());
        Assert.Equal(new Uri("https://hub.test/fhir"), factory.Hub.ResultUris.Last());   // no ResultsUrl: ClinicHub's own /fhir
        var bundle = factory.Hub.Results.Last();
        var reports = bundle["entry"]!.AsArray().Select(e => e!["resource"]!).Where(r => (string)r["resourceType"]! == "DiagnosticReport").ToList();
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal("final", (string)r["status"]!));
        Assert.Equal(["R950-0", "R950-1"], reports.Select(r => (string)r["basedOn"]![0]!["identifier"]!["value"]!).Order());
        Assert.All(reports, r => Assert.Equal("urn:clinichub:harbourview:service-request", (string)r["basedOn"]![0]!["identifier"]!["system"]!));
        var hemoglobin = bundle["entry"]!.AsArray().Select(e => e!["resource"]!).Single(r => (string)r["resourceType"]! == "Observation" && (string)r["code"]!["text"]! == "Hemoglobin");
        Assert.Equal("718-7", (string)hemoglobin["code"]!["coding"]![0]!["code"]!);
        Assert.Equal(98m, hemoglobin["valueQuantity"]!["value"]!.GetValue<decimal>());
        Assert.Equal("L", (string)hemoglobin["interpretation"]![0]!["coding"]![0]!["code"]!);

        // A correction: released again as an amended report.
        await Json(await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new { results = new[] { Value("A1C", "A1C", "7.1") } }));
        var again = await Json(await lab.PostAsync($"/api/lab/requisitions/{requisition}/release", null));
        Assert.Equal(2, (int)again["reportVersion"]!);
        Assert.Equal(1, await sender.SendAsync());
        Assert.All(factory.Hub.Results.Last()["entry"]!.AsArray().Select(e => e!["resource"]!).Where(r => (string)r["resourceType"]! == "DiagnosticReport"),
            r => Assert.Equal("amended", (string)r["status"]!));
    }

    [SqlFact]
    public async Task Only_lab_staff_with_results_entry_see_the_worklist()
    {
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var desk = await factory.SignInAsync("41", "Desk", "harbourview", "frontdesk");
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.GetAsync("/api/lab/worklist")).StatusCode);
    }
}

public class CancelTests(PerClinicFactory factory) : IClassFixture<PerClinicFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    /// <summary>What ClinicHub sends to withdraw an order: the ServiceRequest again, revoked.</summary>
    private static string Revoke(string id) => new JsonObject
    {
        ["resourceType"] = "Bundle", ["type"] = "transaction",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["resource"] = new JsonObject
            {
                ["resourceType"] = "ServiceRequest", ["status"] = "revoked", ["intent"] = "order",
                ["identifier"] = new JsonArray(new JsonObject { ["system"] = "urn:clinichub:harbourview:service-request", ["value"] = id }),
                ["note"] = new JsonArray(new JsonObject { ["text"] = "Ordered in error" }),
            },
            ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = $"ServiceRequest?identifier=urn:clinichub:harbourview:service-request|{id}" },
        }),
    }.ToJsonString();

    private Task<HttpResponseMessage> Send(string bundle) => OrderTests.Send(factory, bundle, ConnectedFactory.ServiceToken("harbourview"));

    [SqlFact]
    public async Task A_waiting_order_loses_the_test_and_a_requisition_cancels_it_until_results_are_in()
    {
        await Json(await Send(OrderTests.Bundle("R970", ("urn:labflow:test-code", "CBC"), ("http://loinc.org", "4548-4"), ("http://loinc.org", "2857-1"))));
        await using (var conn = new SqlConnection(factory.ClinicDatabase("harbourview")))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM core.Location) INSERT core.Location (Name, AddressLine1, City, PostalCode) VALUES (N'Site', N'1 St', N'Toronto', 'M5V 0A1')", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Still waiting: the test comes off the order; sent again, the same answer.
        await Json(await Send(Revoke("R970-2")));
        await Json(await Send(Revoke("R970-2")));
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk", "results.enter");
        var lab = await factory.SignInAsync("50", "Lab Tech", "harbourview", "frontdesk", "results.enter");
        var order = (await Json(await lab.GetAsync("/api/reception/electronic-orders"))).AsArray().Single()!;
        Assert.Equal(2, order["tests"]!.AsArray().Count);

        var location = (int)(await Json(await lab.GetAsync("/api/locations")))[0]!["locationId"]!;
        var requisition = (int)(await Json(await lab.PostAsJsonAsync($"/api/reception/electronic-orders/{(int)order["electronicOrderId"]!}/requisition", new { locationId = location })))["requisitionId"]!;
        var sheet = await Json(await lab.GetAsync($"/api/lab/requisitions/{requisition}"));
        Assert.Equal(["A1C", "CBC"], sheet["items"]!.AsArray().Select(i => (string)i!["code"]!).Order());

        // A result entered for the CBC: too late to cancel it; the A1c, not started, can still go.
        var cbc = sheet["items"]!.AsArray().Single(i => (string)i!["code"]! == "CBC")!;
        var hgb = cbc["analytes"]!.AsArray().Single(a => (string)a!["code"]! == "HGB")!;
        await Json(await lab.PutAsJsonAsync($"/api/lab/requisitions/{requisition}/results", new
        {
            results = new[] { new { requisitionItemId = (int)cbc["requisitionItemId"]!, testId = (int)hgb["testId"]!, value = "130" } },
        }));
        var refused = await Send(Revoke("R970-0"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("call the lab", await refused.Content.ReadAsStringAsync());

        await Json(await Send(Revoke("R970-1")));
        sheet = await Json(await lab.GetAsync($"/api/lab/requisitions/{requisition}"));
        Assert.Equal(["CBC"], sheet["items"]!.AsArray().Select(i => (string)i!["code"]!));

        Assert.Equal(HttpStatusCode.NotFound, (await Send(Revoke("R999-0"))).StatusCode);
    }
}

public class OneLabOrderTests(OneLabFactory factory) : IClassFixture<OneLabFactory>
{
    [SqlFact]
    public async Task Orders_from_two_clinics_reach_the_one_lab()
    {
        Assert.True((await OrderTests.Send(factory, OrderTests.Bundle("R1", ("http://loinc.org", "3016-3")), ConnectedFactory.ServiceToken("harbourview"))).IsSuccessStatusCode);
        Assert.True((await OrderTests.Send(factory, OrderTests.Bundle("R1", ("http://loinc.org", "3016-3")), ConnectedFactory.ServiceToken("lakeshore"))).IsSuccessStatusCode);
        factory.TokenEndpoint = () => ConnectedFactory.Granted("frontdesk");
        var desk = await factory.SignInAsync("31", "Lab Desk", "harbourview", "frontdesk");
        var waiting = await desk.GetFromJsonAsync<JsonArray>("/api/reception/electronic-orders");
        // Same requisition number from two clinics: two orders, told apart by where they came from.
        Assert.Equal(["harbourview", "lakeshore"], waiting!.Select(o => (string)o!["sourceClinic"]!).Order());
    }
}
