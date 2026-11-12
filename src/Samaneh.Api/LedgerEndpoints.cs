using System.Security.Claims;
using Npgsql;

public static class LedgerEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/ledger").RequireAuthorization();

        group.MapGet("/trial-balance", async Task<IResult> (
            Guid companyId, Guid fiscalPeriodId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
        {
            if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
            await using var c = await ds.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await SetTenant(c, tx, tenant, ct);
            if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();

            await using (var period = new NpgsqlCommand("""
                SELECT period_code, period_status FROM fiscal_periods
                 WHERE tenant_id=@tenant AND company_id=@company AND id=@period
                """, c, tx))
            {
                period.Parameters.AddWithValue("tenant", tenant);
                period.Parameters.AddWithValue("company", companyId);
                period.Parameters.AddWithValue("period", fiscalPeriodId);
                await using var reader = await period.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                var periodCode = reader.GetString(0);
                var periodStatus = reader.GetString(1);
                await reader.CloseAsync();

                var rows = new List<object>();
                await using var cmd = new NpgsqlCommand("""
                    SELECT a.id, a.account_code, a.account_name, a.normal_balance,
                           coalesce(sum(g.debit_amount),0) AS debit_total,
                           coalesce(sum(g.credit_amount),0) AS credit_total
                      FROM accounts a
                      LEFT JOIN general_ledger_lines g
                        ON g.tenant_id=a.tenant_id AND g.company_id=a.company_id
                       AND g.account_id=a.id AND g.fiscal_period_id=@period
                     WHERE a.tenant_id=@tenant AND a.company_id=@company
                       AND a.is_postable=true
                     GROUP BY a.id,a.account_code,a.account_name,a.normal_balance
                     ORDER BY a.account_code
                    """, c, tx);
                cmd.Parameters.AddWithValue("tenant", tenant);
                cmd.Parameters.AddWithValue("company", companyId);
                cmd.Parameters.AddWithValue("period", fiscalPeriodId);
                await using var result = await cmd.ExecuteReaderAsync(ct);
                decimal debitGrandTotal = 0m;
                decimal creditGrandTotal = 0m;
                while (await result.ReadAsync(ct))
                {
                    var debit = result.GetDecimal(4);
                    var credit = result.GetDecimal(5);
                    debitGrandTotal += debit;
                    creditGrandTotal += credit;
                    rows.Add(new {
                        accountId=result.GetGuid(0), code=result.GetString(1), name=result.GetString(2),
                        normalBalance=result.GetString(3), debit, credit,
                        debitBalance=Math.Max(debit-credit,0m), creditBalance=Math.Max(credit-debit,0m)
                    });
                }
                await result.CloseAsync();
                await tx.CommitAsync(ct);
                return Results.Ok(new {
                    fiscalPeriodId, periodCode, periodStatus, rows,
                    debitTotal=debitGrandTotal, creditTotal=creditGrandTotal,
                    isBalanced=debitGrandTotal==creditGrandTotal
                });
            }
        });

        group.MapGet("/accounts/{accountId:guid}/activity", async Task<IResult> (
            Guid companyId, Guid accountId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
        {
            if (!Identity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
            await using var c = await ds.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await SetTenant(c, tx, tenant, ct);
            if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct)) return Results.Forbid();

            await using var cmd = new NpgsqlCommand("""
                SELECT g.entry_date, j.entry_number, g.journal_entry_id, g.journal_line_id,
                       g.debit_amount, g.credit_amount, j.description, g.posted_at,
                       sum(g.debit_amount-g.credit_amount) OVER (
                           ORDER BY g.entry_date,g.posted_at,g.journal_entry_id,g.journal_line_id
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                       ) AS running_debit_net
                  FROM general_ledger_lines g
                  JOIN journal_entries j
                    ON j.tenant_id=g.tenant_id AND j.company_id=g.company_id AND j.id=g.journal_entry_id
                 WHERE g.tenant_id=@tenant AND g.company_id=@company AND g.account_id=@account
                 ORDER BY g.entry_date,g.posted_at,g.journal_entry_id,g.journal_line_id
                 LIMIT 500
                """, c, tx);
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            cmd.Parameters.AddWithValue("account", accountId);
            var rows = new List<object>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    rows.Add(new {
                        entryDate=reader.GetFieldValue<DateOnly>(0), entryNumber=reader.GetString(1),
                        journalId=reader.GetGuid(2), journalLineId=reader.GetGuid(3),
                        debit=reader.GetDecimal(4), credit=reader.GetDecimal(5),
                        description=reader.GetString(6), postedAt=reader.GetFieldValue<DateTime>(7),
                        runningDebitNet=reader.GetDecimal(8)
                    });
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { accountId, rows, limit=500 });
        });
    }

    private static bool Identity(ClaimsPrincipal p, out Guid tenant, out Guid actor)
    {
        tenant=Guid.Empty; actor=Guid.Empty;
        return Guid.TryParse(p.FindFirst("tenant_id")?.Value,out tenant) &&
            Guid.TryParse(p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value,out actor);
    }

    private static async Task<bool> HasCompanyAccess(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, Guid company, Guid actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM company_memberships
             WHERE tenant_id=@tenant AND company_id=@company AND user_id=@actor AND is_active=true)
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant",tenant);
        cmd.Parameters.AddWithValue("company",company);
        cmd.Parameters.AddWithValue("actor",actor);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task SetTenant(NpgsqlConnection c,NpgsqlTransaction tx,Guid tenant,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)",c,tx);
        cmd.Parameters.AddWithValue("tenant",tenant.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
