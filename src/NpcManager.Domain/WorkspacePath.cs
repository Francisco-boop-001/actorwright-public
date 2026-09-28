namespace NpcManager.Domain;

public readonly record struct WorkspacePath
{
    public WorkspacePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new ArgumentException($"Workspace path must be non-empty and contain no NUL characters. CLI path value '{value}' must be an absolute filesystem path.", nameof(value));
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"Workspace paths must be fully qualified; implicit current-directory roots are not accepted. CLI path value '{value}' must be absolute; JSON document paths use their declared relative-path convention.", nameof(value));
        }

        Value = Path.GetFullPath(value);
    }

    public string Value { get; }

    public bool IsUnder(WorkspacePath ancestor)
    {
        var relative = Path.GetRelativePath(ancestor.Value, Value);
        return relative is "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 ".." + Path.DirectorySeparatorChar,
                 StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    public override string ToString() => Value;
}
