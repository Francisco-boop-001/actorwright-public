using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimExteriorTransform
{
    public SkyrimExteriorTransform(
        double x,
        double y,
        double z,
        double rotationX,
        double rotationY,
        double rotationZ)
    {
        double[] values = [x, y, z, rotationX, rotationY, rotationZ];
        if (values.Any(value => !double.IsFinite(value)))
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "Exterior transforms require finite coordinates and rotations.");

        X = x;
        Y = y;
        Z = z;
        RotationX = rotationX;
        RotationY = rotationY;
        RotationZ = rotationZ;
    }

    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }

    public double RotationX { get; init; }

    public double RotationY { get; init; }

    public double RotationZ { get; init; }
}

public sealed record SkyrimFollowerFinishSourceAuthority
{
    public SkyrimFollowerFinishSourceAuthority(
        WorkspacePath zip,
        long zipByteLength,
        Sha256Hash zipSha256,
        WorkspacePath packageManifest,
        Sha256Hash packageManifestSha256,
        PluginName plugin,
        Sha256Hash pluginSha256,
        Sha256Hash faceGeomSha256,
        Sha256Hash faceTintSha256)
    {
        if (zipByteLength <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(zipByteLength),
                zipByteLength,
                "Source ZIP length must be positive.");
        if (!plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The follower-finish source plugin must retain an .esp filename.",
                nameof(plugin));

        Zip = zip;
        ZipByteLength = zipByteLength;
        ZipSha256 = zipSha256;
        PackageManifest = packageManifest;
        PackageManifestSha256 = packageManifestSha256;
        Plugin = plugin;
        PluginSha256 = pluginSha256;
        FaceGeomSha256 = faceGeomSha256;
        FaceTintSha256 = faceTintSha256;
    }

    public WorkspacePath Zip { get; init; }

    public long ZipByteLength { get; init; }

    public Sha256Hash ZipSha256 { get; init; }

    public WorkspacePath PackageManifest { get; init; }

    public Sha256Hash PackageManifestSha256 { get; init; }

    public PluginName Plugin { get; init; }

    public Sha256Hash PluginSha256 { get; init; }

    public Sha256Hash FaceGeomSha256 { get; init; }

    public Sha256Hash FaceTintSha256 { get; init; }
}

public sealed record SkyrimFollowerFinishFileAuthority
{
    public SkyrimFollowerFinishFileAuthority(
        WorkspacePath path,
        long byteLength,
        Sha256Hash sha256)
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                byteLength,
                "External authority byte length must be positive.");

        Path = path;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    public WorkspacePath Path { get; init; }

    public long ByteLength { get; init; }

    public Sha256Hash Sha256 { get; init; }
}

