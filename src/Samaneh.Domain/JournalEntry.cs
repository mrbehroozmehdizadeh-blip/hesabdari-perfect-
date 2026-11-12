namespace Samaneh.Domain;

public sealed record JournalLine(string AccountCode, Money Debit, Money Credit)
{
    public JournalLine(string accountCode, Money debit, Money credit) : this()
    {
        if (string.IsNullOrWhiteSpace(accountCode))
            throw new ArgumentException("Account code is required.", nameof(accountCode));
        if (debit.Amount < 0m || credit.Amount < 0m)
            throw new ArgumentOutOfRangeException(nameof(debit), "Debit and credit amounts cannot be negative.");
        if (debit.Currency != credit.Currency)
            throw new InvalidOperationException("Debit and credit currencies must match.");
        if (debit.Amount > 0m && credit.Amount > 0m)
            throw new InvalidOperationException("A journal line cannot have both debit and credit amounts.");
        AccountCode = accountCode;
        Debit = debit;
        Credit = credit;
    }
}

public sealed class JournalEntry
{
    public string EntryNumber { get; }
    public DateOnly Date { get; }
    public IReadOnlyList<JournalLine> Lines { get; }
    public string Currency { get; }

    public JournalEntry(string entryNumber, DateOnly date, IEnumerable<JournalLine> lines)
    {
        if (string.IsNullOrWhiteSpace(entryNumber))
            throw new ArgumentException("Entry number is required.", nameof(entryNumber));

        var materialized = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (materialized.Length < 2)
            throw new InvalidOperationException("A journal entry must contain at least two lines.");

        var currencies = materialized
            .SelectMany(x => new[] { x.Debit.Currency, x.Credit.Currency })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (currencies.Length != 1)
            throw new InvalidOperationException("A journal entry must use one currency per entry.");

        var debitTotal = materialized.Sum(x => x.Debit.Amount);
        var creditTotal = materialized.Sum(x => x.Credit.Amount);

        if (debitTotal != creditTotal)
            throw new InvalidOperationException("Journal entry is not balanced.");

        if (debitTotal <= 0m)
            throw new InvalidOperationException("A journal entry must have a positive total.");

        EntryNumber = entryNumber;
        Date = date;
        Lines = Array.AsReadOnly(materialized);
        Currency = currencies[0];
    }
}
