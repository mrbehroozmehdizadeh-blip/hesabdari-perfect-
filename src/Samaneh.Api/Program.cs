using System.Security.Claims;
using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

var key = builder.Configuration["Authentication:SigningKey"];
if (!string.IsNullOrWhiteSpace(key) && System.Text.Encoding.UTF8.GetByteCount(key) < 32)
    throw new InvalidOperationException("Authentication:SigningKey must contain at least 32 UTF-8 bytes.");
if (!string.IsNullOrWhiteSpace(key))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new()
        {
            ValidateIssuer = true, ValidIssuer = builder.Configuration["Authentication:Issuer"],
            ValidateAudience = true, ValidAudience = builder.Configuration["Authentication:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
    builder.Services.AddAuthorization();
}

var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { name = "سامانه مالی و جامع هوشمند", service = "Samaneh.Api", status = "running", maturity = "prototype" }));
app.MapHealthChecks("/health");
app.MapGet("/health/db", async (IServiceProvider sp, CancellationToken ct) =>
{
    var ds = sp.GetService<NpgsqlDataSource>();
    if (ds is null) return Results.Problem(title: "Database is not configured", statusCode: 503);
    try {
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1", c);
        return Equals(await cmd.ExecuteScalarAsync(ct), 1) ? Results.Ok(new { database = "PostgreSQL", status = "connected" }) : Results.Problem(statusCode: 503);
    } catch (NpgsqlException) { return Results.Problem(title: "PostgreSQL connection failed", statusCode: 503); }
});

