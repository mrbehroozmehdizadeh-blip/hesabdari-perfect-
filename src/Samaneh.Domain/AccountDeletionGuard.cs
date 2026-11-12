namespace Samaneh.Domain;

/// <summary>
/// Central deletion rule for chart-of-account nodes.
/// The caller must evaluate these facts within a consistent database transaction.
/// </summary>
public static class AccountDeletionGuard
{
    public static void EnsureDeletable(
        bool hasChildren,
        bool hasNonZeroBalance,
        bool hasTransactions)
    {
        if (hasChildren)
            throw new InvalidOperationException("An account with child accounts cannot be deleted.");
        if (hasNonZeroBalance)
            throw new InvalidOperationException("An account with a non-zero balance cannot be deleted.");
        if (hasTransactions)
            throw new InvalidOperationException("An account referenced by transactions cannot be deleted.");
    }
}
