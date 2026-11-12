namespace Samaneh.Domain;

/// <summary>
/// One account's expected permanent-account balance and the amount carried into
/// the next period. Values are expressed as debit/credit in one functional currency.
/// </summary>
public sealed record CarryForwardAccountBalance(
    Guid AccountId,
    string AccountCode,
    string AccountName,
    decimal ExpectedDebit,
    decimal ExpectedCredit,
    decimal OpeningDebit,
    decimal OpeningCredit)
{
    public bool Matched => ExpectedDebit == OpeningDebit && ExpectedCredit == OpeningCredit;
    public decimal DifferenceDebit => OpeningDebit - ExpectedDebit;
    public decimal DifferenceCredit => OpeningCredit - ExpectedCredit;
}

public sealed record CarryForwardReconciliationResult(
    bool Matched,
    bool IsBalanced,
    bool Reconciled,
    decimal ExpectedDebitTotal,
    decimal ExpectedCreditTotal,
    decimal OpeningDebitTotal,
    decimal OpeningCreditTotal);

/// <summary>
/// Deterministic reconciliation rules. This type performs no I/O and never
/// estimates, rounds, or converts monetary values.
/// </summary>
public static class CarryForwardReconciliation
{
    public static CarryForwardReconciliationResult Evaluate(
        string journalStatus,
        string sourcePeriodStatus,
        DateOnly sourcePeriodEnd,
        DateOnly targetPeriodStart,
        IReadOnlyCollection<CarryForwardAccountBalance> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var seenAccounts = new HashSet<Guid>();
        foreach (var account in accounts)
        {
            if (account is null || account.AccountId == Guid.Empty)
                throw new ArgumentException("Every reconciliation row must identify an account.", nameof(accounts));
            if (!seenAccounts.Add(account.AccountId))
                throw new ArgumentException("An account may appear only once in a reconciliation.", nameof(accounts));
            if (account.ExpectedDebit < 0m || account.ExpectedCredit < 0m ||
                account.OpeningDebit < 0m || account.OpeningCredit < 0m)
                throw new ArgumentException("Reconciliation amounts cannot be negative.", nameof(accounts));
            if ((account.ExpectedDebit > 0m && account.ExpectedCredit > 0m) ||
                (account.OpeningDebit > 0m && account.OpeningCredit > 0m))
                throw new ArgumentException("An account balance must be represented on one side only.", nameof(accounts));
        }

        var expectedDebit = accounts.Sum(x => x.ExpectedDebit);
        var expectedCredit = accounts.Sum(x => x.ExpectedCredit);
        var openingDebit = accounts.Sum(x => x.OpeningDebit);
        var openingCredit = accounts.Sum(x => x.OpeningCredit);
        var matched = accounts.Count > 0 && accounts.All(x => x.Matched);
        var balanced = expectedDebit == expectedCredit && openingDebit == openingCredit;
        var reconciled = string.Equals(journalStatus, "POSTED", StringComparison.Ordinal) &&
                         string.Equals(sourcePeriodStatus, "LOCKED", StringComparison.Ordinal) &&
                         targetPeriodStart == sourcePeriodEnd.AddDays(1) &&
                         matched && balanced;

        return new CarryForwardReconciliationResult(
            matched, balanced, reconciled,
            expectedDebit, expectedCredit, openingDebit, openingCredit);
    }
}
