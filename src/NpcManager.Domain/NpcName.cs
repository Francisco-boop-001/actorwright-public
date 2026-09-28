namespace NpcManager.Domain;

public readonly record struct NpcName
{
    public NpcName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(char.IsControl))
        {
            throw new ArgumentException("NPC display name must be non-empty, at most 255 characters, and contain no control characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
