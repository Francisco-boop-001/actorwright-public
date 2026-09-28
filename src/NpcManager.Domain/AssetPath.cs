namespace NpcManager.Domain;

public readonly record struct AssetPath
{
    public AssetPath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            normalized.Contains(':') || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"Asset paths must be normalized, relative, and traversal-free. Value '{value}' must use the document's relative forward-slash asset-path convention.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
