using Npgsql;

public static class AuthorizationQueries
{
    public static async Task<bool> HasPermission(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        Guid companyId,
        Guid userId,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                  FROM company_memberships m
                  JOIN company_roles r
                    ON r.role_code = m.role_code
                   AND r.is_active = true
                  JOIN role_permissions rp
                    ON rp.role_code = r.role_code
                 WHERE m.tenant_id = @tenant
                   AND m.company_id = @company
                   AND m.user_id = @user
                   AND m.is_active = true
                   AND rp.permission_code = @permission
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("permission", permissionCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }
}
