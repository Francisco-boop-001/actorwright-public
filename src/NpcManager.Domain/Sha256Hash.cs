namespace NpcManager.Domain;

public readonly record struct Sha256Hash
{
    public Sha256Hash(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 must be exactly 64 hexadecimal characters.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
