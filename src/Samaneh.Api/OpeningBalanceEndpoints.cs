using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

public static class OpeningBalanceEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/opening-balances")
            .RequireAuthorization();
        group.MapPost("/", CreateDraft);
    }

    private static async Task<IResult> CreateDraft(
        Guid companyId, CreateOpeningBalanceRequest input,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        var number = input.EntryNumber?.Trim();
        var key = input.IdempotencyKey?.Trim();
        var description = input.Description?.Trim() ?? "سند افتتاحیه";
        var lines = input.Lines;

        if (string.IsNullOrWhiteSpace(number) || number.Length > 50 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 120 ||
            description.Length > 1000 || input.FiscalPeriodId == Guid.Empty ||
            lines is null || lines.Count is < 2 or > 500)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["openingBalance"] = ["شماره سند، دوره مالی، کلید تکرارناپذیری و حداقل دو سطر معتبر الزامی است."]
            });

        decimal debit = 0m, credit = 0m;
        var seenAccounts = new HashSet<Guid>();
        foreach (var line in lines)
        {
            if (line.AccountId == Guid.Empty || !seenAccounts.Add(line.AccountId) ||
                line.DebitAmount < 0m || line.CreditAmount < 0m ||
                (line.DebitAmount == 0m && line.CreditAmount == 0m) ||
                (line.DebitAmount > 0m && line.CreditAmount > 0m) ||
                (line.Description?.Length ?? 0) > 500)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lines"] = ["هر حساب باید فقط یک‌بار و با یک مبلغ بدهکار یا بستانکار مثبت ثبت شود."]
                });
            debit += line.DebitAmount;
            credit += line.CreditAmount;
        }

        if (debit <= 0m || debit != credit)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["balance"] = ["سند افتتاحیه باید تراز باشد؛ جمع بدهکار و بستانکار باید برابر و مثبت باشد."]
            });

        var canonical = JsonSerializer.Serialize(new {
            input.FiscalPeriodId, EntryNumber = number, Description = description,
            Lines = lines.OrderBy(x => x.AccountId).Select(x => new {
                x.AccountId, x.DebitAmount, x.CreditAmount,
                Description = x.Description?.Trim() ?? string.Empty
            }).ToArray()
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "OPENING_BALANCES_CREATE", ct))
            return Results.Forbid();

        await using (var existing = new NpgsqlCommand("""
            SELECT id, request_fingerprint, entry_status FROM journal_entries
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
                var currentStatus = reader.GetString(2);
                if (!string.Equals(prior, fingerprint, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "کلید تکرارناپذیری قبلاً برای محتوای دیگری استفاده شده است." });
                await reader.CloseAsync();
                await tx.CommitAsync(ct);
                return Results.Ok(new { id, status = currentStatus, journalType = "OPENING", reused = true });
            }
        }

        DateOnly startsOn;
        string periodStatus, currency;
        await using (var period = new NpgsqlCommand("""
            SELECT p.starts_on, p.period_status, co.functional_currency
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
            startsOn = reader.GetFieldValue<DateOnly>(0);
            periodStatus = reader.GetString(1);
            currency = reader.GetString(2).Trim();
        }
        if (periodStatus != "OPEN")
            return Results.Conflict(new { error = "ثبت افتتاحیه فقط در دوره مالی باز مجاز است." });

        var accountIds = lines.Select(x => x.AccountId).ToArray();
        await using (var accounts = new NpgsqlCommand("""
            SELECT count(*) FROM accounts
             WHERE tenant_id=@tenant AND company_id=@company
               AND id=ANY(@ids) AND is_active=true AND is_postable=true
            """, c, tx))
        {
            accounts.Parameters.AddWithValue("tenant", tenant);
            accounts.Parameters.AddWithValue("company", companyId);
            accounts.Parameters.AddWithValue("ids", accountIds);
            if (Convert.ToInt64(await accounts.ExecuteScalarAsync(ct)) != accountIds.Length)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lines"] = ["تمام حساب‌ها باید در همین شرکت فعال و قابل ثبت باشند."]
                });
        }

        await using (var openingExists = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM journal_entries
             WHERE tenant_id=@tenant AND company_id=@company
               AND fiscal_period_id=@period AND journal_type='OPENING')
            """, c, tx))
        {
            openingExists.Parameters.AddWithValue("tenant", tenant);
            openingExists.Parameters.AddWithValue("company", companyId);
            openingExists.Parameters.AddWithValue("period", input.FiscalPeriodId);
            if ((bool)(await openingExists.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { error = "برای این دوره مالی قبلاً سند افتتاحیه ایجاد شده است." });
        }

        var journalId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO journal_entries
                (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,currency_code,
                 entry_status,description,idempotency_key,request_fingerprint,created_by,journal_type)
            VALUES (@id,@tenant,@company,@period,@number,@date,@currency,
                    'DRAFT',@description,@key,@fingerprint,@actor,'OPENING')
            """, c, tx))
        {
            insert.Parameters.AddWithValue("id", journalId);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("company", companyId);
            insert.Parameters.AddWithValue("period", input.FiscalPeriodId);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("date", startsOn);
            insert.Parameters.AddWithValue("currency", currency);
            insert.Parameters.AddWithValue("description", description);
            insert.Parameters.AddWithValue("key", key);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("actor", actor);
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
            insertLine.Parameters.AddWithValue("debit", line.DebitAmount);
            insertLine.Parameters.AddWithValue("credit", line.CreditAmount);
            insertLine.Parameters.AddWithValue("description", line.Description?.Trim() ?? string.Empty);
            await insertLine.ExecuteNonQueryAsync(ct);
        }

        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
            VALUES(@tenant,@company,@actor,'OPENING_BALANCE_DRAFT_CREATED','JOURNAL_ENTRY',@id,
                   jsonb_build_object('periodId',@period,'debitTotal',@debit,'creditTotal',@credit,'currency',@currency))
            """, c, tx))
        {
            audit.Parameters.AddWithValue("tenant", tenant);
            audit.Parameters.AddWithValue("company", companyId);
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", journalId);
            audit.Parameters.AddWithValue("period", input.FiscalPeriodId);
            audit.Parameters.AddWithValue("debit", debit);
            audit.Parameters.AddWithValue("credit", credit);
            audit.Parameters.AddWithValue("currency", currency);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/journal-entries/{journalId}",
            new { id = journalId, status = "DRAFT", journalType = "OPENING",
                  entryDate = startsOn, currency, debitTotal = debit, creditTotal = credit });
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

    }

public sealed record CreateOpeningBalanceRequest(
    string? EntryNumber, Guid FiscalPeriodId, string? Description,
    string? IdempotencyKey, List<CreateOpeningBalanceLineRequest>? Lines);

public sealed record CreateOpeningBalanceLineRequest(
    Guid AccountId, decimal DebitAmount, decimal CreditAmount, string? Description);
