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
                  FROM company_memberships membership
                  JOIN company_roles role
                    ON role.role_code = membership.role_code
                   AND role.is_active = true
                  JOIN role_permissions grant
                    ON grant.role_code = role.role_code
                 WHERE membership.tenant_id = @tenant
                   AND membership.company_id = @company
                   AND membership.user_id = @user
                   AND membership.is_active = true
                   AND grant.permission_code = @permission
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("permission", permissionCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }
}
