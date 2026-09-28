namespace NpcManager.Domain;

/// <summary>Explicit plugin and FormID identity for a cross-record reference.</summary>
public readonly record struct FormReference(PluginName Plugin, FormId FormId)
{
    public override string ToString() => $"{Plugin.Value}|{FormId}";

    public static bool TryParse(string value, out FormReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator != value.LastIndexOf('|') || separator == value.Length - 1) return false;
        try
        {
            if (!FormId.TryParse(value[(separator + 1)..], out var formId)) return false;
            reference = new FormReference(new PluginName(value[..separator]), formId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
