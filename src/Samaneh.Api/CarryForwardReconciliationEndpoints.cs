using System.Security.Claims;
using Npgsql;

public static class CarryForwardReconciliationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/companies/{companyId:guid}/carry-forwards/{journalId:guid}/reconciliation",
            Reconcile).RequireAuthorization();
    }

    private static async Task<IResult> Reconcile(
        Guid companyId, Guid journalId, ClaimsPrincipal user,
        NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirst("tenant_id")?.Value, out var tenant) ||
            !Guid.TryParse(user.FindFirst("sub")?.Value ??
                user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var actor) || ds is null)
            return Results.Unauthorized();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var set = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id',@tenant,true)", c, tx))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync(ct);
        }

        await using (var access = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM company_memberships
             WHERE tenant_id=@tenant AND company_id=@company AND user_id=@actor AND is_active=true)
            """, c, tx))
        {
            access.Parameters.AddWithValue("tenant", tenant);
            access.Parameters.AddWithValue("company", companyId);
            access.Parameters.AddWithValue("actor", actor);
            if ((bool)(await access.ExecuteScalarAsync(ct) ?? false) == false)
                return Results.Forbid();
        }

        Guid sourcePeriodId, targetPeriodId;
        DateOnly sourceEnd, targetStart;
        string status, currency, sourceStatus, targetStatus;
        await using (var header = new NpgsqlCommand("""
            SELECT j.opening_source_period_id,j.fiscal_period_id,j.entry_status,j.currency_code,
                   src.ends_on,src.period_status,tgt.starts_on,tgt.period_status
              FROM journal_entries j
              JOIN fiscal_periods src
                ON src.tenant_id=j.tenant_id AND src.company_id=j.company_id
               AND src.id=j.opening_source_period_id
              JOIN fiscal_periods tgt
                ON tgt.tenant_id=j.tenant_id AND tgt.company_id=j.company_id
               AND tgt.id=j.fiscal_period_id
             WHERE j.tenant_id=@tenant AND j.company_id=@company AND j.id=@journal
               AND j.journal_type='OPENING' AND j.opening_source_period_id IS NOT NULL
            """, c, tx))
        {
            header.Parameters.AddWithValue("tenant", tenant);
            header.Parameters.AddWithValue("company", companyId);
            header.Parameters.AddWithValue("journal", journalId);
            await using var reader = await header.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            sourcePeriodId = reader.GetGuid(0);
            targetPeriodId = reader.GetGuid(1);
            status = reader.GetString(2);
            currency = reader.GetString(3).Trim();
            sourceEnd = reader.GetFieldValue<DateOnly>(4);
            sourceStatus = reader.GetString(5);
            targetStart = reader.GetFieldValue<DateOnly>(6);
            targetStatus = reader.GetString(7);
        }

        var lines = new List<object>();
        var matched = true;
        decimal expectedDebitTotal = 0m, expectedCreditTotal = 0m;
        decimal actualDebitTotal = 0m, actualCreditTotal = 0m;
        await using (var compare = new NpgsqlCommand("""
            WITH expected_net AS (
                SELECT a.id AS account_id,a.account_code,a.account_name,
                       sum(gl.debit_amount-gl.credit_amount) AS net
                  FROM accounts a
                  JOIN general_ledger_lines gl
                    ON gl.tenant_id=a.tenant_id AND gl.company_id=a.company_id
                   AND gl.account_id=a.id
                 WHERE a.tenant_id=@tenant AND a.company_id=@company
                   AND a.is_postable=true
                   AND a.account_class IN ('ASSET','LIABILITY','EQUITY')
                   AND gl.entry_date<=@sourceEnd AND gl.currency_code=@currency
                 GROUP BY a.id,a.account_code,a.account_name
                HAVING sum(gl.debit_amount-gl.credit_amount)<>0
            ), expected AS (
                SELECT account_id,account_code,account_name,
                       greatest(net,0) AS debit, greatest(-net,0) AS credit
                  FROM expected_net
            ), actual AS (
                SELECT l.account_id,a.account_code,a.account_name,
                       sum(l.debit_amount) AS debit,sum(l.credit_amount) AS credit
                  FROM journal_lines l
                  JOIN accounts a ON a.tenant_id=l.tenant_id
                   AND a.company_id=l.company_id AND a.id=l.account_id
                 WHERE l.tenant_id=@tenant AND l.company_id=@company
                   AND l.journal_entry_id=@journal
                 GROUP BY l.account_id,a.account_code,a.account_name
            )
            SELECT coalesce(e.account_id,x.account_id),
                   coalesce(e.account_code,x.account_code),
                   coalesce(e.account_name,x.account_name),
                   coalesce(e.debit,0),coalesce(e.credit,0),
                   coalesce(x.debit,0),coalesce(x.credit,0)
              FROM expected e FULL OUTER JOIN actual x ON x.account_id=e.account_id
             ORDER BY coalesce(e.account_code,x.account_code)
            """, c, tx))
        {
            compare.Parameters.AddWithValue("tenant", tenant);
            compare.Parameters.AddWithValue("company", companyId);
            compare.Parameters.AddWithValue("sourceEnd", sourceEnd);
            compare.Parameters.AddWithValue("currency", currency);
            compare.Parameters.AddWithValue("journal", journalId);
            await using var reader = await compare.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var accountId = reader.GetGuid(0);
                var code = reader.GetString(1);
                var name = reader.GetString(2);
                var ed = reader.GetDecimal(3); var ec = reader.GetDecimal(4);
                var ad = reader.GetDecimal(5); var ac = reader.GetDecimal(6);
                var isMatch = ed == ad && ec == ac;
                matched &= isMatch;
                expectedDebitTotal += ed; expectedCreditTotal += ec;
                actualDebitTotal += ad; actualCreditTotal += ac;
                lines.Add(new { accountId, accountCode=code, accountName=name,
                    expectedDebit=ed, expectedCredit=ec, openingDebit=ad, openingCredit=ac,
                    differenceDebit=ad-ed, differenceCredit=ac-ec, matched=isMatch });
            }
        }

        var isBalanced = expectedDebitTotal == expectedCreditTotal &&
                         actualDebitTotal == actualCreditTotal;
        var canReconcile = status == "POSTED" && sourceStatus == "LOCKED" &&
                           targetStatus == "OPEN" && targetStart == sourceEnd.AddDays(1) &&
                           isBalanced && matched && lines.Count > 0;
        await tx.CommitAsync(ct);
        return Results.Ok(new {
            journalId, sourcePeriodId, targetPeriodId, sourceEnd, targetStart,
            journalStatus=status, sourcePeriodStatus=sourceStatus, targetPeriodStatus=targetStatus,
            currency, lineCount=lines.Count, matched, isBalanced, reconciled=canReconcile,
            expectedDebitTotal, expectedCreditTotal, openingDebitTotal=actualDebitTotal,
            openingCreditTotal=actualCreditTotal, lines
        });
    }
}
