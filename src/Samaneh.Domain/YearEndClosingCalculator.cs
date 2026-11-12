namespace Samaneh.Domain;

public sealed record TemporaryAccountBalance(
    Guid AccountId, string AccountCode, string AccountName, decimal DebitNet);

public sealed record YearEndClosingLine(
    Guid AccountId, decimal Debit, decimal Credit, string Description);

public sealed record YearEndClosingResult(
    IReadOnlyList<YearEndClosingLine> Lines, decimal NetDebit)
{
    public decimal DebitTotal => Lines.Sum(x => x.Debit);
    public decimal CreditTotal => Lines.Sum(x => x.Credit);
    public bool IsBalanced => DebitTotal == CreditTotal;
    public bool HasTemporaryBalances => Lines.Count > 0;
}

/// <summary>
/// Deterministic closing calculation for revenue and expense accounts.
/// Positive debit-net balances are expenses; negative debit-net balances are revenue.
/// </summary>
public static class YearEndClosingCalculator
{
    public static YearEndClosingResult Calculate(
        IReadOnlyCollection<TemporaryAccountBalance> balances,
        Guid retainedEarningsAccountId)
    {
        ArgumentNullException.ThrowIfNull(balances);
        if (retainedEarningsAccountId == Guid.Empty)
            throw new ArgumentException("Retained earnings account is required.", nameof(retainedEarningsAccountId));

        var lines = new List<YearEndClosingLine>();
        decimal netDebit = 0m;

        foreach (var balance in balances.OrderBy(x => x.AccountCode, StringComparer.Ordinal))
        {
            if (balance.AccountId == Guid.Empty)
                throw new ArgumentException("Temporary account ID is required.", nameof(balances));
            if (balance.DebitNet == 0m)
                continue;

            netDebit += balance.DebitNet;
            lines.Add(balance.DebitNet > 0m
                ? new YearEndClosingLine(balance.AccountId, 0m, balance.DebitNet,
                    $"بستن حساب {balance.AccountCode} - {balance.AccountName}")
                : new YearEndClosingLine(balance.AccountId, -balance.DebitNet, 0m,
                    $"بستن حساب {balance.AccountCode} - {balance.AccountName}"));
        }

        if (lines.Count > 0 && netDebit > 0m)
            lines.Add(new YearEndClosingLine(retainedEarningsAccountId, netDebit, 0m,
                "انتقال زیان خالص دوره به سود و زیان انباشته"));
        else if (lines.Count > 0 && netDebit < 0m)
            lines.Add(new YearEndClosingLine(retainedEarningsAccountId, 0m, -netDebit,
                "انتقال سود خالص دوره به سود و زیان انباشته"));

        var result = new YearEndClosingResult(lines, netDebit);
        if (!result.IsBalanced)
            throw new InvalidOperationException("Year-end closing calculation did not produce a balanced journal.");

        return result;
    }
}
