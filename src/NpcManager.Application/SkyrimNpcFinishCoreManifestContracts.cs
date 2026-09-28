using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimNpcFinishCoreExternalHeadPartBinding(
    Sha256Hash DescriptorId,
    Sha256Hash FaceGeomExclusionAttestationSha256);

public sealed record SkyrimNpcFinishCoreExternalHeadPartAuthority(
    AssetPath SelectedManifestPath,
    Sha256Hash SelectedManifestSha256,
    ImmutableArray<SkyrimNpcFinishCoreExternalHeadPartBinding> Bindings);

public sealed record SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
    SkyrimNpcFinishCoreExternalHeadPartAuthority Authority,
    ExternalHeadPartInstallVerificationArtifact Verification,
    ExternalHeadPartInstallContextFingerprint? ContextFingerprint);

public sealed record SkyrimNpcFinishCoreExternalHeadPartManifestAuthority(
    AssetPath SelectedManifestPath,
    Sha256Hash SelectedManifestSha256,
    ImmutableArray<ExternalHeadPartDependencyDescriptor> Descriptors,
    ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation> Attestations,
    ExternalHeadPartVerifiedInstallSnapshot VerifiedInstallSnapshot)
{
    public AssetPath PromotedOutputBindingPath { get; init; }

    public Sha256Hash PromotedOutputBindingSha256 { get; init; }
}

public sealed record SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding(
    Sha256Hash DescriptorId,
    Sha256Hash AttestationSha256,
    PluginName OutputPlugin,
    Sha256Hash OutputPluginSha256,
    long OutputPluginByteLength,
    ImmutableArray<PluginName> MasterOrder,
    ImmutableArray<FormReference> DeclaredExternalPnam);

public sealed record SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding(
    string SchemaIdentifier,
    AssetPath SourceSelectedManifestPath,
    Sha256Hash SourceSelectedManifestSha256,
    ImmutableArray<SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding> Groups,
    PluginName OutputPlugin,
    Sha256Hash OutputPluginSha256,
    long OutputPluginByteLength,
    ImmutableArray<PluginName> MasterOrder,
    ImmutableArray<FormReference> DeclaredExternalPnam)
{
    public const string SchemaIdentifierValue =
        "npc.finish-core.external-head-part-output-binding.v1";
}

public sealed record SkyrimNpcFinishCoreExternalHeadPartVerification(
    ExternalHeadPartInstallVerificationArtifact Verification);

/// <summary>Runtime identity evidence for a world-clean Finish Core package.</summary>
public sealed record SkyrimNpcFinishCoreRuntimeIdentity
{
    public FormReference? BaseNpc { get; init; }

    public FormReference? PlacedReference { get; init; }

    public bool PlacementIncluded { get; init; }
}

/// <summary>One hash-bound evidence member retained under the package manifest.</summary>
public sealed record SkyrimNpcFinishCoreEvidenceEntry
{
    public AssetPath Path { get; init; }

    public long ByteLength { get; init; }

    public Sha256Hash Sha256 { get; init; }
}

/// <summary>
/// One evidence member the source package already carried under its own
/// <c>NPCManager/Evidence</c> namespace (for example a create-from-jslot host's
/// racemenu-bundle.json or FaceGeom authority). Finish relocates it under
/// <c>NPCManager/Evidence/Inherited/&lt;source-tree-sha256-8&gt;/</c> and retains
/// it as hash-bound provenance; it is never part of the canonical Finish set.
/// </summary>
public sealed record SkyrimNpcFinishCoreInheritedEvidenceEntry
{
    /// <summary>Package-relative path of the relocated member.</summary>
    public AssetPath Path { get; init; }

    /// <summary>Package-relative path the member had in the source package.</summary>
    public AssetPath SourcePath { get; init; }

    public long ByteLength { get; init; }

    public Sha256Hash Sha256 { get; init; }
}

public sealed record SkyrimNpcFinishCoreManifestEvidence
{
    public const string InheritedNamespacePrefix = "NPCManager/Evidence/Inherited/";

    public ImmutableArray<SkyrimNpcFinishCoreEvidenceEntry> Files { get; init; } =
        ImmutableArray<SkyrimNpcFinishCoreEvidenceEntry>.Empty;

    /// <summary>
    /// Additive: absent from manifests whose source carried no evidence namespace,
    /// so those manifests keep their existing bytes and hashes.
    /// </summary>
    public ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry> Inherited { get; init; } =
        ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry>.Empty;

    public Sha256Hash? PackageTreeSha256 { get; init; }

    public Sha256Hash? SourcePackageTreeSha256 { get; init; }
}

public sealed record SkyrimNpcFinishCoreRelationshipSnapshot(
    FormReference Record,
    FormReference Parent,
    FormReference Child,
    string Rank,
    byte Flags,
    FormReference? AssociationType);

public sealed record SkyrimNpcFinishCoreInventoryEntry(
    FormReference Item,
    int Count);

public sealed record SkyrimNpcFinishCoreSourceReadResult(
    bool Admitted,
    PackageVerificationArtifact? Package,
    Sha256Hash PackageTreeSha256,
    Sha256Hash PluginSha256,
    FormReference BaseNpc,
    EditorId TargetEditorId,
    bool ActorAssemblyPass,
    string? PlacementMode,
    ImmutableDictionary<string, int> TypedForbiddenCounts,
    ImmutableDictionary<string, int> RawForbiddenCounts,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public FormId NextFormId { get; init; }

    public uint Tes4Flags { get; init; }

    public ImmutableArray<string> MasterOrder { get; init; } =
        ImmutableArray<string>.Empty;

    /// <summary>
    /// Physical additional-master authority retained with this source
    /// admission.  This is additive evidence; the TES4 source prefix above
    /// remains immutable and positional.
    /// </summary>
    public ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster>
        VerifiedAdditionalMasters { get; init; } =
        ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster>.Empty;

    public ImmutableArray<string> OccupiedIds { get; init; } =
        ImmutableArray<string>.Empty;

    public uint TargetConfigurationFlags { get; init; }

    public ImmutableArray<NpcFactionEntry> FactionRanks { get; init; } =
        ImmutableArray<NpcFactionEntry>.Empty;

    public FormReference? CombatStyle { get; init; }

    public bool CombatStyleMatchesDefensiveContract { get; init; }

    public ImmutableArray<SkyrimNpcFinishCorePerk> Perks { get; init; } = [];

    public FormReference? DefaultOutfit { get; init; }

    public ImmutableArray<FormReference> OutfitArmaturesToClone { get; init; } = [];

    public ImmutableArray<FormReference> OutfitArmorsToClone { get; init; } = [];

    public ImmutableArray<SkyrimNpcFinishCoreInventoryEntry> Inventory { get; init; } =
        ImmutableArray<SkyrimNpcFinishCoreInventoryEntry>.Empty;

    public ImmutableArray<FormReference> PackageLinks { get; init; } =
        ImmutableArray<FormReference>.Empty;

    public ImmutableArray<SkyrimNpcFinishCoreRelationshipSnapshot> Relationships { get; init; } =
        ImmutableArray<SkyrimNpcFinishCoreRelationshipSnapshot>.Empty;

    public SkyrimNpcFinishCoreAiPolicy? AiData { get; init; }

    public ImmutableArray<string> SemanticSurfaceValues { get; init; } =
        ImmutableArray<string>.Empty;

    public bool AlreadySatisfied { get; init; }
}
