namespace NpcManager.Domain;

/// <summary>
/// A host-owned application resource path. Unlike <see cref="WorkspacePath"/>,
/// this path is never supplied by a mod request and may live in a single-file
/// bundle extraction directory outside the admitted mod workspace.
/// </summary>
public readonly record struct ApplicationResourcePath
{
    public ApplicationResourcePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new ArgumentException(
                "Application resource paths must be non-empty and contain no NUL characters.",
                nameof(value));
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException(
                "Application resource paths must be fully qualified.",
                nameof(value));
        Value = Path.GetFullPath(value);
    }

    public string Value { get; }

    public bool IsUnder(ApplicationResourcePath ancestor)
    {
        string relative = Path.GetRelativePath(ancestor.Value, Value);
        return relative is "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 ".." + Path.DirectorySeparatorChar,
                 StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    public override string ToString() => Value;
}
