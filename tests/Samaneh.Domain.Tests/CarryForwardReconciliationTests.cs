using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class CarryForwardReconciliationTests
{
    private static readonly DateOnly SourceEnd = new(2025, 12, 31);
    private static readonly DateOnly TargetStart = new(2026, 1, 1);

    private static CarryForwardAccountBalance[] BalancedAccounts() =>
    [
        new(Guid.NewGuid(), "1001", "Cash", 120m, 0m, 120m, 0m),
        new(Guid.NewGuid(), "3001", "Capital", 0m, 120m, 0m, 120m)
    ];

    [Fact]
    public void Evaluate_AcceptsExactPostedCarryForwardFromLockedContiguousPeriod()
    {
        var result = CarryForwardReconciliation.Evaluate(
            "POSTED", "LOCKED", SourceEnd, TargetStart, BalancedAccounts());

        Assert.True(result.Matched);
        Assert.True(result.IsBalanced);
        Assert.True(result.Reconciled);
        Assert.Equal(120m, result.ExpectedDebitTotal);
        Assert.Equal(120m, result.OpeningCreditTotal);
    }

    [Fact]
    public void Evaluate_RejectsAccountLevelDifferenceEvenWhenTotalsBalance()
    {
        var accounts = new[]
        {
            new CarryForwardAccountBalance(Guid.NewGuid(), "1001", "Cash", 120m, 0m, 100m, 0m),
            new CarryForwardAccountBalance(Guid.NewGuid(), "1002", "Bank", 0m, 0m, 20m, 0m),
            new CarryForwardAccountBalance(Guid.NewGuid(), "3001", "Capital", 0m, 120m, 0m, 120m)
        };

        var result = CarryForwardReconciliation.Evaluate(
            "POSTED", "LOCKED", SourceEnd, TargetStart, accounts);

        Assert.False(result.Matched);
        Assert.True(result.IsBalanced);
        Assert.False(result.Reconciled);
    }

    [Theory]
    [InlineData("DRAFT", "LOCKED", 2026, 1, 1)]
    [InlineData("POSTED", "CLOSED", 2026, 1, 1)]
    [InlineData("POSTED", "LOCKED", 2026, 1, 2)]
    public void Evaluate_RejectsInvalidLifecycleOrPeriodContinuity(
        string journalStatus, string sourceStatus, int year, int month, int day)
    {
        var result = CarryForwardReconciliation.Evaluate(
            journalStatus, sourceStatus, SourceEnd,
            new DateOnly(year, month, day), BalancedAccounts());

        Assert.True(result.Matched);
        Assert.True(result.IsBalanced);
        Assert.False(result.Reconciled);
    }

    [Fact]
    public void Evaluate_RejectsUnbalancedOpeningTotals()
    {
        var accounts = new[]
        {
            new CarryForwardAccountBalance(Guid.NewGuid(), "1001", "Cash", 120m, 0m, 120m, 0m),
            new CarryForwardAccountBalance(Guid.NewGuid(), "3001", "Capital", 0m, 120m, 0m, 119m)
        };

        var result = CarryForwardReconciliation.Evaluate(
            "POSTED", "LOCKED", SourceEnd, TargetStart, accounts);

        Assert.True(result.Matched is false);
        Assert.False(result.IsBalanced);
        Assert.False(result.Reconciled);
    }

    [Fact]
    public void Evaluate_RejectsEmptyAccountSet()
    {
        var result = CarryForwardReconciliation.Evaluate(
            "POSTED", "LOCKED", SourceEnd, TargetStart, []);

        Assert.False(result.Matched);
        Assert.True(result.IsBalanced);
        Assert.False(result.Reconciled);
    }
}
