using System.Text.RegularExpressions;

namespace NpcManager.Domain;

public readonly record struct EditorId
{
    private static readonly Regex Pattern = new("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public EditorId(string value)
    {
        if (!Pattern.IsMatch(value))
        {
            throw new ArgumentException("EditorID must start with a letter and contain only ASCII letters, digits, or underscores.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
