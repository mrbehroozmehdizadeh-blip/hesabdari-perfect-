using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class AccountDeletionGuardTests
{
    [Fact]
    public void EnsureDeletable_AllowsUnusedLeafAccount()
    {
        AccountDeletionGuard.EnsureDeletable(
            hasChildren: false,
            hasNonZeroBalance: false,
            hasTransactions: false);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void EnsureDeletable_RejectsAccountWithDependencies(
        bool hasChildren, bool hasNonZeroBalance, bool hasTransactions)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AccountDeletionGuard.EnsureDeletable(hasChildren, hasNonZeroBalance, hasTransactions));
    }
}