public sealed record SkyrimFollowerFinishPluginProviderAuthority
{
    public SkyrimFollowerFinishPluginProviderAuthority(
        PluginName plugin,
        WorkspacePath path,
        long byteLength,
        Sha256Hash sha256)
    {
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                byteLength,
                "External provider byte length must be positive.");
        if (!string.Equals(
                System.IO.Path.GetFileName(path.Value),
                plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "An external provider filename must agree with its declared plugin name.",
                nameof(path));

        Plugin = plugin;
        Path = path;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    public PluginName Plugin { get; init; }

    public WorkspacePath Path { get; init; }

    public long ByteLength { get; init; }

    public Sha256Hash Sha256 { get; init; }
}

public sealed record SkyrimFollowerFinishExternalAuthorities
{
    public SkyrimFollowerFinishExternalAuthorities(
        SkyrimFollowerFinishFileAuthority placementEvidence,
        ImmutableArray<SkyrimFollowerFinishPluginProviderAuthority> providers)
    {
        ArgumentNullException.ThrowIfNull(placementEvidence);
        if (providers.IsDefaultOrEmpty)
            throw new ArgumentException(
                "External provider authority must be a non-empty ordered surface.",
                nameof(providers));
        if (providers
                .Select(provider => provider.Path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != providers.Length)
            throw new ArgumentException(
                "External provider paths must be case-insensitively unique.",
                nameof(providers));
        if (providers
                .Select(provider => provider.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != providers.Length)
            throw new ArgumentException(
                "External provider plugin names must be case-insensitively unique.",
                nameof(providers));
        if (providers.Count(provider =>
                string.Equals(
                    provider.Plugin.Value,
                    "Skyrim.esm",
                    StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ArgumentException(
                "External provider authority requires exactly one Skyrim.esm.",
                nameof(providers));
        if (providers.Any(provider =>
                string.Equals(
                    provider.Path.Value,
                    placementEvidence.Path.Value,
                    StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                "Placement evidence and provider paths must be distinct.",
                nameof(providers));

        PlacementEvidence = placementEvidence;
        Providers = providers
            .OrderBy(
                provider => provider.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                provider => provider.Plugin.Value,
                StringComparer.Ordinal)
            .ThenBy(
                provider => provider.Path.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                provider => provider.Path.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public SkyrimFollowerFinishFileAuthority PlacementEvidence { get; init; }

    public ImmutableArray<SkyrimFollowerFinishPluginProviderAuthority>
        Providers { get; init; }
}

/// <summary>Closed old-to-new mutation for one output-owned CLFM record.</summary>
public sealed record SkyrimFollowerFinishHairChange
{
    public SkyrimFollowerFinishHairChange(
        FormId colorFormId,
        SkyrimPackedRgb oldPackedRgb,
        SkyrimPackedRgb newPackedRgb)
    {
        ValidatePackedRgb(oldPackedRgb, nameof(oldPackedRgb));
        ValidatePackedRgb(newPackedRgb, nameof(newPackedRgb));
        ColorFormId = colorFormId;
        OldPackedRgb = oldPackedRgb;
        NewPackedRgb = newPackedRgb;
    }

    public FormId ColorFormId { get; init; }

    public SkyrimPackedRgb OldPackedRgb { get; init; }

    public SkyrimPackedRgb NewPackedRgb { get; init; }

    private static void ValidatePackedRgb(
        SkyrimPackedRgb value,
        string parameterName)
    {
        if (value.Value > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(
                parameterName,
                value.Value,
                "A packed RGB value must be between 0x000000 and 0xFFFFFF.");
    }
}

/// <summary>
/// Request-owned package shape. The procedure, schedule, target, and condition
/// remain explicit transaction data.
/// </summary>
public sealed record SkyrimFollowerFinishSandbox
{
    public SkyrimFollowerFinishSandbox(
        string procedure,
        int radius,
        string schedule,
        FormReference target,
        string condition)
    {
        if (string.IsNullOrWhiteSpace(procedure))
            throw new ArgumentException(
                "Sandbox procedure must be non-empty.",
                nameof(procedure));
        if (radius is <= 0 or > 2048)
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                radius,
                "Sandbox radius must be from 1 through 2048.");
        if (string.IsNullOrWhiteSpace(schedule))
            throw new ArgumentException(
                "Sandbox schedule must be non-empty.",
                nameof(schedule));
        if (string.IsNullOrWhiteSpace(condition))
            throw new ArgumentException(
                "Sandbox condition must be non-empty.",
                nameof(condition));

        Procedure = procedure;
        Radius = radius;
        Schedule = schedule;
        Target = target;
        Condition = condition;
    }

    public string Procedure { get; init; }

    public int Radius { get; init; }

    public string Schedule { get; init; }

    public FormReference Target { get; init; }

    public string Condition { get; init; }
}

public sealed record SkyrimFollowerFinishPlacement(
    FormReference Worldspace,
    FormReference Cell,
    FormReference MarkerBase,
    SkyrimExteriorTransform Actor,
    SkyrimExteriorTransform Anchor);

/// <summary>
/// Deterministic local FormID allocation for the bounded simple-follower
/// finish operation.
/// </summary>
public sealed record SkyrimFollowerFinishAllocation
{
    public SkyrimFollowerFinishAllocation(
        FormId package,
        FormId anchor,
        FormId actor,
        FormId nextFormId)
    {
        FormId[] ids = [package, anchor, actor, nextFormId];
        foreach (var id in ids)
        {
            if (id.Value is < 0x800 or > 0xFFF)
                throw new ArgumentOutOfRangeException(
                    nameof(package),
                    id,
                    "Follower-finish local FormIDs must be inside 0x800..0xFFF.");
        }
        if (ids.Select(id => id.Value).Distinct().Count() != ids.Length)
            throw new ArgumentException(
                "Follower-finish local FormIDs must be unique.",
                nameof(package));
        if (package.Value > 0x0FFC)
            throw new ArgumentOutOfRangeException(
                nameof(package),
                package,
                "Follower-finish allocations require four contiguous local FormIDs inside 0x800..0xFFF.");
        if (anchor.Value != package.Value + 1 ||
            actor.Value != package.Value + 2 ||
            nextFormId.Value != package.Value + 3)
            throw new ArgumentException(
                "Follower-finish allocations must be contiguous: Package, Anchor, Actor, then NextFormID.",
                nameof(package));

        Package = package;
        Anchor = anchor;
        Actor = actor;
        NextFormId = nextFormId;
    }

    public FormId Package { get; init; }

    public FormId Anchor { get; init; }

    public FormId Actor { get; init; }

    public FormId NextFormId { get; init; }

    public static SkyrimFollowerFinishAllocation SimpleFollowerV1 { get; } =
        new(
            new FormId(0x805),
            new FormId(0x806),
            new FormId(0x807),
            new FormId(0x808));
}

public sealed record SkyrimFollowerFinishRequest
{
    public const string OperationName = "skyrim-simple-follower-finish";

    public SkyrimFollowerFinishRequest(
        int schemaVersion,
        string operation,
        SkyrimFollowerFinishSourceAuthority source,
        EditorId npcEditorId,
        FormId npcFormId,
        ImmutableArray<FormId> occupiedLocalFormIds,
        FormReference expectedRace,
        string expectedBodyRoute,
        bool expectedDefaultOutfitNull,
        ImmutableArray<NpcFactionEntry> expectedFactionRanks,
        FormId relationshipFormId,
        string expectedRelationshipRank,
        byte expectedRelationshipRankRawDiscriminator,
        SkyrimFollowerFinishHairChange hair,
        bool setEslFlag,
        bool compactFormIds,
        SkyrimFollowerFinishSandbox sandbox,
        SkyrimFollowerFinishPlacement placement,
        SkyrimFollowerFinishAllocation allocation,
        ImmutableArray<string> allowedNewRecords,
        ImmutableArray<string> allowedExistingRecordChanges,
        ImmutableArray<AssetPath> allowedPackageFiles,
        WorkspacePath outputRoot,
        WorkspacePath outputZip,
        string narrative,
        SkyrimFollowerFinishExternalAuthorities? externalAuthorities = null)
    {
        if (schemaVersion != 1)
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Follower-finish requests require schema version 1.");
        if (!string.Equals(operation, OperationName, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Follower-finish operation must be '{OperationName}'.",
                nameof(operation));
        if (!setEslFlag || compactFormIds)
            throw new ArgumentException(
                "Simple follower finish requires ESL flagging without FormID compaction.");
        if (!expectedDefaultOutfitNull)
            throw new ArgumentException(
                "Simple follower finish preserves a null default outfit.",
                nameof(expectedDefaultOutfitNull));
        if (string.IsNullOrWhiteSpace(expectedBodyRoute))
            throw new ArgumentException(
                "The expected body route must remain explicit request data.",
                nameof(expectedBodyRoute));
        if (string.IsNullOrWhiteSpace(expectedRelationshipRank))
            throw new ArgumentException(
                "The expected typed relationship rank must remain explicit request data.",
                nameof(expectedRelationshipRank));
        if (string.IsNullOrWhiteSpace(narrative))
            throw new ArgumentException(
                "The narrative intent must remain explicit request data.",
                nameof(narrative));

        ValidateOccupiedIds(occupiedLocalFormIds);
        if (!occupiedLocalFormIds.Contains(npcFormId) ||
            !occupiedLocalFormIds.Contains(hair.ColorFormId) ||
            !occupiedLocalFormIds.Contains(relationshipFormId))
            throw new ArgumentException(
                "NPC, hair-color, and relationship records must be present in the occupied local-ID inventory.");
        if (occupiedLocalFormIds.Any(id => id.Value >= allocation.Package.Value))
            throw new ArgumentException(
                "Every occupied local FormID must precede the follower-finish allocation frontier.",
                nameof(allocation));

        ValidateStringSurface(
            allowedNewRecords,
            nameof(allowedNewRecords));
        ValidateStringSurface(
            allowedExistingRecordChanges,
            nameof(allowedExistingRecordChanges));
        ValidatePackageFiles(allowedPackageFiles);
        if (expectedFactionRanks.IsDefaultOrEmpty)
            throw new ArgumentException(
                "Expected faction ranks must be a non-empty ordered surface.",
                nameof(expectedFactionRanks));
        if (expectedFactionRanks
                .Select(entry => entry.Faction)
                .Distinct()
                .Count() != expectedFactionRanks.Length)
            throw new ArgumentException(
                "Expected faction references must be unique.",
                nameof(expectedFactionRanks));
        var transactionPaths = new List<WorkspacePath>
        {
            source.Zip,
            source.PackageManifest,
            outputRoot,
            outputZip
        };
        if (externalAuthorities is not null)
        {
            transactionPaths.Add(
                externalAuthorities.PlacementEvidence.Path);
            transactionPaths.AddRange(
                externalAuthorities.Providers.Select(provider =>
                    provider.Path));
        }
        ValidateDisjointPaths(transactionPaths.ToArray());

        SchemaVersion = schemaVersion;
        Operation = operation;
        Source = source;
        NpcEditorId = npcEditorId;
        NpcFormId = npcFormId;
        OccupiedLocalFormIds = occupiedLocalFormIds;
        ExpectedRace = expectedRace;
        ExpectedBodyRoute = expectedBodyRoute;
        ExpectedDefaultOutfitNull = expectedDefaultOutfitNull;
        ExpectedFactionRanks = expectedFactionRanks;
        RelationshipFormId = relationshipFormId;
        ExpectedRelationshipRank = expectedRelationshipRank;
        ExpectedRelationshipRankRawDiscriminator =
            expectedRelationshipRankRawDiscriminator;
        Hair = hair;
        SetEslFlag = setEslFlag;
        CompactFormIds = compactFormIds;
        Sandbox = sandbox;
        Placement = placement;
        Allocation = allocation;
        AllowedNewRecords = allowedNewRecords;
        AllowedExistingRecordChanges = allowedExistingRecordChanges;
        AllowedPackageFiles = allowedPackageFiles;
        OutputRoot = outputRoot;
        OutputZip = outputZip;
        Narrative = narrative;
        ExternalAuthorities = externalAuthorities!;
    }

    public int SchemaVersion { get; init; }

    public string Operation { get; init; }

    public SkyrimFollowerFinishSourceAuthority Source { get; init; }

    public EditorId NpcEditorId { get; init; }

    public FormId NpcFormId { get; init; }

    public ImmutableArray<FormId> OccupiedLocalFormIds { get; init; }

    public FormReference ExpectedRace { get; init; }

    public string ExpectedBodyRoute { get; init; }

    public bool ExpectedDefaultOutfitNull { get; init; }

    public ImmutableArray<NpcFactionEntry> ExpectedFactionRanks { get; init; }

    public FormId RelationshipFormId { get; init; }

    public string ExpectedRelationshipRank { get; init; }

    public byte ExpectedRelationshipRankRawDiscriminator { get; init; }

    public SkyrimFollowerFinishHairChange Hair { get; init; }

    public bool SetEslFlag { get; init; }

    public bool CompactFormIds { get; init; }

    public SkyrimFollowerFinishSandbox Sandbox { get; init; }

    public SkyrimFollowerFinishPlacement Placement { get; init; }

    public SkyrimFollowerFinishAllocation Allocation { get; init; }

    public ImmutableArray<string> AllowedNewRecords { get; init; }

    public ImmutableArray<string> AllowedExistingRecordChanges { get; init; }

    public ImmutableArray<AssetPath> AllowedPackageFiles { get; init; }

    public WorkspacePath OutputRoot { get; init; }

    public WorkspacePath OutputZip { get; init; }

    public string Narrative { get; init; }

    public SkyrimFollowerFinishExternalAuthorities ExternalAuthorities
    {
        get;
        init;
    }

    private static void ValidateOccupiedIds(
        ImmutableArray<FormId> occupiedLocalFormIds)
    {
        if (occupiedLocalFormIds.IsDefaultOrEmpty)
            throw new ArgumentException(
                "The occupied local-ID inventory must be non-empty.",
                nameof(occupiedLocalFormIds));
        if (occupiedLocalFormIds.Any(id => id.Value is < 0x800 or > 0xFFF))
            throw new ArgumentOutOfRangeException(
                nameof(occupiedLocalFormIds),
                "Occupied local FormIDs must be inside 0x800..0xFFF.");
        if (occupiedLocalFormIds
                .Select(id => id.Value)
                .Distinct()
                .Count() != occupiedLocalFormIds.Length)
            throw new ArgumentException(
                "Occupied local FormIDs must be unique.",
                nameof(occupiedLocalFormIds));
    }

    internal static void ValidateStringSurface(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefaultOrEmpty ||
            values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "Ordered change surfaces must contain non-empty values.",
                parameterName);
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException(
                "Ordered change surfaces must not contain duplicate values.",
                parameterName);
    }

    internal static void ValidatePackageFiles(
        ImmutableArray<AssetPath> files)
    {
        if (files.IsDefaultOrEmpty)
            throw new ArgumentException(
                "The package-file allowlist must be non-empty.",
                nameof(files));
        if (files
                .Select(path => path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != files.Length)
            throw new ArgumentException(
                "The package-file allowlist must be case-insensitively unique.",
                nameof(files));
    }

    private static void ValidateDisjointPaths(
        params WorkspacePath[] paths)
    {
        for (int first = 0; first < paths.Length; first++)
        {
            for (int second = first + 1; second < paths.Length; second++)
            {
                if (IsSameOrDescendant(paths[first], paths[second]) ||
                    IsSameOrDescendant(paths[second], paths[first]))
                    throw new ArgumentException(
                        "Follower-finish source inputs and outputs must be pairwise distinct and must not contain each other.");
            }
        }
    }

    private static bool IsSameOrDescendant(
        WorkspacePath candidate,
        WorkspacePath ancestor)
    {
        string candidateValue = Path.TrimEndingDirectorySeparator(
            candidate.Value);
        string ancestorValue = Path.TrimEndingDirectorySeparator(
            ancestor.Value);
        if (string.Equals(
                candidateValue,
                ancestorValue,
                StringComparison.OrdinalIgnoreCase))
            return true;
        if (!candidateValue.StartsWith(
                ancestorValue,
                StringComparison.OrdinalIgnoreCase) ||
            candidateValue.Length <= ancestorValue.Length)
            return false;
        char boundary = candidateValue[ancestorValue.Length];
        return boundary == Path.DirectorySeparatorChar ||
               boundary == Path.AltDirectorySeparatorChar;
    }
}

public sealed record SkyrimFollowerFinishPluginSnapshot
{
    public SkyrimFollowerFinishPluginSnapshot(
        bool valid,
        PluginName plugin,
        Sha256Hash pluginSha256,
        uint tes4Flags,
        ImmutableArray<PluginName> masters,
        FormId nextFormId,
        ImmutableArray<string> recordInventory,
        ImmutableArray<string> actorSubrecordDigests,
        SkyrimPackedRgb hairPackedRgb,
        FormReference actorHairColor,
        bool defaultOutfitNull,
        ImmutableArray<NpcFactionEntry> factionRanks,
        string relationshipRank,
        byte relationshipRankRawDiscriminator,
        ImmutableArray<string> absentSignatures,
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (hairPackedRgb.Value > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(
                nameof(hairPackedRgb),
                "Snapshot hair RGB must be between 0x000000 and 0xFFFFFF.");
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            recordInventory,
            nameof(recordInventory));
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            actorSubrecordDigests,
            nameof(actorSubrecordDigests));
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            absentSignatures,
            nameof(absentSignatures));
        if (masters.IsDefault ||
            factionRanks.IsDefault ||
            diagnostics.IsDefault)
            throw new ArgumentException(
                "Snapshot ordered collections must be initialized.");
        if (string.IsNullOrWhiteSpace(relationshipRank))
            throw new ArgumentException(
                "Snapshot typed relationship rank must be non-empty.",
                nameof(relationshipRank));

        Valid = valid;
        Plugin = plugin;
        PluginSha256 = pluginSha256;
        Tes4Flags = tes4Flags;
        Masters = masters;
        NextFormId = nextFormId;
        RecordInventory = recordInventory;
        ActorSubrecordDigests = actorSubrecordDigests;
        HairPackedRgb = hairPackedRgb;
        ActorHairColor = actorHairColor;
        DefaultOutfitNull = defaultOutfitNull;
        FactionRanks = factionRanks;
        RelationshipRank = relationshipRank;
        RelationshipRankRawDiscriminator =
            relationshipRankRawDiscriminator;
        AbsentSignatures = absentSignatures;
        Diagnostics = diagnostics;
    }

    public bool Valid { get; init; }

    public PluginName Plugin { get; init; }

    public Sha256Hash PluginSha256 { get; init; }

    public uint Tes4Flags { get; init; }

    public ImmutableArray<PluginName> Masters { get; init; }

    public FormId NextFormId { get; init; }

    public ImmutableArray<string> RecordInventory { get; init; }

    public ImmutableArray<string> ActorSubrecordDigests { get; init; }

    public SkyrimPackedRgb HairPackedRgb { get; init; }

    public FormReference ActorHairColor { get; init; }

    public bool DefaultOutfitNull { get; init; }

    public ImmutableArray<NpcFactionEntry> FactionRanks { get; init; }

    public string RelationshipRank { get; init; }

    public byte RelationshipRankRawDiscriminator { get; init; }

    public ImmutableArray<string> AbsentSignatures { get; init; }

    public ImmutableArray<Diagnostic> Diagnostics { get; init; }
}

public sealed record SkyrimFollowerFinishProposal
{
    public SkyrimFollowerFinishProposal(
        int schemaVersion,
        string operation,
        Sha256Hash requestSha256,
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishPluginSnapshot sourceSnapshot,
        ImmutableArray<string> existingRecordChanges,
        ImmutableArray<string> newRecords,
        FormId nextFormId,
        ImmutableArray<string> rawGroupTreeSurface,
        ImmutableArray<AssetPath> allowedPackageFiles,
        bool runtimeAuthority)
    {
        if (schemaVersion != 1)
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Follower-finish proposals require schema version 1.");
        if (!string.Equals(
                operation,
                SkyrimFollowerFinishRequest.OperationName,
                StringComparison.Ordinal))
            throw new ArgumentException(
                $"Follower-finish operation must be '{SkyrimFollowerFinishRequest.OperationName}'.",
                nameof(operation));
        if (runtimeAuthority)
            throw new ArgumentException(
                "A static follower-finish proposal cannot claim runtime authority.",
                nameof(runtimeAuthority));
        if (nextFormId != request.Allocation.NextFormId)
            throw new ArgumentException(
                "Proposal NextFormID must equal the nested request allocation.",
                nameof(nextFormId));
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            existingRecordChanges,
            nameof(existingRecordChanges));
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            newRecords,
            nameof(newRecords));
        SkyrimFollowerFinishRequest.ValidateStringSurface(
            rawGroupTreeSurface,
            nameof(rawGroupTreeSurface));
        SkyrimFollowerFinishRequest.ValidatePackageFiles(
            allowedPackageFiles);
        if (!existingRecordChanges.SequenceEqual(
                request.AllowedExistingRecordChanges))
            throw new ArgumentException(
                "Proposal existing-record changes must equal the nested request authorization.",
                nameof(existingRecordChanges));
        if (!newRecords.SequenceEqual(request.AllowedNewRecords))
            throw new ArgumentException(
                "Proposal new records must equal the nested request authorization.",
                nameof(newRecords));
        if (!allowedPackageFiles.SequenceEqual(request.AllowedPackageFiles))
            throw new ArgumentException(
                "Proposal package-file allowlist must equal the nested request allowlist.",
                nameof(allowedPackageFiles));
        var expectedHairColor = new FormReference(
            request.Source.Plugin,
            request.Hair.ColorFormId);
        if (!sourceSnapshot.Valid ||
            sourceSnapshot.Plugin != request.Source.Plugin ||
            sourceSnapshot.PluginSha256 != request.Source.PluginSha256 ||
            sourceSnapshot.NextFormId != request.Allocation.Package ||
            sourceSnapshot.HairPackedRgb != request.Hair.OldPackedRgb ||
            sourceSnapshot.ActorHairColor != expectedHairColor ||
            sourceSnapshot.DefaultOutfitNull !=
            request.ExpectedDefaultOutfitNull ||
            !sourceSnapshot.FactionRanks.SequenceEqual(
                request.ExpectedFactionRanks) ||
            sourceSnapshot.RelationshipRank !=
            request.ExpectedRelationshipRank ||
            sourceSnapshot.RelationshipRankRawDiscriminator !=
            request.ExpectedRelationshipRankRawDiscriminator)
            throw new ArgumentException(
                "Proposal source snapshot does not match the nested request identity.",
                nameof(sourceSnapshot));

        SchemaVersion = schemaVersion;
        Operation = operation;
        RequestSha256 = requestSha256;
        Request = request;
        SourceSnapshot = sourceSnapshot;
        ExistingRecordChanges = existingRecordChanges;
        NewRecords = newRecords;
        NextFormId = nextFormId;
        RawGroupTreeSurface = rawGroupTreeSurface;
        AllowedPackageFiles = allowedPackageFiles;
        RuntimeAuthority = runtimeAuthority;
    }

    public int SchemaVersion { get; init; }

    public string Operation { get; init; }

    public Sha256Hash RequestSha256 { get; init; }

    public SkyrimFollowerFinishRequest Request { get; init; }

    public SkyrimFollowerFinishPluginSnapshot SourceSnapshot { get; init; }

    public ImmutableArray<string> ExistingRecordChanges { get; init; }

    public ImmutableArray<string> NewRecords { get; init; }

    public FormId NextFormId { get; init; }

    public ImmutableArray<string> RawGroupTreeSurface { get; init; }

    public ImmutableArray<AssetPath> AllowedPackageFiles { get; init; }

    public bool RuntimeAuthority { get; init; }
}

public sealed record SkyrimFollowerFinishRequestLoadResult(
    bool Loaded,
    WorkspacePath Path,
    Sha256Hash ExpectedSha256,
    Sha256Hash? ActualSha256,
    long? ByteLength,
    SkyrimFollowerFinishRequest? Request,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishProposalLoadResult(
    bool Loaded,
    WorkspacePath Path,
    Sha256Hash ExpectedSha256,
    Sha256Hash? ActualSha256,
    long? ByteLength,
    SkyrimFollowerFinishProposal? Proposal,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishProposalResult(
    bool Proposed,
    SkyrimFollowerFinishProposal? Proposal,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishPluginWriteResult(
    bool Written,
    WorkspacePath? OutputPlugin,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishPluginVerification(
    bool Verified,
    Sha256Hash? SourceSha256,
    Sha256Hash? OutputSha256,
    ImmutableArray<string> ExistingRecordChanges,
    ImmutableArray<string> NewRecords,
    ImmutableArray<string> RawGroupTreeSurface,
    bool RuntimeAuthority,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishVerificationResult(
    bool Verified,
    SkyrimFollowerFinishPluginVerification? PluginVerification,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishResult(
    bool Completed,
    SkyrimFollowerFinishPluginWriteResult? PluginWrite,
    PackageVerifyResult? PackageVerification,
    PackageArchiveResult? PackageArchive,
    bool RuntimeAuthority,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimFollowerFinishProgress(
    string Stage,
    int Percent,
    string Message);

public enum SkyrimFollowerFinishDocumentLoadMode
{
    PreWrite,
    PostWriteVerification
}

public interface ISkyrimFollowerFinishRequestFileLoader
{
    ValueTask<SkyrimFollowerFinishRequestLoadResult> LoadRequestAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        CancellationToken cancellationToken,
        SkyrimFollowerFinishDocumentLoadMode mode =
            SkyrimFollowerFinishDocumentLoadMode.PreWrite);

    ValueTask<SkyrimFollowerFinishProposalLoadResult> LoadProposalAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        CancellationToken cancellationToken,
        SkyrimFollowerFinishDocumentLoadMode mode =
            SkyrimFollowerFinishDocumentLoadMode.PreWrite);
}

public interface ISkyrimFollowerFinishPluginService
{
    ValueTask<SkyrimFollowerFinishPluginSnapshot> InspectAsync(
        SkyrimFollowerFinishRequest request,
        WorkspacePath extractedPlugin,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishPluginWriteResult> WriteAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath extractedPlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishPluginVerification> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken);
}

public interface ISkyrimFollowerFinishService
{
    ValueTask<SkyrimFollowerFinishProposalResult> AnalyzeAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        WorkspacePath proposalOutput,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishResult> ApplyAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        IProgress<SkyrimFollowerFinishProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishVerificationResult> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        WorkspacePath manifest,
        CancellationToken cancellationToken);
}
