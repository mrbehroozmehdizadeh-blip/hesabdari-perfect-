using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class MoneyTests
{
    [Fact]
    public void Add_UsesExactDecimalArithmetic()
    {
        var result = new Money(0.1m, "IRR").Add(new Money(0.2m, "IRR"));
        Assert.Equal(0.3m, result.Amount);
    }

    [Fact]
    public void Add_RejectsDifferentCurrencies()
    {
        var amount = new Money(100m, "IRR");
        Assert.Throws<InvalidOperationException>(() => amount.Add(new Money(1m, "USD")));
    }

    [Theory]
    [InlineData("irr")]
    [InlineData("IR")]
    [InlineData("")]
    public void Constructor_RejectsInvalidCurrency(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, currency));
    }
}
