using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

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
