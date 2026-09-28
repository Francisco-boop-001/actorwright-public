using System.Globalization;

namespace NpcManager.Domain;

public readonly record struct FormId(uint Value)
{
    public static bool TryParse(string value, out FormId formId)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (uint.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
        {
            formId = new FormId(parsed);
            return true;
        }

        formId = default;
        return false;
    }

    public override string ToString() => $"0x{Value:X8}";
}
