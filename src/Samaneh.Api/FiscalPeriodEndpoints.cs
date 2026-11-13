using System.Security.Claims;
using Npgsql;

public static class FiscalPeriodEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/fiscal-periods")
            .RequireAuthorization();

        group.MapGet("/", async Task<IResult> (
            Guid companyId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
        {
            if (!Identity(user, out var tenant, out var actor) || ds is null)
                return Results.Unauthorized();

            await using var c = await ds.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await SetTenant(c, tx, tenant, ct);
            if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "FISCAL_PERIODS_READ", ct))
                return Results.Forbid();

            await using var cmd = new NpgsqlCommand("""
                SELECT id, period_code, starts_on, ends_on, period_status
                  FROM fiscal_periods
                 WHERE tenant_id=@tenant AND company_id=@company
                 ORDER BY starts_on DESC
                """, c, tx);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var periods = new List<object>();
            while (await reader.ReadAsync(ct))
                periods.Add(new {
                    id = reader.GetGuid(0), code = reader.GetString(1),
                    startsOn = reader.GetFieldValue<DateOnly>(2),
                    endsOn = reader.GetFieldValue<DateOnly>(3),
                    status = reader.GetString(4)
                });
            await reader.CloseAsync();
            await tx.CommitAsync(ct);
            return Results.Ok(periods);
        });

        group.MapPost("/{periodId:guid}/close", (Guid companyId, Guid periodId,
            ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
            ChangeStatus(companyId, periodId, "OPEN", "CLOSED",
                "FISCAL_PERIOD_CLOSE", "FISCAL_PERIOD_CLOSED", user, ds, ct));

        group.MapPost("/{periodId:guid}/lock", (Guid companyId, Guid periodId,
            ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
            ChangeStatus(companyId, periodId, "CLOSED", "LOCKED",
                "FISCAL_PERIOD_LOCK", "FISCAL_PERIOD_LOCKED", user, ds, ct));
    }

    private static async Task<IResult> ChangeStatus(Guid companyId, Guid periodId,
        string expected, string target, string requiredRole, string auditType,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);

        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, requiredRole, ct))
            return Results.Forbid();

        string? code;
        await using (var update = new NpgsqlCommand("""
            UPDATE fiscal_periods
               SET period_status=@target
             WHERE tenant_id=@tenant AND company_id=@company
               AND id=@period AND period_status=@expected
            RETURNING period_code
            """, c, tx))
        {
            update.Parameters.AddWithValue("target", target);
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("period", periodId);
            update.Parameters.AddWithValue("expected", expected);
            code = await update.ExecuteScalarAsync(ct) as string;
        }

        if (code is null)
        {
            await tx.RollbackAsync(ct);
            return Results.Conflict(new {
                error = expected == "OPEN"
                    ? "دوره وجود ندارد، بسته شده است یا هنوز اسناد پیش‌نویس/در انتظار تأیید/تأییدشده دارد."
                    : "برای قفل نهایی، دوره باید قبلاً بسته شده باشد."
            });
        }

        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_events
                (tenant_id, company_id, actor_id, event_type, entity_type, entity_id, payload)
            VALUES
                (@tenant, @company, @actor, @event, 'FISCAL_PERIOD', @period,
                 jsonb_build_object('periodCode', @code, 'from', @old, 'to', @target))
            """, c, tx))
        {
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("event", auditType);
            audit.Parameters.AddWithValue("period", periodId);
            audit.Parameters.AddWithValue("code", code);
            audit.Parameters.AddWithValue("old", expected);
            audit.Parameters.AddWithValue("target", target);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Ok(new { id = periodId, code, status = target });
    }

    private static bool Identity(ClaimsPrincipal p, out Guid tenant, out Guid actor)
    {
        tenant = Guid.Empty; actor = Guid.Empty;
        return Guid.TryParse(p.FindFirst("tenant_id")?.Value, out tenant) &&
            Guid.TryParse(p.FindFirst("sub")?.Value ??
                p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out actor);
    }

    private static async Task SetTenant(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasMembership(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, Guid company, Guid actor, string? role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM company_memberships
                 WHERE tenant_id=@tenant AND company_id=@company
                   AND user_id=@actor AND is_active=true
                   AND (@role IS NULL OR role_code=@role)
            )
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", company);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("role", (object?)role ?? DBNull.Value);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }
}
