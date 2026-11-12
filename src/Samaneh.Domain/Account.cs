namespace Samaneh.Domain;

/// <summary>Defines how a posting account receives its analytical detail.</summary>
public enum AccountDetailPolicy
{
    None = 0,
    FixedDetail = 1,
    FloatingDimension = 2
}

/// <summary>
/// A node in the chart of accounts. The hierarchy is limited to ten levels.
/// Floating dimensions are separate master-data entities and are not child accounts.
/// </summary>
public sealed record Account
{
    public const int MaximumLevel = 10;

    public string Code { get; }
    public string Name { get; }
    public int Level { get; }
    public string? ParentCode { get; }
    public bool IsGroup { get; }
    public AccountDetailPolicy DetailPolicy { get; }

    public Account(
        string code,
        string name,
        int level,
        string? parentCode,
        bool isGroup,
        AccountDetailPolicy detailPolicy = AccountDetailPolicy.None)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Account code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Account name is required.", nameof(name));
        if (level is < 1 or > MaximumLevel)
            throw new ArgumentOutOfRangeException(nameof(level), $"Account level must be between 1 and {MaximumLevel}.");
        if (level == 1 && !string.IsNullOrWhiteSpace(parentCode))
            throw new ArgumentException("A level-one account cannot have a parent.", nameof(parentCode));
        if (level > 1 && string.IsNullOrWhiteSpace(parentCode))
            throw new ArgumentException("An account below level one must have a parent.", nameof(parentCode));
        if (parentCode is not null && StringComparer.Ordinal.Equals(code.Trim(), parentCode.Trim()))
            throw new ArgumentException("An account cannot be its own parent.", nameof(parentCode));
        if (!Enum.IsDefined(detailPolicy))
            throw new ArgumentOutOfRangeException(nameof(detailPolicy));
        if (isGroup && detailPolicy != AccountDetailPolicy.None)
            throw new ArgumentException("Group accounts cannot carry posting detail policies.", nameof(detailPolicy));

        Code = code.Trim();
        Name = name.Trim();
        Level = level;
        ParentCode = string.IsNullOrWhiteSpace(parentCode) ? null : parentCode.Trim();
        IsGroup = isGroup;
        DetailPolicy = detailPolicy;
    }

    /// <summary>Validates the parent relationship after both nodes have been loaded.</summary>
    public void ValidateParent(Account? parent)
    {
        if (Level == 1)
        {
            if (parent is not null)
                throw new InvalidOperationException("A level-one account cannot have a parent.");
            return;
        }

        if (parent is null)
            throw new InvalidOperationException("The parent account does not exist.");
        if (!StringComparer.Ordinal.Equals(ParentCode, parent.Code))
            throw new InvalidOperationException("The supplied parent does not match ParentCode.");
        if (parent.Level != Level - 1)
            throw new InvalidOperationException("The parent level must be exactly one level above the child.");
        if (!parent.IsGroup)
            throw new InvalidOperationException("A posting account cannot have child accounts.");
    }
}
