using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class YearEndClosingCalculatorTests
{
    private static readonly Guid RetainedEarnings = Guid.NewGuid();

    [Fact]
    public void Calculate_TransfersNetProfitToCreditSideAndBalancesJournal()
    {
        var expense = new TemporaryAccountBalance(Guid.NewGuid(), "5001", "Expenses", 700m);
        var revenue = new TemporaryAccountBalance(Guid.NewGuid(), "4001", "Revenue", -1000m);

        var result = YearEndClosingCalculator.Calculate([expense, revenue], RetainedEarnings);

        Assert.Equal(-300m, result.NetDebit);
        Assert.Equal(1000m, result.DebitTotal);
        Assert.Equal(1000m, result.CreditTotal);
        Assert.True(result.IsBalanced);
        Assert.Contains(result.Lines, x => x.AccountId == RetainedEarnings && x.Credit == 300m && x.Debit == 0m);
    }

    [Fact]
    public void Calculate_TransfersNetLossToDebitSideAndBalancesJournal()
    {
        var expense = new TemporaryAccountBalance(Guid.NewGuid(), "5001", "Expenses", 1200m);
        var revenue = new TemporaryAccountBalance(Guid.NewGuid(), "4001", "Revenue", -900m);

        var result = YearEndClosingCalculator.Calculate([expense, revenue], RetainedEarnings);

        Assert.Equal(300m, result.NetDebit);
        Assert.Equal(1200m, result.DebitTotal);
        Assert.Equal(1200m, result.CreditTotal);
        Assert.Contains(result.Lines, x => x.AccountId == RetainedEarnings && x.Debit == 300m && x.Credit == 0m);
    }

    [Fact]
    public void Calculate_ClosesTemporaryAccountsWhenNetResultIsZero()
    {
        var expense = new TemporaryAccountBalance(Guid.NewGuid(), "5001", "Expenses", 900m);
        var revenue = new TemporaryAccountBalance(Guid.NewGuid(), "4001", "Revenue", -900m);

        var result = YearEndClosingCalculator.Calculate([expense, revenue], RetainedEarnings);

        Assert.Equal(0m, result.NetDebit);
        Assert.Equal(900m, result.DebitTotal);
        Assert.Equal(900m, result.CreditTotal);
        Assert.DoesNotContain(result.Lines, x => x.AccountId == RetainedEarnings);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public void Calculate_EmptyBalancesProducesBalancedEmptyResult()
    {
        var result = YearEndClosingCalculator.Calculate([], RetainedEarnings);

        Assert.Empty(result.Lines);
        Assert.True(result.IsBalanced);
        Assert.False(result.HasTemporaryBalances);
    }

    [Fact]
    public void Calculate_RejectsMissingRetainedEarningsAccount()
    {
        Assert.Throws<ArgumentException>(() =>
            YearEndClosingCalculator.Calculate([], Guid.Empty));
    }

    [Fact]
    public void Calculate_RejectsDuplicateTemporaryAccount()
    {
        var accountId = Guid.NewGuid();
        var duplicate = new[]
        {
            new TemporaryAccountBalance(accountId, "5001", "Expenses", 100m),
            new TemporaryAccountBalance(accountId, "5001", "Expenses", 100m)
        };

        Assert.Throws<ArgumentException>(() =>
            YearEndClosingCalculator.Calculate(duplicate, RetainedEarnings));
    }

    [Fact]
    public void Calculate_RejectsRetainedEarningsAsTemporaryAccount()
    {
        var temporary = new TemporaryAccountBalance(
            RetainedEarnings, "3001", "Retained earnings", 100m);

        Assert.Throws<ArgumentException>(() =>
            YearEndClosingCalculator.Calculate([temporary], RetainedEarnings));
    }

    [Fact]
    public void Calculate_RejectsTemporaryAccountWithoutCode()
    {
        var temporary = new TemporaryAccountBalance(Guid.NewGuid(), " ", "Expenses", 100m);

        Assert.Throws<ArgumentException>(() =>
            YearEndClosingCalculator.Calculate([temporary], RetainedEarnings));
    }
}
