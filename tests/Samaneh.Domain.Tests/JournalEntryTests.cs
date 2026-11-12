using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class JournalEntryTests
{
    [Fact]
    public void Constructor_AcceptsBalancedEntry()
    {
        var lines = new[]
        {
            new JournalLine("1001", new Money(1250.25m, "IRR"), Money.Zero("IRR")),
            new JournalLine("2001", Money.Zero("IRR"), new Money(1250.25m, "IRR"))
        };

        var entry = new JournalEntry("JV-0001", new DateOnly(2026, 11, 12), lines);

        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal("IRR", entry.Currency);
    }

    [Fact]
    public void Constructor_RejectsUnbalancedEntry()
    {
        var lines = new[]
        {
            new JournalLine("1001", new Money(100m, "IRR"), Money.Zero("IRR")),
            new JournalLine("2001", Money.Zero("IRR"), new Money(99m, "IRR"))
        };

        Assert.Throws<InvalidOperationException>(() =>
            new JournalEntry("JV-0002", new DateOnly(2026, 11, 12), lines));
    }

    [Fact]
    public void Constructor_RejectsMixedCurrencies()
    {
        var lines = new[]
        {
            new JournalLine("1001", new Money(100m, "IRR"), Money.Zero("IRR")),
            new JournalLine("2001", Money.Zero("USD"), new Money(100m, "USD"))
        };

        Assert.Throws<InvalidOperationException>(() =>
            new JournalEntry("JV-0003", new DateOnly(2026, 11, 12), lines));
    }
}
