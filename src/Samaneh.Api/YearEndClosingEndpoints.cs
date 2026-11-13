using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Samaneh.Domain;

public static class YearEndClosingEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/year-end-closings")
            .RequireAuthorization();
        group.MapPost("/", CreateClosingDraft);
    }

    private static async Task<IResult> CreateClosingDraft(
        Guid companyId, CreateYearEndClosingRequest input,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        var number = input.EntryNumber?.Trim();
        var key = input.IdempotencyKey?.Trim();
        var description = input.Description?.Trim() ?? "سند بستن حساب‌های موقت پایان سال";
        if (string.IsNullOrWhiteSpace(number) || number.Length > 50 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 120 ||
            description.Length > 1000 || input.FiscalPeriodId == Guid.Empty ||
            input.RetainedEarningsAccountId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["yearEndClosing"] = ["دوره مالی، شماره سند، حساب سود و زیان انباشته و کلید یکتا الزامی است."]
            });

        var fingerprintPayload = JsonSerializer.Serialize(new {
            input.FiscalPeriodId, EntryNumber = number, input.RetainedEarningsAccountId,
            Description = description
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fingerprintPayload))).ToLowerInvariant();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "YEAR_END_CLOSE", ct))
            return Results.Forbid();

        await using (var existingKey = new NpgsqlCommand("""
            SELECT id,request_fingerprint,entry_status FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND idempotency_key=@key
            """, c, tx))
        {
            existingKey.Parameters.AddWithValue("tenant", tenant);
            existingKey.Parameters.AddWithValue("company", companyId);
            existingKey.Parameters.AddWithValue("key", key);
            await using var reader = await existingKey.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var prior = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                var currentStatus = reader.GetString(2);
                if (!string.Equals(prior, fingerprint, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "کلید یکتا قبلاً برای محتوای دیگری استفاده شده است." });
                await reader.CloseAsync();
                await tx.CommitAsync(ct);
                return Results.Ok(new { id, status = currentStatus, journalType = "CLOSING", reused = true });
            }
        }

        DateOnly periodEnd;
        string periodStatus, currency;
        await using (var period = new NpgsqlCommand("""
            SELECT p.ends_on,p.period_status,co.functional_currency
              FROM fiscal_periods p
              JOIN companies co ON co.tenant_id=p.tenant_id AND co.id=p.company_id
             WHERE p.tenant_id=@tenant AND p.company_id=@company AND p.id=@period
             FOR UPDATE OF p
            """, c, tx))
        {
            period.Parameters.AddWithValue("tenant", tenant);
            period.Parameters.AddWithValue("company", companyId);
            period.Parameters.AddWithValue("period", input.FiscalPeriodId);
            await using var reader = await period.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            periodEnd = reader.GetFieldValue<DateOnly>(0);
            periodStatus = reader.GetString(1);
            currency = reader.GetString(2).Trim();
        }
        if (periodStatus != "OPEN")
            return Results.Conflict(new { error = "تهیه سند بستن فقط در دوره مالی باز مجاز است." });

        await using (var alreadyClosed = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company
               AND fiscal_period_id=@period AND journal_type='CLOSING')
            """, c, tx))
        {
            alreadyClosed.Parameters.AddWithValue("tenant", tenant);
            alreadyClosed.Parameters.AddWithValue("company", companyId);
            alreadyClosed.Parameters.AddWithValue("period", input.FiscalPeriodId);
            if ((bool)(await alreadyClosed.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { error = "برای این دوره سند بستن حساب‌های موقت قبلاً ایجاد شده است." });
        }

        await using (var destination = new NpgsqlCommand("""
            SELECT account_name FROM accounts
             WHERE tenant_id=@tenant AND company_id=@company AND id=@account
               AND is_active=true AND is_postable=true AND account_class='EQUITY'
            """, c, tx))
        {
            destination.Parameters.AddWithValue("tenant", tenant);
            destination.Parameters.AddWithValue("company", companyId);
            destination.Parameters.AddWithValue("account", input.RetainedEarningsAccountId);
            if (await destination.ExecuteScalarAsync(ct) is not string)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["retainedEarningsAccountId"] = ["حساب مقصد باید حساب تفصیلی/قابل ثبت، فعال و از طبقه حقوق مالکانه باشد."]
                });
        }

        await using (var unfinished = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company
               AND fiscal_period_id=@period
               AND entry_status NOT IN ('POSTED','REVERSED'))
            """, c, tx))
        {
            unfinished.Parameters.AddWithValue("tenant", tenant);
            unfinished.Parameters.AddWithValue("company", companyId);
            unfinished.Parameters.AddWithValue("period", input.FiscalPeriodId);
            if ((bool)(await unfinished.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new {
                    error = "پیش از تهیه سند بستن، همه اسناد عادی دوره باید تعیین تکلیف و ثبت قطعی شوند؛ اسناد پیش‌نویس یا در انتظار تأیید باقی مانده‌اند."
                });
        }

        await using (var inactive = new NpgsqlCommand("""
            SELECT a.account_code
              FROM accounts a
              JOIN general_ledger_lines gl
                ON gl.tenant_id=a.tenant_id AND gl.company_id=a.company_id
               AND gl.account_id=a.id AND gl.fiscal_period_id=@period
             WHERE a.tenant_id=@tenant AND a.company_id=@company
               AND a.is_postable=true AND a.is_active=false
               AND a.account_class IN ('REVENUE','EXPENSE')
             GROUP BY a.id,a.account_code
            HAVING sum(gl.debit_amount-gl.credit_amount) <> 0
             ORDER BY a.account_code
             LIMIT 1
            """, c, tx))
        {
            inactive.Parameters.AddWithValue("tenant", tenant);
            inactive.Parameters.AddWithValue("company", companyId);
            inactive.Parameters.AddWithValue("period", input.FiscalPeriodId);
            if (await inactive.ExecuteScalarAsync(ct) is string inactiveCode)
                return Results.Conflict(new {
                    error = $"حساب درآمد یا هزینه {inactiveCode} دارای مانده است اما غیرفعال است؛ پیش از بستن سال باید وضعیت آن بررسی شود."
                });
        }

        var temporaryBalances = new List<TemporaryAccountBalance>();
        await using (var balances = new NpgsqlCommand("""
            SELECT a.id,a.account_code,a.account_name,
                   sum(gl.debit_amount-gl.credit_amount) AS debit_net
              FROM accounts a
              JOIN general_ledger_lines gl
                ON gl.tenant_id=a.tenant_id AND gl.company_id=a.company_id
               AND gl.account_id=a.id AND gl.fiscal_period_id=@period
             WHERE a.tenant_id=@tenant AND a.company_id=@company
               AND a.is_active=true AND a.is_postable=true
               AND a.account_class IN ('REVENUE','EXPENSE')
             GROUP BY a.id,a.account_code,a.account_name
            HAVING sum(gl.debit_amount-gl.credit_amount) <> 0
             ORDER BY a.account_code
            """, c, tx))
        {
            balances.Parameters.AddWithValue("tenant", tenant);
            balances.Parameters.AddWithValue("company", companyId);
            balances.Parameters.AddWithValue("period", input.FiscalPeriodId);
            await using var reader = await balances.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                temporaryBalances.Add(new TemporaryAccountBalance(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3)));
        }

        var closing = YearEndClosingCalculator.Calculate(
            temporaryBalances, input.RetainedEarningsAccountId);
        if (!closing.HasTemporaryBalances)
            return Results.Conflict(new { error = "برای این دوره مانده‌ای در حساب‌های درآمد یا هزینه وجود ندارد." });
        var closingLines = closing.Lines;
        var netDebit = closing.NetDebit;

        var journalId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO journal_entries
                (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                 entry_status,description,idempotency_key,request_fingerprint,created_by,journal_type)
            VALUES (@id,@tenant,@company,@period,@number,@date,@currency,
                    'DRAFT',@description,@key,@fingerprint,@actor,'CLOSING')
            """, c, tx))
        {
            insert.Parameters.AddWithValue("id", journalId);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("company", companyId);
            insert.Parameters.AddWithValue("period", input.FiscalPeriodId);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("date", periodEnd);
            insert.Parameters.AddWithValue("currency", currency);
            insert.Parameters.AddWithValue("description", description);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("actor", actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < closingLines.Count; i++)
        {
            var line = closingLines[i];
            await using var insertLine = new NpgsqlCommand("""
                INSERT INTO journal_lines
                    (tenant_id,company_id,journal_entry_id,line_number,account_id,
                     debit_amount,credit_amount,description)
                VALUES (@tenant,@company,@journal,@number,@account,@debit,@credit,@description)
                """, c, tx);
            insertLine.Parameters.AddWithValue("tenant", tenant);
            insertLine.Parameters.AddWithValue("company", companyId);
            insertLine.Parameters.AddWithValue("journal", journalId);
            insertLine.Parameters.AddWithValue("number", i + 1);
            insertLine.Parameters.AddWithValue("account", line.AccountId);
            insertLine.Parameters.AddWithValue("debit", line.Debit);
            insertLine.Parameters.AddWithValue("credit", line.Credit);
            insertLine.Parameters.AddWithValue("description", line.Description);
            await insertLine.ExecuteNonQueryAsync(ct);
        }

        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
            VALUES(@tenant,@company,@actor,'YEAR_END_CLOSING_DRAFT_CREATED','JOURNAL_ENTRY',@id,
                   jsonb_build_object('periodId',@period,'lineCount',@count,
                     'netDebit',@netDebit,'retainedEarningsAccountId',@destination,'currency',@currency))
            """, c, tx))
        {
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", journalId);
            audit.Parameters.AddWithValue("period", input.FiscalPeriodId);
            audit.Parameters.AddWithValue("count", closingLines.Count);
            audit.Parameters.AddWithValue("netDebit", netDebit);
            audit.Parameters.AddWithValue("destination", input.RetainedEarningsAccountId);
            audit.Parameters.AddWithValue("currency", currency);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/journal-entries/{journalId}",
            new { id = journalId, status = "DRAFT", journalType = "CLOSING",
                  entryDate = periodEnd, currency, lineCount = closingLines.Count,
                  netDebit, retainedEarningsAccountId = input.RetainedEarningsAccountId });
    }

    private static bool TryIdentity(ClaimsPrincipal p, out Guid tenant, out Guid actor)
    {
        tenant = Guid.Empty; actor = Guid.Empty;
        return Guid.TryParse(p.FindFirst("tenant_id")?.Value, out tenant) &&
            Guid.TryParse(p.FindFirst("sub")?.Value ??
                p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out actor);
    }

    private static async Task SetTenant(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)", c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

        private sealed record ClosingLine(Guid AccountId, decimal Debit, decimal Credit, string Description);
}

public sealed record CreateYearEndClosingRequest(
    string? EntryNumber, Guid FiscalPeriodId, Guid RetainedEarningsAccountId,
    string? Description, string? IdempotencyKey);
