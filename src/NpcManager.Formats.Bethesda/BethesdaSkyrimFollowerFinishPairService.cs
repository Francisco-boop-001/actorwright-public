using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Hash-bound finish transaction for two independently accepted
/// Manager-authored actors. Schema 2 preserves the companion package; schema 3
/// may finish its explicitly bound hair/outfit surfaces. Both emit one
/// deterministic runtime package.
/// </summary>
public sealed partial class BethesdaSkyrimFollowerFinishPairService(
    WorkspacePath labRoot,
    ISkyrimFollowerFinishPairFaceGeomService faceGeomService) :
    ISkyrimFollowerFinishPairService
{
    private const uint EslFlag = 0x200;
    private const ushort RecordFormVersion = 44;
    private const long MaximumDocumentBytes = 1024 * 1024;
    private static readonly DateTimeOffset ArchiveTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    public async ValueTask<SkyrimFollowerFinishPairResult> AnalyzeAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateNewOutput(proposalPath, "proposal");
            SkyrimFollowerFinishPairRequest request =
                await LoadRequestAsync(
                    requestPath,
                    requestSha256,
                    cancellationToken).ConfigureAwait(false);
            ValidateRequest(request, prewrite: true);
            ValidateBoundFiles(request);
            SkyrimFollowerFinishPairSourceSnapshot companion =
                InspectActor(request.Companion);
            SkyrimFollowerFinishPairSourceSnapshot subject =
                InspectActor(request.Subject);
            ValidateSourceSemantics(request, companion, subject);
            ValidateOutfitAuthority(request.Outfit);
            ValidateCompanionFinishAuthority(request, companion);

            ImmutableArray<PluginName> masters =
                BuildOutputMasters(subject.Masters, request);
            SkyrimFollowerFinishPairProposal proposal =
                BuildProposal(
                    request,
                    requestSha256,
                    companion,
                    subject,
                    masters);
            await WriteNewJsonAsync(
                proposalPath,
                proposal,
                cancellationToken).ConfigureAwait(false);
            Sha256Hash proposalHash = HashFile(proposalPath.Value);
            return Success(
                "READY_FOR_REVIEWED_WRITE",
                proposalPath,
                proposalHash,
                diagnostics:
                [
                    Info(
                        "follower-finish-pair-proposal-bound",
                        "The paired proposal binds both accepted actor packages, the external anchor, the exact outfit providers, and the closed reviewed change surface.")
                ]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAdmittedFailure(exception))
        {
            return Refused(
                "follower-finish-pair-analyze-refused",
                exception.Message);
        }
    }

    public async ValueTask<SkyrimFollowerFinishPairResult> ApplyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        string? stageRoot = null;
        string? archiveStageOne = null;
        string? archiveStageTwo = null;
        try
        {
            SkyrimFollowerFinishPairRequest request =
                await LoadRequestAsync(
                    requestPath,
                    requestSha256,
                    cancellationToken).ConfigureAwait(false);
            ValidateRequest(request, prewrite: true);
            SkyrimFollowerFinishPairProposal proposal =
                await LoadProposalAsync(
                    proposalPath,
                    proposalSha256,
                    cancellationToken).ConfigureAwait(false);
            ValidateProposal(proposal, request, requestSha256);
            ValidateBoundFiles(request);
            ValidateSourceSemantics(
                request,
                InspectActor(request.Companion),
                InspectActor(request.Subject));
            ValidateOutfitAuthority(request.Outfit);
            ValidateCompanionFinishAuthority(
                request,
                InspectActor(request.Companion));

            stageRoot = request.OutputRoot.Value +
                        $".stage-{Guid.NewGuid():N}";
            ValidateTransactionOwnedPath(stageRoot);
            Directory.CreateDirectory(stageRoot);
            WorkspacePath outputPlugin = new(Path.Combine(
                stageRoot,
                "Data",
                request.Subject.Plugin.Value));
            Directory.CreateDirectory(
                Path.GetDirectoryName(outputPlugin.Value)!);
            WriteSubjectPlugin(request, proposal, outputPlugin);
            VerifySubjectPlugin(request, proposal, outputPlugin);

            WorkspacePath outputCompanionPlugin =
                CompanionOutputPlugin(request, stageRoot);
            WorkspacePath outputCompanionFaceGeom =
                CompanionOutputFaceGeom(request, stageRoot);
            if (request.CompanionFinish is not null)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(
                        outputCompanionPlugin.Value)!);
                WriteCompanionPlugin(
                    request,
                    proposal,
                    outputCompanionPlugin);
                VerifyCompanionPlugin(
                    request,
                    proposal,
                    outputCompanionPlugin);
                WriteCompanionFaceGeom(
                    request,
                    outputCompanionFaceGeom);
                VerifyCompanionFaceGeom(
                    request,
                    outputCompanionFaceGeom);
            }

            CopyRuntimePayload(request, stageRoot);
            await WritePackageDocumentsAsync(
                request,
                proposal,
                requestPath,
                requestSha256,
                proposalPath,
                proposalSha256,
                outputPlugin,
                outputCompanionPlugin,
                outputCompanionFaceGeom,
                stageRoot,
                cancellationToken).ConfigureAwait(false);
            WorkspacePath manifest = new(Path.Combine(
                stageRoot,
                "npcmanager-paired-package.json"));
            await WriteManifestAsync(
                request,
                stageRoot,
                manifest,
                cancellationToken).ConfigureAwait(false);
            VerifyPackageTree(request, proposal, stageRoot, manifest);

            archiveStageOne = request.OutputZip.Value +
                              $".stage-{Guid.NewGuid():N}";
            archiveStageTwo = request.OutputZip.Value +
                              $".repeat-{Guid.NewGuid():N}";
            ValidateTransactionOwnedPath(archiveStageOne);
            ValidateTransactionOwnedPath(archiveStageTwo);
            CreateDeterministicArchive(stageRoot, archiveStageOne);
            CreateDeterministicArchive(stageRoot, archiveStageTwo);
            Sha256Hash firstArchiveHash = HashFile(archiveStageOne);
            Sha256Hash secondArchiveHash = HashFile(archiveStageTwo);
            if (firstArchiveHash != secondArchiveHash)
                Refuse(
                    "follower-finish-pair-archive-nondeterministic",
                    "A repeat archive build was not byte-identical.");

            Directory.Move(stageRoot, request.OutputRoot.Value);
            stageRoot = null;
            File.Move(archiveStageOne, request.OutputZip.Value);
            archiveStageOne = null;
            File.Delete(archiveStageTwo);
            archiveStageTwo = null;

            WorkspacePath finalPlugin = new(Path.Combine(
                request.OutputRoot.Value,
                "Data",
                request.Subject.Plugin.Value));
            WorkspacePath finalManifest = new(Path.Combine(
                request.OutputRoot.Value,
                "npcmanager-paired-package.json"));
            return new SkyrimFollowerFinishPairResult(
                true,
                "STATIC_PASS_RUNTIME_REQUIRED",
                proposalPath,
                proposalSha256,
                finalManifest,
                HashFile(finalManifest.Value),
                finalPlugin,
                HashFile(finalPlugin.Value),
                request.OutputZip,
                HashFile(request.OutputZip.Value),
                [
                    Info(
                        "follower-finish-pair-static-pass",
                        "Both light plugins and the closed paired package passed static verification. Runtime appearance, outfit fit, placement, and follower behavior remain required.")
                ],
                RuntimeAuthority: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAdmittedFailure(exception))
        {
            DeleteTransactionPath(stageRoot);
            DeleteTransactionFile(archiveStageOne);
            DeleteTransactionFile(archiveStageTwo);
            return Refused(
                "follower-finish-pair-apply-refused",
                exception.Message);
        }
    }

    public async ValueTask<SkyrimFollowerFinishPairResult> VerifyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        WorkspacePath manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            SkyrimFollowerFinishPairRequest request =
                await LoadRequestAsync(
                    requestPath,
                    requestSha256,
                    cancellationToken).ConfigureAwait(false);
            ValidateRequest(request, prewrite: false);
            SkyrimFollowerFinishPairProposal proposal =
                await LoadProposalAsync(
                    proposalPath,
                    proposalSha256,
                    cancellationToken).ConfigureAwait(false);
            ValidateProposal(proposal, request, requestSha256);
            WorkspacePath expectedManifest = new(Path.Combine(
                request.OutputRoot.Value,
                "npcmanager-paired-package.json"));
            if (manifestPath != expectedManifest)
                Refuse(
                    "follower-finish-pair-manifest-path",
                    "Verification must target the transaction-owned paired manifest.");
            WorkspacePath outputPlugin = new(Path.Combine(
                request.OutputRoot.Value,
                "Data",
                request.Subject.Plugin.Value));
            VerifySubjectPlugin(request, proposal, outputPlugin);
            WorkspacePath outputCompanionPlugin =
                CompanionOutputPlugin(
                    request,
                    request.OutputRoot.Value);
            WorkspacePath outputCompanionFaceGeom =
                CompanionOutputFaceGeom(
                    request,
                    request.OutputRoot.Value);
            if (request.CompanionFinish is not null)
            {
                VerifyCompanionPlugin(
                    request,
                    proposal,
                    outputCompanionPlugin);
                VerifyCompanionFaceGeom(
                    request,
                    outputCompanionFaceGeom);
            }
            VerifyPackageTree(
                request,
                proposal,
                request.OutputRoot.Value,
                manifestPath);
            VerifyArchive(request.OutputZip, request.OutputRoot.Value);
            return new SkyrimFollowerFinishPairResult(
                true,
                "STATIC_PASS_RUNTIME_REQUIRED",
                proposalPath,
                proposalSha256,
                manifestPath,
                HashFile(manifestPath.Value),
                outputPlugin,
                HashFile(outputPlugin.Value),
                request.OutputZip,
                HashFile(request.OutputZip.Value),
                [
                    Info(
                        "follower-finish-pair-verify-pass",
                        "Independent reopen and package inventory checks passed; runtime authority remains false.")
                ],
                RuntimeAuthority: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAdmittedFailure(exception))
        {
            return Refused(
                "follower-finish-pair-verify-refused",
                exception.Message);
        }
    }

    private void ValidateRequest(
        SkyrimFollowerFinishPairRequest request,
        bool prewrite)
    {
        bool legacy =
            request.SchemaVersion ==
                SkyrimFollowerFinishPairRequest
                    .LegacySchemaVersionValue &&
            request.CompanionFinish is null;
        bool current =
            request.SchemaVersion ==
                SkyrimFollowerFinishPairRequest.SchemaVersionValue &&
            request.CompanionFinish is not null;
        if ((!legacy && !current) ||
            !string.Equals(
                request.Operation,
                SkyrimFollowerFinishPairRequest.OperationName,
                StringComparison.Ordinal))
            Refuse(
                "follower-finish-pair-schema",
                "The request is not the closed schema-2 or schema-3 paired operation.");
        if (!string.Equals(
                request.Companion.Role,
                "companion",
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Subject.Role,
                "subject",
                StringComparison.Ordinal) ||
            request.Companion.Plugin == request.Subject.Plugin)
            Refuse(
                "follower-finish-pair-roles",
                "The transaction requires distinct companion and subject actors.");
        if (request.Companion.ActorFormId.Value is < 0x800 or > 0xFFF ||
            request.Subject.ActorFormId.Value is < 0x800 or > 0xFFF)
            Refuse(
                "follower-finish-pair-actor-id",
                "Both accepted actor IDs must be inside the ESL object range.");
        if (request.CompanionAnchor.Plugin !=
                request.Companion.Plugin ||
            request.CompanionAnchor.FormId.Value is < 0x800 or > 0xFFF)
            Refuse(
                "follower-finish-pair-anchor",
                "The shared anchor must be owned by the admitted companion plugin.");
        if (request.Outfit.Plugin !=
                request.Outfit.TorsoArmor.Plugin ||
            request.Outfit.Plugin !=
                request.Outfit.TorsoArmorAddon.Plugin ||
            request.Outfit.Plugin != request.Outfit.Boots.Plugin ||
            request.Outfit.Plugin != request.Outfit.Gauntlets.Plugin ||
            request.Outfit.TargetFemaleSkinTextureSet.Plugin ==
                request.Outfit.Plugin)
            Refuse(
                "follower-finish-pair-outfit-routing",
                "The selected outfit forms and target skin route are not explicitly separated.");
        if (!request.Allocation.Equals(
                SkyrimFollowerFinishPairAllocation.Default) ||
            request.Allocation.NewRecordIds.Distinct().Count() !=
                request.Allocation.NewRecordIds.Length ||
            request.Allocation.NewRecordIds.Any(id =>
                id.Value is < 0x800 or > 0xFFF) ||
            request.Allocation.NextFormId.Value != 0x80C)
            Refuse(
                "follower-finish-pair-allocation",
                "The schema-2 allocation must be the closed 0x805..0x80B sequence.");
        if (request.CompanionFinish is { } finish &&
            (!finish.Allocation.Equals(
                    SkyrimFollowerFinishPairCompanionAllocation.Default) ||
             finish.Allocation.NewRecordIds.Distinct().Count() !=
                finish.Allocation.NewRecordIds.Length ||
             finish.Allocation.NewRecordIds.Any(id =>
                id.Value is < 0x800 or > 0xFFF) ||
             finish.Allocation.NextFormId.Value != 0x80B ||
             finish.Hair.ColorFormId.Value is < 0x800 or > 0xFFF ||
             finish.Hair.OldPackedRgb > 0xFFFFFF ||
             finish.Hair.NewPackedRgb > 0xFFFFFF ||
             finish.Hair.OldPackedRgb ==
                finish.Hair.NewPackedRgb ||
             finish.Hair.FaceGeomShapeNames.IsDefaultOrEmpty ||
             finish.Hair.FaceGeomShapeNames.Length > 16 ||
             finish.Hair.FaceGeomShapeNames
                .Distinct(StringComparer.Ordinal).Count() !=
                finish.Hair.FaceGeomShapeNames.Length ||
             finish.Hair.OldFaceGeomRgb.Length != 3 ||
             finish.Hair.NewFaceGeomRgb.Length != 3 ||
             finish.Hair.OldFaceGeomRgb.SequenceEqual(
                 finish.Hair.NewFaceGeomRgb)))
            Refuse(
                "follower-finish-pair-companion-finish",
                "The schema-3 companion finish is outside the closed hair/outfit surface.");
        if (string.IsNullOrWhiteSpace(request.Narrative) ||
            request.Narrative.Length > 2048)
            Refuse(
                "follower-finish-pair-narrative",
                "A bounded paired-release narrative is required.");

        ValidateKPath(request.OutputRoot, allowMissingLeaf: true);
        ValidateKPath(request.OutputZip, allowMissingLeaf: true);
        string expectedParent =
            Path.GetDirectoryName(request.OutputZip.Value) ??
            string.Empty;
        if (!request.OutputRoot.IsUnder(labRoot) ||
            !request.OutputZip.IsUnder(labRoot) ||
            !string.Equals(
                Path.GetExtension(request.OutputZip.Value),
                ".zip",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                expectedParent,
                Path.Combine(
                    labRoot.Value,
                    ".actorwright",
                    "04-packages"),
                StringComparison.OrdinalIgnoreCase))
            Refuse(
                "follower-finish-pair-output-boundary",
                "Paired outputs must remain in the project build/package roots.");
        if (prewrite &&
            (File.Exists(request.OutputZip.Value) ||
             Directory.Exists(request.OutputZip.Value) ||
             File.Exists(request.OutputRoot.Value) ||
             Directory.Exists(request.OutputRoot.Value)))
            Refuse(
                "follower-finish-pair-output-exists",
                "Paired writes never overwrite an existing output.");
        if (!prewrite &&
            (!File.Exists(request.OutputZip.Value) ||
             !Directory.Exists(request.OutputRoot.Value)))
            Refuse(
                "follower-finish-pair-output-missing",
                "The paired output root or archive is missing.");
    }

    private void ValidateBoundFiles(
        SkyrimFollowerFinishPairRequest request)
    {
        foreach (SkyrimFollowerFinishPairFile file in
                 EnumerateBoundFiles(request))
        {
            ValidateKPath(file.Path, allowMissingLeaf: false);
            if (!File.Exists(file.Path.Value) ||
                Directory.Exists(file.Path.Value))
                Refuse(
                    "follower-finish-pair-bound-file-missing",
                    $"A bound authority file is missing: {file.Path.Value}");
            var info = new FileInfo(file.Path.Value);
            if (info.Length != file.ByteLength ||
                HashFile(file.Path.Value) != file.Sha256)
                Refuse(
                    "follower-finish-pair-bound-file-drift",
                    $"A bound authority file changed: {file.Path.Value}");
        }
        foreach (SkyrimFollowerFinishPairActor actor in
                 new[] { request.Companion, request.Subject })
        {
            ValidateKPath(actor.PackageRoot, allowMissingLeaf: false);
            if (!Directory.Exists(actor.PackageRoot.Value) ||
                !actor.PluginFile.Path.IsUnder(actor.PackageRoot) ||
                !actor.FaceGeom.Path.IsUnder(actor.PackageRoot) ||
                !actor.FaceTint.Path.IsUnder(actor.PackageRoot) ||
                !actor.BodyGenTemplates.Path.IsUnder(actor.PackageRoot) ||
                !actor.BodyGenMorphs.Path.IsUnder(actor.PackageRoot) ||
                !actor.RuntimeScript.Path.IsUnder(actor.PackageRoot) ||
                !string.Equals(
                    Path.GetFileName(actor.PluginFile.Path.Value),
                    actor.Plugin.Value,
                    StringComparison.Ordinal))
                Refuse(
                    "follower-finish-pair-actor-closure",
                    $"The {actor.Role} runtime files are not a closed package-root subset.");
        }
        if (request.Companion.RuntimeScript.Sha256 !=
            request.Subject.RuntimeScript.Sha256)
            Refuse(
                "follower-finish-pair-script-mismatch",
                "The shared runtime script differs between accepted actor packages.");
    }

    private static IEnumerable<SkyrimFollowerFinishPairFile>
        EnumerateBoundFiles(
            SkyrimFollowerFinishPairRequest request)
    {
        foreach (SkyrimFollowerFinishPairActor actor in
                 new[] { request.Companion, request.Subject })
        {
            yield return actor.PluginFile;
            yield return actor.FaceGeom;
            yield return actor.FaceTint;
            yield return actor.BodyGenTemplates;
            yield return actor.BodyGenMorphs;
            yield return actor.RuntimeScript;
        }
        yield return request.Outfit.PluginFile;
        yield return request.Outfit.BaseMeshArchive;
        yield return request.Outfit.WinningFemaleMeshArchive;
        yield return request.Outfit.TextureArchive;
    }

    private static SkyrimFollowerFinishPairSourceSnapshot InspectActor(
        SkyrimFollowerFinishPairActor actor)
    {
        ModKey key = ModKey.FromNameAndExtension(actor.Plugin.Value);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                key,
                new FilePath(actor.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        INpcGetter npc = mod.Npcs.Single(record =>
            record.FormKey == new FormKey(
                key,
                actor.ActorFormId.Value));
        ImmutableArray<string> inventory =
            mod.EnumerateMajorRecords()
                .Where(record => record.FormKey.ModKey == key)
                .Select(record =>
                    $"{Signature(record)} 0x{record.FormKey.ID:X8}")
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
        return new SkyrimFollowerFinishPairSourceSnapshot(
            actor.Plugin,
            actor.PluginFile.Sha256,
            (uint)mod.ModHeader.Flags,
            mod.ModHeader.MasterReferences
                .Select(master =>
                    new PluginName(master.Master.ToString()))
                .ToImmutableArray(),
            new FormId(mod.ModHeader.Stats.NextFormID),
            inventory,
            ToReference(npc.Race.FormKey),
            npc.HairColor.FormKeyNullable is { } hair
                ? ToReference(hair)
                : null,
            npc.HeadTexture.FormKeyNullable is { } face
                ? ToReference(face)
                : null,
            npc.DefaultOutfit.FormKeyNullable is { } outfit
                ? ToReference(outfit)
                : null,
            npc.Packages.Select(link =>
                ToReference(link.FormKey)).ToImmutableArray());
    }

    private static void ValidateSourceSemantics(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairSourceSnapshot companion,
        SkyrimFollowerFinishPairSourceSnapshot subject)
    {
        if (companion.Plugin != request.Companion.Plugin ||
            companion.PluginSha256 !=
                request.Companion.PluginFile.Sha256 ||
            (companion.Tes4Flags & EslFlag) == 0 ||
            companion.Packages.Length != 1)
            Refuse(
                "follower-finish-pair-companion-source",
                "The accepted companion is not the expected light follower.");
        if (subject.Plugin != request.Subject.Plugin ||
            subject.PluginSha256 != request.Subject.PluginFile.Sha256 ||
            (subject.Tes4Flags & EslFlag) != 0 ||
            subject.NextFormId !=
                request.Allocation.PrivateArmorAddon ||
            subject.DefaultOutfit is not null ||
            !subject.Packages.IsDefaultOrEmpty)
            Refuse(
                "follower-finish-pair-subject-source",
                "The accepted subject is not the expected undressed, unplaced appearance source.");
        if (subject.RecordInventory.IsDefaultOrEmpty ||
            subject.RecordInventory.Any(row =>
                ParseInventoryId(row) >=
                request.Allocation.PrivateArmorAddon.Value) ||
            !subject.RecordInventory.Contains(
                $"NPC_ 0x{request.Subject.ActorFormId.Value:X8}",
                StringComparer.Ordinal))
            Refuse(
                "follower-finish-pair-subject-inventory",
                "The accepted subject inventory is empty, collides with the allocation, or omits the admitted actor.");
        ModKey companionKey =
            ModKey.FromNameAndExtension(request.Companion.Plugin.Value);
        using var companionMod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                companionKey,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        FormReference companionPackage =
            companion.Packages.Single();
        if (companionPackage.Plugin !=
                request.Companion.Plugin ||
            !companionMod.EnumerateMajorRecords()
                .OfType<IPlacedObjectGetter>().Any(record =>
                record.FormKey ==
                new FormKey(
                    companionKey,
                    request.CompanionAnchor.FormId.Value)) ||
            !companionMod.Packages.Any(record =>
                record.FormKey ==
                ToFormKey(companionPackage)))
            Refuse(
                "follower-finish-pair-companion-anchor",
                "The companion plugin does not own the admitted anchor and Sandbox package.");
    }

    private static void ValidateOutfitAuthority(
        SkyrimFollowerFinishPairOutfit outfit)
    {
        ModKey key = ModKey.FromNameAndExtension(outfit.Plugin.Value);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                key,
                new FilePath(outfit.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IArmorGetter torso = mod.Armors.Single(record =>
            record.FormKey == ToFormKey(outfit.TorsoArmor));
        IArmorAddonGetter addon = mod.ArmorAddons.Single(record =>
            record.FormKey == ToFormKey(outfit.TorsoArmorAddon));
        _ = mod.Armors.Single(record =>
            record.FormKey == ToFormKey(outfit.Boots));
        _ = mod.Armors.Single(record =>
            record.FormKey == ToFormKey(outfit.Gauntlets));
        if (torso.Armature.Count(link =>
                link.FormKey == addon.FormKey) != 1 ||
            addon.SkinTexture?.Female?.FormKeyNullable is not null ||
            string.IsNullOrWhiteSpace(
                addon.WorldModel?.Female?.File))
            Refuse(
                "follower-finish-pair-outfit-shape",
                "The selected torso does not expose exactly one admitted female mesh route without a prebound female skin texture.");
        if (outfit.WinningFemaleMeshArchive.Sha256 ==
            outfit.BaseMeshArchive.Sha256)
            Refuse(
                "follower-finish-pair-outfit-provider",
                "The winning female mesh provider was not distinguished from the base archive.");
    }

    private static ImmutableArray<PluginName> BuildOutputMasters(
        ImmutableArray<PluginName> source,
        SkyrimFollowerFinishPairRequest request)
    {
        if (source.Contains(request.Companion.Plugin) ||
            source.Contains(request.Outfit.Plugin))
            Refuse(
                "follower-finish-pair-master-preexisting",
                "A direct pair/outfit master already exists in the accepted subject.");
        return source
            .Add(request.Companion.Plugin)
            .Add(request.Outfit.Plugin);
    }

    private static ImmutableArray<PluginName>
        BuildCompanionOutputMasters(
            ImmutableArray<PluginName> source,
            SkyrimFollowerFinishPairRequest request)
    {
        if (request.CompanionFinish is null)
            return source;
        if (source.Contains(request.Outfit.Plugin))
            Refuse(
                "follower-finish-pair-companion-master-preexisting",
                "The outfit master already exists in the accepted companion.");
        return source.Add(request.Outfit.Plugin);
    }

    private static SkyrimFollowerFinishPairProposal BuildProposal(
        SkyrimFollowerFinishPairRequest request,
        Sha256Hash requestSha256,
        SkyrimFollowerFinishPairSourceSnapshot companion,
        SkyrimFollowerFinishPairSourceSnapshot subject,
        ImmutableArray<PluginName> outputMasters)
    {
        ImmutableArray<string>.Builder existing =
            ImmutableArray.CreateBuilder<string>();
        existing.Add(
            "TES4: append companion/outfit masters and set ESL flag without compaction");
        existing.Add(
            $"NPC_ 0x{request.Subject.ActorFormId.Value:X8}: add one PKID and one output-owned default OTFT");
        ImmutableArray<string>.Builder records =
            ImmutableArray.CreateBuilder<string>();
        records.Add(
            $"ARMA 0x{request.Allocation.PrivateArmorAddon.Value:X8}: private exposed-torso skin binding");
        records.Add(
            $"ARMO 0x{request.Allocation.PrivateArmor.Value:X8}: private admitted torso");
        records.Add(
            $"OTFT 0x{request.Allocation.Outfit.Value:X8}: torso, boots, gauntlets");
        records.Add(
            $"PACK 0x{request.Allocation.Package.Value:X8}: companion-anchor Sandbox");
        records.Add(
            $"ACHR 0x{request.Allocation.PlacedActor.Value:X8}: adjacent subject placement");
        records.Add(
            $"RELA 0x{request.Allocation.SubjectToCompanionRelationship.Value:X8}: subject-to-companion Ally");
        records.Add(
            $"RELA 0x{request.Allocation.CompanionToSubjectRelationship.Value:X8}: companion-to-subject Ally");
        if (request.CompanionFinish is { } finish)
        {
            existing.Add(
                $"Companion TES4: append {request.Outfit.Plugin.Value} and advance NextFormID to 0x{finish.Allocation.NextFormId.Value:X8}");
            existing.Add(
                $"Companion CLFM 0x{finish.Hair.ColorFormId.Value:X8}: 0x{finish.Hair.OldPackedRgb:X6} -> 0x{finish.Hair.NewPackedRgb:X6}");
            existing.Add(
                $"Companion NPC_ 0x{request.Companion.ActorFormId.Value:X8}: add one output-owned default OTFT");
            existing.Add(
                $"Companion FaceGeom: rewrite HairTint on {string.Join(", ", finish.Hair.FaceGeomShapeNames)}");
            records.Add(
                $"Companion ARMA 0x{finish.Allocation.PrivateArmorAddon.Value:X8}: private exposed-torso skin binding");
            records.Add(
                $"Companion ARMO 0x{finish.Allocation.PrivateArmor.Value:X8}: private admitted torso");
            records.Add(
                $"Companion OTFT 0x{finish.Allocation.Outfit.Value:X8}: torso, boots, gauntlets");
        }
        return new(
            request.SchemaVersion,
            SkyrimFollowerFinishPairRequest.OperationName,
            requestSha256,
            request,
            companion,
            subject,
            outputMasters,
            existing.ToImmutable(),
            records.ToImmutable(),
            RuntimeAuthority: false,
            request.CompanionFinish is null
                ? null
                : BuildCompanionOutputMasters(
                    companion.Masters,
                    request));
    }

    private static void ValidateProposal(
        SkyrimFollowerFinishPairProposal proposal,
        SkyrimFollowerFinishPairRequest request,
        Sha256Hash requestSha256)
    {
        if (proposal.SchemaVersion != request.SchemaVersion ||
            proposal.Operation !=
                SkyrimFollowerFinishPairRequest.OperationName ||
            proposal.RequestSha256 != requestSha256 ||
            !JsonSerializer.SerializeToUtf8Bytes(
                    proposal.Request,
                    JsonOptions)
                .SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(
                        request,
                        JsonOptions)) ||
            proposal.RuntimeAuthority ||
            !proposal.OutputMasters.SequenceEqual(
                BuildOutputMasters(
                    proposal.SubjectSnapshot.Masters,
                    request)) ||
            (request.CompanionFinish is null
                ? proposal.CompanionOutputMasters is not null
                : proposal.CompanionOutputMasters is not
                    { } companionMasters ||
                  !companionMasters.SequenceEqual(
                      BuildCompanionOutputMasters(
                          proposal.CompanionSnapshot.Masters,
                          request))))
            Refuse(
                "follower-finish-pair-proposal-binding",
                "The proposal does not bind the exact request and closed master plan.");
    }

    private static void WriteSubjectPlugin(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        WorkspacePath output)
    {
        ModKey subjectKey =
            ModKey.FromNameAndExtension(request.Subject.Plugin.Value);
        ModKey companionKey =
            ModKey.FromNameAndExtension(request.Companion.Plugin.Value);
        ModKey outfitKey =
            ModKey.FromNameAndExtension(request.Outfit.Plugin.Value);
        using var subjectOverlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                subjectKey,
                new FilePath(
                    request.Subject.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        var mod = (SkyrimMod)subjectOverlay.DeepCopy();
        mod.IsSmallMaster = true;
        mod.IsMaster = false;
        mod.ModHeader.Stats.NextFormID =
            request.Allocation.NextFormId.Value;
        foreach (PluginName master in
                 proposal.OutputMasters.Skip(
                     proposal.SubjectSnapshot.Masters.Length))
        {
            mod.ModHeader.MasterReferences.Add(
                new MasterReference
                {
                    Master =
                        ModKey.FromNameAndExtension(master.Value)
                });
        }

        using var outfit = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                outfitKey,
                new FilePath(
                    request.Outfit.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IArmorAddonGetter sourceAddon =
            outfit.ArmorAddons.Single(record =>
                record.FormKey ==
                ToFormKey(request.Outfit.TorsoArmorAddon));
        FormKey privateAddonKey = new(
            subjectKey,
            request.Allocation.PrivateArmorAddon.Value);
        ArmorAddon privateAddon =
            mod.ArmorAddons.DuplicateInAsNewRecord(
                sourceAddon,
                privateAddonKey);
        privateAddon.FormVersion = RecordFormVersion;
        privateAddon.EditorID =
            BuildEditorId(
                request.Subject.Plugin,
                "PairPrivateTorsoAA");
        privateAddon.SkinTexture =
            new GenderedItem<
                IFormLinkNullableGetter<ITextureSetGetter>>(
                privateAddon.SkinTexture?.Male ??
                new FormLinkNullable<ITextureSetGetter>(),
                new FormLinkNullable<ITextureSetGetter>(
                    ToFormKey(
                        request.Outfit
                            .TargetFemaleSkinTextureSet)));

        IArmorGetter sourceArmor = outfit.Armors.Single(record =>
            record.FormKey ==
            ToFormKey(request.Outfit.TorsoArmor));
        FormKey privateArmorKey = new(
            subjectKey,
            request.Allocation.PrivateArmor.Value);
        Armor privateArmor = mod.Armors.DuplicateInAsNewRecord(
            sourceArmor,
            privateArmorKey);
        privateArmor.FormVersion = RecordFormVersion;
        privateArmor.EditorID =
            BuildEditorId(
                request.Subject.Plugin,
                "PairPrivateTorso");
        FormKey sourceAddonKey =
            ToFormKey(request.Outfit.TorsoArmorAddon);
        FormKey[] armatures = sourceArmor.Armature
            .Select(link => link.FormKey == sourceAddonKey
                ? privateAddonKey
                : link.FormKey)
            .ToArray();
        if (armatures.Count(key => key == privateAddonKey) != 1)
            Refuse(
                "follower-finish-pair-private-armor",
                "The private armor could not replace exactly one admitted torso ARMA.");
        privateArmor.Armature.Clear();
        foreach (FormKey armature in armatures)
            privateArmor.Armature.Add(
                new FormLink<IArmorAddonGetter>(armature));

        FormKey outfitFormKey = new(
            subjectKey,
            request.Allocation.Outfit.Value);
        var ownedOutfit = new Outfit(
            outfitFormKey,
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = RecordFormVersion,
            EditorID =
                BuildEditorId(
                    request.Subject.Plugin,
                    "PairOutfit"),
            Items =
            [
                new FormLink<IOutfitTargetGetter>(
                    privateArmorKey),
                new FormLink<IOutfitTargetGetter>(
                    ToFormKey(request.Outfit.Boots)),
                new FormLink<IOutfitTargetGetter>(
                    ToFormKey(request.Outfit.Gauntlets))
            ]
        };
        mod.Outfits.Add(ownedOutfit);

        using var companion = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                companionKey,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IPackageGetter sourcePackage =
            companion.Packages.Single(record =>
                record.FormKey ==
                ToFormKey(
                    proposal.CompanionSnapshot.Packages.Single()));
        FormKey packageKey = new(
            subjectKey,
            request.Allocation.Package.Value);
        Package package = mod.Packages.DuplicateInAsNewRecord(
            sourcePackage,
            packageKey);
        package.FormVersion = RecordFormVersion;
        package.EditorID =
            BuildEditorId(
                request.Subject.Plugin,
                "PairSandbox");

        Npc npc = mod.Npcs.Single(record =>
            record.FormKey ==
            new FormKey(
                subjectKey,
                request.Subject.ActorFormId.Value));
        npc.DefaultOutfit =
            new FormLinkNullable<IOutfitGetter>(outfitFormKey);
        npc.Packages.Clear();
        npc.Packages.Add(
            new FormLink<IPackageGetter>(packageKey));

        mod.Relationships.Add(BuildPairRelationship(
            subjectKey,
            request.Allocation
                .SubjectToCompanionRelationship.Value,
            BuildEditorId(
                request.Subject.Plugin,
                $"{EditorIdStem(request.Companion.Plugin)}AllyRELA"),
            new FormKey(
                subjectKey,
                request.Subject.ActorFormId.Value),
            new FormKey(
                companionKey,
                request.Companion.ActorFormId.Value)));
        mod.Relationships.Add(BuildPairRelationship(
            subjectKey,
            request.Allocation
                .CompanionToSubjectRelationship.Value,
            BuildEditorId(
                request.Companion.Plugin,
                $"{EditorIdStem(request.Subject.Plugin)}AllyRELA"),
            new FormKey(
                companionKey,
                request.Companion.ActorFormId.Value),
            new FormKey(
                subjectKey,
                request.Subject.ActorFormId.Value)));
        AddSubjectPlacement(mod, request);

        mod.WriteToBinary(
            new FilePath(output.Value),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                MastersListOrdering =
                    MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
        SkyrimStructuralWorldspaceRecordSanitizer
            .RemoveRequiredPartialMasterRecord(
                output.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        SkyrimStructuralWorldspaceRecordSanitizer
            .MinimizeRequiredExteriorCellRecord(
                output.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value,
                request.Placement.Cell.FormId.Value,
                request.Placement.CellGridX,
                request.Placement.CellGridY);
    }

    private static Relationship BuildPairRelationship(
        ModKey owner,
        uint localId,
        string editorId,
        FormKey parent,
        FormKey child) =>
        new(
            new FormKey(owner, localId),
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = RecordFormVersion,
            MajorRecordFlagsRaw = 0,
            EditorID = editorId,
            Parent = new FormLink<INpcGetter>(parent),
            Child = new FormLink<INpcGetter>(child),
            Rank = Relationship.RankType.Ally,
            Unknown = 0,
            Flags = 0,
            AssociationType =
                new FormLink<IAssociationTypeGetter>(FormKey.Null)
        };

    private static void AddSubjectPlacement(
        SkyrimMod mod,
        SkyrimFollowerFinishPairRequest request)
    {
        var world = new Worldspace(
            ToFormKey(request.Placement.Worldspace),
            SkyrimRelease.SkyrimSE);
        var block = new WorldspaceBlock
        {
            BlockNumberX = FloorDivide(
                request.Placement.CellGridX,
                32),
            BlockNumberY = FloorDivide(
                request.Placement.CellGridY,
                32),
            GroupType = (GroupTypeEnum)4
        };
        var subBlock = new WorldspaceSubBlock
        {
            BlockNumberX = FloorDivide(
                request.Placement.CellGridX,
                8),
            BlockNumberY = FloorDivide(
                request.Placement.CellGridY,
                8),
            GroupType = (GroupTypeEnum)5
        };
        var cell = new Cell(
            ToFormKey(request.Placement.Cell),
            SkyrimRelease.SkyrimSE)
        {
            Grid = new CellGrid
            {
                Point = new P2Int(
                    checked((short)
                        request.Placement.CellGridX),
                    checked((short)
                        request.Placement.CellGridY)),
                Flags = default
            }
        };
        var actor = new PlacedNpc(
            new FormKey(
                mod.ModKey,
                request.Allocation.PlacedActor.Value),
            SkyrimRelease.SkyrimSE)
        {
            Placement = new Placement
            {
                Position = new P3Float(
                    checked((float)
                        request.Placement.Subject.X),
                    checked((float)
                        request.Placement.Subject.Y),
                    checked((float)
                        request.Placement.Subject.Z)),
                Rotation = new P3Float(
                    checked((float)
                        request.Placement.Subject.RotationX),
                    checked((float)
                        request.Placement.Subject.RotationY),
                    checked((float)
                        request.Placement.Subject.RotationZ))
            }
        };
        actor.Base.SetTo(new FormKey(
            mod.ModKey,
            request.Subject.ActorFormId.Value));
        SetPersistent(actor);
        cell.Persistent.Add(actor);
        subBlock.Items.Add(cell);
        block.Items.Add(subBlock);
        world.SubCells.Add(block);
        mod.Worldspaces.Add(world);
    }

    private static void VerifySubjectPlugin(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        WorkspacePath outputPath)
    {
        if (!File.Exists(outputPath.Value))
            Refuse(
                "follower-finish-pair-output-plugin-missing",
                "The staged subject plugin is missing.");
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireNoMasterRecord(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireSinglePlacedNpc(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value,
                request.Placement.Cell.FormId.Value,
                request.Placement.CellGridX,
                request.Placement.CellGridY,
                request.Allocation.PlacedActor.Value,
                request.Subject.ActorFormId.Value,
                request.Placement.Subject);
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireNoForbiddenWorldRecords(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value);
        ModKey subjectKey =
            ModKey.FromNameAndExtension(request.Subject.Plugin.Value);
        ModKey companionKey =
            ModKey.FromNameAndExtension(request.Companion.Plugin.Value);
        using var source = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                subjectKey,
                new FilePath(
                    request.Subject.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        using var output = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                subjectKey,
                new FilePath(outputPath.Value)),
            SkyrimRelease.SkyrimSE);
        if (!output.IsSmallMaster ||
            output.IsMaster ||
            output.ModHeader.Stats.NextFormID !=
                request.Allocation.NextFormId.Value ||
            !output.ModHeader.MasterReferences
                .Select(master =>
                    new PluginName(master.Master.ToString()))
                .SequenceEqual(proposal.OutputMasters))
            Refuse(
                "follower-finish-pair-output-header",
                "The subject TES4 light/master surface differs from the reviewed proposal.");

        INpcGetter sourceNpc = source.Npcs.Single();
        INpcGetter outputNpc = output.Npcs.Single(record =>
            record.FormKey ==
            new FormKey(
                subjectKey,
                request.Subject.ActorFormId.Value));
        if (!AppearanceMatches(sourceNpc, outputNpc) ||
            outputNpc.DefaultOutfit.FormKeyNullable !=
                new FormKey(
                    subjectKey,
                    request.Allocation.Outfit.Value) ||
            outputNpc.Packages.Select(link => link.FormKey)
                .SingleOrDefault() !=
                new FormKey(
                    subjectKey,
                    request.Allocation.Package.Value))
            Refuse(
                "follower-finish-pair-output-appearance",
                "The subject appearance changed outside PKID/DOFT or the follower links are wrong.");

        IArmorAddonGetter addon =
            output.ArmorAddons.Single(record =>
                record.FormKey.ID ==
                request.Allocation.PrivateArmorAddon.Value);
        IArmorGetter armor = output.Armors.Single(record =>
            record.FormKey.ID ==
            request.Allocation.PrivateArmor.Value);
        IOutfitGetter outfit = output.Outfits.Single(record =>
            record.FormKey.ID ==
            request.Allocation.Outfit.Value);
        FormKey privateAddon = new(
            subjectKey,
            request.Allocation.PrivateArmorAddon.Value);
        FormKey privateArmor = new(
            subjectKey,
            request.Allocation.PrivateArmor.Value);
        FormKey[] expectedItems =
        [
            privateArmor,
            ToFormKey(request.Outfit.Boots),
            ToFormKey(request.Outfit.Gauntlets)
        ];
        if (addon.SkinTexture?.Female?.FormKeyNullable !=
                ToFormKey(
                    request.Outfit.TargetFemaleSkinTextureSet) ||
            armor.Armature.Count(link =>
                link.FormKey == privateAddon) != 1 ||
            outfit.Items is null ||
            !outfit.Items.Select(link => link.FormKey)
                .SequenceEqual(expectedItems))
            Refuse(
                "follower-finish-pair-output-outfit",
                "The private COtR outfit graph differs from the reviewed route.");

        IPackageGetter package = output.Packages.Single(record =>
            record.FormKey.ID ==
            request.Allocation.Package.Value);
        using var companion = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                companionKey,
                new FilePath(
                    request.Companion.PluginFile.Path.Value)),
            SkyrimRelease.SkyrimSE);
        IPackageGetter companionPackage =
            companion.Packages.Single(record =>
                record.FormKey ==
                ToFormKey(
                    proposal.CompanionSnapshot.Packages.Single()));
        PackageDataLocation location = package.Data.Values
            .OfType<PackageDataLocation>().Single();
        PackageDataLocation companionLocation =
            companionPackage.Data.Values
                .OfType<PackageDataLocation>().Single();
        LocationTarget? target =
            location.Location?.Target as LocationTarget;
        LocationTarget? companionTarget =
            companionLocation.Location?.Target as LocationTarget;
        if (target?.Link.FormKey !=
                ToFormKey(request.CompanionAnchor) ||
            location.Location?.Radius != 768 ||
            package.Conditions.Count != 1 ||
            package.ScheduleMonth !=
                companionPackage.ScheduleMonth ||
            package.ScheduleDayOfWeek !=
                companionPackage.ScheduleDayOfWeek ||
            package.ScheduleDate !=
                companionPackage.ScheduleDate ||
            package.ScheduleHour !=
                companionPackage.ScheduleHour ||
            package.ScheduleMinute !=
                companionPackage.ScheduleMinute ||
            package.ScheduleDurationInMinutes !=
                companionPackage.ScheduleDurationInMinutes ||
            companionTarget?.Link.FormKey !=
                ToFormKey(request.CompanionAnchor))
            Refuse(
                "follower-finish-pair-output-package",
                "The subject Sandbox is not the admitted companion-anchor routine.");

        IRelationshipGetter[] relationships =
            output.Relationships.Where(record =>
                    record.FormKey.ID ==
                        request.Allocation
                            .SubjectToCompanionRelationship.Value ||
                    record.FormKey.ID ==
                        request.Allocation
                            .CompanionToSubjectRelationship.Value)
                .OrderBy(record => record.FormKey.ID)
                .ToArray();
        FormKey subjectActor = new(
            subjectKey,
            request.Subject.ActorFormId.Value);
        FormKey companionActor = new(
            companionKey,
            request.Companion.ActorFormId.Value);
        if (relationships.Length != 2 ||
            !RelationshipMatches(
                relationships[0],
                subjectActor,
                companionActor) ||
            !RelationshipMatches(
                relationships[1],
                companionActor,
                subjectActor))
            Refuse(
                "follower-finish-pair-output-relationships",
                "The two directional Ally relationships are incomplete.");

        string[] expectedNew =
        [
            $"ACHR 0x{request.Allocation.PlacedActor.Value:X8}",
            $"ARMA 0x{request.Allocation.PrivateArmorAddon.Value:X8}",
            $"ARMO 0x{request.Allocation.PrivateArmor.Value:X8}",
            $"OTFT 0x{request.Allocation.Outfit.Value:X8}",
            $"PACK 0x{request.Allocation.Package.Value:X8}",
            $"RELA 0x{request.Allocation.SubjectToCompanionRelationship.Value:X8}",
            $"RELA 0x{request.Allocation.CompanionToSubjectRelationship.Value:X8}"
        ];
        SkyrimStructuralWorldspaceRecordSanitizer
            .RequireSelfOwnedInventory(
                outputPath.Value,
                request.Placement.Worldspace.Plugin.Value,
                request.Placement.Worldspace.FormId.Value,
                proposal.SubjectSnapshot.RecordInventory
                    .Concat(expectedNew));
    }

    private static bool AppearanceMatches(
        INpcGetter source,
        INpcGetter output) =>
        source.FormKey == output.FormKey &&
        source.EditorID == output.EditorID &&
        source.Name?.String == output.Name?.String &&
        source.Race.FormKey == output.Race.FormKey &&
        source.HairColor.FormKeyNullable ==
            output.HairColor.FormKeyNullable &&
        source.HeadTexture.FormKeyNullable ==
            output.HeadTexture.FormKeyNullable &&
        source.WornArmor.FormKeyNullable ==
            output.WornArmor.FormKeyNullable &&
        source.Height.Equals(output.Height) &&
        source.Weight.Equals(output.Weight) &&
        source.HeadParts.Select(link => link.FormKey)
            .SequenceEqual(
                output.HeadParts.Select(link => link.FormKey)) &&
        source.Factions.Select(entry =>
                (entry.Faction.FormKey, entry.Rank))
            .SequenceEqual(output.Factions.Select(entry =>
                (entry.Faction.FormKey, entry.Rank))) &&
        source.Class.FormKey == output.Class.FormKey &&
        source.Voice.FormKeyNullable ==
            output.Voice.FormKeyNullable &&
        source.CombatStyle.FormKeyNullable ==
            output.CombatStyle.FormKeyNullable &&
        source.Template.FormKeyNullable ==
            output.Template.FormKeyNullable &&
        Equals(source.PlayerSkills, output.PlayerSkills) &&
        Equals(source.Configuration, output.Configuration) &&
        Equals(source.AIData, output.AIData) &&
        Equals(source.FaceMorph, output.FaceMorph) &&
        Equals(source.FaceParts, output.FaceParts) &&
        source.TintLayers.SequenceEqual(output.TintLayers);

    private static bool RelationshipMatches(
        IRelationshipGetter relationship,
        FormKey parent,
        FormKey child) =>
        relationship.Parent.FormKey == parent &&
        relationship.Child.FormKey == child &&
        relationship.Rank == Relationship.RankType.Ally &&
        relationship.Unknown == 0 &&
        relationship.Flags == 0 &&
        relationship.AssociationType.IsNull;

    private static bool TransformMatches(
        IPlacementGetter placement,
        SkyrimFollowerFinishPairTransform expected) =>
        Near(placement.Position.X, expected.X) &&
        Near(placement.Position.Y, expected.Y) &&
        Near(placement.Position.Z, expected.Z) &&
        Near(placement.Rotation.X, expected.RotationX) &&
        Near(placement.Rotation.Y, expected.RotationY) &&
        Near(placement.Rotation.Z, expected.RotationZ);

    private static bool Near(double left, double right) =>
        Math.Abs(left - right) < 0.01;

    private static void CopyRuntimePayload(
        SkyrimFollowerFinishPairRequest request,
        string stageRoot)
    {
        if (request.CompanionFinish is null)
        {
            CopyBoundFile(
                request.Companion.PluginFile,
                Path.Combine(
                    stageRoot,
                    "Data",
                    request.Companion.Plugin.Value));
        }
        CopyActorSidecars(
            request.Companion,
            stageRoot,
            includeFaceGeom:
                request.CompanionFinish is null);
        CopyActorSidecars(
            request.Subject,
            stageRoot,
            includeFaceGeom: true);
        string scriptDestination = Path.Combine(
            stageRoot,
            "Data",
            "Scripts",
            Path.GetFileName(
                request.Subject.RuntimeScript.Path.Value));
        CopyBoundFile(
            request.Subject.RuntimeScript,
            scriptDestination);
    }

    private static void CopyActorSidecars(
        SkyrimFollowerFinishPairActor actor,
        string stageRoot,
        bool includeFaceGeom)
    {
        if (includeFaceGeom)
        {
            CopyBoundFile(
                actor.FaceGeom,
                Path.Combine(
                    stageRoot,
                    "Data",
                    "meshes",
                    "actors",
                    "character",
                    "FaceGenData",
                    "FaceGeom",
                    actor.Plugin.Value,
                    $"{actor.ActorFormId.Value:X8}.nif"));
        }
        CopyBoundFile(
            actor.FaceTint,
            Path.Combine(
                stageRoot,
                "Data",
                "textures",
                "actors",
                "character",
                "FaceGenData",
                "FaceTint",
                actor.Plugin.Value,
                $"{actor.ActorFormId.Value:X8}.dds"));
        CopyBoundFile(
            actor.BodyGenTemplates,
            Path.Combine(
                stageRoot,
                "Data",
                "meshes",
                "actors",
                "character",
                "BodyGenData",
                actor.Plugin.Value,
                "templates.ini"));
        CopyBoundFile(
            actor.BodyGenMorphs,
            Path.Combine(
                stageRoot,
                "Data",
                "meshes",
                "actors",
                "character",
                "BodyGenData",
                actor.Plugin.Value,
                "morphs.ini"));
    }

    private static void CopyBoundFile(
        SkyrimFollowerFinishPairFile source,
        string destination)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length !=
                    source.ByteLength ||
                HashFile(destination) != source.Sha256)
                Refuse(
                    "follower-finish-pair-copy-collision",
                    $"A package collision differs: {destination}");
            return;
        }
        File.Copy(source.Path.Value, destination);
        if (new FileInfo(destination).Length !=
                source.ByteLength ||
            HashFile(destination) != source.Sha256)
            Refuse(
                "follower-finish-pair-copy-verification",
                $"A copied runtime file differs: {destination}");
    }

    private static async Task WritePackageDocumentsAsync(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        WorkspacePath outputPlugin,
        WorkspacePath outputCompanionPlugin,
        WorkspacePath outputCompanionFaceGeom,
        string stageRoot,
        CancellationToken cancellationToken)
    {
        string evidence = Path.Combine(stageRoot, "evidence");
        Directory.CreateDirectory(evidence);
        File.Copy(
            requestPath.Value,
            Path.Combine(evidence, "paired-follower-request.json"));
        File.Copy(
            proposalPath.Value,
            Path.Combine(evidence, "paired-follower-proposal.json"));
        var postwrite = new
        {
            schemaVersion = request.SchemaVersion,
            operation =
                SkyrimFollowerFinishPairRequest.OperationName,
            status = "STATIC_PASS_RUNTIME_REQUIRED",
            requestSha256 = requestSha256.Value,
            proposalSha256 = proposalSha256.Value,
            companionPluginSha256 =
                HashFile(outputCompanionPlugin.Value).Value,
            companionSourcePluginSha256 =
                request.Companion.PluginFile.Sha256.Value,
            companionFaceGeomSourceSha256 =
                request.Companion.FaceGeom.Sha256.Value,
            companionFaceGeomOutputSha256 =
                HashFile(outputCompanionFaceGeom.Value).Value,
            subjectSourcePluginSha256 =
                request.Subject.PluginFile.Sha256.Value,
            subjectOutputPluginSha256 =
                HashFile(outputPlugin.Value).Value,
            companionPluginByteIdentical =
                request.CompanionFinish is null,
            companionFaceGeomByteIdentical =
                request.CompanionFinish is null,
            companionUnchangedSidecarsByteIdentical = true,
            subjectFaceGeomByteIdentical = true,
            subjectFaceTintByteIdentical = true,
            subjectBodyGenByteIdentical = true,
            subjectRuntimeScriptByteIdentical = true,
            outfitProvider = new
            {
                plugin = request.Outfit.Plugin.Value,
                pluginSha256 =
                    request.Outfit.PluginFile.Sha256.Value,
                winningFemaleMeshArchiveSha256 =
                    request.Outfit
                        .WinningFemaleMeshArchive.Sha256.Value,
                targetFemaleSkinTexture =
                    request.Outfit
                        .TargetFemaleSkinTextureSet.ToString()
            },
            outputMasters =
                proposal.OutputMasters.Select(master =>
                    master.Value).ToArray(),
            companionOutputMasters =
                proposal.CompanionOutputMasters?
                    .Select(master => master.Value).ToArray(),
            runtimeAuthority = false
        };
        await WriteNewJsonAsync(
            new WorkspacePath(Path.Combine(
                evidence,
                "paired-follower-postwrite-verification.json")),
            postwrite,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(
                stageRoot,
                "README-NPCMANAGER-RUNTIME-TEST.txt"),
            BuildReadme(request),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(
                stageRoot,
                "Data",
                "diag-paired-followers.txt"),
            $"help \"{EditorIdStem(request.Companion.Plugin)}\" 4\n" +
            $"help \"{EditorIdStem(request.Subject.Plugin)}\" 4\n" +
            "player.getfactionrank 0005C84E\n",
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildReadme(
        SkyrimFollowerFinishPairRequest request) =>
        $"""
        NPC Manager paired follower runtime candidate

        Status: STATIC_PASS_RUNTIME_REQUIRED
        Runtime authority: false

        Primary question:
        Do both accepted actors load together with their preserved appearances,
        their requested outfit/skin continuity, adjacent routine, and
        separate follower behavior?

        Replace, do not stack:
        Install this ZIP as one mod and disable the earlier standalone source
        actor candidates. Exactly one copy of each plugin and its FaceGen/
        FaceTint paths may win. Required enabled dependencies include the
        subject's accepted race/headpart/skin closure plus
        {request.Outfit.Plugin.Value}. The request-bound winning female mesh
        provider must win over its base archive.

        Load order is enforced by the plugin dependency:
          1. {request.Companion.Plugin.Value}
          2. {request.Subject.Plugin.Value}

        Runtime safeguards:
        - Keep Face Discoloration Fix ENABLED. This test is not a runtime face
          regeneration diagnostic.
        - Prefer a NEW GAME or a main-menu console start into a clean context.
          If that is impractical, report that an existing save was used.
        - Immediately before testing, create a new K-local environment snapshot:
          python <workspace-root>\tools\gates\env_fingerprint.py snapshot
            --out <new workspace-local JSON under this project's reports>
          Diff it against the handoff fingerprint if the enabled mod/plugin set
          has changed.
        - Console-click each actor and retain the console header showing the
          selected reference/name. Run: bat diag-paired-followers
        - If the game CTDs, stop and provide the newest CrashLoggerSSE
          crash-*.log before changing anything else.

        Requested runtime intent:
        {request.Narrative}

        Smallest sufficient qualitative checkpoint:
        - one clear image may establish the requested face/outfit judgment;
        - report neck, wrist, and ankle continuity in that image;
        - recruit, trade, wait, dismiss, and recruit each actor separately;
        - save, reload, change cell, and return once;
        - report collision, pathing, return-routine, or CTD problems.

        One image plus a verbal behavior report is qualitative user evidence.
        It does not become full formal visual authority without provider hashes,
        an in-frame control, full body-part coverage, and alternate lighting.

        Console helper: bat diag-paired-followers
        """;

    private static async Task WriteManifestAsync(
        SkyrimFollowerFinishPairRequest request,
        string root,
        WorkspacePath manifest,
        CancellationToken cancellationToken)
    {
        var artifacts = Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                path,
                manifest.Value,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new PairManifestArtifact(
                NormalizeRelative(root, path),
                new FileInfo(path).Length,
                HashFile(path).Value))
            .OrderBy(row => row.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var document = new PairManifest(
            request.SchemaVersion,
            "skyrim-paired-follower-finish-package",
            "STATIC_PASS_RUNTIME_REQUIRED",
            request.Companion.Plugin.Value,
            request.Subject.Plugin.Value,
            artifacts,
            RuntimeAuthority: false);
        await WriteNewJsonAsync(
            manifest,
            document,
            cancellationToken).ConfigureAwait(false);
    }

    private void VerifyPackageTree(
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        string root,
        WorkspacePath manifestPath)
    {
        if (!Directory.Exists(root) ||
            !File.Exists(manifestPath.Value))
            Refuse(
                "follower-finish-pair-package-missing",
                "The paired package root or manifest is missing.");
        PairManifest manifest = ReadJson<PairManifest>(
            manifestPath.Value);
        if (manifest.SchemaVersion != request.SchemaVersion ||
            manifest.ArtifactKind !=
                "skyrim-paired-follower-finish-package" ||
            manifest.Status !=
                "STATIC_PASS_RUNTIME_REQUIRED" ||
            manifest.RuntimeAuthority ||
            manifest.CompanionPlugin !=
                request.Companion.Plugin.Value ||
            manifest.SubjectPlugin !=
                request.Subject.Plugin.Value)
            Refuse(
                "follower-finish-pair-manifest-envelope",
                "The paired manifest envelope is invalid.");
        PairManifestArtifact[] actual =
            Directory.EnumerateFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    path,
                    manifestPath.Value,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new PairManifestArtifact(
                    NormalizeRelative(root, path),
                    new FileInfo(path).Length,
                    HashFile(path).Value))
                .OrderBy(row =>
                    row.RelativePath,
                    StringComparer.Ordinal)
                .ToArray();
        if (!manifest.Artifacts.SequenceEqual(actual) ||
            manifest.Artifacts.Select(row => row.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != manifest.Artifacts.Length ||
            manifest.Artifacts.Any(row =>
                IsUnsafeArchivePath(row.RelativePath)))
            Refuse(
                "follower-finish-pair-manifest-inventory",
                "The paired manifest has missing, extra, duplicate, unsafe, or mismatched files.");

        WorkspacePath companionPlugin =
            CompanionOutputPlugin(request, root);
        WorkspacePath companionFaceGeom =
            CompanionOutputFaceGeom(request, root);
        if (request.CompanionFinish is null)
        {
            RequirePackageHash(
                root,
                $"Data/{request.Companion.Plugin.Value}",
                request.Companion.PluginFile.Sha256);
            RequirePackageHash(
                root,
                $"Data/meshes/actors/character/FaceGenData/FaceGeom/{request.Companion.Plugin.Value}/{request.Companion.ActorFormId.Value:X8}.nif",
                request.Companion.FaceGeom.Sha256);
        }
        else
        {
            VerifyCompanionPlugin(
                request,
                proposal,
                companionPlugin);
            VerifyCompanionFaceGeom(
                request,
                companionFaceGeom);
        }
        RequirePackageHash(
            root,
            $"Data/textures/actors/character/FaceGenData/FaceTint/{request.Companion.Plugin.Value}/{request.Companion.ActorFormId.Value:X8}.dds",
            request.Companion.FaceTint.Sha256);
        RequirePackageHash(
            root,
            $"Data/meshes/actors/character/FaceGenData/FaceGeom/{request.Subject.Plugin.Value}/{request.Subject.ActorFormId.Value:X8}.nif",
            request.Subject.FaceGeom.Sha256);
        RequirePackageHash(
            root,
            $"Data/textures/actors/character/FaceGenData/FaceTint/{request.Subject.Plugin.Value}/{request.Subject.ActorFormId.Value:X8}.dds",
            request.Subject.FaceTint.Sha256);
        RequirePackageHash(
            root,
            $"Data/Scripts/{Path.GetFileName(request.Subject.RuntimeScript.Path.Value)}",
            request.Subject.RuntimeScript.Sha256);
        VerifySubjectPlugin(
            request,
            proposal,
            new WorkspacePath(Path.Combine(
                root,
                "Data",
                request.Subject.Plugin.Value)));
    }

    private static void RequirePackageHash(
        string root,
        string relative,
        Sha256Hash expected)
    {
        string path = Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path) || HashFile(path) != expected)
            Refuse(
                "follower-finish-pair-package-payload",
                $"An immutable payload differs: {relative}");
    }

    private static void CreateDeterministicArchive(
        string root,
        string archivePath)
    {
        (string SourcePath, string ArchivePath)[] members =
            EnumerateInstallArchiveMembers(root);
        using var stream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Create,
            leaveOpen: false);
        foreach ((string sourcePath, string relative) in members)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                relative,
                CompressionLevel.Optimal);
            entry.LastWriteTime = ArchiveTimestamp;
            using Stream input = File.OpenRead(sourcePath);
            using Stream output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static (string SourcePath, string ArchivePath)[]
        EnumerateInstallArchiveMembers(string packageRoot)
    {
        string dataRoot = Path.Combine(packageRoot, "Data");
        string readme = Path.Combine(
            packageRoot,
            "README-NPCMANAGER-RUNTIME-TEST.txt");
        if (!Directory.Exists(dataRoot) || !File.Exists(readme))
            Refuse(
                "follower-finish-pair-install-root-missing",
                "The paired transaction lacks its Data root or runtime README.");

        (string SourcePath, string ArchivePath)[] members =
            Directory.EnumerateFiles(
                    dataRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => (
                    SourcePath: path,
                    ArchivePath: NormalizeRelative(dataRoot, path)))
                .Append((
                    SourcePath: readme,
                    ArchivePath: Path.GetFileName(readme)))
                .OrderBy(
                    row => row.ArchivePath,
                    StringComparer.Ordinal)
                .ToArray();
        if (members.Length == 0 ||
            members.Select(row => row.ArchivePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != members.Length ||
            members.Any(row =>
                IsUnsafeArchivePath(row.ArchivePath)))
            Refuse(
                "follower-finish-pair-install-inventory",
                "The paired install projection contains a missing, duplicate, or unsafe member.");
        if (!members.Any(row =>
                !row.ArchivePath.Contains('/') &&
                Path.GetExtension(row.ArchivePath) is
                    ".esp" or ".esm" or ".esl"))
            Refuse(
                "follower-finish-pair-install-plugin",
                "The paired install projection has no root-level plugin.");
        return members;
    }

    private static void VerifyArchive(
        WorkspacePath archivePath,
        string packageRoot)
    {
        if (!File.Exists(archivePath.Value))
            Refuse(
                "follower-finish-pair-archive-missing",
                "The paired archive is missing.");
        using ZipArchive archive = ZipFile.OpenRead(
            archivePath.Value);
        (string SourcePath, string ArchivePath)[] expectedMembers =
            EnumerateInstallArchiveMembers(packageRoot);
        string[] expected = expectedMembers
            .Select(row => row.ArchivePath)
            .ToArray();
        string[] actual = archive.Entries
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected) ||
            actual.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                actual.Length ||
            actual.Any(IsUnsafeArchivePath))
            Refuse(
                "follower-finish-pair-archive-inventory",
                "The paired archive inventory differs from the verified package tree.");
        var sources = expectedMembers.ToDictionary(
            row => row.ArchivePath,
            row => row.SourcePath,
            StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using Stream stream = entry.Open();
            Sha256Hash hash = new(
                Convert.ToHexString(SHA256.HashData(stream)));
            if (hash != HashFile(sources[entry.FullName]))
                Refuse(
                    "follower-finish-pair-archive-hash",
                    $"An archived member differs: {entry.FullName}");
        }
    }

    private async Task<SkyrimFollowerFinishPairRequest>
        LoadRequestAsync(
            WorkspacePath path,
            Sha256Hash expected,
            CancellationToken cancellationToken)
    {
        ValidateKPath(path, allowMissingLeaf: false);
        byte[] bytes = await ReadBoundDocumentAsync(
            path,
            expected,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<
                   SkyrimFollowerFinishPairRequest>(
                   bytes,
                   JsonOptions) ??
               throw new InvalidDataException(
                   "The paired request deserialized to null.");
    }

    private async Task<SkyrimFollowerFinishPairProposal>
        LoadProposalAsync(
            WorkspacePath path,
            Sha256Hash expected,
            CancellationToken cancellationToken)
    {
        ValidateKPath(path, allowMissingLeaf: false);
        byte[] bytes = await ReadBoundDocumentAsync(
            path,
            expected,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<
                   SkyrimFollowerFinishPairProposal>(
                   bytes,
                   JsonOptions) ??
               throw new InvalidDataException(
                   "The paired proposal deserialized to null.");
    }

    private static async Task<byte[]> ReadBoundDocumentAsync(
        WorkspacePath path,
        Sha256Hash expected,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length > MaximumDocumentBytes)
            throw new InvalidDataException(
                "A paired document is missing or exceeds 1 MiB.");
        byte[] bytes = await File.ReadAllBytesAsync(
            path.Value,
            cancellationToken).ConfigureAwait(false);
        Sha256Hash actual = new(
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (actual != expected)
            throw new InvalidDataException(
                "A paired document hash differs from the supplied binding.");
        return bytes;
    }

    private static async Task WriteNewJsonAsync<T>(
        WorkspacePath path,
        T value,
        CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(path.Value);
        if (string.IsNullOrWhiteSpace(parent) ||
            !Directory.Exists(parent) ||
            File.Exists(path.Value) ||
            Directory.Exists(path.Value))
            throw new IOException(
                "JSON evidence writes require an existing parent and a new destination.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        await using var stream = new FileStream(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        await stream.WriteAsync(
            bytes,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(
            cancellationToken).ConfigureAwait(false);
    }

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllBytes(path),
            JsonOptions) ??
        throw new InvalidDataException(
            $"JSON document deserialized to null: {path}");

    private void ValidateNewOutput(
        WorkspacePath path,
        string label)
    {
        ValidateKPath(path, allowMissingLeaf: true);
        if (File.Exists(path.Value) ||
            Directory.Exists(path.Value))
            Refuse(
                $"follower-finish-pair-{label}-exists",
                $"The {label} destination already exists.");
        string? parent = Path.GetDirectoryName(path.Value);
        if (string.IsNullOrWhiteSpace(parent) ||
            !Directory.Exists(parent))
            Refuse(
                $"follower-finish-pair-{label}-parent",
                $"The {label} parent directory does not exist.");
    }

    private void ValidateKPath(
        WorkspacePath path,
        bool allowMissingLeaf)
    {
        if (!path.IsUnder(labRoot) ||
            !string.Equals(
                Path.GetPathRoot(path.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase) ||
            path.Value.StartsWith(
                @"\\",
                StringComparison.Ordinal) ||
            path.Value.StartsWith(
                @"\\?\",
                StringComparison.Ordinal) ||
            path.Value.StartsWith(
                @"\\.\",
                StringComparison.Ordinal) ||
            path.Value.IndexOf(':', 2) >= 0)
            Refuse(
                "follower-finish-pair-path-outside",
                "Paired transaction paths must be ordinary K-local workspace paths.");
        string current = allowMissingLeaf
            ? Path.GetDirectoryName(path.Value) ??
              path.Value
            : path.Value;
        while (current.Length >= labRoot.Value.Length)
        {
            if ((File.Exists(current) ||
                 Directory.Exists(current)) &&
                File.GetAttributes(current)
                    .HasFlag(FileAttributes.ReparsePoint))
                Refuse(
                    "follower-finish-pair-reparse",
                    "Paired transaction paths may not traverse reparse points.");
            if (string.Equals(
                    current,
                    labRoot.Value,
                    StringComparison.OrdinalIgnoreCase))
                break;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null ||
                string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                Refuse(
                    "follower-finish-pair-path-outside",
                    "A paired transaction path did not resolve under the lab root.");
            current = parent;
        }
    }

    private void ValidateTransactionOwnedPath(string path)
    {
        ValidateKPath(
            new WorkspacePath(path),
            allowMissingLeaf: true);
        if (!path.Contains(
                ".stage-",
                StringComparison.Ordinal) &&
            !path.Contains(
                ".repeat-",
                StringComparison.Ordinal))
            Refuse(
                "follower-finish-pair-stage-path",
                "A cleanup path is not transaction-owned.");
    }

    private static string NormalizeRelative(
        string root,
        string path) =>
        Path.GetRelativePath(root, path)
            .Replace('\\', '/');

    private static bool IsUnsafeArchivePath(string path) =>
        string.IsNullOrWhiteSpace(path) ||
        path.StartsWith('/') ||
        path.StartsWith('\\') ||
        path.Contains('\\') ||
        path.Split('/').Any(part =>
            part is "" or "." or "..") ||
        path.Contains(':');

    private static short FloorDivide(int value, int divisor)
    {
        int quotient = Math.DivRem(
            value,
            divisor,
            out int remainder);
        if (remainder < 0)
            quotient--;
        return checked((short)quotient);
    }

    private static void SetPersistent(IMajorRecord record)
    {
        PropertyInfo? property = record.GetType()
            .GetProperty("SkyrimMajorRecordFlags");
        object? current = property?.GetValue(record);
        if (property is null ||
            !property.CanWrite ||
            current is null ||
            !current.GetType().IsEnum)
            Refuse(
                "follower-finish-pair-persistent",
                "The placed actor does not expose writable Skyrim flags.");
        property.SetValue(
            record,
            Enum.ToObject(current.GetType(), 0x400));
    }

    private static FormKey ToFormKey(FormReference reference) =>
        new(
            ModKey.FromNameAndExtension(
                reference.Plugin.Value),
            reference.FormId.Value);

    private static FormReference ToReference(FormKey key) =>
        new(
            new PluginName(key.ModKey.ToString()),
            new FormId(key.ID));

    private static string Signature(
        IMajorRecordGetter record) =>
        record switch
        {
            INpcGetter => "NPC_",
            IColorRecordGetter => "CLFM",
            ITextureSetGetter => "TXST",
            IHeadPartGetter => "HDPT",
            IRelationshipGetter => "RELA",
            IArmorAddonGetter => "ARMA",
            IArmorGetter => "ARMO",
            IOutfitGetter => "OTFT",
            IPackageGetter => "PACK",
            IPlacedNpcGetter => "ACHR",
            IPlacedObjectGetter => "REFR",
            IWorldspaceGetter => "WRLD",
            ICellGetter => "CELL",
            _ => record.GetType().Name
        };

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void DeleteTransactionPath(string? path)
    {
        try
        {
            if (path is not null &&
                path.Contains(
                    ".stage-",
                    StringComparison.Ordinal) &&
                Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A unique stage is never promoted or accepted as evidence.
        }
        catch (UnauthorizedAccessException)
        {
            // A unique stage is never promoted or accepted as evidence.
        }
    }

    private static void DeleteTransactionFile(string? path)
    {
        try
        {
            if (path is not null &&
                (path.Contains(
                     ".stage-",
                     StringComparison.Ordinal) ||
                 path.Contains(
                     ".repeat-",
                     StringComparison.Ordinal)) &&
                File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A unique archive stage is never promoted.
        }
        catch (UnauthorizedAccessException)
        {
            // A unique archive stage is never promoted.
        }
    }

    private static uint ParseInventoryId(string inventoryRow)
    {
        int separator = inventoryRow.LastIndexOf(' ');
        if (separator > 0 &&
            FormId.TryParse(
                inventoryRow[(separator + 1)..],
                out FormId id))
            return id.Value;
        Refuse(
            "follower-finish-pair-inventory-shape",
            "A source record inventory row is malformed.");
        return 0;
    }

    private static string BuildEditorId(
        PluginName owner,
        string suffix) =>
        $"{EditorIdStem(owner)}_{suffix}";

    private static string EditorIdStem(PluginName plugin)
    {
        string fileStem =
            Path.GetFileNameWithoutExtension(plugin.Value);
        string value = new(
            fileStem.Where(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character == '_')
                .ToArray());
        if (string.IsNullOrWhiteSpace(value))
            Refuse(
                "follower-finish-pair-editor-id",
                "A plugin name cannot produce a safe EditorID stem.");
        return value.Length <= 48 ? value : value[..48];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new WorkspacePathConverter(),
                new Sha256HashConverter(),
                new PluginNameConverter(),
                new FormIdConverter(),
                new FormReferenceConverter()
            }
        };
        return options;
    }

    private static SkyrimFollowerFinishPairResult Success(
        string verdict,
        WorkspacePath? proposal = null,
        Sha256Hash? proposalHash = null,
        ImmutableArray<Diagnostic> diagnostics = default) =>
        new(
            true,
            verdict,
            proposal,
            proposalHash,
            null,
            null,
            null,
            null,
            null,
            null,
            diagnostics.IsDefault ? [] : diagnostics,
            RuntimeAuthority: false);

    private static SkyrimFollowerFinishPairResult Refused(
        string code,
        string message) =>
        new(
            false,
            "REFUSED",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [Error(code, message)],
            RuntimeAuthority: false);

    private static Diagnostic Info(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Info, message);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static bool IsAdmittedFailure(Exception exception) =>
        exception is ArgumentException or
            FormatException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            OverflowException or
            JsonException;

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");

    private sealed record PairManifest(
        int SchemaVersion,
        string ArtifactKind,
        string Status,
        string CompanionPlugin,
        string SubjectPlugin,
        PairManifestArtifact[] Artifacts,
        bool RuntimeAuthority);

    private sealed record PairManifestArtifact(
        string RelativePath,
        long ByteLength,
        string Sha256);

    private sealed class WorkspacePathConverter :
        JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "Workspace path must be a string."));

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashConverter :
        JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "SHA-256 must be a string."));

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Value.ToLowerInvariant());
    }

    private sealed class PluginNameConverter :
        JsonConverter<PluginName>
    {
        public override PluginName Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "Plugin name must be a string."));

        public override void Write(
            Utf8JsonWriter writer,
            PluginName value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class FormIdConverter :
        JsonConverter<FormId>
    {
        public override FormId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string text = reader.GetString() ??
                          throw new JsonException(
                              "FormID must be a string.");
            if (!FormId.TryParse(text, out FormId value))
                throw new JsonException(
                    "FormID is invalid.");
            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class FormReferenceConverter :
        JsonConverter<FormReference>
    {
        public override FormReference Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string text = reader.GetString() ??
                          throw new JsonException(
                              "Form reference must be a string.");
            int separator = text.LastIndexOf('|');
            if (separator <= 0 ||
                separator == text.Length - 1 ||
                !FormId.TryParse(
                    text[(separator + 1)..],
                    out FormId id))
                throw new JsonException(
                    "Form reference is invalid.");
            return new FormReference(
                new PluginName(text[..separator]),
                id);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormReference value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