if (!string.IsNullOrWhiteSpace(key))
{
    var group = app.MapGroup("/api/companies/{companyId:guid}/accounts").RequireAuthorization();
    group.MapGet("/", async Task<IResult> (Guid companyId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        await using var cmd = new NpgsqlCommand("""
            SELECT id, account_code, account_name, account_level, parent_account_id, account_class,
                   normal_balance, detail_mode, is_postable, is_active
              FROM accounts WHERE tenant_id=@t AND company_id=@c ORDER BY account_code
            """, c, tx);
        cmd.Parameters.AddWithValue("t", tenant); cmd.Parameters.AddWithValue("c", companyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await r.ReadAsync(ct))
            rows.Add(new { id=r.GetGuid(0), code=r.GetString(1), name=r.GetString(2), level=r.GetInt16(3),
                parentId=r.IsDBNull(4)?(Guid?)null:r.GetGuid(4), accountClass=r.GetString(5),
                normalBalance=r.GetString(6), detailMode=r.GetString(7), isPostable=r.GetBoolean(8), isActive=r.GetBoolean(9) });
        await r.CloseAsync(); await tx.CommitAsync(ct);
        return Results.Ok(rows);
    });

    group.MapPost("/", async Task<IResult> (Guid companyId, CreateAccount input, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        var code=input.Code?.Trim(); var name=input.Name?.Trim();
        var cls=input.AccountClass?.ToUpperInvariant(); var balance=input.NormalBalance?.ToUpperInvariant();
        var detail=(input.DetailMode ?? "NONE").ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name) || input.Level is < 1 or > 10 ||
            !new[]{"ASSET","LIABILITY","EQUITY","REVENUE","EXPENSE"}.Contains(cls) ||
            !new[]{"DEBIT","CREDIT"}.Contains(balance) || !new[]{"NONE","FIXED","FLOATING"}.Contains(detail) ||
            (!input.IsPostable && detail!="NONE"))
            return Results.ValidationProblem(new Dictionary<string,string[]>{{"account",["اطلاعات حساب معتبر نیست."]}});

        await using var c=await ds.OpenConnectionAsync(ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        await Tenant(c,tx,tenant,ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        if ((input.Level==1 && input.ParentId is not null) || (input.Level>1 && input.ParentId is null))
            return Results.ValidationProblem(new Dictionary<string,string[]>{{"parentId",["والد با سطح حساب سازگار نیست."]}});
        if (input.ParentId is Guid pid) {
            await using var pc=new NpgsqlCommand("SELECT account_level,is_postable FROM accounts WHERE tenant_id=@t AND company_id=@c AND id=@p",c,tx);
            pc.Parameters.AddWithValue("t",tenant); pc.Parameters.AddWithValue("c",companyId); pc.Parameters.AddWithValue("p",pid);
            await using var reader=await pc.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetInt16(0)!=input.Level-1 || reader.GetBoolean(1))
                return Results.ValidationProblem(new Dictionary<string,string[]>{{"parentId",["والد باید در همان شرکت، یک سطح بالاتر و غیرقابل ثبت باشد."]}});
        }
        var id=Guid.NewGuid();
        await using (var cmd=new NpgsqlCommand("""
            INSERT INTO accounts(id,tenant_id,company_id,parent_account_id,account_code,account_name,account_level,
              account_class,normal_balance,detail_mode,is_postable)
            VALUES(@id,@t,@c,@p,@code,@name,@level,@class,@balance,@detail,@postable)
            """,c,tx)) {
            cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("t",tenant); cmd.Parameters.AddWithValue("c",companyId);
            cmd.Parameters.AddWithValue("p",(object?)input.ParentId??DBNull.Value); cmd.Parameters.AddWithValue("code",code!);
            cmd.Parameters.AddWithValue("name",name!); cmd.Parameters.AddWithValue("level",input.Level);
            cmd.Parameters.AddWithValue("class",cls!); cmd.Parameters.AddWithValue("balance",balance!);
            cmd.Parameters.AddWithValue("detail",detail); cmd.Parameters.AddWithValue("postable",input.IsPostable);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var audit=new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
            VALUES(@t,@c,@actor,'ACCOUNT_CREATED','ACCOUNT',@id,jsonb_build_object('code',@code,'level',@level))
            """,c,tx)) {
            audit.Parameters.AddWithValue("t",tenant); audit.Parameters.AddWithValue("c",companyId);
            audit.Parameters.AddWithValue("actor",actor); audit.Parameters.AddWithValue("id",id);
            audit.Parameters.AddWithValue("code",code!); audit.Parameters.AddWithValue("level",input.Level);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/accounts/{id}",new{id,code,name});
    });
    group.MapPut("/{accountId:guid}", async Task<IResult> (Guid companyId, Guid accountId, UpdateAccount input, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.ValidationProblem(new Dictionary<string,string[]>{{"name",["نام حساب الزامی است."]}});
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        await using (var update = new NpgsqlCommand("""
            UPDATE accounts SET account_name=@name, is_active=@active
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
            RETURNING account_code
            """, c, tx))
        {
            update.Parameters.AddWithValue("name", name);
            update.Parameters.AddWithValue("active", input.IsActive);
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("id", accountId);
            var code = await update.ExecuteScalarAsync(ct) as string;
            if (code is null) return Results.NotFound();
            await using var audit = new NpgsqlCommand("""
                INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
                VALUES(@tenant,@company,@actor,'ACCOUNT_UPDATED','ACCOUNT',@id,
                       jsonb_build_object('name',@name,'isActive',@active))
                """, c, tx);
            audit.Parameters.AddWithValue("tenant", tenant); audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor); audit.Parameters.AddWithValue("id", accountId);
            audit.Parameters.AddWithValue("name", name); audit.Parameters.AddWithValue("active", input.IsActive);
            await audit.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id = accountId, code, name, isActive = input.IsActive });
        }
    });

    group.MapDelete("/{accountId:guid}", async Task<IResult> (Guid companyId, Guid accountId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        await using (var lockAccount = new NpgsqlCommand("""
            SELECT account_code FROM accounts
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
             FOR UPDATE
            """, c, tx))
        {
            lockAccount.Parameters.AddWithValue("tenant", tenant);
            lockAccount.Parameters.AddWithValue("company", companyId);
            lockAccount.Parameters.AddWithValue("id", accountId);
            var code = await lockAccount.ExecuteScalarAsync(ct) as string;
            if (code is null) return Results.NotFound();

            await using var guard = new NpgsqlCommand("""
                SELECT
                  EXISTS(SELECT 1 FROM accounts WHERE tenant_id=@tenant AND company_id=@company AND parent_account_id=@id),
                  EXISTS(SELECT 1 FROM journal_lines WHERE tenant_id=@tenant AND company_id=@company AND account_id=@id)
                """, c, tx);
            guard.Parameters.AddWithValue("tenant", tenant);
            guard.Parameters.AddWithValue("company", companyId);
            guard.Parameters.AddWithValue("id", accountId);
            await using var reader = await guard.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var hasChildren = reader.GetBoolean(0);
            var hasTransactions = reader.GetBoolean(1);
            await reader.CloseAsync();
            if (hasChildren || hasTransactions)
                return Results.Conflict(new { error = "حساب دارای زیرحساب یا سابقه گردش است و قابل حذف نیست؛ آن را غیرفعال کنید." });

            await using var delete = new NpgsqlCommand(
                "DELETE FROM accounts WHERE tenant_id=@tenant AND company_id=@company AND id=@id", c, tx);
            delete.Parameters.AddWithValue("tenant", tenant);
            delete.Parameters.AddWithValue("company", companyId);
            delete.Parameters.AddWithValue("id", accountId);
            await delete.ExecuteNonQueryAsync(ct);

            await using var audit = new NpgsqlCommand("""
                INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
                VALUES(@tenant,@company,@actor,'ACCOUNT_DELETED','ACCOUNT',@id,jsonb_build_object('code',@code))
                """, c, tx);
            audit.Parameters.AddWithValue("tenant", tenant); audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor); audit.Parameters.AddWithValue("id", accountId);
            audit.Parameters.AddWithValue("code", code);
            await audit.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }
    });


    var dimensions = app.MapGroup("/api/companies/{companyId:guid}/dimensions").RequireAuthorization();
    dimensions.MapGet("/", async Task<IResult> (Guid companyId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        await using var cmd = new NpgsqlCommand("""
            SELECT id, dimension_type, dimension_code, dimension_name, is_active
              FROM floating_dimensions
             WHERE tenant_id=@tenant AND company_id=@company
             ORDER BY dimension_type, dimension_code
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", companyId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
            rows.Add(new { id=reader.GetGuid(0), type=reader.GetString(1), code=reader.GetString(2),
                name=reader.GetString(3), isActive=reader.GetBoolean(4) });
        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return Results.Ok(rows);
    });
    dimensions.MapPost("/", async Task<IResult> (Guid companyId, CreateDimension input, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        var type = input.Type?.Trim().ToUpperInvariant();
        var code = input.Code?.Trim();
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(type) || type.Length > 40 ||
            !type.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') ||
            string.IsNullOrWhiteSpace(code) || code.Length > 60 ||
            string.IsNullOrWhiteSpace(name) || name.Length > 250)
            return Results.ValidationProblem(new Dictionary<string,string[]> { ["dimension"] = ["نوع، کد یا نام بُعد تحلیلی معتبر نیست."] });
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        var id = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO floating_dimensions(id,tenant_id,company_id,dimension_type,dimension_code,dimension_name)
            VALUES(@id,@tenant,@company,@type,@code,@name)
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            cmd.Parameters.AddWithValue("type", type);
            cmd.Parameters.AddWithValue("code", code!);
            cmd.Parameters.AddWithValue("name", name!);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
            VALUES(@tenant,@company,@actor,'DIMENSION_CREATED','FLOATING_DIMENSION',@id,
                   jsonb_build_object('type',@type,'code',@code))
            """, c, tx))
        {
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", id);
            audit.Parameters.AddWithValue("type", type);
            audit.Parameters.AddWithValue("code", code!);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/dimensions/{id}", new { id, type, code, name, isActive = true });
    });
    dimensions.MapPatch("/{dimensionId:guid}/active", async Task<IResult> (Guid companyId, Guid dimensionId, SetDimensionActive input, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();
        await using (var cmd = new NpgsqlCommand("""
            UPDATE floating_dimensions SET is_active=@active
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
            RETURNING dimension_type, dimension_code
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("active", input.IsActive);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            cmd.Parameters.AddWithValue("id", dimensionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var type = reader.GetString(0);
            var code = reader.GetString(1);
            await reader.CloseAsync();
            await using var audit = new NpgsqlCommand("""
                INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
                VALUES(@tenant,@company,@actor,'DIMENSION_ACTIVE_CHANGED','FLOATING_DIMENSION',@id,
                       jsonb_build_object('isActive',@active))
                """, c, tx);
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", dimensionId);
            audit.Parameters.AddWithValue("active", input.IsActive);
            await audit.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id = dimensionId, type, code, isActive = input.IsActive });
        }
    });

    JournalEndpoints.Map(app);
    LedgerEndpoints.Map(app);
    FiscalPeriodEndpoints.Map(app);
    OpeningBalanceEndpoints.Map(app);
    YearEndClosingEndpoints.Map(app);
}
if (allowedOrigins.Length > 0) app.UseCors();
if (!string.IsNullOrWhiteSpace(key)) { app.UseAuthentication(); app.UseAuthorization(); }
app.Run();

