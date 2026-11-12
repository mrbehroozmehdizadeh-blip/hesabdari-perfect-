using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

var key = builder.Configuration["Authentication:SigningKey"];
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
        if (!Identity(user, out var tenant, out _) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await Tenant(c, tx, tenant, ct);
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
}
app.UseAuthentication(); app.UseAuthorization();
app.Run();

static bool Identity(ClaimsPrincipal p,out Guid tenant,out Guid actor) {
    tenant=Guid.Empty; actor=Guid.Empty;
    return Guid.TryParse(p.FindFirst("tenant_id")?.Value,out tenant) &&
      Guid.TryParse(p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value,out actor);
}
static async Task Tenant(NpgsqlConnection c,NpgsqlTransaction tx,Guid id,CancellationToken ct) {
    await using var cmd=new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)",c,tx);
    cmd.Parameters.AddWithValue("tenant",id.ToString()); await cmd.ExecuteNonQueryAsync(ct);
}
public sealed record CreateAccount(string? Code,string? Name,int Level,Guid? ParentId,string? AccountClass,string? NormalBalance,string? DetailMode,bool IsPostable);
public partial class Program { }
