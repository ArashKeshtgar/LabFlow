using LabFlow.Api.Auth;

namespace LabFlow.Api.Tests;

/// <summary>Where released results go: ClinicHub's own /fhir, or IntegrationHub's clinichub route when set.</summary>
public class ResultsEndpointTests
{
    [Fact]
    public void Without_a_results_url_results_go_to_clinichubs_fhir()
    {
        var options = new ClinicHubOptions { Authority = "http://localhost:5301/" };
        Assert.Equal(new Uri("http://localhost:5301/fhir"), options.ResultsEndpoint());
    }

    [Fact]
    public void A_results_url_sends_them_there_instead()
    {
        var options = new ClinicHubOptions
        {
            Authority = "http://localhost:5301/", ResultsUrl = "http://localhost:5700/route/clinichub/fhir",
        };
        Assert.Equal(new Uri("http://localhost:5700/route/clinichub/fhir"), options.ResultsEndpoint());
    }

    [Fact]
    public void A_blank_results_url_is_the_same_as_none()
    {
        var options = new ClinicHubOptions { Authority = "http://localhost:5301/", ResultsUrl = " " };
        Assert.Equal(new Uri("http://localhost:5301/fhir"), options.ResultsEndpoint());
    }
}
