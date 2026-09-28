using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Two-pass production transaction for accepted Skyrim CharGen options and one
/// options-bound native FaceTint DDS. Review is proposal-only; Apply creates a
/// fresh owned output root and rolls it back on any downstream refusal.
/// </summary>
public sealed class SkyrimCharGenOptionsProductionService(
    IFaceGenOptionsService optionsReader,
    IFaceGenOptionsDocumentWriter optionsWriter,
    ISkyrimCharGenFaceTintBakeService faceTintBake,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimCharGenOptionsProductionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<SkyrimCharGenOptionsProductionReviewResult> ReviewAsync(
        SkyrimCharGenOptionsProductionReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateProposalOutput(request.OutputProposal, diagnostics);
        ValidateFreshOutputRoot(request.OutputRoot, diagnostics);
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-data-root-missing",
                "The copied Skyrim Data root does not exist."));
        }
        if (request.PluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-plugins-required",
                "At least one ordered copied plugin is required for the native FaceTint bake."));
        }
        diagnostics.AddRange(
            SkyrimCharGenNativeFaceTintConsumptionRules.Validate(
                request.AcceptedOptions));
        if (HasErrors(diagnostics)) return RefusedReview(diagnostics);

        FaceGenOptionsResult source = await optionsReader.ValidateAsync(
            new FaceGenOptionsRequest(
                GameEdition.SkyrimSpecialEdition,
                request.SourceOptions,
                null,
                request.SourceOptionsSha256,
                false),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(source.Diagnostics);
        if (!source.IsValid)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-source-review",
                "The exact opening options artifact is stale or invalid."));
            return RefusedReview(diagnostics);
        }

        var proposal = new SkyrimCharGenOptionsProductionProposal(
            "1",
            "skyrim-chargen-options-production-proposal",
            request.SourceOptions.Value,
            request.SourceOptionsSha256.Value,
            request.AcceptedOptions,
            request.DataRoot.Value,
            request.PluginOrder.Select(item => item.Value).ToImmutableArray(),
            request.Npc.ToString(),
            request.ExpectedSex.ToString().ToLowerInvariant(),
            request.ExpectedRace.ToString(),
            request.OutputRoot.Value,
            RuntimeAuthority: false);
        byte[] bytes = Serialize(proposal);
        Sha256Hash hash = Hash(bytes);
        try
        {
            WriteAtomically(request.OutputProposal, bytes);
            return new SkyrimCharGenOptionsProductionReviewResult(
                true,
                request.OutputProposal,
                hash,
                proposal,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-write",
                exception.Message));
            return RefusedReview(diagnostics);
        }
    }

    public async ValueTask<SkyrimCharGenOptionsProductionApplyResult> ApplyAsync(
        SkyrimCharGenOptionsProductionApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot,
            request.ProposalPath));
        if (!File.Exists(request.ProposalPath.Value))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-missing",
                "The reviewed production proposal does not exist."));
            return RefusedApply(diagnostics);
        }

        byte[] proposalBytes;
        SkyrimCharGenOptionsProductionProposal? proposal;
        Sha256Hash proposalHash;
        try
        {
            proposalBytes = await File.ReadAllBytesAsync(
                request.ProposalPath.Value,
                cancellationToken).ConfigureAwait(false);
            proposalHash = Hash(proposalBytes);
            if (proposalHash != request.ExpectedProposalSha256)
            {
                diagnostics.Add(Error(
                    "skyrim-chargen-production-proposal-stale",
                    "The production proposal SHA-256 changed after review."));
                return RefusedApply(diagnostics, proposalHash);
            }
            using var document = JsonDocument.Parse(proposalBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            proposal = document.RootElement
                .Deserialize<SkyrimCharGenOptionsProductionProposal>(
                    JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-read",
                exception.Message));
            return RefusedApply(diagnostics);
        }

        if (proposal is null)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-invalid",
                "The reviewed production proposal has an invalid typed contract."));
            return RefusedApply(diagnostics, proposalHash);
        }

        bool sourceHashValid = TryParseHash(proposal.SourceOptionsSha256,
            out Sha256Hash sourceHash);
        bool npcValid = FormReference.TryParse(proposal.Npc,
            out FormReference npc);
        bool raceValid = FormReference.TryParse(proposal.ExpectedRace,
            out FormReference race);
        bool sexValid = Enum.TryParse(proposal.ExpectedSex, true,
            out NpcSex sex);
        bool contractValid = proposal.SchemaVersion == "1" &&
                             proposal.ArtifactKind ==
                             "skyrim-chargen-options-production-proposal" &&
                             !proposal.RuntimeAuthority &&
                             sourceHashValid &&
                             npcValid &&
                             raceValid &&
                             sexValid &&
                             !proposal.PluginOrder.IsDefaultOrEmpty;
        if (!contractValid)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-invalid",
                "The reviewed production proposal has an invalid typed contract."));
            return RefusedApply(diagnostics, proposalHash);
        }

        var outputRoot = new WorkspacePath(proposal.OutputRoot);
        ValidateFreshOutputRoot(outputRoot, diagnostics);
        diagnostics.AddRange(
            SkyrimCharGenNativeFaceTintConsumptionRules.Validate(
                proposal.AcceptedOptions));
        FaceGenOptionsResult source = await optionsReader.ValidateAsync(
            new FaceGenOptionsRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(proposal.SourceOptions),
                null,
                sourceHash,
                false),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(source.Diagnostics);
        if (!source.IsValid)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-source-stale",
                "The opening options artifact changed after proposal review."));
        }
        if (HasErrors(diagnostics))
            return RefusedApply(diagnostics, proposalHash);

        bool outputRootCreated = false;
        try
        {
            Directory.CreateDirectory(outputRoot.Value);
            outputRootCreated = true;
            var optionsOutput = new WorkspacePath(Path.Combine(
                outputRoot.Value,
                "accepted-options.json"));
            FaceGenOptionsDocumentWriteResult options =
                await optionsWriter.WriteAsync(
                    new FaceGenOptionsDocumentWriteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        proposal.AcceptedOptions,
                        optionsOutput),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(options.Diagnostics);
            if (!options.Written || options.OutputSha256 is null)
            {
                Rollback(outputRoot, diagnostics);
                return new SkyrimCharGenOptionsProductionApplyResult(
                    false,
                    proposalHash,
                    options,
                    null,
                    diagnostics.ToImmutable());
            }

            var plugins = proposal.PluginOrder
                .Select(value => new PluginName(value))
                .ToImmutableArray();
            SkyrimCharGenFaceTintBakeResult bake =
                await faceTintBake.BuildAsync(
                    new SkyrimCharGenFaceTintBakeRequest(
                        optionsOutput,
                        options.OutputSha256.Value,
                        new WorkspacePath(proposal.DataRoot),
                        plugins,
                        npc,
                        sex,
                        race,
                        new WorkspacePath(Path.Combine(outputRoot.Value,
                            "facetint.dds")),
                        new WorkspacePath(Path.Combine(outputRoot.Value,
                            "options-to-facetint-receipt.json"))),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(bake.Diagnostics);
            if (!bake.Written)
            {
                Rollback(outputRoot, diagnostics);
                return new SkyrimCharGenOptionsProductionApplyResult(
                    false,
                    proposalHash,
                    options,
                    bake,
                    diagnostics.ToImmutable());
            }

            return new SkyrimCharGenOptionsProductionApplyResult(
                true,
                proposalHash,
                options,
                bake,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (outputRootCreated) Rollback(outputRoot, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            if (outputRootCreated) Rollback(outputRoot, diagnostics);
            diagnostics.Add(Error(
                "skyrim-chargen-production-apply",
                exception.Message));
            return RefusedApply(diagnostics, proposalHash);
        }
    }

    private void ValidateProposalOutput(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? parent = Path.GetDirectoryName(output.Value);
        if (parent is null)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-parent",
                "The proposal requires an explicit parent directory."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot,
            new WorkspacePath(parent)));
        if (!Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-parent-missing",
                "The proposal parent must already exist."));
        }
        if (File.Exists(output.Value))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-exists",
                "The proposal output already exists."));
        }
        if (!string.Equals(Path.GetExtension(output.Value), ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-proposal-extension",
                "The proposal output must use the .json extension."));
        }
    }

    private void ValidateFreshOutputRoot(
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.Evaluate(labRoot, outputRoot));
        string? parent = Path.GetDirectoryName(outputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-output-parent",
                "The fresh output root requires an existing parent directory."));
        }
        if (Directory.Exists(outputRoot.Value) || File.Exists(outputRoot.Value))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-output-exists",
                "The production output root must not already exist."));
        }
    }

    private static byte[] Serialize(
        SkyrimCharGenOptionsProductionProposal proposal) =>
        new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(proposal, JsonOptions) +
            Environment.NewLine);

    private static void WriteAtomically(WorkspacePath output, byte[] bytes)
    {
        string temporary = output.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N",
                               CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, output.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void Rollback(
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (Directory.Exists(outputRoot.Value))
                Directory.Delete(outputRoot.Value, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-production-rollback-failed",
                $"The owned fresh output root could not be removed: {exception.Message}"));
        }
    }

    private static void ValidateDuplicateProperties(
        JsonElement element,
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    diagnostics.Add(Error(
                        "skyrim-chargen-production-proposal-duplicate",
                        $"Duplicate proposal property '{path}.{property.Name}'."));
                }
                ValidateDuplicateProperties(property.Value,
                    $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                ValidateDuplicateProperties(item, $"{path}[{index++}]",
                    diagnostics);
        }
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool TryParseHash(string? value, out Sha256Hash hash)
    {
        hash = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            hash = new Sha256Hash(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimCharGenOptionsProductionReviewResult RefusedReview(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, null, diagnostics.ToImmutable());

    private static SkyrimCharGenOptionsProductionApplyResult RefusedApply(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? proposalHash = null) =>
        new(false, proposalHash, null, null, diagnostics.ToImmutable());
}
