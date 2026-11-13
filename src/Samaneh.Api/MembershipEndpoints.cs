using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

public static class MembershipEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/companies/{companyId:guid}/membership-changes")
            .RequireAuthorization();

        group.MapGet("/", List);
        group.MapPost("/", RequestChange);
        group.MapPost("/{requestId:guid}/approve", Approve);
        group.MapPost("/{requestId:guid}/reject", Reject);
    }

    private static async Task<IResult> List(
        Guid companyId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "MEMBERSHIPS_READ", ct))
            return Results.Forbid();

        await using var cmd = new NpgsqlCommand("""
            SELECT id, target_user_id, requested_role_code, requested_by, request_status,
                   reviewed_by, reviewed_at, reason, created_at
              FROM membership_change_requests
             WHERE tenant_id=@tenant AND company_id=@company
             ORDER BY created_at DESC, id
             LIMIT 500
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", companyId);

        var rows = new List<object>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                rows.Add(new
                {
                    id = reader.GetGuid(0),
                    targetUserId = reader.GetGuid(1),
                    requestedRoleCode = reader.GetString(2),
                    requestedBy = reader.GetGuid(3),
                    status = reader.GetString(4),
                    reviewedBy = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5),
                    reviewedAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6),
                    reason = reader.GetString(7),
                    createdAt = reader.GetFieldValue<DateTimeOffset>(8)
                });
        }

        await tx.CommitAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> RequestChange(
        Guid companyId, CreateMembershipChangeRequest input, ClaimsPrincipal user,
        NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var actor) || ds is null)
            return Results.Unauthorized();

        var roleCode = input.RequestedRoleCode?.Trim().ToUpperInvariant();
        var reason = input.Reason?.Trim();
        if (input.TargetUserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(roleCode) || roleCode.Length > 60 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = ["شناسه کاربر، نقش درخواستی و علت معتبر الزامی است."]
            });

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, actor, "MEMBERSHIPS_REQUEST", ct))
            return Results.Forbid();

        await using (var role = new NpgsqlCommand("""
            SELECT is_active
              FROM company_roles
             WHERE role_code=@role
            """, c, tx))
        {
            role.Parameters.AddWithValue("role", roleCode);
            var active = await role.ExecuteScalarAsync(ct);
            if (active is not bool isActive || !isActive || roleCode == "FINANCE_ADMIN")
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requestedRoleCode"] = ["نقش انتخاب‌شده معتبر یا قابل درخواست نیست."]
                });
        }

        var requestId = Guid.NewGuid();
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO membership_change_requests
                    (id,tenant_id,company_id,target_user_id,requested_role_code,requested_by,reason)
                VALUES (@id,@tenant,@company,@target,@role,@actor,@reason)
                """, c, tx);
            insert.Parameters.AddWithValue("id", requestId);
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("company", companyId);
            insert.Parameters.AddWithValue("target", input.TargetUserId);
            insert.Parameters.AddWithValue("role", roleCode);
            insert.Parameters.AddWithValue("actor", actor);
            insert.Parameters.AddWithValue("reason", reason);
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                                            ex.ConstraintName == "uq_membership_one_pending_change")
        {
            return Results.Conflict(new { error = "برای این کاربر در این شرکت یک درخواست در انتظار بررسی وجود دارد." });
        }

        await WriteAudit(c, tx, tenant, companyId, actor, "MEMBERSHIP_CHANGE_REQUESTED",
            requestId, new { targetUserId = input.TargetUserId, requestedRoleCode = roleCode, reason }, ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/companies/{companyId}/membership-changes/{requestId}",
            new { id = requestId, status = "PENDING", targetUserId = input.TargetUserId, requestedRoleCode = roleCode });
    }

    private static Task<IResult> Approve(
        Guid companyId, Guid requestId, ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
        Review(companyId, requestId, true, null, user, ds, ct);

    private static Task<IResult> Reject(
        Guid companyId, Guid requestId, RejectMembershipChangeRequest input,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct) =>
        Review(companyId, requestId, false, input.Reason, user, ds, ct);

    private static async Task<IResult> Review(
        Guid companyId, Guid requestId, bool approve, string? reviewReason,
        ClaimsPrincipal user, NpgsqlDataSource? ds, CancellationToken ct)
    {
        if (!TryIdentity(user, out var tenant, out var reviewer) || ds is null)
            return Results.Unauthorized();

        reviewReason = reviewReason?.Trim();
        if (!approve && (string.IsNullOrWhiteSpace(reviewReason) || reviewReason.Length > 1000))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["علت رد درخواست الزامی و حداکثر ۱۰۰۰ نویسه است."]
            });

        await using var c = await ds.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await SetTenant(c, tx, tenant, ct);
        if (!await AuthorizationQueries.HasPermission(c, tx, tenant, companyId, reviewer, "MEMBERSHIPS_REVIEW", ct))
            return Results.Forbid();

        Guid targetUserId;
        Guid requestedBy;
        string requestedRoleCode;
        await using (var select = new NpgsqlCommand("""
            SELECT target_user_id, requested_by, requested_role_code
              FROM membership_change_requests
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
               AND request_status='PENDING'
             FOR UPDATE
            """, c, tx))
        {
            select.Parameters.AddWithValue("tenant", tenant);
            select.Parameters.AddWithValue("company", companyId);
            select.Parameters.AddWithValue("id", requestId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.NotFound();
            targetUserId = reader.GetGuid(0);
            requestedBy = reader.GetGuid(1);
            requestedRoleCode = reader.GetString(2);
        }

        if (reviewer == requestedBy || reviewer == targetUserId)
            return Results.Forbid();

        string? previousRole = null;
        if (approve)
        {
            await using (var role = new NpgsqlCommand("""
                SELECT is_active FROM company_roles WHERE role_code=@role
                """, c, tx))
            {
                role.Parameters.AddWithValue("role", requestedRoleCode);
                if (await role.ExecuteScalarAsync(ct) is not bool active || !active)
                    return Results.Conflict(new { error = "نقش درخواستی غیرفعال شده است." });
            }

            await using (var current = new NpgsqlCommand("""
                SELECT role_code FROM company_memberships
                 WHERE tenant_id=@tenant AND company_id=@company AND user_id=@target
                 FOR UPDATE
                """, c, tx))
            {
                current.Parameters.AddWithValue("tenant", tenant);
                current.Parameters.AddWithValue("company", companyId);
                current.Parameters.AddWithValue("target", targetUserId);
                previousRole = (string?)await current.ExecuteScalarAsync(ct);
            }

            await using var upsert = new NpgsqlCommand("""
                INSERT INTO company_memberships(tenant_id,company_id,user_id,role_code,is_active)
                VALUES(@tenant,@company,@target,@role,true)
                ON CONFLICT (tenant_id,company_id,user_id)
                DO UPDATE SET role_code=EXCLUDED.role_code, is_active=true
                """, c, tx);
            upsert.Parameters.AddWithValue("tenant", tenant);
            upsert.Parameters.AddWithValue("company", companyId);
            upsert.Parameters.AddWithValue("target", targetUserId);
            upsert.Parameters.AddWithValue("role", requestedRoleCode);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        var status = approve ? "APPROVED" : "REJECTED";
        await using (var update = new NpgsqlCommand("""
            UPDATE membership_change_requests
               SET request_status=@status, reviewed_by=@reviewer, reviewed_at=now()
             WHERE tenant_id=@tenant AND company_id=@company AND id=@id
               AND request_status='PENDING'
            """, c, tx))
        {
            update.Parameters.AddWithValue("status", status);
            update.Parameters.AddWithValue("reviewer", reviewer);
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("id", requestId);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                return Results.Conflict(new { error = "وضعیت درخواست هم‌زمان تغییر کرده است." });
        }

        await WriteAudit(c, tx, tenant, companyId, reviewer,
            approve ? "MEMBERSHIP_CHANGE_APPROVED" : "MEMBERSHIP_CHANGE_REJECTED",
            requestId, new
            {
                targetUserId, requestedBy, requestedRoleCode, previousRole,
                reviewReason
            }, ct);

        await tx.CommitAsync(ct);
        return Results.Ok(new { id = requestId, status, targetUserId, previousRole, roleCode = approve ? requestedRoleCode : previousRole });
    }

    private static async Task WriteAudit(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, Guid company,
        Guid actor, string eventType, Guid entityId, object payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO audit_events(tenant_id,company_id,actor_id,event_type,entity_type,entity_id,payload)
            VALUES(@tenant,@company,@actor,@event,'MEMBERSHIP_CHANGE_REQUEST',@id,@payload)
            """, c, tx);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("company", company);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("event", eventType);
        cmd.Parameters.AddWithValue("id", entityId);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
        {
            Value = System.Text.Json.JsonSerializer.Serialize(payload)
        });
        await cmd.ExecuteNonQueryAsync(ct);
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

public sealed record CreateMembershipChangeRequest(Guid TargetUserId, string? RequestedRoleCode, string? Reason);
public sealed record RejectMembershipChangeRequest(string? Reason);
