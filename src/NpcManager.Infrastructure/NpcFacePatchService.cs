using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Validates and writes the explicit PNAM/HCLF face section without touching other NPC fields.</summary>
public sealed class NpcFacePatchService(IWorkspacePolicy policy, WorkspacePath labRoot) : INpcFacePatchService
{
    private static readonly ImmutableArray<string> PreservedFields = ["EDID", "FULL", "ACBS", "SNAM", "CNTO", "OTFT", "SPLO", "KWDA", "VMAD"];
    private static readonly JsonSerializerOptions ProposalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<NpcFacePatchProposal> AnalyzeAsync(NpcFacePatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var inputHash = File.Exists(request.InputPlugin.Value) ? ComputeHash(request.InputPlugin.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match the expected hash {expected}."));

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        var providers = ImmutableArray.CreateBuilder<NpcHeadPartProvider>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var current = BethesdaNpcFaceAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
                if (request.Patch.Replacement is { } replace)
                {
                    NpcHeadPartSelection[] replaced = current.HeadParts
                        .Where(item => SameReference(item.Reference, replace.Old)).ToArray();
                    if (replaced.Length != 1)
                        throw new InvalidDataException("Headpart replacement requires exactly one matching old PNAM entry.");
                    var next = new FormReference(new PluginName(Path.GetFileName(request.InputPlugin.Value)), replace.NewLocalFormId);
                    WorkspacePath previousProviderPath = ProviderPath(request, replace.Old.Plugin);
                    var previousProvider = BethesdaNpcFaceAdapter.ReadProviders(request.Edition, previousProviderPath,
                        [new NpcHeadPartSelection(replace.Old, NpcHeadPartType.Misc)]).Single();
                    var provider = BethesdaNpcFaceAdapter.ReadProviders(request.Edition, request.InputPlugin, [new NpcHeadPartSelection(next, NpcHeadPartType.Misc)]).Single();
                    if (current.HeadParts.Any(item => SameReference(item.Reference, next))) throw new InvalidDataException("Replacement headpart is already selected.");
                    if (provider.Type != previousProvider.Type)
                        diagnostics.Add(new Diagnostic("headpart-replacement-type-mismatch", DiagnosticSeverity.Error,
                            $"A '{previousProvider.Type.ToWireName()}' headpart cannot be replaced by '{provider.Type.ToWireName()}'."));
                    ValidatePatch(request with
                    {
                        Patch = request.Patch with
                        {
                            HeadParts = [new NpcHeadPartSelection(next, provider.Type)
                                { RawPnamType = provider.RawPnamType }]
                        }
                    }, current, providers, diagnostics);
                    changes.Add(new MutationChange("PNAM", replace.Old.ToString(), next.ToString()));
                }
                else ValidatePatch(request, current, providers, diagnostics);
                if (request.Patch.HeadParts is { } requested &&
                    !requested.Select(item => item.Reference).SequenceEqual(current.HeadParts.Select(item => item.Reference)))
                    changes.Add(new MutationChange("PNAM", string.Join(',', current.HeadParts.Select(FormatSelection)), string.Join(',', requested.Select(FormatSelection))));
                if (request.Patch.HairColor.IsSpecified && request.Patch.HairColor.Value != current.HairColor)
                    changes.Add(new MutationChange("HCLF", current.HairColor?.ToString() ?? "none", request.Patch.HairColor.Value?.ToString() ?? "none"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("face-read-failed", DiagnosticSeverity.Error,
                    $"NPC face data could not be read: {exception.Message}"));
            }
        }

        if (changes.Count == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("face-noop", DiagnosticSeverity.Info, "The requested headparts and hair color already match the NPC."));
        var proposal = new NpcFacePatchProposal(request.Edition, request.InputPlugin, request.OutputPlugin,
            request.TargetFormId, inputHash, changes.ToImmutable(), PreservedFields, providers.ToImmutable(), diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics)) WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    private WorkspacePath ProviderPath(
        NpcFacePatchRequest request,
        PluginName provider)
    {
        string inputPlugin = Path.GetFileName(request.InputPlugin.Value);
        if (string.Equals(
                inputPlugin,
                provider.Value,
                StringComparison.OrdinalIgnoreCase))
            return request.InputPlugin;

        var providerPath = new WorkspacePath(
            Path.Combine(request.DataRoot.Value, provider.Value));
        new FaceGeomHairRegionsWorkspaceBoundary(labRoot)
            .RequireExistingFileBeneath(
                request.DataRoot,
                providerPath,
                "old headpart provider");
        return providerPath;
    }

