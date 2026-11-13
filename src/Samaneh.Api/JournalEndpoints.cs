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

        group.MapPost("/", CreateDraft);
        group.MapPost("/{journalId:guid}/submit", SubmitForApproval);
        group.MapPost("/{journalId:guid}/approve", Approve);
        group.MapPost("/{journalId:guid}/post", PostToLedger);
        group.MapPost("/{journalId:guid}/reverse", CreateReversalDraft);
        group.MapGet("/{journalId:guid}", GetById);
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

        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_CREATE", ct))
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


    private static async Task<IResult> SubmitForApproval(
        Guid companyId, Guid journalId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_SUBMIT", ct)) return Results.Forbid();

        await using (var check = new NpgsqlCommand("""
            SELECT created_by, entry_status FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
             FOR UPDATE
            """, c, tx))
        {
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("company", companyId);
            check.Parameters.AddWithValue("id", journalId);
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var creator = reader.GetGuid(0);
            var status = reader.GetString(1);
            await reader.CloseAsync();
            if (creator != actor) return Results.Forbid();
            if (status != "DRAFT") return Results.Conflict(new { error = "فقط پیش‌نویس سند قابل ارسال برای تأیید است." });
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE journal_entries
               SET entry_status='PENDING', submitted_by=@actor, submitted_at=now()
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id AND entry_status='DRAFT'
            """, c, tx))
        {
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("id", journalId);
            update.Parameters.AddWithValue("actor", actor);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return Results.Conflict();
        }
        await WriteAudit(c, tx, tenant, companyId, actor, journalId, "JOURNAL_SUBMITTED_FOR_APPROVAL", ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = journalId, status = "PENDING" });
    }

    private static async Task<IResult> Approve(
        Guid companyId, Guid journalId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_APPROVE", ct)) return Results.Forbid();

        await using (var check = new NpgsqlCommand("""
            SELECT j.created_by, j.entry_status, p.period_status, p.starts_on, p.ends_on, j.entry_date
              FROM journal_entries j
              JOIN fiscal_periods p
                ON p.tenant_id=j.tenant_id AND p.company_id=j.company_id AND p.id=j.fiscal_period_id
             WHERE j.tenant_id=@tenant AND j.company_id=@company AND j.id=@id
             FOR UPDATE OF j, p
            """, c, tx))
        {
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("company", companyId);
            check.Parameters.AddWithValue("id", journalId);
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var creator = reader.GetGuid(0);
            var status = reader.GetString(1);
            var periodStatus = reader.GetString(2);
            var starts = reader.GetFieldValue<DateOnly>(3);
            var ends = reader.GetFieldValue<DateOnly>(4);
            var entryDate = reader.GetFieldValue<DateOnly>(5);
            await reader.CloseAsync();
            if (creator == actor) return Results.Forbid();
            if (status != "PENDING") return Results.Conflict(new { error = "فقط سند در انتظار تأیید قابل تأیید است." });
            if (periodStatus != "OPEN" || entryDate < starts || entryDate > ends)
                return Results.Conflict(new { error = "دوره مالی سند باز نیست یا تاریخ سند خارج از دوره است." });
        }

        await using (var validate = new NpgsqlCommand("""
            SELECT count(*), coalesce(sum(debit_amount),0), coalesce(sum(credit_amount),0),
                   count(*) FILTER (WHERE a.is_active AND a.is_postable)
              FROM journal_lines l
              JOIN accounts a
                ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
             WHERE l.tenant_id=@tenant AND l.company_id=@company AND l.journal_entry_id=@id
            """, c, tx))
        {
            validate.Parameters.AddWithValue("tenant", tenant);
            validate.Parameters.AddWithValue("company", companyId);
            validate.Parameters.AddWithValue("id", journalId);
            await using var reader = await validate.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var count = reader.GetInt64(0);
            var debit = reader.GetDecimal(1);
            var credit = reader.GetDecimal(2);
            var validAccounts = reader.GetInt64(3);
            if (count < 2 || debit <= 0m || debit != credit || validAccounts != count)
                return Results.Conflict(new { error = "توازن سند یا اعتبار حساب‌های آن برای تأیید معتبر نیست." });
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE journal_entries
               SET entry_status='APPROVED', approved_by=@actor, approved_at=now()
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id AND entry_status='PENDING'
            """, c, tx))
        {
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("id", journalId);
            update.Parameters.AddWithValue("actor", actor);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return Results.Conflict();
        }
        await WriteAudit(c, tx, tenant, companyId, actor, journalId, "JOURNAL_APPROVED", ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = journalId, status = "APPROVED" });
    }



    private static async Task<IResult> CreateReversalDraft(
        Guid companyId, Guid journalId, CreateReversalDraftRequest input,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        var number = input.EntryNumber?.Trim();
        var key = input.IdempotencyKey?.Trim();
        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(number) || number.Length > 50 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 120 || description.Length > 1000 ||
            input.FiscalPeriodId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string,string[]> {
                ["reversal"] = ["شماره سند، کلید تکرارناپذیری و دوره مالی معتبر الزامی است."]
            });

        var normalized = new {
            SourceJournalId = journalId, EntryNumber = number,
            EntryDate = input.EntryDate.ToString("yyyy-MM-dd"),
            input.FiscalPeriodId, Description = description
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)))).ToLowerInvariant();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_REVERSE", ct)) return Results.Forbid();

        string currency;
        await using (var source = new NpgsqlCommand("""
            SELECT entry_status, currency_code
              FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
             FOR UPDATE
            """, c, tx))
        {
            source.Parameters.AddWithValue("tenant",tenant);
            source.Parameters.AddWithValue("company",companyId);
            source.Parameters.AddWithValue("id",journalId);
            await using var reader = await source.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            var status = reader.GetString(0);
            currency = reader.GetString(1).Trim();
            await reader.CloseAsync();
            if (status != "POSTED")
                return Results.Conflict(new { error = "فقط سند ثبت‌شده در دفتر کل قابل تهیه سند معکوس است." });
        }

        await using (var existing = new NpgsqlCommand("""
            SELECT idempotency_key, request_fingerprint, id, entry_status
              FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND reversal_of_journal_id=@source
            """, c, tx))
        {
            existing.Parameters.AddWithValue("tenant",tenant);
            existing.Parameters.AddWithValue("company",companyId);
            existing.Parameters.AddWithValue("source",journalId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var oldKey = reader.GetString(0);
                var oldFingerprint = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
                var oldId = reader.GetGuid(2);
                var oldStatus = reader.GetString(3);
                await reader.CloseAsync();
                if (oldKey == key && oldFingerprint == fingerprint)
                {
                    await tx.CommitAsync(ct);
                    return Results.Ok(new { id=oldId, status=oldStatus, reused=true });
                }
                return Results.Conflict(new { error = "برای این سند قبلاً سند معکوس ایجاد شده است؛ از همان سند استفاده کنید." });
            }
        }

        await using (var period = new NpgsqlCommand("""
            SELECT p.period_status,p.starts_on,p.ends_on,co.functional_currency
              FROM fiscal_periods p
              JOIN companies co ON co.tenant_id=p.tenant_id AND co.id=p.company_id
             WHERE p.tenant_id=@tenant AND p.company_id=@company AND p.id=@period
            """, c, tx))
        {
            period.Parameters.AddWithValue("tenant",tenant);
            period.Parameters.AddWithValue("company",companyId);
            period.Parameters.AddWithValue("period",input.FiscalPeriodId);
            await using var reader = await period.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.ValidationProblem(
                new Dictionary<string,string[]> { ["fiscalPeriodId"]=["دوره مالی متعلق به این شرکت نیست."] });
            var status=reader.GetString(0);
            var starts=reader.GetFieldValue<DateOnly>(1);
            var ends=reader.GetFieldValue<DateOnly>(2);
            var functional=reader.GetString(3).Trim();
            if(status!="OPEN" || input.EntryDate<starts || input.EntryDate>ends)
                return Results.Conflict(new { error="دوره مالی جدید باز نیست یا تاریخ سند خارج از محدوده آن است." });
            if(currency!=functional)
                return Results.Conflict(new { error="ارز سند مبدأ با ارز عملیاتی شرکت برابر نیست؛ مسیر تسعیر/اصلاح ارزی هنوز تکمیل نشده است." });
        }

        long sourceLines;
        decimal debit;
        decimal credit;
        await using (var totals = new NpgsqlCommand("""
            SELECT count(*),coalesce(sum(debit_amount),0),coalesce(sum(credit_amount),0)
              FROM journal_lines
             WHERE tenant_id=@tenant AND company_id=@company AND journal_entry_id=@source
            """, c, tx))
        {
            totals.Parameters.AddWithValue("tenant",tenant);
            totals.Parameters.AddWithValue("company",companyId);
            totals.Parameters.AddWithValue("source",journalId);
            await using var reader=await totals.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            sourceLines=reader.GetInt64(0);
            debit=reader.GetDecimal(1);
            credit=reader.GetDecimal(2);
        }
        if(sourceLines<2 || debit<=0m || debit!=credit)
            return Results.Conflict(new { error="سند مبدأ از نظر تعداد سطر یا توازن قابل معکوس‌سازی نیست." });

        var reversalId=Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO journal_entries
                (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                 entry_status,description,idempotency_key,request_fingerprint,created_by,reversal_of_journal_id)
            VALUES
                (@id,@tenant,@company,@period,@number,@date,@currency,
                 'DRAFT',@description,@key,@fingerprint,@actor,@source)
            """, c, tx))
        {
            insert.Parameters.AddWithValue("id",reversalId);
            insert.Parameters.AddWithValue("tenant",tenant);
            insert.Parameters.AddWithValue("company",companyId);
            insert.Parameters.AddWithValue("period",input.FiscalPeriodId);
            insert.Parameters.AddWithValue("number",number);
            insert.Parameters.AddWithValue("date",input.EntryDate);
            insert.Parameters.AddWithValue("currency",currency);
            insert.Parameters.AddWithValue("description",string.IsNullOrWhiteSpace(description)
                ? $"سند معکوس سند {journalId}" : description);
            insert.Parameters.AddWithValue("key",key);
            insert.Parameters.AddWithValue("fingerprint",fingerprint);
            insert.Parameters.AddWithValue("actor",actor);
            insert.Parameters.AddWithValue("source",journalId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var copy = new NpgsqlCommand("""
            INSERT INTO journal_lines
                (tenant_id,company_id,journal_entry_id,line_number,account_id,debit_amount,credit_amount,description)
            SELECT tenant_id,company_id,@reversal,
                   row_number() OVER (ORDER BY line_number)::integer,
                   account_id,credit_amount,debit_amount,
                   CASE WHEN description='' THEN 'سطر معکوس' ELSE 'معکوس: ' || description END
              FROM journal_lines
             WHERE tenant_id=@tenant AND company_id=@company AND journal_entry_id=@source
             ORDER BY line_number
            """, c, tx))
        {
            copy.Parameters.AddWithValue("reversal",reversalId);
            copy.Parameters.AddWithValue("tenant",tenant);
            copy.Parameters.AddWithValue("company",companyId);
            copy.Parameters.AddWithValue("source",journalId);
            if(await copy.ExecuteNonQueryAsync(ct)!=sourceLines) return Results.Conflict();
        }

        await WriteAudit(c,tx,tenant,companyId,actor,reversalId,"JOURNAL_REVERSAL_DRAFT_CREATED",ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/journal-entries/{reversalId}",
            new { id=reversalId, sourceJournalId=journalId, status="DRAFT", lineCount=sourceLines,
                  debitTotal=debit, creditTotal=credit, currency, reused=false });
    }

    private static async Task<IResult> PostToLedger(
        Guid companyId, Guid journalId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_POST", ct)) return Results.Forbid();

        Guid creator;
        Guid approver;
        Guid periodId;
        DateOnly entryDate;
        string currency;
        string periodStatus;
        await using (var check = new NpgsqlCommand("""
            SELECT j.created_by, j.approved_by, j.fiscal_period_id, j.entry_date,
                   j.currency_code, j.entry_status, p.period_status, p.starts_on, p.ends_on
              FROM journal_entries j
              JOIN fiscal_periods p
                ON p.tenant_id=j.tenant_id AND p.company_id=j.company_id AND p.id=j.fiscal_period_id
             WHERE j.tenant_id=@tenant AND j.company_id=@company AND j.id=@id
             FOR UPDATE OF j, p
            """, c, tx))
        {
            check.Parameters.AddWithValue("tenant", tenant);
            check.Parameters.AddWithValue("company", companyId);
            check.Parameters.AddWithValue("id", journalId);
            await using var reader = await check.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            creator = reader.GetGuid(0);
            approver = reader.GetGuid(1);
            periodId = reader.GetGuid(2);
            entryDate = reader.GetFieldValue<DateOnly>(3);
            currency = reader.GetString(4).Trim();
            var status = reader.GetString(5);
            periodStatus = reader.GetString(6);
            var starts = reader.GetFieldValue<DateOnly>(7);
            var ends = reader.GetFieldValue<DateOnly>(8);
            await reader.CloseAsync();

            if (status != "APPROVED")
                return Results.Conflict(new { error = "فقط سند تأییدشده قابل ثبت در دفتر کل است." });
            if (actor == creator || actor == approver)
                return Results.Forbid();
            if (periodStatus != "OPEN" || entryDate < starts || entryDate > ends)
                return Results.Conflict(new { error = "دوره مالی باز نیست یا تاریخ سند خارج از محدوده دوره است." });
        }

        long lineCount;
        decimal debit;
        decimal credit;
        long validAccounts;
        await using (var validate = new NpgsqlCommand("""
            SELECT count(*), coalesce(sum(l.debit_amount),0), coalesce(sum(l.credit_amount),0),
                   count(*) FILTER (WHERE a.is_active=true AND a.is_postable=true)
              FROM journal_lines l
              JOIN accounts a
                ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
             WHERE l.tenant_id=@tenant AND l.company_id=@company AND l.journal_entry_id=@id
            """, c, tx))
        {
            validate.Parameters.AddWithValue("tenant", tenant);
            validate.Parameters.AddWithValue("company", companyId);
            validate.Parameters.AddWithValue("id", journalId);
            await using var reader = await validate.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            lineCount = reader.GetInt64(0);
            debit = reader.GetDecimal(1);
            credit = reader.GetDecimal(2);
            validAccounts = reader.GetInt64(3);
        }

        if (lineCount < 2 || debit <= 0m || debit != credit || validAccounts != lineCount)
            return Results.Conflict(new { error = "توازن سند یا وضعیت حساب‌های آن معتبر نیست؛ ثبت دفتر کل انجام نشد." });

        // The source journal, ledger rows, status change and audit event commit or roll back together.
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO general_ledger_lines
                (tenant_id,company_id,fiscal_period_id,journal_entry_id,journal_line_id,
                 account_id,entry_date,currency_code,debit_amount,credit_amount,posted_by)
            SELECT l.tenant_id,l.company_id,@period,j.id,l.id,l.account_id,j.entry_date,
                   j.currency_code,l.debit_amount,l.credit_amount,@actor
              FROM journal_lines l
              JOIN journal_entries j
                ON j.tenant_id=l.tenant_id AND j.company_id=l.company_id AND j.id=l.journal_entry_id
             WHERE l.tenant_id=@tenant AND l.company_id=@company AND j.id=@id
            """, c, tx))
        {
            insert.Parameters.AddWithValue("period", periodId);
            insert.Parameters.AddWithValue("actor", actor);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("company", companyId);
            insert.Parameters.AddWithValue("id", journalId);
            if (await insert.ExecuteNonQueryAsync(ct) != lineCount)
                return Results.Conflict(new { error = "تعداد سطرهای انتقال‌یافته به دفتر کل با سند مبدأ برابر نیست." });
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE journal_entries
               SET entry_status='POSTED', posted_by=@actor, posted_at=now()
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id AND entry_status='APPROVED'
            """, c, tx))
        {
            update.Parameters.AddWithValue("actor", actor);
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("id", journalId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return Results.Conflict();
        }

        await WriteAudit(c, tx, tenant, companyId, actor, journalId, "JOURNAL_POSTED_TO_GENERAL_LEDGER", ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { id = journalId, status = "POSTED", ledgerLines = lineCount, debitTotal = debit, creditTotal = credit, currency });
    }

    private static async Task<IResult> GetById(
        Guid companyId, Guid journalId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null) return Results.Unauthorized();
        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "JOURNALS_READ", ct)) return Results.Forbid();

        object? header;
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, entry_number, entry_date, currency_code, entry_status, description,
                   created_by, submitted_by, submitted_at, approved_by, approved_at, posted_by, posted_at
              FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            cmd.Parameters.AddWithValue("id", journalId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            header = new {
                id=reader.GetGuid(0), entryNumber=reader.GetString(1), entryDate=reader.GetFieldValue<DateOnly>(2),
                currency=reader.GetString(3), status=reader.GetString(4), description=reader.GetString(5),
                createdBy=reader.GetGuid(6), submittedBy=reader.IsDBNull(7)?(Guid?)null:reader.GetGuid(7),
                submittedAt=reader.IsDBNull(8)?(DateTimeOffset?)null:reader.GetFieldValue<DateTimeOffset>(8),
                approvedBy=reader.IsDBNull(9)?(Guid?)null:reader.GetGuid(9),
                approvedAt=reader.IsDBNull(10)?(DateTimeOffset?)null:reader.GetFieldValue<DateTimeOffset>(10),
                postedBy=reader.IsDBNull(11)?(Guid?)null:reader.GetGuid(11),
                postedAt=reader.IsDBNull(12)?(DateTimeOffset?)null:reader.GetFieldValue<DateTimeOffset>(12)
            };
        }

        var lines = new List<object>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT l.line_number, l.account_id, a.account_code, a.account_name,
                   l.debit_amount, l.credit_amount, l.description
              FROM journal_lines l
              JOIN accounts a ON a.tenant_id=l.tenant_id AND a.company_id=l.company_id AND a.id=l.account_id
             WHERE l.tenant_id=@tenant AND l.company_id=@company AND l.journal_entry_id=@id
             ORDER BY l.line_number
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("tenant", tenant);
            cmd.Parameters.AddWithValue("company", companyId);
            cmd.Parameters.AddWithValue("id", journalId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lines.Add(new { lineNumber=reader.GetInt32(0), accountId=reader.GetGuid(1),
                    accountCode=reader.GetString(2), accountName=reader.GetString(3),
                    debit=reader.GetDecimal(4), credit=reader.GetDecimal(5), description=reader.GetString(6) });
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { journal = header, lines });
    }

            private static async Task WriteAudit(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, Guid company, Guid actor,
        Guid journalId, string eventType, CancellationToken ct)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id)
            VALUES(@tenant,@company,@actor,@event,'JOURNAL_ENTRY',@id)
            """, c, tx);
        audit.Parameters.AddWithValue("tenant", tenant);
        audit.Parameters.AddWithValue("company", company);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("event", eventType);
        audit.Parameters.AddWithValue("id", journalId);
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out Guid tenant, out Guid actor)
    {
        tenant = Guid.Empty;
        actor = Guid.Empty;
        return Guid.TryParse(principal.FindFirst("tenant_id")?.Value, out tenant) &&
            Guid.TryParse(principal.FindFirst("sub")?.Value ??
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out actor);
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

public sealed record CreateReversalDraftRequest(
    string? EntryNumber, DateOnly EntryDate, Guid FiscalPeriodId,
    string? IdempotencyKey, string? Description);
