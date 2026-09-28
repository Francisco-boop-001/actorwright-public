using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Observable stages for the first complete blank-to-NPC product journey.</summary>
public enum BlankNpcBuildStage
{
    Preflight,
    Plugin,
    FaceGeom,
    FaceTint,
    Package,
    Verification,
    Complete
}

public sealed record BlankNpcBuildProgress(
    BlankNpcBuildStage Stage,
    int Percent,
    string Message);

/// <summary>
/// Closed output plugin kinds for a newly allocated NPC. Both kinds keep an
/// ordinary '.esp' file name; <see cref="Espfe"/> additionally sets the TES4
/// light flag (0x200) at creation without compacting any owned FormID.
/// </summary>
public enum BlankNpcPluginType
{
    Esp,
    Espfe
}

/// <summary>
/// The one blank-route output predicate shared by build preflight and the
/// blank writer so the two layers cannot disagree about the output contract.
/// Existing-NPC edits do not consume it and may emit other plugin kinds.
/// </summary>
public static class BlankNpcOutputPolicy
{
    /// <summary>Wire names accepted for <c>output.pluginType</c>.</summary>
    public const string EspWireName = "esp";
    public const string EspfeWireName = "espfe";

    /// <summary>Highest local FormID a light plugin may own (0x800..0xFFF).</summary>
    public const uint LightLocalFormIdLimit = 0xFFF;

    public const string LightBudgetDiagnosticCode = "blank-npc-light-budget";

    public static bool TryParseWireName(string? value, out BlankNpcPluginType pluginType)
    {
        switch (value)
        {
            case null:
            case EspWireName:
                pluginType = BlankNpcPluginType.Esp;
                return true;
            case EspfeWireName:
                pluginType = BlankNpcPluginType.Espfe;
                return true;
            default:
                pluginType = BlankNpcPluginType.Esp;
                return false;
        }
    }

    /// <summary>
    /// Refuses any blank-route output name other than an ordinary '.esp'. The
    /// diagnostic names the offending file and the supported light route.
    /// </summary>
    public static ImmutableArray<Diagnostic> Evaluate(
        PluginName outputPlugin,
        BlankNpcPluginType pluginType,
        string diagnosticCode)
    {
        if (outputPlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            return [];
        return
        [
            new Diagnostic(diagnosticCode, DiagnosticSeverity.Error,
                $"The blank NPC route writes an ordinary '.esp' file name only; '{outputPlugin.Value}' " +
                $"is unsupported for output.pluginType '{WireName(pluginType)}'. For a light plugin keep " +
                $"the '.esp' name and set output.pluginType to \"{EspfeWireName}\" (ESL-flagged .esp, " +
                "owned FormIDs unchanged in 0x800..0xFFF).")
        ];
    }

    /// <summary>
    /// Refuses a light plugin whose owned local FormIDs (0x800 up to the next
    /// free ID) would exceed 0xFFF. Compaction is never performed.
    /// </summary>
    public static ImmutableArray<Diagnostic> EvaluateLightBudget(
        BlankNpcPluginType pluginType,
        uint nextLocalFormId)
    {
        if (pluginType != BlankNpcPluginType.Espfe ||
            nextLocalFormId <= LightLocalFormIdLimit + 1)
            return [];
        return
        [
            new Diagnostic(LightBudgetDiagnosticCode, DiagnosticSeverity.Error,
                $"A light plugin may own local FormIDs 0x800..0x{LightLocalFormIdLimit:X} only, but this " +
                $"build would own 0x800..0x{nextLocalFormId - 1:X} (next free 0x{nextLocalFormId:X}). " +
                $"FormIDs are never compacted; request output.pluginType \"{EspWireName}\" instead.")
        ];
    }

    public static string WireName(BlankNpcPluginType pluginType) => pluginType switch
    {
        BlankNpcPluginType.Espfe => EspfeWireName,
        _ => EspWireName
    };
}

/// <summary>
/// Complete, explicit input for one newly allocated Skyrim NPC and its
/// qualified FaceGen package. The output root must not already exist.
/// </summary>
public sealed record BlankNpcBuildRequest(
    GameEdition Edition,
    WorkspacePath ProviderManifest,
    Sha256Hash ExpectedProviderManifestSha256,
    WorkspacePath TemplatePlugin,
    Sha256Hash ExpectedTemplatePluginSha256,
    FormId TemplateNpcFormId,
    WorkspacePath FaceGeomCarrier,
    Sha256Hash ExpectedFaceGeomCarrierSha256,
    WorkspacePath FaceTintManifest,
    WorkspacePath FaceTintProviderRoot,
    WorkspacePath DependencyManifest,
    WorkspacePath OutputRoot,
    PluginName OutputPlugin,
    NpcCreationIdentity Identity,
    SkyrimNpcCreationTraits Traits,
    SkyrimNpcCreationReferences References,
    NpcCreationAppearanceSource Appearance,
    SkyrimNpcCreationStats Stats)
{
    public ProviderResourceAuthoritySet? ProviderResources { get; init; }

    /// <summary>
    /// Output plugin kind. The default keeps the historical ordinary '.esp';
    /// <see cref="BlankNpcPluginType.Espfe"/> sets the TES4 light flag at
    /// creation, flag-only, after <see cref="BlankNpcOutputPolicy"/> checks
    /// the owned-FormID budget.
    /// </summary>
    public BlankNpcPluginType PluginType { get; init; } = BlankNpcPluginType.Esp;

    /// <summary>
    /// Selects the FaceGeom materialization source. Existing blank-NPC callers
    /// retain the qualified provider carrier; preset builds can bind their
    /// admitted CharGen NIF without weakening the provider qualification used
    /// for the template and generated-asset route.
    /// </summary>
    public BlankNpcFaceGeomSource FaceGeomSource { get; init; } =
        ProviderBlankNpcFaceGeomSource.Instance;

    /// <summary>
    /// Selects the sole FaceTint materialization route. Existing callers keep
    /// the qualified generated route unless they explicitly bind an exact DDS.
    /// </summary>
    public BlankNpcFaceTintSource FaceTintSource { get; init; } =
        GeneratedBlankNpcFaceTintSource.Instance;

    /// <summary>
    /// Optional complete SSE apply-script payload authored into the newly
    /// created NPC by the same plugin transaction. Null preserves the blank
    /// carrier's existing VMAD behavior byte-for-byte.
    /// </summary>
    public SkyrimNpcApplySseVmadPayload? RuntimeAppearance { get; init; }

    /// <summary>
    /// Optional exact output-master authority. A default array means the
    /// caller relies on the provider-prefix gate; an initialized array requires
    /// exact deterministic equality before the plugin write, except that one
    /// provider-bound exposed-outfit clone may append independently derived
    /// retained-reference owners while preserving this array as an exact
    /// ordered prefix.
    /// </summary>
    public ImmutableArray<PluginName> ExpectedOutputMasters { get; init; }

    /// <summary>
    /// Exact copied plugin authorities required by an admitted appearance.
    /// They remain read-only inputs and are never copied into the package.
    /// </summary>
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];