    public ValueTask<NpcFacePatchResult> ApplyAsync(NpcFacePatchRequest request, NpcFacePatchProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error, "The input changed after face analysis."));
        if (request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new NpcFacePatchResult(false, proposal, null, diagnostics.ToImmutable()));

        var temporary = request.OutputPlugin.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        var published = false;
        try
        {
            BethesdaNpcFaceAdapter.Write(request, new WorkspacePath(temporary));
            if (!File.Exists(temporary)) throw new IOException("The face adapter did not produce an output file.");
            File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
            published = true;
            VerifyWritten(request, diagnostics);
            if (HasErrors(diagnostics))
            {
                TryDelete(request.OutputPlugin.Value);
                return ValueTask.FromResult(new NpcFacePatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new NpcFacePatchResult(true, proposal, ComputeHash(request.OutputPlugin.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (published) TryDelete(request.OutputPlugin.Value);
            diagnostics.Add(new Diagnostic("face-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new NpcFacePatchResult(false, proposal, null, diagnostics.ToImmutable()));
        }
        finally { TryDelete(temporary); }
    }

    private static void ValidatePatch(NpcFacePatchRequest request, NpcFaceSnapshot current,
        ImmutableArray<NpcHeadPartProvider>.Builder providers, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Patch.IsEmpty)
        {
            diagnostics.Add(new Diagnostic("face-patch-empty", DiagnosticSeverity.Error, "At least one of headparts or hair-color must be specified."));
            return;
        }
        if (request.Patch.HeadParts is { } selections)
        {
            var inputPlugin = Path.GetFileName(request.InputPlugin.Value);
            var externalSelections = selections.Where(item =>
                !string.Equals(item.Reference.Plugin.Value, inputPlugin, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (externalSelections.Length > 0)
                diagnostics.Add(new Diagnostic("headpart-external-reference", DiagnosticSeverity.Error,
                    "Headpart references must resolve in the input plugin for this bounded writer."));
            var duplicates = selections.GroupBy(item => item.Reference).Where(group => group.Count() > 1).ToArray();
            if (duplicates.Length > 0) diagnostics.Add(new Diagnostic("headpart-duplicate", DiagnosticSeverity.Error, "Headpart FormIDs must be unique."));
            foreach (var type in Enum.GetValues<NpcHeadPartType>().Where(item => item != NpcHeadPartType.Misc && request.Patch.Replacement is null))
                if (selections.Count(item => item.Type == type) != 1)
                    diagnostics.Add(new Diagnostic("headpart-required-type", DiagnosticSeverity.Error,
                        $"Exactly one '{type.ToWireName()}' headpart is required."));
            if (selections.Any(item => (int)item.Type is < 0 or > 9))
                diagnostics.Add(new Diagnostic("headpart-type-invalid", DiagnosticSeverity.Error, "Headpart types must be in the TES4 range 0 through 9."));
            try
            {
                if (externalSelections.Length > 0) return;
                providers.AddRange(BethesdaNpcFaceAdapter.ReadProviders(request.Edition, request.InputPlugin, selections));
                foreach (var provider in providers)
                {
                    var requested = selections.First(item => item.Reference == provider.Reference);
                    if (requested.Type != provider.Type ||
                        requested.RawPnamType is not null && requested.PnamType != provider.PnamType)
                        diagnostics.Add(new Diagnostic("headpart-type-mismatch", DiagnosticSeverity.Error,
                            $"{provider.Reference} is '{provider.Type.ToWireName(provider.RawPnamType)}', not " +
                            $"'{requested.Type.ToWireName(requested.RawPnamType)}'."));
                    if (requested.Type != NpcHeadPartType.Misc && provider.IsExtra)
                        diagnostics.Add(new Diagnostic("headpart-extra-not-selectable", DiagnosticSeverity.Error,
                            $"{provider.Reference} is an HNAM extra and cannot be selected as a main headpart."));
                    if (provider.ValidRaces.Length > 0 && current.Race is { } race &&
                        !provider.ValidRaces.Any(item => SameReference(item, race)))
                        diagnostics.Add(new Diagnostic("headpart-race-incompatible", DiagnosticSeverity.Error,
                            $"{provider.Reference} is restricted to {string.Join(',', provider.ValidRaces)}, not the NPC race {race}."));
                    if (provider.ValidRaces.Length > 0 && current.Race is null)
                        diagnostics.Add(new Diagnostic("npc-race-missing", DiagnosticSeverity.Error, "A typed race is required to validate restricted headparts."));
                    var female = provider.Flags.Contains("Female", StringComparison.OrdinalIgnoreCase);
                    var male = provider.Flags.Contains("Male", StringComparison.OrdinalIgnoreCase);
                    if ((female || male) && ((current.Sex == NpcSex.Female && !female) || (current.Sex == NpcSex.Male && !male)))
                        diagnostics.Add(new Diagnostic("headpart-sex-incompatible", DiagnosticSeverity.Error,
                            $"{provider.Reference} is not compatible with the NPC sex {current.Sex}."));
                    if (string.IsNullOrWhiteSpace(provider.ModelPath))
                        diagnostics.Add(new Diagnostic("headpart-provider-missing", DiagnosticSeverity.Error,
                            $"{provider.Reference} has no model provider path."));
                    else if (!AssetExists(request.DataRoot.Value, provider.ModelPath))
                        diagnostics.Add(new Diagnostic("headpart-provider-missing", DiagnosticSeverity.Error,
                            $"Headpart provider '{provider.ModelPath}' for {provider.Reference} is absent under the data root."));
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic("headpart-provider-read-failed", DiagnosticSeverity.Error, exception.Message));
            }
        }
        if (request.Patch.HairColor is { IsSpecified: true, Value: { } color } &&
            !string.Equals(color.Plugin.Value, Path.GetFileName(request.InputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("hair-color-external-reference", DiagnosticSeverity.Error,
                "Hair color references must resolve in the input plugin for this bounded writer."));
    }

    private ImmutableArray<Diagnostic> ValidateRequest(NpcFacePatchRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Patch.Replacement is not null && request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("headpart-replacement-edition", DiagnosticSeverity.Error, "Output-owned headpart replacement supports Skyrim SE."));
        if (!request.InputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Input plugins must remain under the K-only lab root."));
        if (!request.OutputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error, "Output plugins must remain under the K-only lab root."));
        if (!request.DataRoot.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("data-root-outside-lab", DiagnosticSeverity.Error, "The provider data root must remain under the K-only lab root."));
        if (!File.Exists(request.InputPlugin.Value)) diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The input plugin does not exist."));
        if (!Directory.Exists(request.DataRoot.Value)) diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error, "The provider data root does not exist."));
        var parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; face patches never overwrite."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("plugin-identity-mismatch", DiagnosticSeverity.Error, "Input and output must keep the same plugin filename."));
        if (!IsPluginPath(request.InputPlugin.Value) || !IsPluginPath(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Input and output must use .esp, .esm, or .esl."));
        if (requireExpectedHash && request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input-plugin");
        AddReparseDiagnostic(diagnostics, request.DataRoot.Value, "data-root");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static void VerifyWritten(NpcFacePatchRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var after = BethesdaNpcFaceAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
        if (request.Patch.HeadParts is { } expected && !after.HeadParts.Select(item => item.Reference).SequenceEqual(expected.Select(item => item.Reference)))
            diagnostics.Add(new Diagnostic("face-headparts-mismatch", DiagnosticSeverity.Error, "Output PNAM does not match the requested ordered headpart list."));
        if (request.Patch.HairColor.IsSpecified && after.HairColor != request.Patch.HairColor.Value)
            diagnostics.Add(new Diagnostic("face-hair-color-mismatch", DiagnosticSeverity.Error, "Output HCLF does not match the requested hair color."));
        if (request.Patch.Replacement is { } replacement)
        {
            var before = BethesdaNpcFaceAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
            var next = new FormReference(new PluginName(Path.GetFileName(request.InputPlugin.Value)), replacement.NewLocalFormId);
            if (!after.HeadParts.Select(item => item.Reference).SequenceEqual(before.HeadParts.Select(item => SameReference(item.Reference, replacement.Old) ? next : item.Reference)))
                diagnostics.Add(new Diagnostic("face-headparts-mismatch", DiagnosticSeverity.Error, "Output PNAM differs from the single requested replacement."));
        }
    }

    private static bool AssetExists(string dataRoot, string modelPath)
    {
        if (Path.IsPathRooted(modelPath)) return false;
        var normalized = modelPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment == "..")) return false;
        return File.Exists(Path.Combine(dataRoot, normalized)) || File.Exists(Path.Combine(dataRoot, "meshes", normalized));
    }

    private static string FormatSelection(NpcHeadPartSelection value) => $"{value.Type.ToWireName()}={value.Reference}";
    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(left.Plugin.Value, right.Plugin.Value, StringComparison.OrdinalIgnoreCase);
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static bool IsPluginPath(string path) => Path.GetExtension(path) is ".esp" or ".esm" or ".esl";
    private static Sha256Hash ComputeHash(string path) { using var stream = File.OpenRead(path); return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()); }
    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, $"The {role} could not be inspected: {exception.Message}")); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }
    private static void WriteProposal(WorkspacePath path, NpcFacePatchProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, ProposalOptions), new UTF8Encoding(false));
                File.Move(temporary, path.Value, overwrite: false);
            }
            finally { TryDelete(temporary); }
        }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
