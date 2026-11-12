using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class FloatingDimensionTests
{
    [Fact]
    public void Constructor_TrimsValuesAndDefaultsToActive()
    {
        var dimension = new FloatingDimension(" Customer ", " C-100 ", " Example customer ");

        Assert.Equal("Customer", dimension.Type);
        Assert.Equal("C-100", dimension.Code);
        Assert.Equal("Example customer", dimension.Name);
        Assert.True(dimension.IsActive);
    }

    [Fact]
    public void Constructor_AllowsInactiveDimension()
    {
        var dimension = new FloatingDimension("PROJECT", "P-01", "Closed project", false);

        Assert.False(dimension.IsActive);
    }

    [Theory]
    [InlineData("", "C-1", "Customer")]
    [InlineData("CUSTOMER", "", "Customer")]
    [InlineData("CUSTOMER", "C-1", " ")]
    public void Constructor_RejectsMissingRequiredValues(string type, string code, string name)
    {
        Assert.Throws<ArgumentException>(() => new FloatingDimension(type, code, name));
    }
}