static bool Identity(ClaimsPrincipal p,out Guid tenant,out Guid actor) {
    tenant=Guid.Empty; actor=Guid.Empty;
    return Guid.TryParse(p.FindFirst("tenant_id")?.Value,out tenant) &&
      Guid.TryParse(p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value,out actor);
}
static async Task<bool> HasCompanyAccess(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, Guid company, Guid actor, CancellationToken ct)
{
    await using var cmd = new NpgsqlCommand("""
        SELECT EXISTS(
            SELECT 1 FROM company_memberships
             WHERE tenant_id=@tenant AND company_id=@company AND user_id=@actor AND is_active=true
        )
        """, c, tx);
    cmd.Parameters.AddWithValue("tenant", tenant);
    cmd.Parameters.AddWithValue("company", company);
    cmd.Parameters.AddWithValue("actor", actor);
    return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
}
static async Task Tenant(NpgsqlConnection c,NpgsqlTransaction tx,Guid id,CancellationToken ct) {
    await using var cmd=new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)",c,tx);
    cmd.Parameters.AddWithValue("tenant",id.ToString()); await cmd.ExecuteNonQueryAsync(ct);
}
public sealed record CreateAccount(string? Code,string? Name,int Level,Guid? ParentId,string? AccountClass,string? NormalBalance,string? DetailMode,bool IsPostable);
public sealed record UpdateAccount(string? Name, bool IsActive);
public sealed record CreateDimension(string? Type, string? Code, string? Name);
public sealed record SetDimensionActive(bool IsActive);
public partial class Program { }
