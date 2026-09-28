namespace NpcManager.Domain;

public readonly record struct PluginName
{
    public PluginName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            value.Any(char.IsControl) || value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar) || value.Contains(':'))
        {
            throw new ArgumentException("Plugin name must be a single safe filename.", nameof(value));
        }

        if (!value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
            !value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
            !value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Plugin name must end in .esp, .esm, or .esl.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
