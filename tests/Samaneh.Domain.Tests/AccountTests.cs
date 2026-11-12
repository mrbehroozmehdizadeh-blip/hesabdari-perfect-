using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class AccountTests
{
    [Fact]
    public void Constructor_AcceptsLevelTenAndRejectsLevelEleven()
    {
        var account = new Account("100", "Level ten", 10, "90", false);
        Assert.Equal(10, account.Level);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Account("110", "Level eleven", 11, "100", false));
    }

    [Fact]
    public void Constructor_RequiresParentBelowRoot()
    {
        Assert.Throws<ArgumentException>(() =>
            new Account("110", "Missing parent", 2, null, false));
    }

    [Fact]
    public void ValidateParent_AcceptsImmediateGroupParent()
    {
        var parent = new Account("10", "Assets", 1, null, true);
        var child = new Account("101", "Cash", 2, "10", false);

        child.ValidateParent(parent);
    }

    [Fact]
    public void ValidateParent_RejectsSkippedLevel()
    {
        var parent = new Account("1", "Assets", 1, null, true);
        var child = new Account("10101", "Cash detail", 3, "1", false);

        Assert.Throws<InvalidOperationException>(() => child.ValidateParent(parent));
    }

    [Fact]
    public void ValidateParent_RejectsPostingParent()
    {
        var parent = new Account("10", "Cash", 1, null, false);
        var child = new Account("101", "Bank", 2, "10", false);

        Assert.Throws<InvalidOperationException>(() => child.ValidateParent(parent));
    }

    [Fact]
    public void Constructor_RejectsDetailPolicyOnGroup()
    {
        Assert.Throws<ArgumentException>(() =>
            new Account("10", "Receivables", 1, null, true, AccountDetailPolicy.FixedDetail));
    }

    [Fact]
    public void FloatingDimension_IsIndependentMasterData()
    {
        var customer = new FloatingDimension("Customer", "C-100", "Sample customer");

        Assert.Equal("Customer", customer.Type);
        Assert.Equal("C-100", customer.Code);
        Assert.True(customer.IsActive);
    }
}
