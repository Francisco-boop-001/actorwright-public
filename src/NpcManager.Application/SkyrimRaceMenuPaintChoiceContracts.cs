using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimRaceMenuPaintCategory
{
    Warpaint,
    Body,
    Hands,
    Feet,
    Face
}

public enum SkyrimRaceMenuPaintProviderKind
{
    Loose,
    Bsa
}

public enum SkyrimRaceMenuPaintSlotKind
{
    Texture,
    Empty,
    Ignore,
    Computed
}

public sealed record SkyrimRaceMenuPaintTextureSlot(
    int Index,
    SkyrimRaceMenuPaintSlotKind Kind,
    string? RegisteredValue,
    AssetPath? TexturePath);

public sealed record SkyrimRaceMenuPaintRegistrationSource(
    AssetPath ScriptPath,
    SkyrimRaceMenuPaintProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    Sha256Hash PexSha256,
    long PexLength);

public sealed record SkyrimRaceMenuPaintChoiceRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    SkyrimRaceMenuPaintCategory Category,
    string? Search);

public sealed record SkyrimRaceMenuPaintChoiceCandidate(
    SkyrimRaceMenuPaintCategory Category,
    string RegisteredName,
    string DisplayName,
    AssetPath RegisteredPath,
    AssetPath CanonicalTexturePath,
    ImmutableArray<SkyrimRaceMenuPaintTextureSlot> TextureSlots,
    ImmutableArray<SkyrimRaceMenuPaintRegistrationSource> Sources);

public sealed record SkyrimRaceMenuPaintCatalogSummary(
    int LoadedPluginCount,
    int AdmittedArchiveCount,
    int ArchiveScriptEntryCount,
    int LooseScriptEntryCount,
    int WinningScriptCount,
    int PaintScriptCount,
    int MalformedPaintScriptCount,
    int RegistrationCount,
    int CandidateCount,
    Sha256Hash CatalogSha256);

public sealed record SkyrimRaceMenuPaintChoiceResult(
    bool Accepted,
    ImmutableArray<SkyrimRaceMenuPaintChoiceCandidate> Candidates,
    SkyrimRaceMenuPaintCatalogSummary? Summary,
    ImmutableArray<Diagnostic> Diagnostics,
    bool RuntimeAuthority = false,
    bool TextureRenderAuthority = false);

public interface ISkyrimRaceMenuPaintChoiceService
{
    ValueTask<SkyrimRaceMenuPaintChoiceResult> SearchAsync(
        SkyrimRaceMenuPaintChoiceRequest request,
        CancellationToken cancellationToken);
}

public static class SkyrimRaceMenuPaintCategoryExtensions
{
    public static string ToWireName(this SkyrimRaceMenuPaintCategory category) => category switch
    {
        SkyrimRaceMenuPaintCategory.Warpaint => "warpaint",
        SkyrimRaceMenuPaintCategory.Body => "body",
        SkyrimRaceMenuPaintCategory.Hands => "hands",
        SkyrimRaceMenuPaintCategory.Feet => "feet",
        SkyrimRaceMenuPaintCategory.Face => "face",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    public static bool TryParseWireName(
        string? value,
        out SkyrimRaceMenuPaintCategory category)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "warpaint":
                category = SkyrimRaceMenuPaintCategory.Warpaint;
                return true;
            case "body":
                category = SkyrimRaceMenuPaintCategory.Body;
                return true;
            case "hand":
            case "hands":
                category = SkyrimRaceMenuPaintCategory.Hands;
                return true;
            case "foot":
            case "feet":
                category = SkyrimRaceMenuPaintCategory.Feet;
                return true;
            case "face":
                category = SkyrimRaceMenuPaintCategory.Face;
                return true;
            default:
                category = default;
                return false;
        }
    }
}