    /// <summary>
    /// Additional hash-bound game assets required by the admitted appearance.
    /// Destinations are relative to the package Data directory.
    /// </summary>
    public ImmutableArray<BlankNpcTransitivePackageAsset> TransitivePackageAssets { get; init; } = [];
}

/// <summary>Closed choice for the FaceGeom bytes consumed by a blank-NPC build.</summary>
public abstract record BlankNpcFaceGeomSource
{
    private protected BlankNpcFaceGeomSource()
    {
    }
}

/// <summary>Uses the complete carrier bound by the qualified provider manifest.</summary>
public sealed record ProviderBlankNpcFaceGeomSource : BlankNpcFaceGeomSource
{
    public static ProviderBlankNpcFaceGeomSource Instance { get; } = new();

    private ProviderBlankNpcFaceGeomSource()
    {
    }
}

/// <summary>
/// Uses one exact, hash-bound CharGen NIF. The existing carrier analyzer still
/// requires a complete qualified graph and rewrites only its FaceTint route.
/// </summary>
public sealed record ExactNifBlankNpcFaceGeomSource(
    WorkspacePath SourceNif,
    Sha256Hash ExpectedSha256) : BlankNpcFaceGeomSource
{
    /// <summary>
    /// The exact structural envelope that qualified this non-provider NIF.
    /// Defaults to the historical provider-pinned contract.
    /// </summary>
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

/// <summary>Closed choice for the FaceTint bytes owned by a blank-NPC build.</summary>
public abstract record BlankNpcFaceTintSource
{
    private protected BlankNpcFaceTintSource()
    {
    }
}

/// <summary>Builds FaceTint from the existing qualified provider recipe.</summary>
public sealed record GeneratedBlankNpcFaceTintSource : BlankNpcFaceTintSource
{
    public static GeneratedBlankNpcFaceTintSource Instance { get; } = new();

    private GeneratedBlankNpcFaceTintSource()
    {
    }
}

/// <summary>Copies one admitted DDS byte-for-byte after hash and decode qualification.</summary>
public sealed record ExactDdsBlankNpcFaceTintSource(
    WorkspacePath SourceDds,
    Sha256Hash ExpectedSha256,
    int Width,
    int Height) : BlankNpcFaceTintSource;

/// <summary>One exact source file and its normalized destination below package Data.</summary>
public sealed record BlankNpcTransitivePackageAsset(
    WorkspacePath Source,
    Sha256Hash ExpectedSha256,
    AssetPath Destination,
    string? Kind = null);

/// <summary>
/// Explicit inventory kinds are intentionally closed. A null kind retains
/// the generated transitive-package-asset-NNNN identity used by ordinary
/// callers.
/// </summary>
public static class BlankNpcTransitivePackageAssetKinds
{
    public const string ExternalHeadPartDependencies =
        "external-headpart-dependencies";
}

/// <summary>Paths and hashes of the game-facing walking-product package.</summary>
public sealed record BlankNpcBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Verdict,
    WorkspacePath OutputRoot,
    WorkspacePath Plugin,
    Sha256Hash PluginSha256,
    FormId AllocatedFormId,
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256,
    WorkspacePath FaceTint,
    Sha256Hash FaceTintSha256,
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256,
    bool RuntimeAuthority);

public sealed record BlankNpcBuildResult(
    bool Completed,
    BlankNpcBuildArtifact? Artifact,
    NpcCreationResult? NpcCreation,
    QualifiedFaceGeomCarrierMaterializationResult? FaceGeom,
    FaceTintBuildResult? FaceTint,
    FaceTintTextureDecodeResult? FaceTintReadback,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Shared application workflow used unchanged by the CLI and desktop UI.
/// Completion is capped at STATIC_PASS_RUNTIME_REQUIRED.
/// </summary>
public interface IBlankNpcBuildService
{
    ValueTask<BlankNpcBuildResult> ExecuteAsync(
        BlankNpcBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken);
}
