namespace NpcManager.Domain;

public readonly record struct RecordSignature
{
    public RecordSignature(string value)
    {
        if (value.Length != 4 || value.Any(character => character != '_' && (character < 'A' || character > 'Z')))
        {
            throw new ArgumentException("Record signature must contain exactly four uppercase ASCII letters or underscores.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
