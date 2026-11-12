using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public static class JournalEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/journal-entries")
            .RequireAuthorization();

        // This endpoint creates DRAFTS only. Approval and posting are deliberately separate workflows.
        group.MapPost("/", CreateDraft);
    }

    private static async Task<IResult> CreateDraft(
        Guid companyId,
        CreateJournalDraftRequest input,
        ClaimsPrincipal user,
        NpgsqlDataSource? ds,
        CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        var entryNumber = input.EntryNumber?.Trim();
        var currency = input.CurrencyCode?.Trim().ToUpperInvariant();
        var idempotencyKey = input.IdempotencyKey?.Trim();
        var description = input.Description?.Trim() ?? string.Empty;
        var lines = input.Lines;

        if (string.IsNullOrWhiteSpace(entryNumber) || entryNumber.Length > 50 ||
            string.IsNullOrWhiteSpace(currency) || currency.Length != 3 ||
            !currency.All(char.IsAsciiLetterUpper) ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 120 ||
            description.Length > 1000 || lines is null || lines.Count is < 2 or > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["journal"] = ["شماره سند، ارز، کلید تکرارناپذیری و حداقل دو سطر معتبر الزامی است."]
            });
        }

        decimal debitTotal = 0m;
        decimal creditTotal = 0m;
        foreach (var line in lines)
        {
            if (line.AccountId == Guid.Empty || line.DebitAmount < 0m || line.CreditAmount < 0m ||
                (line.DebitAmount == 0m && line.CreditAmount == 0m) ||
                (line.DebitAmount > 0m && line.CreditAmount > 0m) ||
                (line.Description?.Length ?? 0) > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lines"] = ["هر سطر باید حساب معتبر و فقط یک مبلغ بدهکار یا بستانکار مثبت داشته باشد."]
                });
            }
            debitTotal += line.DebitAmount;
            creditTotal += line.CreditAmount;
        }

        if (debitTotal <= 0m || debitTotal != creditTotal)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["balance"] = ["جمع بدهکار و بستانکار سند باید برابر و بزرگ‌تر از صفر باشد."]
            });
        }

        var normalized = new
        {
            EntryNumber = entryNumber,
            EntryDate = input.EntryDate.ToString("yyyy-MM-dd"),
            input.FiscalPeriodId,
            CurrencyCode = currency,
            Description = description,
            Lines = lines.Select(x => new
            {
                x.AccountId,
                x.DebitAmount,
                x.CreditAmount,
                Description = x.Description?.Trim() ?? string.Empty
            }).ToArray()
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)))).ToLowerInvariant();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);

        if (!await HasCompanyAccess(c, tx, tenant, companyId, actor, ct))
            return Results.Forbid();

        // Drafts are recorded in the company's functional currency until explicit FX
        // valuation and revaluation workflows are implemented.
        await using (var period = new NpgsqlCommand("""
            SELECT p.period_status, p.starts_on, p.ends_on, co.functional_currency
              FROM fiscal_periods p
              JOIN companies co ON co.tenant_id=p.tenant_id AND co.id=p.company_id
             WHERE p.tenant_id=@tenant AND p.company_id=@company AND p.id=@period
            """, c, tx))
        {
            period.Parameters.AddWithValue("tenant", tenant);
            period.Parameters.AddWithValue("company", companyId);
            period.Parameters.AddWithValue("period", input.FiscalPeriodId);
            await using var reader = await period.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["دوره مالی متعلق به این شرکت نیست."] });

            var status = reader.GetString(0);
            var starts = reader.GetFieldValue<DateOnly>(1);
            var ends = reader.GetFieldValue<DateOnly>(2);
            var functionalCurrency = reader.GetString(3).Trim();

            if (status != "OPEN" || input.EntryDate < starts || input.EntryDate > ends)
                return Results.Conflict(new { error = "دوره مالی بسته است یا تاریخ سند خارج از بازه دوره قرار دارد." });
            if (!string.Equals(currency, functionalCurrency, StringComparison.Ordinal))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencyCode"] = ["تا زمان تکمیل گردش تسعیر، ارز سند باید با ارز عملیاتی شرکت یکسان باشد."] });
        }

        var accountIds = lines.Select(x => x.AccountId).Distinct().ToArray();
        await using (var accounts = new NpgsqlCommand("""
            SELECT count(*)
              FROM accounts
             WHERE tenant_id=@tenant AND company_id=@company
               AND id = ANY(@ids) AND is_active=true AND is_postable=true
            """, c, tx))
        {
            accounts.Parameters.AddWithValue("tenant", tenant);
            accounts.Parameters.AddWithValue("company", companyId);
            accounts.Parameters.AddWithValue("ids", accountIds);
            var validCount = Convert.ToInt64(await accounts.ExecuteScalarAsync(ct));
            if (validCount != accountIds.Length)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["تمام حساب‌های انتخاب‌شده باید در همین شرکت فعال و قابل ثبت باشند."] });
        }

        // A repeated key with identical content returns the original draft.
        // Reusing the same key for different content is rejected.
        await using (var existing = new NpgsqlCommand("""
            SELECT id, request_fingerprint
              FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND idempotency_key=@key
            """, c, tx))
        {
            existing.Parameters.AddWithValue("tenant", tenant);
            existing.Parameters.AddWithValue("company", companyId);
            existing.Parameters.AddWithValue("key", idempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existingId = reader.GetGuid(0);
                var existingFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                await reader.CloseAsync();
                if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "این کلید تکرارناپذیری قبلاً برای محتوای دیگری استفاده شده است." });
                return Results.Ok(new { id = existingId, status = "DRAFT", reused = true });
            }
        }

        var journalId = Guid.NewGuid();
        try
        {
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO journal_entries
                    (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                     entry_status,description,idempotency_key,request_fingerprint,created_by)
                VALUES
                    (@id,@tenant,@company,@period,@number,@date,@currency,
                     'DRAFT',@description,@key,@fingerprint,@actor)
                ON CONFLICT (tenant_id,company_id,idempotency_key) DO NOTHING
                RETURNING id
                """, c, tx))
            {
                insert.Parameters.AddWithValue("id", journalId);
                insert.Parameters.AddWithValue("tenant", tenant);
                insert.Parameters.AddWithValue("company", companyId);
                insert.Parameters.AddWithValue("period", input.FiscalPeriodId);
                insert.Parameters.AddWithValue("number", entryNumber);
                insert.Parameters.AddWithValue("date", input.EntryDate);
                insert.Parameters.AddWithValue("currency", currency);
                insert.Parameters.AddWithValue("description", description);
                insert.Parameters.AddWithValue("key", idempotencyKey);
                insert.Parameters.AddWithValue("fingerprint", fingerprint);
                insert.Parameters.AddWithValue("actor", actor);
                var inserted = await insert.ExecuteScalarAsync(ct);
                if (inserted is null)
                {
                    await using var retry = new NpgsqlCommand("""
                        SELECT id, request_fingerprint FROM journal_entries
                         WHERE tenant_id=@tenant AND company_id=@company AND idempotency_key=@key
                        """, c, tx);
                    retry.Parameters.AddWithValue("tenant", tenant);
                    retry.Parameters.AddWithValue("company", companyId);
                    retry.Parameters.AddWithValue("key", idempotencyKey);
                    await using var reader = await retry.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) return Results.Conflict();
                    var existingId = reader.GetGuid(0);
                    var existingFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                    await reader.CloseAsync();
                    if (!string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                        return Results.Conflict(new { error = "این کلید تکرارناپذیری هم‌زمان برای محتوای دیگری ثبت شده است." });
                    await tx.CommitAsync(ct);
                    return Results.Ok(new { id = existingId, status = "DRAFT", reused = true });
                }
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
                insertLine.Parameters.AddWithValue("debit", line.DebitAmount);
                insertLine.Parameters.AddWithValue("credit", line.CreditAmount);
                insertLine.Parameters.AddWithValue("description", line.Description?.Trim() ?? string.Empty);
                await insertLine.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand("""
                INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
                VALUES(@tenant,@company,@actor,'JOURNAL_DRAFT_CREATED','JOURNAL_ENTRY',@id,
                       jsonb_build_object('entryNumber',@number,'lineCount',@lineCount,
                                          'currency',@currency,'debitTotal',@total))
                """, c, tx))
            {
                audit.Parameters.AddWithValue("tenant", tenant);
                audit.Parameters.AddWithValue("company", companyId);
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("id", journalId);
                audit.Parameters.AddWithValue("number", entryNumber);
                audit.Parameters.AddWithValue("lineCount", lines.Count);
                audit.Parameters.AddWithValue("currency", currency);
                audit.Parameters.AddWithValue("total", debitTotal);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return Results.Created($"/api/companies/{companyId}/journal-entries/{journalId}",
                new { id = journalId, entryNumber, status = "DRAFT", debitTotal, creditTotal, currency, reused = false });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { error = "شماره سند تکراری است یا با یک درخواست هم‌زمان برخورد کرده است." });
        }
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out Guid tenant, out Guid actor)
    {
        tenant = Guid.Empty;
        actor = Guid.Empty;
        return Guid.TryParse(principal.FindFirst("tenant_id")?.Value, out tenant) &&
            Guid.TryParse(principal.FindFirst("sub")?.Value ??
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out actor);
    }

    private static async Task<bool> HasCompanyAccess(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, Guid company, Guid actor, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM company_memberships
                 WHERE tenant_id=@tenant AND company_id=@company AND user_id=@actor AND is_active=true
            )
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", company);
        cmd.Parameters.AddWithValue("actor", actor);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task SetTenant(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id',@tenant,true)", c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record CreateJournalDraftRequest(
    string? EntryNumber,
    DateOnly EntryDate,
    Guid FiscalPeriodId,
    string? CurrencyCode,
    string? Description,
    string? IdempotencyKey,
    List<CreateJournalLineRequest>? Lines);

public sealed record CreateJournalLineRequest(
    Guid AccountId,
    decimal DebitAmount,
    decimal CreditAmount,
    string? Description);
