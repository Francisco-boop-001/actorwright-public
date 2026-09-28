namespace NpcManager.Domain;

public enum GameEdition
{
    Fallout4,
    SkyrimSpecialEdition
}

public static class GameEditionExtensions
{
    public static string ToWireName(this GameEdition edition) => edition switch
    {
        GameEdition.Fallout4 => "fallout4",
        GameEdition.SkyrimSpecialEdition => "skyrimse",
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
    };

    public static bool TryParseWireName(string value, out GameEdition edition)
    {
        if (string.Equals(value, "fallout4", StringComparison.OrdinalIgnoreCase))
        {
            edition = GameEdition.Fallout4;
            return true;
        }

        if (string.Equals(value, "skyrimse", StringComparison.OrdinalIgnoreCase))
        {
            edition = GameEdition.SkyrimSpecialEdition;
            return true;
        }

        edition = default;
        return false;
    }
}
