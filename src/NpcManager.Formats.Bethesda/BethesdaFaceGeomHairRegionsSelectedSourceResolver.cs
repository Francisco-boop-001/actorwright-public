using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class
    BethesdaFaceGeomHairRegionsSelectedSourceResolver(
        IWorkspacePolicy policy,
        WorkspacePath labRoot) :
        IFaceGeomHairRegionsSelectedSourceResolver
{
    private readonly BethesdaNpcVisualSourceComposer composer =
        new(policy, labRoot);

    public ValueTask<FaceGeomHairRegionsSelectedSourceResult>
        ResolveAsync(
            FaceGeomHairRegionsSelectedSourceRequest request,
            CancellationToken cancellationToken) =>
        composer.ResolveSelectedHairRegionsSourceAsync(
            request,
            cancellationToken);
}

public sealed partial class BethesdaNpcVisualSourceComposer
{
    internal async ValueTask<
        FaceGeomHairRegionsSelectedSourceResult>
        ResolveSelectedHairRegionsSourceAsync(
            FaceGeomHairRegionsSelectedSourceRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!ValidateSelectedHairRegionsRequest(
                request,
                diagnostics))
        {
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        PreviewAssetAuthorityPlan? authorityPlan =
            await BuildHairRegionsAuthorityPlanAsync(
                request.Intake,
                diagnostics,
                cancellationToken);
        if (authorityPlan is null ||
            HasErrors(diagnostics))
        {
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        ImmutableArray<PluginAuthority> plugins =
            await ReadPluginAuthoritiesAsync(
                request.Intake,
                overlay: null,
                diagnostics,
                cancellationToken);
        if (plugins.IsDefaultOrEmpty ||
            HasErrors(diagnostics))
        {
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        RecordGraph? graph = ReadRecordGraph(
            plugins,
            diagnostics,
            cancellationToken);
        if (graph is null ||
            HasErrors(diagnostics))
        {
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        FormKey npcKey = ToFormKey(
            request.Identity.OwnerPlugin,
            request.Identity.FormId);
        if (!graph.Npcs.TryGetValue(
                npcKey,
                out ProviderRecord<Npc>? providerRecord) ||
            providerRecord.Record.IsDeleted)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-npc",
                $"NPC {request.Identity.OwnerPlugin}|{request.Identity.FormId} is missing or deleted in the exact reviewed closure."));
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }
        if (providerRecord.Provider !=
            request.Identity.WinningProvider)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-provider",
                $"NPC winner is '{providerRecord.Provider}', not the selected '{request.Identity.WinningProvider}'."));
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        Npc npc = providerRecord.Record;
        string faceGenOwner =
            npc.FormKey.ModKey.FileName.String;
        string relative =
            $"meshes/actors/character/FaceGenData/FaceGeom/{faceGenOwner}/{npc.FormKey.ID:X8}.nif";
        string normalized =
            NormalizeAssetPath(relative);
        if (!authorityPlan.Winners.TryGetValue(
                normalized,
                out PreviewAssetWinner? winner))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-provider",
                $"Reviewed provider inventory has no unambiguous winner for '{normalized}'."));
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        WorkspacePath exactPath;
        if (winner.Kind == AssetProviderKind.Loose)
        {
            string expectedLoose = Path.GetFullPath(
                Path.Combine(
                    request.Intake.DataRoot.Value,
                    normalized.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            exactPath = new WorkspacePath(
                winner.SourcePath.Value);
            if (exactPath !=
                    new WorkspacePath(expectedLoose) ||
                !ValidateSelectedHairRegionsReadPath(
                    exactPath,
                    "selected loose FaceGeom",
                    diagnostics))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-provider",
                    "The reviewed loose FaceGeom winner does not equal its canonical copied-Data path."));
                return RefusedSelectedHairRegionsSource(
                    diagnostics);
            }

            FileInfo before = new(
                exactPath.Value);
            Sha256Hash observed =
                await HashFileAsync(
                    exactPath,
                    cancellationToken);
            FileInfo after = new(
                exactPath.Value);
            if (before.Length is <= 0 or
                    > MaximumHairRegionsCandidateBytes ||
                before.Length != after.Length ||
                before.LastWriteTimeUtc !=
                    after.LastWriteTimeUtc ||
                before.Length != winner.Bytes ||
                observed != winner.Sha256)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-stale",
                    "The loose FaceGeom winner changed after its reviewed provider inventory was admitted."));
                return RefusedSelectedHairRegionsSource(
                    diagnostics);
            }
        }
        else if (winner.Kind ==
                 AssetProviderKind.Archive)
        {
            if (!IsSelectedHairRegionsArchiveLengthAdmitted(
                    winner.Bytes))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-size",
                    $"The selected archive FaceGeom declares {winner.Bytes} bytes; HairTint authoring admits 1 through {MaximumHairRegionsCandidateBytes} bytes."));
                return RefusedSelectedHairRegionsSource(
                    diagnostics);
            }
            var resolver = new PreviewAssetResolver(
                request.Intake.DataRoot,
                overlayPackage: null,
                request.ArchiveStagingRoot,
                diagnostics,
                policy,
                labRoot,
                authorityPlan,
                MaximumHairRegionsCandidateBytes);
            ResolvedPreviewAsset? materialized =
                await resolver.ResolveAsync(
                    normalized,
                    cancellationToken);
            if (materialized is null ||
                HasErrors(diagnostics) ||
                materialized.Provider !=
                    winner.Provider ||
                materialized.Sha256 !=
                    winner.Sha256 ||
                materialized.Bytes !=
                    winner.Bytes ||
                !materialized.MaterializedPath.IsUnder(
                    request.ArchiveStagingRoot) ||
                materialized.MaterializedPath ==
                    request.ArchiveStagingRoot)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-stale",
                    "The archive FaceGeom winner could not be materialized as one exact reviewed K-local file."));
                return RefusedSelectedHairRegionsSource(
                    diagnostics);
            }
            exactPath =
                materialized.MaterializedPath;
        }
        else
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-provider",
                "The selected FaceGeom winner has an unsupported provider kind."));
            return RefusedSelectedHairRegionsSource(
                diagnostics);
        }

        string? hairColorHex = ResolveHairColor(
            npc,
            graph,
            diagnostics);
        string? hairColor =
            npc.HairColor.FormKeyNullable is { } colorKey
                ? colorKey.ToString()
                : null;
        var source = new FaceGeomHairRegionsSelectedSource(
            new FaceGeomHairRegionsFile(
                exactPath,
                winner.Bytes,
                winner.Sha256),
            new AssetPath(normalized),
            winner.Kind,
            winner.Provider,
            winner.Kind ==
                AssetProviderKind.Archive,
            new FaceGeomHairRegionPluginColorContext(
                providerRecord.Provider.Value,
                hairColor,
                hairColorHex));
        return new FaceGeomHairRegionsSelectedSourceResult(
            true,
            source,
            diagnostics.ToImmutable());
    }

    private bool ValidateSelectedHairRegionsRequest(
        FaceGeomHairRegionsSelectedSourceRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Intake.Edition !=
                GameEdition.SkyrimSpecialEdition ||
            request.Intake.RuntimeAuthority)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-intake",
                "A reviewed authority-false Skyrim SE/AE intake is required."));
        }
        if (!string.Equals(
                request.Identity.Signature,
                "NPC_",
                StringComparison.Ordinal) ||
            request.Identity.FormId.Value == 0)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-identity",
                "The selected record must be one nonzero NPC_ identity."));
        }
        if (!request.ArchiveStagingRoot.IsUnder(
                labRoot) ||
            request.ArchiveStagingRoot ==
                labRoot ||
            !Directory.Exists(
                request.ArchiveStagingRoot.Value) ||
            File.Exists(
                request.ArchiveStagingRoot.Value))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-staging",
                "Archive staging must be an existing strict K-local directory owned by the desktop transaction."));
        }
        else
        {
            ImmutableArray<Diagnostic> pathDiagnostics =
                policy.Evaluate(
                    labRoot,
                    request.ArchiveStagingRoot);
            diagnostics.AddRange(
                pathDiagnostics);
            try
            {
                FileAttributes attributes =
                    File.GetAttributes(
                        request.ArchiveStagingRoot.Value);
                if ((attributes &
                     (FileAttributes.ReparsePoint |
                      FileAttributes.Device)) != 0 ||
                    Directory.EnumerateFileSystemEntries(
                        request.ArchiveStagingRoot.Value)
                        .Any())
                {
                    diagnostics.Add(Error(
                        "facegeom-hair-regions-selected-source-staging",
                        "Archive staging must be an empty ordinary non-reparse directory."));
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-staging",
                    exception.Message));
            }
        }
        return !HasErrors(diagnostics);
    }

    private bool ValidateSelectedHairRegionsReadPath(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot) ||
            !File.Exists(path.Value) ||
            Directory.Exists(path.Value))
        {
            return false;
        }
        ImmutableArray<Diagnostic> pathDiagnostics =
            policy.EvaluateReadRoot(
                labRoot,
                path);
        diagnostics.AddRange(
            pathDiagnostics);
        if (pathDiagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
        {
            return false;
        }
        try
        {
            FileAttributes attributes =
                File.GetAttributes(path.Value);
            if ((attributes &
                 (FileAttributes.Directory |
                  FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-selected-source-reparse",
                    $"The {role} is not an ordinary non-reparse file."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-selected-source-read",
                exception.Message));
            return false;
        }
    }

    private static FaceGeomHairRegionsSelectedSourceResult
        RefusedSelectedHairRegionsSource(
            ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            null,
            diagnostics.ToImmutable());

    internal static bool
        IsSelectedHairRegionsArchiveLengthAdmitted(
            long bytes) =>
        bytes is > 0 and
            <= MaximumHairRegionsCandidateBytes;
}
