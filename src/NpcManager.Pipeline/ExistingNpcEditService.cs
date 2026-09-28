using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Pipeline;

/// <summary>
/// Runs bounded existing-NPC identity and nonvisual gameplay edits as one transactional package.
/// Appearance-affecting fields intentionally remain outside this slice so a
/// successful result cannot leave stale FaceGen assets behind.
/// </summary>
public sealed partial class ExistingNpcEditService(
    INpcOverrideService overrideService,
    IPackageVerifyService packageVerifier,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IExistingNpcEditService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<ExistingNpcEditInspection> InspectAsync(
        GameEdition edition,
        WorkspacePath inputPlugin,
        Sha256Hash expectedInputSha256,
        FormId targetFormId,
        CancellationToken cancellationToken)
    {
        var result = await overrideService.InspectSourceAsync(
            edition,
            inputPlugin,
            expectedInputSha256,
            targetFormId,
            cancellationToken);
        return new ExistingNpcEditInspection(
            result.IsValid,
            result.SourceSha256,
            result.Snapshot,
            result.Diagnostics);
    }

    public async ValueTask<ExistingNpcEditReview> ReviewAsync(
        ExistingNpcEditRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request).ToBuilder();
        if (HasErrors(diagnostics)) return RefusedReview(diagnostics);

        var parent = Directory.GetParent(request.OutputRoot.Value)!.FullName;
        var reviewOutput = new WorkspacePath(Path.Combine(parent,
            $".npc-edit-review-{Guid.NewGuid():N}.esp"));
        var proposal = await overrideService.AnalyzeAsync(
            BuildOverrideRequest(request, null, reviewOutput), cancellationToken);
        diagnostics.AddRange(proposal.Diagnostics);
        return new ExistingNpcEditReview(
            proposal.IsApplicable && !HasErrors(diagnostics),
            proposal.SourceSha256,
            proposal.Changes,
            proposal.PreservedFields,
            diagnostics.ToImmutable());
    }

    public async ValueTask<ExistingNpcEditResult> ExecuteAsync(
        ExistingNpcEditRequest request,
        IProgress<ExistingNpcEditProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.ConsolidationOutput is not null)
            return await ConsolidateAsync(request, cancellationToken);
        Report(progress, ExistingNpcEditStage.Validate, 0, "Validating the hash-bound edit and fresh output.");
        var diagnostics = Validate(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var stagingRoot = request.OutputRoot.Value + ".stage-" + Guid.NewGuid().ToString("N");
        var promoted = false;
        var completed = false;
        try
        {
            var dataRoot = Path.Combine(stagingRoot, "Data");
            var evidenceRoot = Path.Combine(stagingRoot, "evidence");
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(evidenceRoot);

            var stagingPlugin = new WorkspacePath(Path.Combine(dataRoot, request.OutputPlugin.Value));
            var applyProposalPath = Path.Combine(evidenceRoot, "npc-override-apply-proposal.json");
            Report(progress, ExistingNpcEditStage.Analyze, 15, "Analyzing the selected NPC and exact reviewed changes.");
            var overrideRequest = BuildOverrideRequest(
                request,
                new WorkspacePath(applyProposalPath),
                stagingPlugin);
            var proposal = await overrideService.AnalyzeAsync(overrideRequest, cancellationToken);
            diagnostics.AddRange(proposal.Diagnostics);
            if (!proposal.IsApplicable || HasErrors(diagnostics)) return Refused(diagnostics, proposal.Changes);

            Report(progress, ExistingNpcEditStage.WritePlugin, 40,
                request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                    ? "Writing a raw standalone NPC copy with target-only mutation."
                    : "Writing one source-mastered NPC override without copying unrelated records.");
            var overrideResult = await overrideService.ApplyAsync(
                overrideRequest, proposal, cancellationToken);
            diagnostics.AddRange(overrideResult.Diagnostics);
            if (!overrideResult.Applied || overrideResult.OutputSha256 is null ||
                overrideResult.Verification is not { IsValid: true } verificationResult ||
                (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy &&
                 !verificationResult.IndependentPreservation) || HasErrors(diagnostics))
                return Refused(diagnostics, proposal.Changes);

            Report(progress, ExistingNpcEditStage.VerifyPlugin, 62, "Reopening the override and checking the bounded change surface.");
            var verification = await overrideService.VerifyAsync(
                overrideRequest, proposal, cancellationToken);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.IsValid ||
                (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy &&
                 !verification.IndependentPreservation) || HasErrors(diagnostics))
                return Refused(diagnostics, proposal.Changes);

            var sidecars = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                ? BethesdaNpcStandaloneCopyAdapter.CopyLooseTargetSidecars(
                    request.InputPlugin,
                    new WorkspacePath(dataRoot),
                    request.OutputPlugin,
                    request.TargetFormId)
                : ImmutableArray<BethesdaNpcStandaloneCopyAdapter.Sidecar>.Empty;
            var sourceInventory = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                ? BethesdaRawPluginInventory.Read(request.InputPlugin)
                : ImmutableArray<BethesdaRawPluginInventoryEntry>.Empty;
            var outputInventory = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                ? BethesdaRawPluginInventory.Read(stagingPlugin)
                : ImmutableArray<BethesdaRawPluginInventoryEntry>.Empty;
            var sourceGroups = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                ? BethesdaRawPluginInventory.ReadGroups(request.InputPlugin)
                : ImmutableArray<BethesdaRawPluginGroupInventoryEntry>.Empty;
            var outputGroups = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                ? BethesdaRawPluginInventory.ReadGroups(stagingPlugin)
                : ImmutableArray<BethesdaRawPluginGroupInventoryEntry>.Empty;

            var proposalPath = Path.Combine(evidenceRoot, "npc-edit-proposal.json");
            var verificationPath = Path.Combine(evidenceRoot, "npc-edit-verification.json");
            var runtimePath = Path.Combine(evidenceRoot, "runtime-test-instructions.json");
            await WriteJsonAsync(proposalPath, new EditProposalEvidence(
                1,
                "existing-npc-edit-proposal",
                request.Edition.ToWireName(),
                request.InputPlugin.Value,
                request.ExpectedInputSha256.Value,
                request.TargetFormId.ToString(),
                $"Data/{request.OutputPlugin.Value}",
                proposal.SourcePluginName.Value,
                OutputKindWireName(request.OutputKind),
                request.OutputKind == ExistingNpcEditOutputKind.SourceMasteredOverride
                    ? proposal.SourcePluginName.Value
                    : null,
                request.OutputKind == ExistingNpcEditOutputKind.SourceMasteredOverride
                    ? proposal.SourceSha256.Value
                    : null,
                proposal.RequiredMasters.Select(item => item.Value).ToImmutableArray(),
                proposal.Changes,
                proposal.PreservedFields,
                proposal.ProposalSha256!.Value.Value,
                "evidence/npc-override-apply-proposal.json",
                request.OutputKind == ExistingNpcEditOutputKind.SourceMasteredOverride,
                false,
                false), cancellationToken);
            await WriteJsonAsync(verificationPath, new EditVerificationEvidence(
                1,
                "existing-npc-edit-verification",
                request.TargetFormId.ToString(),
                overrideResult.OutputSha256.Value.Value,
                verification.ObservedMasters.Select(item => item.Value).ToImmutableArray(),
                verification.MajorRecordCount,
                verification.NpcRecordCount,
                verification.SourceOwnedTargetCount,
                verification.SelfOwnedTargetCount,
                verification.ObservedChanges,
                sourceInventory,
                outputInventory,
                sourceGroups,
                outputGroups,
                verification.IndependentPreservation,
                verification.IsValid,
                request.OutputKind == ExistingNpcEditOutputKind.SourceMasteredOverride,
                false), cancellationToken);
            var runtimeInstructions = request.OutputKind == ExistingNpcEditOutputKind.SourceMasteredOverride
                ? $"Install the source plugin '{proposal.SourcePluginName.Value}' (SHA-256 {proposal.SourceSha256.Value}) and the override '{request.OutputPlugin.Value}' together in an isolated test mod, confirm the override loads after the source, and inspect the edited actor in Skyrim. This package is source-mastered and is not independently standalone."
                : $"Install the standalone plugin '{request.OutputPlugin.Value}' and its package-relative loose FaceGen sidecars under Data; do not install the source plugin. Confirm the copied plugin is the winning provider and inspect the edited actor in Skyrim.";
            await WriteJsonAsync(runtimePath, new RuntimeInstructionsEvidence(
                1,
                "existing-npc-runtime-test-instructions",
                request.OutputPlugin.Value,
                request.TargetFormId.ToString(),
                OutputKindWireName(request.OutputKind),
                runtimeInstructions,
                false), cancellationToken);

            Report(progress, ExistingNpcEditStage.Package, 78, "Writing and independently verifying the exact package manifest.");
            var artifactBuilder = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
            artifactBuilder.Add(CreateArtifact("plugin", stagingRoot, stagingPlugin.Value));
            foreach (var sidecar in sidecars)
            {
                artifactBuilder.Add(CreateArtifact(
                    $"standalone-sidecar-{sidecar.Kind}",
                    stagingRoot,
                    Path.Combine(stagingRoot, "Data",
                        sidecar.RelativeDataPath.Replace('/', Path.DirectorySeparatorChar))));
            }
            artifactBuilder.Add(CreateArtifact("npc-override-apply-proposal", stagingRoot, applyProposalPath));
            artifactBuilder.Add(CreateArtifact("npc-edit-proposal", stagingRoot, proposalPath));
            artifactBuilder.Add(CreateArtifact("npc-edit-verification", stagingRoot, verificationPath));
            artifactBuilder.Add(CreateArtifact("runtime-kit", stagingRoot, runtimePath));
            var artifacts = artifactBuilder.ToImmutable();
            var proposalHash = HashFile(proposalPath);
            var manifest = new PresetToNpcPackageManifest(
                1,
                request.Edition.ToWireName(),
                "existing-npc-edit-proposal",
                "evidence/npc-edit-proposal.json",
                proposalHash,
                request.InputPlugin.Value,
                request.ExpectedInputSha256,
                request.OutputPlugin.Value,
                request.TargetFormId,
                artifacts);
            var stagingManifest = new WorkspacePath(Path.Combine(stagingRoot, "npcmanager-package.json"));
            var manifestWrite = await PackageManifestWriter.WriteAsync(
                manifest, stagingManifest, cancellationToken);
            diagnostics.AddRange(manifestWrite.Diagnostics);
            if (!manifestWrite.Written || HasErrors(diagnostics))
                return Refused(diagnostics, proposal.Changes);

            var stagingVerification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(stagingManifest), cancellationToken);
            diagnostics.AddRange(stagingVerification.Diagnostics);
            if (!stagingVerification.Verified || HasErrors(diagnostics))
                return Refused(diagnostics, proposal.Changes);

            Directory.Move(stagingRoot, request.OutputRoot.Value);
            promoted = true;
            var finalManifest = new WorkspacePath(Path.Combine(request.OutputRoot.Value,
                "npcmanager-package.json"));
            var finalVerification = await packageVerifier.VerifyAsync(
                new PackageVerifyRequest(finalManifest), cancellationToken);
            diagnostics.AddRange(finalVerification.Diagnostics);
            if (!finalVerification.Verified || finalVerification.Artifact is null || HasErrors(diagnostics))
                return Refused(diagnostics, proposal.Changes);

            var finalPlugin = new WorkspacePath(Path.Combine(request.OutputRoot.Value,
                "Data", request.OutputPlugin.Value));
            completed = true;
            Report(progress, ExistingNpcEditStage.Complete, 100,
                "Static package created and independently verified; Skyrim runtime proof remains separate.");
            return new ExistingNpcEditResult(
                true,
                "STATIC_PASS_RUNTIME_REQUIRED",
                finalPlugin,
                HashFile(finalPlugin.Value),
                finalManifest,
                finalVerification.Artifact.ManifestSha256,
                proposal.Changes,
                finalVerification,
                diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(FromTypedException(exception));
            return Refused(diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("existing-npc-edit-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDeleteOwnedDirectory(stagingRoot);
            if (promoted && !completed) TryDeleteOwnedDirectory(request.OutputRoot.Value);
        }
    }

    private ImmutableArray<Diagnostic> Validate(ExistingNpcEditRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("existing-npc-edit-edition-unsupported", DiagnosticSeverity.Error,
                "The walking existing-NPC package currently supports Skyrim SE/AE only."));
        if (request.OutputKind is not ExistingNpcEditOutputKind.SourceMasteredOverride and
            not ExistingNpcEditOutputKind.StandaloneCopy)
            diagnostics.Add(new Diagnostic("existing-npc-edit-output-kind-unsupported",
                DiagnosticSeverity.Error,
                "The NPC edit package output kind is unsupported."));
        if (!request.InputPlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("existing-npc-edit-input-outside-lab", DiagnosticSeverity.Error,
                "The input plugin must remain under the K-only workspace."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("existing-npc-edit-output-outside-lab", DiagnosticSeverity.Error,
                "The output root must remain under the K-only workspace."));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        if (!File.Exists(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("existing-npc-edit-input-missing", DiagnosticSeverity.Error,
                "The copied input plugin does not exist."));
        if (Directory.Exists(request.OutputRoot.Value) || File.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("existing-npc-edit-output-exists", DiagnosticSeverity.Error,
                "The output root must not already exist."));
        var outputParent = Directory.GetParent(request.OutputRoot.Value)?.FullName;
        if (outputParent is null || !Directory.Exists(outputParent))
            diagnostics.Add(new Diagnostic("existing-npc-edit-output-parent-missing", DiagnosticSeverity.Error,
                "The output root parent must already exist."));
        if (!string.Equals(Path.GetFileName(request.OutputPlugin.Value), request.OutputPlugin.Value,
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(request.OutputPlugin.Value), ".esp",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("existing-npc-edit-plugin-name-invalid", DiagnosticSeverity.Error,
                "The output plugin must be one ordinary .esp filename."));
        if (request.EditorId is null && request.Name is null &&
            (request.Names is null || request.Names.IsEmpty) &&
            (request.Archetype is null || request.Archetype.IsEmpty) &&
            (request.Stats is null || request.Stats.IsEmpty) &&
            (request.Keywords is null || request.Keywords.IsEmpty) &&
            (request.Factions is null || request.Factions.IsEmpty) &&
            (request.Inventory is null || request.Inventory.IsEmpty) &&
            (request.Outfits is null || request.Outfits.IsEmpty) &&
            (request.Perks is null || request.Perks.IsEmpty) &&
            (request.ActorEffects is null || request.ActorEffects.IsEmpty))
            diagnostics.Add(new Diagnostic("existing-npc-edit-empty", DiagnosticSeverity.Error,
                "At least one identity or supported gameplay field must change."));
        AddStatsDiagnostics(diagnostics, request.Stats);
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input plugin");
        if (outputParent is not null) AddReparseDiagnostic(diagnostics, outputParent, "output parent");
        return diagnostics.ToImmutable();
    }

    private static NpcOverrideRequest BuildOverrideRequest(
        ExistingNpcEditRequest request,
        WorkspacePath? proposalPath,
        WorkspacePath outputPlugin) =>
        new(
            request.Edition,
            request.InputPlugin,
            request.ExpectedInputSha256,
            request.TargetFormId,
            proposalPath,
            outputPlugin,
            new NpcOverridePatch(
                request.EditorId,
                request.Name,
                request.Stats,
                request.Keywords,
                request.Factions,
                request.Inventory,
                request.Outfits,
                request.Perks,
                request.ActorEffects,
                request.Names,
                request.Archetype),
            request.OutputKind);

    private static string OutputKindWireName(ExistingNpcEditOutputKind outputKind) =>
        outputKind switch
        {
            ExistingNpcEditOutputKind.SourceMasteredOverride => "source-mastered-override",
            ExistingNpcEditOutputKind.StandaloneCopy => "standalone-copy",
            _ => throw new ArgumentOutOfRangeException(nameof(outputKind), outputKind,
                "Unsupported NPC edit package output kind.")
        };

    private static Diagnostic FromTypedException(InvalidDataException exception)
    {
        var message = exception.Message;
        var separator = message.IndexOf(':');
        if (separator > 0)
        {
            var code = message[..separator];
            if (code.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_'))
                return new Diagnostic(code, DiagnosticSeverity.Error,
                    message[(separator + 1)..].Trim());
        }
        return new Diagnostic("existing-npc-edit-invalid-data", DiagnosticSeverity.Error, message);
    }

    private static void AddStatsDiagnostics(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        NpcStatsPatch? stats)
    {
        if (stats is null || stats.IsEmpty) return;

        if (stats.XpValueOffset is not null || stats.Height is not null)
            diagnostics.Add(UnsupportedStatsDiagnostic(
                "Skyrim existing-NPC edits do not accept the Fallout 4 XP offset or appearance-affecting height."));
        if (stats.Flags is not { } flags) return;

        if (flags.Set.Concat(flags.Clear).Any(flag => !SafeSkyrimGameplayFlags.Contains(flag)))
            diagnostics.Add(UnsupportedStatsDiagnostic(
                "The requested flag is appearance-owned, level-union-owned, unsupported by Skyrim, or absent from the pinned NPC editor."));
        if (flags.Set.Intersect(flags.Clear).Any())
            diagnostics.Add(new Diagnostic("existing-npc-edit-flag-conflict", DiagnosticSeverity.Error,
                "A gameplay flag cannot be both enabled and disabled."));
    }

    private static readonly ImmutableHashSet<NpcFlag> SafeSkyrimGameplayFlags =
        ImmutableHashSet.Create(
            NpcFlag.Essential,
            NpcFlag.Respawn,
            NpcFlag.AutoCalcStats,
            NpcFlag.Unique,
            NpcFlag.DoesntAffectStealthMeter,
            NpcFlag.Protected,
            NpcFlag.Summonable,
            NpcFlag.DoesNotBleed,
            NpcFlag.OppositeGenderAnims,
            NpcFlag.SimpleActor,
            NpcFlag.IsGhost,
            NpcFlag.Invulnerable);

    private static Diagnostic UnsupportedStatsDiagnostic(string message) =>
        new("existing-npc-edit-stats-unsupported", DiagnosticSeverity.Error, message);

    private static PresetToNpcPackageArtifact CreateArtifact(
        string kind,
        string root,
        string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > int.MaxValue)
            throw new IOException($"Package artifact '{path}' is missing, empty, or too large.");
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new IOException($"Package artifact '{path}' escaped the staging root.");
        return new PresetToNpcPackageArtifact(
            kind,
            new AssetPath(relative),
            checked((int)info.Length),
            HashFile(path));
    }

    private static async ValueTask WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, JsonOptions));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("existing-npc-edit-reparse-refused",
                        DiagnosticSeverity.Error, $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("existing-npc-edit-path-inspection-failed",
                    DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static ExistingNpcEditReview RefusedReview(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, [], [], diagnostics.ToImmutable());

    private static ExistingNpcEditResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<MutationChange> changes = default) =>
        new(false, "REFUSED", null, null, null, null,
            changes.IsDefault ? [] : changes, null, diagnostics.ToImmutable());

    private static void Report(
        IProgress<ExistingNpcEditProgress>? progress,
        ExistingNpcEditStage stage,
        int percent,
        string message) =>
        progress?.Report(new ExistingNpcEditProgress(stage, percent, message));

    private static void TryDeleteOwnedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record EditVerificationEvidence(
        int SchemaVersion,
        string ArtifactKind,
        string TargetFormId,
        string OutputSha256,
        ImmutableArray<string> ObservedMasters,
        int MajorRecordCount,
        int NpcRecordCount,
        int SourceOwnedTargetCount,
        int SelfOwnedTargetCount,
        ImmutableArray<MutationChange> ObservedChanges,
        ImmutableArray<BethesdaRawPluginInventoryEntry> SourceInventory,
        ImmutableArray<BethesdaRawPluginInventoryEntry> OutputInventory,
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry> SourceGroups,
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry> OutputGroups,
        bool IndependentPreservation,
        bool Valid,
        bool TrueOverride,
        bool RuntimeAuthority);

    private sealed record EditProposalEvidence(
        int SchemaVersion,
        string ArtifactKind,
        string Edition,
        string InputPlugin,
        string InputSha256,
        string TargetFormId,
        string OutputPlugin,
        string SourcePluginName,
        string OutputKind,
        string? SourceDependencyPlugin,
        string? SourceDependencySha256,
        ImmutableArray<string> RequiredMasters,
        ImmutableArray<MutationChange> Changes,
        ImmutableArray<string> PreservedFields,
        string ApplyProposalSha256,
        string ApplyProposalPath,
        bool TrueOverride,
        bool AppearanceAssetsAffected,
        bool RuntimeAuthority);

    private sealed record RuntimeInstructionsEvidence(
        int SchemaVersion,
        string ArtifactKind,
        string Plugin,
        string TargetFormId,
        string OutputKind,
        string Instructions,
        bool RuntimeAuthority);
}
