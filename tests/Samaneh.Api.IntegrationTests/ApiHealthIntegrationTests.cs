using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Samaneh.Api.IntegrationTests;

public sealed class ApiHealthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _configuredFactory;

    public ApiHealthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            // UseSetting applies host settings before the top-level Program reads configuration.
            builder.UseSetting("Authentication:SigningKey", "integration-test-signing-key-at-least-32-bytes");
            builder.UseSetting("Authentication:Issuer", "samaneh-integration-tests");
            builder.UseSetting("Authentication:Audience", "samaneh-api");
            builder.UseSetting("ConnectionStrings:Postgres", "");
        });
        _client = _configuredFactory.CreateClient();
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
    public async Task FinancialRoute_RejectsSignedTokenWithoutTenantClaim()
    {
        using var client = CreateAuthenticatedClient(tenantId: null, subject: Guid.NewGuid().ToString());
        using var response = await client.GetAsync($"/api/companies/{Guid.NewGuid()}/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FinancialRoute_RejectsSignedTokenWithoutSubjectClaim()
    {
        using var client = CreateAuthenticatedClient(tenantId: Guid.NewGuid().ToString(), subject: null);
        using var response = await client.GetAsync($"/api/companies/{Guid.NewGuid()}/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FinancialRoute_RejectsTokenSignedWithDisallowedAlgorithm()
    {
        using var client = CreateAuthenticatedClient(
            tenantId: Guid.NewGuid().ToString(),
            subject: Guid.NewGuid().ToString(),
            algorithm: "HS512");
        using var response = await client.GetAsync($"/api/companies/{Guid.NewGuid()}/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient CreateAuthenticatedClient(string? tenantId, string? subject, string algorithm = "HS256", string issuer = "samaneh-integration-tests", string audience = "samaneh-api", DateTimeOffset? expires = null)
    {
        var client = _configuredFactory.CreateClient();
        var header = Base64Url(JsonSerializer.Serialize(new { alg = algorithm, typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = (expires ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds(),
            ["tenant_id"] = tenantId,
            ["sub"] = subject
        }));
        var unsigned = $"{header}.{payload}";
        var key = Encoding.UTF8.GetBytes("integration-test-signing-key-at-least-32-bytes");
        var data = Encoding.UTF8.GetBytes(unsigned);
        var signature = algorithm switch
        {
            "HS512" => HMACSHA512.HashData(key, data),
            _ => HMACSHA256.HashData(key, data)
        };
        var token = $"{unsigned}.{Base64Url(signature)}";
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string Base64Url(string value) =>
        Base64Url(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task MembershipQueue_RejectsAnonymousRequests()
    {
        using var response = await _client.GetAsync($"/api/companies/{Guid.NewGuid()}/membership-changes/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MembershipRequest_RejectsSelfTargetBeforeDatabaseAccess()
    {
        var actor = Guid.NewGuid();
        using var client = CreateAuthenticatedClient(Guid.NewGuid().ToString(), actor.ToString());
        using var response = await client.PostAsJsonAsync(
            $"/api/companies/{Guid.NewGuid()}/membership-changes/",
            new { targetUserId = actor, requestedRoleCode = "FINANCE_VIEWER", reason = "تغییر نقش" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MembershipRejection_RequiresReviewerRationale(string? reason)
    {
        using var client = CreateAuthenticatedClient(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync(
            $"/api/companies/{Guid.NewGuid()}/membership-changes/{Guid.NewGuid()}/reject",
            new { reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DatabaseHealth_ReturnsServiceUnavailableWhenDatabaseIsNotConfigured()
    {
        using var response = await _client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

public sealed class OidcConfigurationIntegrationTests
{
    [Fact]
    public async Task ApiStartsWithHttpsOidcAuthorityAndAudience()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:SigningKey", "");
            builder.UseSetting("Authentication:Authority", "https://identity.example.test");
            builder.UseSetting("Authentication:Audience", "samaneh-api");
            builder.UseSetting("ConnectionStrings:Postgres", "");
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
