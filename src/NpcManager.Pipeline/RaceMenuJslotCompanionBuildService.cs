using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Manufactures the NIF/DDS companions required by the normal preset-selection
/// transaction. Every game-facing artifact is written by Manager services:
/// the temporary ESP by <see cref="INpcCreationService"/> and the pair by
/// <see cref="IFaceGenNpcBakeService"/>.
/// </summary>
public sealed class RaceMenuJslotCompanionBuildService(
    IRaceMenuPresetRecordAuthorityBuilder recordBuilder,
    IRaceMenuNpcStandaloneAuthorityReader standaloneReader,
    ISkyrimFaceMorphSnapshotService faceMorphSnapshotService,
    INpcCreationService npcCreationService,
    IFaceGenNpcBakeService faceGenNpcBakeService,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : IRaceMenuJslotCompanionBuildService
{
    private static readonly FormId TemporaryNpcFormId = new(0x0000_0800);

    public async ValueTask<RaceMenuJslotCompanionBuildResult> BuildAsync(
        RaceMenuJslotCompanionBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(null, null, null, diagnostics);

        RaceMenuNpcStandaloneAuthorityReadResult standalone =
            await standaloneReader.ReadAsync(
                request.CurrentRequest.AssetAuthority,
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, standalone.Diagnostics);
        if (!standalone.Accepted || standalone.Assets is null || HasErrors(diagnostics))
            return Refused(null, null, null, diagnostics);

        RaceMenuNpcNam9TrailingAuthority nam9 = standalone.Assets.Nam9Authority;
        SkyrimFaceMorphSnapshotResult snapshot =
            await faceMorphSnapshotService.ReadAsync(
                new SkyrimFaceMorphSnapshotRequest(
                    GameEdition.SkyrimSpecialEdition,
                    nam9.Plugin,
                    nam9.ExpectedPluginSha256,
                    nam9.NpcFormId),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, snapshot.Diagnostics);
        if (!snapshot.Resolved ||
            snapshot.Snapshot is not { HasNam9: true } morphSnapshot ||
            morphSnapshot.Nam9Trailing != nam9.ExpectedTrailingValue ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error("jslot-companion-nam9-authority",
                "The reviewed template did not reopen with its exact engine-owned NAM9 trailing value."));
            return Refused(null, null, null, diagnostics);
        }

        RaceMenuPresetRecordAuthorityBuildResult record =
            await recordBuilder.BuildAsync(
                request.Preset, request.Target, cancellationToken)
                .ConfigureAwait(false);
        AddDistinct(diagnostics, record.Diagnostics);
        if (!record.Accepted || record.Draft is null || HasErrors(diagnostics))
            return Refused(null, null, null, diagnostics);
        RaceMenuPresetHeadPartAuthority? untypedHeadPart = record.Draft.HeadParts
            .FirstOrDefault(item => item.Binding.HeadPartType is null);
        if (untypedHeadPart is not null)
        {
            diagnostics.Add(Error("jslot-companion-hdpt-authority",
                $"Headpart '{untypedHeadPart.Binding.Reference}' has no non-null typed HDPT authority."));
            return Refused(null, null, null, diagnostics);
        }

        var temporaryPlugin = new PluginName(
            $"NPCM_Jslot_{request.Preset.SourceHash.Value[..12]}.esp");
        var temporaryPluginPath = new WorkspacePath(Path.Combine(
            request.CompanionRoot.Value, "native-bake", "input", "Data",
            temporaryPlugin.Value));
        if (File.Exists(temporaryPluginPath.Value))
        {
            diagnostics.Add(Error("jslot-companion-temp-exists",
                $"Refused to overwrite existing copied-Data plugin '{temporaryPluginPath.Value}'."));
            return Refused(null, null, null, diagnostics);
        }

        NpcCreationResult? temporaryNpc = null;
        FaceGenNpcBakeResult? faceGen = null;
        RaceMenuPresetCompanionExport? companion = null;
        Sha256Hash? ownedTemporaryHash = null;
        try
        {
            Directory.CreateDirectory(request.CompanionRoot.Value);
            EnsureOrdinaryDirectory(request.CompanionRoot.Value, "companion root");
            string companionsDirectory = Path.Combine(
                request.CompanionRoot.Value, "companions");
            Directory.CreateDirectory(companionsDirectory);
            EnsureOrdinaryDirectory(companionsDirectory, "companion directory");
            string nativeInputDataDirectory = Path.GetDirectoryName(
                temporaryPluginPath.Value)!;
            Directory.CreateDirectory(nativeInputDataDirectory);
            EnsureOrdinaryDirectory(nativeInputDataDirectory,
                "native bake input Data directory");

            WorkspacePath templatePlugin =
                request.CurrentRequest.Build.ProviderContext.TemplatePlugin;
            Sha256Hash templatePluginSha256 =
                request.CurrentRequest.Build.ProviderContext.ExpectedTemplatePluginSha256;
            if (request.CurrentRequest.Build.ProviderContext.ProviderResources is
                    { } productResources)
            {
                WorkspacePath? materializedTemplate =
                    MaterializeProductTemplatePlugin(
                        productResources, new WorkspacePath(nativeInputDataDirectory),
                        diagnostics);
                if (materializedTemplate is null)
                    return Refused(null, null, request.CompanionRoot, diagnostics);
                templatePlugin = materializedTemplate.Value;
                templatePluginSha256 =
                    productResources.TemplatePlugin.ExpectedSha256;
            }

            var sourceAuthority = new SkyrimFaceGenSidecarAuthority(
                temporaryPlugin,
                request.PresetPath,
                request.Preset.SourceHash);
            RaceMenuJslotCompanionAppearance mapped =
                RaceMenuJslotCompanionAppearanceMapper.Map(
                    record.Draft,
                    morphSnapshot.Nam9Trailing,
                    temporaryPlugin,
                    TemporaryNpcFormId,
                    sourceAuthority);
            var proposalPath = new WorkspacePath(Path.Combine(
                request.CompanionRoot.Value, "temporary-npc-proposal.json"));
            var creationRequest = new NpcCreationRequest(
                GameEdition.SkyrimSpecialEdition,
                templatePlugin,
                templatePluginSha256,
                request.CurrentRequest.Build.ProviderContext.TemplateNpcFormId,
                proposalPath,
                temporaryPluginPath,
                request.CurrentRequest.Build.Identity,
                request.CurrentRequest.Build.Traits,
                request.CurrentRequest.Build.References,
                mapped.Appearance,
                request.CurrentRequest.Build.Stats with
                {
                    Weight = request.Preset.Appearance.Weight!.Value
                })
            {
                PluginAuthorities = request.Target.PluginOrder
                    .Select(item => new NpcCreationPluginAuthority(
                        item.Plugin, item.Path, item.ExpectedSha256))
                    .ToImmutableArray()
            };
            NpcCreationProposal proposal =
                await npcCreationService.AnalyzeAsync(
                    creationRequest, cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, proposal.Diagnostics);
            if (!proposal.IsApplicable || proposal.AllocatedFormId != TemporaryNpcFormId ||
                HasErrors(diagnostics))
            {
                diagnostics.Add(Error("jslot-companion-temp-proposal",
                    "Manager did not produce an applicable temporary NPC proposal at local FormID 0x800."));
            }
            else
            {
                temporaryNpc = await npcCreationService.ApplyAsync(
                    creationRequest, proposal, cancellationToken).ConfigureAwait(false);
                AddDistinct(diagnostics, temporaryNpc.Diagnostics);
                if (!temporaryNpc.Applied ||
                    temporaryNpc.OutputHash is not { } temporaryHash ||
                    temporaryNpc.Verification is not { IsValid: true } ||
                    HasErrors(diagnostics))
                {
                    diagnostics.Add(Error("jslot-companion-temp-write",
                        "Manager did not write and independently reopen the temporary NPC plugin."));
                }
                else
                {
                    ownedTemporaryHash = temporaryHash;
                    var stagedTemporaryAuthority =
                        new SkyrimFaceRecordPluginAuthority(
                            temporaryPlugin,
                            temporaryPluginPath,
                            temporaryHash);
                    ImmutableArray<PluginName> bakeOrder =
                        request.Target.PluginOrder.Select(item => item.Plugin)
                            .Append(temporaryPlugin)
                            .ToImmutableArray();
                    ImmutableArray<FormReference> bakedHeadParts = record.Draft.HeadParts
                        .Select(item =>
                            item.Binding.HeadPartType == NpcHeadPartType.Face
                                ? new FormReference(temporaryPlugin, new FormId(0x803))
                                : item.Binding.Reference)
                        .ToImmutableArray();
                    var target = new FaceGenBakeTarget(
                        TemporaryNpcFormId,
                        temporaryPlugin,
                        temporaryPlugin,
                        [temporaryPlugin],
                        request.CurrentRequest.Build.Identity.EditorId.Value,
                        request.CurrentRequest.Build.Identity.Name.Value,
                        request.CurrentRequest.Build.Traits.Sex,
                        request.CurrentRequest.Build.References.Race,
                        bakedHeadParts,
                        request.Preset.Appearance.Weight.Value);
                    var bakeData = new WorkspacePath(Path.Combine(
                        request.CompanionRoot.Value, "native-bake", "Data"));
                    var faceGenRequest = new FaceGenNpcBakeRequest(
                        GameEdition.SkyrimSpecialEdition,
                        request.Target.DataRoot,
                        bakeOrder,
                        target,
                        bakeData,
                        mapped.SidecarOverlay)
                    {
                        SkeletonAuthority =
                            request.CurrentRequest
                                .FaceGeomSkeletonAuthority,
                        EffectiveHairColorPackedRgb =
                            record.Draft.HairColorPackedRgb,
                        StagedPluginAuthorities =
                            [stagedTemporaryAuthority],
                        NativeFaceGeomExternalHeadParts =
                            standalone.Assets
                                .NativeFaceGeomExternalHeadParts
                    };
                    if (request.AcceptedExternalHeadPartPrecheck is not null &&
                        faceGenNpcBakeService is not
                            IRaceMenuExternalDescriptorFaceGenNpcBakeService)
                    {
                        diagnostics.Add(Error(
                            ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                            "The native FaceGen baker cannot consume the accepted external-head-part descriptor receipt."));
                    }
                    else
                    {
                        faceGen = request.AcceptedExternalHeadPartPrecheck is { } receipt &&
                                  faceGenNpcBakeService is
                                      IRaceMenuExternalDescriptorFaceGenNpcBakeService typedBaker
                            ? await typedBaker.BakeAsync(
                                faceGenRequest, receipt.Descriptor,
                                cancellationToken).ConfigureAwait(false)
                            : await faceGenNpcBakeService.BakeAsync(
                                faceGenRequest,
                                cancellationToken).ConfigureAwait(false);
                    }
                    if (faceGen is not null)
                        AddDistinct(diagnostics, faceGen.Diagnostics);
                    if (faceGen is null ||
                        faceGen.Status != FaceGenNpcBakeStatus.Baked ||
                        faceGen.Artifact is null || HasErrors(diagnostics))
                    {
                        diagnostics.Add(Error("jslot-companion-native-bake",
                            "Manager's native FaceGen pipeline did not produce an independently reopened NIF/DDS pair."));
                    }
                    else
                    {
                        string stem = Path.GetFileNameWithoutExtension(
                            request.PresetPath.Value);
                        ValidateStem(stem);
                        var copiedPreset = new WorkspacePath(Path.Combine(
                            companionsDirectory, stem + ".jslot"));
                        var copiedNif = new WorkspacePath(Path.Combine(
                            companionsDirectory, stem + ".nif"));
                        var copiedDds = new WorkspacePath(Path.Combine(
                            companionsDirectory, stem + ".dds"));
                        Sha256Hash presetHash = CopyExact(
                            request.PresetPath, request.Preset.SourceHash, copiedPreset);
                        Sha256Hash nifHash = CopyExact(
                            faceGen.Artifact.FaceGeomNif,
                            faceGen.Artifact.FaceGeomSha256,
                            copiedNif);
                        Sha256Hash ddsHash = CopyExact(
                            faceGen.Artifact.FaceTintDds,
                            faceGen.Artifact.FaceTintSha256,
                            copiedDds);
                        companion = new RaceMenuPresetCompanionExport(
                            copiedPreset, presetHash,
                            copiedNif, nifHash,
                            copiedDds, ddsHash);
                        diagnostics.Add(new Diagnostic(
                            "jslot-companion-manager-built",
                            DiagnosticSeverity.Info,
                            "Manager created, hash-reopened, and promoted the JSlot's same-stem NIF/DDS companions; runtime authority remains false."));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException)
        {
            diagnostics.Add(Error("jslot-companion-transaction", exception.Message));
        }
        finally
        {
            if (ownedTemporaryHash is { } expected)
            {
                if (!DeleteExact(temporaryPluginPath, expected, diagnostics))
                    diagnostics.Add(Error("jslot-companion-temp-cleanup",
                        "The transaction-owned temporary plugin could not be hash-deleted."));
            }
            else if (File.Exists(temporaryPluginPath.Value))
            {
                diagnostics.Add(Error("jslot-companion-temp-unowned",
                    "A temporary plugin exists without a retained Manager output hash; it was not deleted."));
            }
        }

        bool completed = companion is not null &&
                         faceGen?.Status == FaceGenNpcBakeStatus.Baked &&
                         temporaryNpc?.Verification is { IsValid: true } &&
                         !File.Exists(temporaryPluginPath.Value) &&
                         !HasErrors(diagnostics);
        return new RaceMenuJslotCompanionBuildResult(
            completed,
            request.CompanionRoot,
            companion,
            temporaryNpc,
            faceGen,
            diagnostics.ToImmutable());
    }

    private void ValidateRequest(
        RaceMenuJslotCompanionBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CurrentRequest.Build.Edition != GameEdition.SkyrimSpecialEdition ||
            request.Preset.Format != PresetFormat.RaceMenuJslot ||
            request.Preset.Edition != GameEdition.SkyrimSpecialEdition ||
            !request.Preset.IsValid ||
            request.Preset.Appearance.RaceMenu is null ||
            request.Preset.Appearance.Weight is null ||
            request.CurrentRequest.Build.References.Race != request.Target.Race ||
            request.CurrentRequest.Build.Traits.Sex != request.Target.Sex)
        {
            diagnostics.Add(Error("jslot-companion-input",
                "Companion creation requires one valid JSlot for the reviewed request's exact Skyrim SE race and sex."));
        }
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.PresetPath));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, request.Target.DataRoot));
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, request.CompanionRoot));
        if (!request.PresetPath.IsUnder(labRoot) ||
            !File.Exists(request.PresetPath.Value) ||
            HashFile(request.PresetPath) != request.Preset.SourceHash)
            diagnostics.Add(Error("jslot-companion-preset-hash",
                "The K-local source JSlot is absent or no longer matches the parsed SHA-256."));
        if (!request.Target.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.Target.DataRoot.Value))
            diagnostics.Add(Error("jslot-companion-data-root",
                "The copied Data root must be an existing directory below the lab root."));
        if (!request.CompanionRoot.IsUnder(labRoot) ||
            request.CompanionRoot == labRoot ||
            Directory.Exists(request.CompanionRoot.Value) ||
            File.Exists(request.CompanionRoot.Value) ||
            request.CompanionRoot.IsUnder(request.Target.DataRoot) ||
            request.Target.DataRoot.IsUnder(request.CompanionRoot) ||
            request.CompanionRoot == request.CurrentRequest.Build.OutputRoot ||
            request.CompanionRoot.IsUnder(
                request.CurrentRequest.Build.OutputRoot) ||
            request.CurrentRequest.Build.OutputRoot.IsUnder(
                request.CompanionRoot))
            diagnostics.Add(Error("jslot-companion-output-root",
                "CompanionRoot must be one absent K-local path disjoint from the copied Data and final output roots."));
    }

    private static Sha256Hash CopyExact(
        WorkspacePath source,
        Sha256Hash expected,
        WorkspacePath destination)
    {
        if (File.Exists(destination.Value))
            throw new InvalidDataException(
                $"Refused to overwrite existing companion '{destination.Value}'.");
        if (HashFile(source) != expected)
            throw new InvalidDataException(
                $"Source '{source.Value}' changed before companion promotion.");
        File.Copy(source.Value, destination.Value, overwrite: false);
        Sha256Hash output = HashFile(destination);
        if (output != expected)
            throw new InvalidDataException(
                $"Promoted companion '{destination.Value}' failed SHA-256 readback.");
        return output;
    }

    private static WorkspacePath? MaterializeProductTemplatePlugin(
        ProviderResourceAuthoritySet resources,
        WorkspacePath nativeInputData,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (resources.TemplatePlugin is not
                    ApplicationProviderResourceAuthority template ||
                template.IsDirectory)
                throw new InvalidDataException(
                    "The product template plugin is not one application-owned file.");
            string directory = Path.Combine(
                nativeInputData.Value, "product-provider");
            Directory.CreateDirectory(directory);
            EnsureOrdinaryDirectory(directory,
                "product-provider template directory");
            var destination = new WorkspacePath(Path.Combine(
                directory, Path.GetFileName(template.Path.Value)));
            if (!destination.IsUnder(nativeInputData) ||
                File.Exists(destination.Value) || Directory.Exists(destination.Value))
                throw new InvalidDataException(
                    "The staged product template destination is unsafe.");
            File.Copy(template.Path.Value, destination.Value, overwrite: false);
            if (HashFile(destination) != template.ExpectedSha256)
                throw new InvalidDataException(
                    "The product template plugin changed during staging.");
            return destination;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(
                "jslot-companion-product-template",
                exception.Message));
            return null;
        }
    }

    private static void ValidateStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem) ||
            stem is "." or ".." ||
            stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The JSlot filename has no safe companion stem.");
    }

    private static void EnsureOrdinaryDirectory(string path, string role)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The {role} is not an ordinary directory.");
    }

    private static bool DeleteExact(
        WorkspacePath path,
        Sha256Hash expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!File.Exists(path.Value)) return true;
            if (HashFile(path) != expected)
            {
                diagnostics.Add(Error("jslot-companion-cleanup-hash",
                    $"Refused to delete '{path.Value}' because its bytes no longer match the Manager transaction."));
                return false;
            }
            File.Delete(path.Value);
            return !File.Exists(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("jslot-companion-cleanup-failed",
                $"Could not delete transaction-owned temporary plugin '{path.Value}': {exception.Message}"));
            return false;
        }
    }

    private static Sha256Hash HashFile(WorkspacePath path)
    {
        using FileStream stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
                target.Add(diagnostic);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuJslotCompanionBuildResult Refused(
        NpcCreationResult? temporaryNpc,
        FaceGenNpcBakeResult? faceGen,
        WorkspacePath? root,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, root, null, temporaryNpc, faceGen, diagnostics.ToImmutable());
}
