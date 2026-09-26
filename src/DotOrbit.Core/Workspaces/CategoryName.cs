namespace DotOrbit.Core.Workspaces;

public enum CategoryNameValidationError
{
    None,
    Required,
}

public sealed class CategoryNameValidationResult
{
    private CategoryNameValidationResult(CategoryName? categoryName, CategoryNameValidationError error)
    {
        CategoryName = categoryName;
        Error = error;
    }

    public bool IsValid => Error == CategoryNameValidationError.None;

    public CategoryName? CategoryName { get; }

    public CategoryNameValidationError Error { get; }

    internal static CategoryNameValidationResult Success(CategoryName categoryName) =>
        new(categoryName, CategoryNameValidationError.None);

    internal static CategoryNameValidationResult Failure(CategoryNameValidationError error) =>
        new(null, error);
}

public sealed class CategoryName
{
    private CategoryName(string value) => Value = value;

    public string Value { get; }

    public static CategoryNameValidationResult Create(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? CategoryNameValidationResult.Failure(CategoryNameValidationError.Required)
            : CategoryNameValidationResult.Success(new CategoryName(trimmed));
    }

    public override string ToString() => Value;
}
