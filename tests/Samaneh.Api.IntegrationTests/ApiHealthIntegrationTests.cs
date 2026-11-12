using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Samaneh.Api.IntegrationTests;

public sealed class ApiHealthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiHealthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var configured = factory.WithWebHostBuilder(builder =>
        {
            // UseSetting applies host settings before the top-level Program reads configuration.
            builder.UseSetting("Authentication:SigningKey", "integration-test-signing-key-at-least-32-bytes");
            builder.UseSetting("Authentication:Issuer", "samaneh-integration-tests");
            builder.UseSetting("Authentication:Audience", "samaneh-api");
            builder.UseSetting("ConnectionStrings:Postgres", "");
        });
        _client = configured.CreateClient();
    }

    [Fact]
    public async Task Root_ReturnsServiceIdentityAndPrototypeMaturity()
    {
        using var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("سامانه مالی و جامع هوشمند", payload.RootElement.GetProperty("name").GetString());
        Assert.Equal("Samaneh.Api", payload.RootElement.GetProperty("service").GetString());
        Assert.Equal("running", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("prototype", payload.RootElement.GetProperty("maturity").GetString());
    }

    [Fact]
    public async Task Health_ReturnsHealthyWithoutDatabaseDependency()
    {
        using var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("fiscal-periods")]
    [InlineData("journal-entries")]
    public async Task CompanyFinancialRoutes_RejectAnonymousRequests(string route)
    {
        var companyId = Guid.NewGuid();
        using var response = await _client.GetAsync(route switch
        {
            "accounts" => $"/api/companies/{companyId}/accounts",
            "fiscal-periods" => $"/api/companies/{companyId}/fiscal-periods",
            "journal-entries" => $"/api/companies/{companyId}/journal-entries/{Guid.NewGuid()}",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseHealth_ReturnsServiceUnavailableWhenDatabaseIsNotConfigured()
    {
        using var response = await _client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
