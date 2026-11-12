namespace Samaneh.Domain;

/// <summary>
/// Exact monetary amount. Currency is explicit; no implicit FX conversion is permitted.
/// Never construct this type from binary floating-point values.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 ||
            currency.Any(c => !char.IsAsciiLetterUpper(c)))
        {
            throw new ArgumentException("Currency must be a three-letter uppercase ISO-style code.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (!StringComparer.Ordinal.Equals(Currency, other.Currency))
            throw new InvalidOperationException("Amounts in different currencies cannot be combined without an explicit conversion.");
    }
}
