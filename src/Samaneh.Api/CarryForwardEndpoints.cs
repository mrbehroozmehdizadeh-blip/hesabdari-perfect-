using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public static class CarryForwardEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/carry-forwards")
            .RequireAuthorization();
        group.MapPost("/", CreateOpeningDraft);
    }

    private static async Task<IResult> CreateOpeningDraft(
        Guid companyId, CreateCarryForwardRequest input,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!Identity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        var number = input.EntryNumber?.Trim();
        var key = input.IdempotencyKey?.Trim();
        var description = input.Description?.Trim() ?? "انتقال مانده حساب‌های دائمی از دوره قبل";
        if (input.SourceFiscalPeriodId == Guid.Empty ||
            input.TargetFiscalPeriodId == Guid.Empty ||
            input.SourceFiscalPeriodId == input.TargetFiscalPeriodId ||
            string.IsNullOrWhiteSpace(number) || number.Length > 50 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 120 ||
            description.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["carryForward"] = ["دوره مبدأ و مقصد متفاوت، شماره سند و کلید یکتا الزامی است."]
            });

        var fingerprintPayload = JsonSerializer.Serialize(new {
            input.SourceFiscalPeriodId, input.TargetFiscalPeriodId,
            EntryNumber = number, Description = description
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fingerprintPayload))).ToLowerInvariant();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);

        if (!await HasRole(c, tx, tenant, companyId, actor, "FINANCE_OPENING_MANAGER", ct))
            return Results.Forbid();

        // Lock both periods in stable UUID order to serialize carry-forward against
        // period administration and competing opening-draft creation.
        var periodIds = new[] { input.SourceFiscalPeriodId, input.TargetFiscalPeriodId }
            .OrderBy(x => x).ToArray();
        await using (var lockPeriods = new NpgsqlCommand("""
            SELECT id FROM fiscal_periods
             WHERE tenant_id=@tenant AND company_id=@company AND id=ANY(@ids)
             ORDER BY id FOR UPDATE
            """, c, tx))
        {
            lockPeriods.Parameters.AddWithValue("tenant", tenant);
            lockPeriods.Parameters.AddWithValue("company", companyId);
            lockPeriods.Parameters.AddWithValue("ids", periodIds);
            var found = 0;
            await using var reader = await lockPeriods.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found++;
            if (found != 2) return Results.NotFound();
        }

        DateOnly sourceEnd, targetStart;
        string sourceStatus, targetStatus, currency;
        await using (var periods = new NpgsqlCommand("""
            SELECT src.ends_on,src.period_status,tgt.starts_on,tgt.period_status,
                   co.functional_currency
              FROM fiscal_periods src
              JOIN fiscal_periods tgt
                ON tgt.tenant_id=src.tenant_id AND tgt.company_id=src.company_id
              JOIN companies co ON co.tenant_id=src.tenant_id AND co.id=src.company_id
             WHERE src.tenant_id=@tenant AND src.company_id=@company
               AND src.id=@source AND tgt.id=@target
            """, c, tx))
        {
            periods.Parameters.AddWithValue("tenant", tenant);
            periods.Parameters.AddWithValue("company", companyId);
            periods.Parameters.AddWithValue("source", input.SourceFiscalPeriodId);
            periods.Parameters.AddWithValue("target", input.TargetFiscalPeriodId);
            await using var reader = await periods.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            sourceEnd = reader.GetFieldValue<DateOnly>(0);
            sourceStatus = reader.GetString(1);
            targetStart = reader.GetFieldValue<DateOnly>(2);
            targetStatus = reader.GetString(3);
            currency = reader.GetString(4).Trim();
        }

        if (sourceStatus != "LOCKED")
            return Results.Conflict(new { error = "دوره مبدأ باید پس از بستن نهایی، در وضعیت قفل‌شده باشد." });
        if (targetStatus != "OPEN")
            return Results.Conflict(new { error = "دوره مقصد باید باز باشد." });
        if (targetStart != sourceEnd.AddDays(1))
            return Results.Conflict(new { error = "دوره مقصد باید دقیقاً از روز بعد پایان دوره مبدأ آغاز شود؛ انتقال بین دوره‌های دارای فاصله یا هم‌پوشانی مجاز نیست." });

        await using (var existing = new NpgsqlCommand("""
            SELECT id,request_fingerprint FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND idempotency_key=@key
            """, c, tx))
        {
            existing.Parameters.AddWithValue("tenant", tenant);
            existing.Parameters.AddWithValue("company", companyId);
            existing.Parameters.AddWithValue("key", key);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var prior = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                if (!string.Equals(prior, fingerprint, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "کلید یکتا قبلاً برای محتوای دیگری استفاده شده است." });
                await reader.CloseAsync();
                await tx.CommitAsync(ct);
                return Results.Ok(new { id, status = "DRAFT", journalType = "OPENING", reused = true });
            }
        }

        await using (var existingOpening = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company
               AND fiscal_period_id=@target AND journal_type='OPENING')
            """, c, tx))
        {
            existingOpening.Parameters.AddWithValue("tenant", tenant);
            existingOpening.Parameters.AddWithValue("company", companyId);
            existingOpening.Parameters.AddWithValue("target", input.TargetFiscalPeriodId);
            if ((bool)(await existingOpening.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { error = "برای دوره مقصد قبلاً سند افتتاحیه ایجاد شده است." });
        }

        var lines = new List<CarryForwardLine>();
        decimal debitTotal = 0m, creditTotal = 0m;
        await using (var balances = new NpgsqlCommand("""
            SELECT a.id,a.account_code,a.account_name,a.is_active,
                   sum(gl.debit_amount-gl.credit_amount) AS debit_net
              FROM accounts a
              JOIN general_ledger_lines gl
                ON gl.tenant_id=a.tenant_id AND gl.company_id=a.company_id
               AND gl.account_id=a.id
             WHERE a.tenant_id=@tenant AND a.company_id=@company
               AND a.is_postable=true
               AND a.account_class IN ('ASSET','LIABILITY','EQUITY')
               AND gl.entry_date <= @sourceEnd
               AND gl.currency_code=@currency
             GROUP BY a.id,a.account_code,a.account_name,a.is_active
            HAVING sum(gl.debit_amount-gl.credit_amount) <> 0
             ORDER BY a.account_code
            """, c, tx))
        {
            balances.Parameters.AddWithValue("tenant", tenant);
            balances.Parameters.AddWithValue("company", companyId);
            balances.Parameters.AddWithValue("sourceEnd", sourceEnd);
            balances.Parameters.AddWithValue("currency", currency);
            await using var reader = await balances.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                var accountCode = reader.GetString(1);
                var accountName = reader.GetString(2);
                var isActive = reader.GetBoolean(3);
                var debitNet = reader.GetDecimal(4);
                if (!isActive)
                    return Results.Conflict(new { error = $"حساب دائمی {accountCode} دارای مانده است اما غیرفعال شده؛ ابتدا وضعیت حساب را بررسی و فعال کنید." });

                var debit = debitNet > 0m ? debitNet : 0m;
                var credit = debitNet < 0m ? -debitNet : 0m;
                lines.Add(new CarryForwardLine(id, debit, credit,
                    $"انتقال مانده {accountCode} - {accountName}"));
                debitTotal += debit;
                creditTotal += credit;
            }
        }

        if (lines.Count == 0)
            return Results.Conflict(new { error = "تا پایان دوره مبدأ، مانده غیرصفر در حساب‌های دائمی یافت نشد." });
        if (debitTotal <= 0m || debitTotal != creditTotal)
            return Results.Conflict(new { error = "مانده حساب‌های دائمی تراز نیست؛ پیش از انتقال باید اختلاف دفتر کل بررسی و رفع شود.", debitTotal, creditTotal });

        var journalId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO journal_entries
                (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                 entry_status,description,idempotency_key,request_fingerprint,created_by,journal_type,
                 opening_source_period_id)
            VALUES (@id,@tenant,@company,@target,@number,@date,@currency,
                    'DRAFT',@description,@key,@fingerprint,@actor,'OPENING',@source)
            """, c, tx))
        {
            insert.Parameters.AddWithValue("id", journalId);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("company", companyId);
            insert.Parameters.AddWithValue("target", input.TargetFiscalPeriodId);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("date", targetStart);
            insert.Parameters.AddWithValue("currency", currency);
            insert.Parameters.AddWithValue("description", description);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("actor", actor);
            insert.Parameters.AddWithValue("source", input.SourceFiscalPeriodId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
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
            VALUES(@tenant,@company,@actor,'FISCAL_BALANCE_CARRY_FORWARD_DRAFT_CREATED','JOURNAL_ENTRY',@id,
                   jsonb_build_object('sourcePeriodId',@source,'targetPeriodId',@target,
                     'lineCount',@count,'debitTotal',@debit,'creditTotal',@credit,'currency',@currency))
            """, c, tx))
        {
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", journalId);
            audit.Parameters.AddWithValue("source", input.SourceFiscalPeriodId);
            audit.Parameters.AddWithValue("target", input.TargetFiscalPeriodId);
            audit.Parameters.AddWithValue("count", lines.Count);
            audit.Parameters.AddWithValue("debit", debitTotal);
            audit.Parameters.AddWithValue("credit", creditTotal);
            audit.Parameters.AddWithValue("currency", currency);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/journal-entries/{journalId}",
            new { id = journalId, status = "DRAFT", journalType = "OPENING",
                  sourceFiscalPeriodId = input.SourceFiscalPeriodId,
                  targetFiscalPeriodId = input.TargetFiscalPeriodId,
                  entryDate = targetStart, currency, lineCount = lines.Count,
                  debitTotal, creditTotal });
    }

    private static bool Identity(ClaimsPrincipal p, out Guid tenant, out Guid actor)
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

    private static async Task<bool> HasRole(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, Guid company, Guid actor, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM company_memberships
             WHERE tenant_id=@tenant AND company_id=@company AND user_id=@actor
               AND is_active=true AND role_code=@role)
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", company);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("role", role);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private sealed record CarryForwardLine(Guid AccountId, decimal Debit, decimal Credit, string Description);
}

public sealed record CreateCarryForwardRequest(
    Guid SourceFiscalPeriodId, Guid TargetFiscalPeriodId,
    string? EntryNumber, string? Description, string? IdempotencyKey);
