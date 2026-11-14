using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Samaneh.Api.IntegrationTests;

/// <summary>
/// Exercises the real account API against a migrated PostgreSQL database.
/// The test is activated by SAMANEH_ACCOUNT_TEST_CONNECTION in CI.
/// </summary>
public sealed class AccountsPostgresIntegrationTests : IAsyncLifetime
{
    private const string SigningKey = "samaneh-postgres-integration-signing-key-32-bytes";
    private const string Issuer = "samaneh-account-integration-tests";
    private const string Audience = "samaneh-account-api";
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("SAMANEH_ACCOUNT_TEST_CONNECTION");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var tenant = new NpgsqlCommand("""
            INSERT INTO tenants(id, tenant_code, display_name)
            VALUES (@tenant, @code, 'Account API integration test')
            """, connection, transaction))
        {
            tenant.Parameters.AddWithValue("tenant", _tenantId);
            tenant.Parameters.AddWithValue("code", "ACCT-" + _tenantId.ToString("N"));
            await tenant.ExecuteNonQueryAsync();
        }
        await using (var company = new NpgsqlCommand("""
            INSERT INTO companies(id, tenant_id, company_code, legal_name, functional_currency)
            VALUES (@company, @tenant, 'MAIN', 'Account API integration test', 'IRR')
            """, connection, transaction))
        {
            company.Parameters.AddWithValue("tenant", _tenantId);
            company.Parameters.AddWithValue("company", _companyId);
            await company.ExecuteNonQueryAsync();
        }
        await using (var membership = new NpgsqlCommand("""
            INSERT INTO company_memberships(tenant_id, company_id, user_id, role_code, is_active)
            VALUES (@tenant, @company, @actor, 'FINANCE_ADMIN', true)
            """, connection, transaction))
        {
            membership.Parameters.AddWithValue("tenant", _tenantId);
            membership.Parameters.AddWithValue("company", _companyId);
            membership.Parameters.AddWithValue("actor", _actorId);
            await membership.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("Authentication:SigningKey", SigningKey);
            builder.UseSetting("Authentication:Issuer", Issuer);
            builder.UseSetting("Authentication:Audience", Audience);
        });
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken());
    }

    [Fact]
    public async Task AccountApi_DoesNotExposeCompanyToAnotherTenant()
    {
        if (_factory is null)
            return;

        // A validly signed identity from another tenant must not inherit access
        // merely by knowing the target company's GUID.
        using var foreignClient = _factory.CreateClient();
        foreignClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(Guid.NewGuid(), Guid.NewGuid()));

        using var response = await foreignClient.GetAsync($"/api/companies/{_companyId}/accounts/");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AccountApi_ReadOnlyRoleCanReadButCannotMutate()
    {
        if (_factory is null || string.IsNullOrWhiteSpace(_connectionString))
            return;

        var readerId = Guid.NewGuid();
        await using (var dataSource = NpgsqlDataSource.Create(_connectionString))
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            INSERT INTO company_memberships(tenant_id, company_id, user_id, role_code, is_active)
            VALUES (@tenant, @company, @actor, 'FINANCE_VIEWER', true)
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", _tenantId);
            command.Parameters.AddWithValue("company", _companyId);
            command.Parameters.AddWithValue("actor", readerId);
            await command.ExecuteNonQueryAsync();
        }

        using var readerClient = _factory.CreateClient();
        readerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(_tenantId, readerId));

        var route = $"/api/companies/{_companyId}/accounts/";
        using (var read = await readerClient.GetAsync(route))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var write = await readerClient.PostAsJsonAsync(route, new
        {
            code = "VIEWER-001", name = "نباید ثبت شود", level = 1, parentId = (Guid?)null,
            accountClass = "ASSET", normalBalance = "DEBIT", detailMode = "NONE", isPostable = false
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task AccountApi_SupportsReadCreateUpdateAndGuardedDeleteAgainstPostgres()
    {
        if (_client is null)
            return; // Local runs without the dedicated PostgreSQL test database remain lightweight.

        var route = $"/api/companies/{_companyId}/accounts/";
        using (var initial = await _client.GetAsync(route))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            var rows = await initial.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.NotNull(rows);
            Assert.Empty(rows);
        }

        Guid rootId;
        using (var root = await _client.PostAsJsonAsync(route, new
        {
            code = "100", name = "دارایی‌ها", level = 1, parentId = (Guid?)null,
            accountClass = "ASSET", normalBalance = "DEBIT", detailMode = "NONE", isPostable = false
        }))
        {
            Assert.Equal(HttpStatusCode.Created, root.StatusCode);
            using var payload = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
            rootId = payload.RootElement.GetProperty("id").GetGuid();
        }

        Guid childId;
        using (var child = await _client.PostAsJsonAsync(route, new
        {
            code = "101", name = "صندوق", level = 2, parentId = rootId,
            accountClass = "ASSET", normalBalance = "DEBIT", detailMode = "FIXED", isPostable = true
        }))
        {
            Assert.Equal(HttpStatusCode.Created, child.StatusCode);
            using var payload = JsonDocument.Parse(await child.Content.ReadAsStringAsync());
            childId = payload.RootElement.GetProperty("id").GetGuid();
        }

        var fixedDetailsRoute = route + childId + "/fixed-details/";
        Guid fixedDetailId;
        using (var fixedDetail = await _client.PostAsJsonAsync(fixedDetailsRoute, new { code = "BR-001", name = "شعبه مرکزی" }))
        {
            Assert.Equal(HttpStatusCode.Created, fixedDetail.StatusCode);
            using var payload = JsonDocument.Parse(await fixedDetail.Content.ReadAsStringAsync());
            fixedDetailId = payload.RootElement.GetProperty("id").GetGuid();
        }

        using (var fixedDetailList = await _client.GetAsync(fixedDetailsRoute))
        {
            Assert.Equal(HttpStatusCode.OK, fixedDetailList.StatusCode);
            var rows = await fixedDetailList.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.NotNull(rows);
            Assert.Single(rows);
        }

        using (var duplicateFixedDetail = await _client.PostAsJsonAsync(fixedDetailsRoute, new { code = "BR-001", name = "کد تکراری" }))
            Assert.Equal(HttpStatusCode.Conflict, duplicateFixedDetail.StatusCode);

        using (var deactivateFixedDetail = await _client.PatchAsJsonAsync(fixedDetailsRoute + fixedDetailId + "/active", new { isActive = false }))
            Assert.Equal(HttpStatusCode.OK, deactivateFixedDetail.StatusCode);

        using (var duplicate = await _client.PostAsJsonAsync(route, new
        {
            code = "100", name = "کد تکراری", level = 1, parentId = (Guid?)null,
            accountClass = "ASSET", normalBalance = "DEBIT", detailMode = "NONE", isPostable = false
        }))
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using (var update = await _client.PutAsJsonAsync(route + childId, new { name = "صندوق اصلی", isActive = false }))
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using (var blockedDelete = await _client.DeleteAsync(route + rootId))
            Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);

        using (var deleteChild = await _client.DeleteAsync(route + childId))
            Assert.Equal(HttpStatusCode.NoContent, deleteChild.StatusCode);

        using (var deleteRoot = await _client.DeleteAsync(route + rootId))
            Assert.Equal(HttpStatusCode.NoContent, deleteRoot.StatusCode);

        using (var final = await _client.GetAsync(route))
        {
            Assert.Equal(HttpStatusCode.OK, final.StatusCode);
            var rows = await final.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.NotNull(rows);
            Assert.Empty(rows);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();

        if (string.IsNullOrWhiteSpace(_connectionString))
            return;

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var statement in new[]
        {
            "DELETE FROM audit_events WHERE tenant_id=@tenant",
            "DELETE FROM fixed_account_details WHERE tenant_id=@tenant",
            "DELETE FROM company_memberships WHERE tenant_id=@tenant",
            "DELETE FROM accounts WHERE tenant_id=@tenant AND parent_account_id IS NOT NULL",
            "DELETE FROM accounts WHERE tenant_id=@tenant",
            "DELETE FROM companies WHERE tenant_id=@tenant",
            "DELETE FROM tenants WHERE id=@tenant"
        })
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            command.Parameters.AddWithValue("tenant", _tenantId);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private string CreateToken(Guid? tenantId = null, Guid? actorId = null)
    {
        static string Encode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var payload = Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = Issuer,
            aud = Audience,
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            tenant_id = tenantId ?? _tenantId,
            sub = actorId ?? _actorId
        })));
        var unsigned = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey));
        var signature = Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));
        return $"{unsigned}.{signature}";
    }
}
