namespace Samaneh.Domain;

/// <summary>
/// Independent analytical master data (for example customer, vendor, project or cost center).
/// It is deliberately not part of the chart-of-accounts hierarchy.
/// </summary>
public sealed record FloatingDimension
{
    public string Type { get; }
    public string Code { get; }
    public string Name { get; }
    public bool IsActive { get; }

    public FloatingDimension(string type, string code, string name, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Dimension type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Dimension code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Dimension name is required.", nameof(name));

        Type = type.Trim();
        Code = code.Trim();
        Name = name.Trim();
        IsActive = isActive;
    }
}
