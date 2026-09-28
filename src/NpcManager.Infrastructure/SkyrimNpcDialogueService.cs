using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed class SkyrimNpcDialogueService : ISkyrimNpcDialogueService
{
    private readonly SkyrimDialogueDocumentSupport documents;
    private readonly ImmutableArray<ISkyrimDialogueCoverageTemplate> templates;
    private readonly Lazy<ILocalOperationJournal> journal;
    public SkyrimNpcDialogueService(WorkspacePath workspaceRoot, IEnumerable<ISkyrimDialogueCoverageTemplate>? templates = null, ILocalOperationJournal? journal = null,
        Func<ILocalOperationJournal>? journalFactory = null)
    {
        documents = new(workspaceRoot); this.templates = templates?.ToImmutableArray() ?? []; this.journal = new(() => journal ?? journalFactory?.Invoke() ?? new LocalOperationJournal(workspaceRoot));
    }

    public async ValueTask<SkyrimDialogueStageResult<SkyrimDialogueManifest>> CreateTemplateAsync(SkyrimDialogueTemplateRequest request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); SkyrimDialogueManifest? result = null; var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); documents.Fresh(request.ManifestOutput.Value);
            var template = templates.SingleOrDefault(x => x.Name == request.Template) ?? throw new InvalidDataException("dialogue-template-unknown: Unknown coverage template.");
            var profile = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimDialogueProfile>(documents.Read(request.Profile.Value));
            result = template.Create(request.Npc, profile, request.Language);
        }
        catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { diagnostics.Add(SkyrimDialogueDocumentSupport.Diagnostic(ex)); }
        return await Finish("npc dialogue analyze", request, result, result is not null ? request.ManifestOutput : null, diagnostics, start, cancellationToken);
    }

    public async ValueTask<SkyrimDialogueStageResult<SkyrimDialogueProposal>> AnalyzeAsync(SkyrimDialogueAnalyzeRequest request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); SkyrimDialogueProposal? proposal = null; var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); documents.Fresh(request.Output.Value);
            var manifest = documents.Read<SkyrimDialogueManifest>(request.Manifest.Value, request.ManifestSha256);
            ValidateManifest(manifest);
            byte[] source = documents.Read(request.Plugin.Value, request.PluginSha256);
            ValidateSample(manifest, request.SampleAuthority.Value, request.SampleAuthoritySha256);
            var masters = ReadMasters(source, manifest.Npc.Plugin, request.DataRoot.Value, request.LoadOrder);
            var authority = new BethesdaSkyrimVanillaDialogueAuthority(manifest, source, masters);
            var seq = ReadSourceSeq(request.Plugin.Value, request.DataRoot.Value);
            proposal = BethesdaSkyrimDialogueScratchWriter.Plan(request, manifest, source, authority) with
            { CopiedMasterSha256 = masters.ToImmutableDictionary(x => x.Key, x => SkyrimNpcVoiceDocumentCodec.Hash(x.Value), StringComparer.OrdinalIgnoreCase), SourceSeqPath = seq.Path, SourceSeqSha256 = seq.Hash };
            diagnostics.AddRange(proposal.Diagnostics);
        }
        catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { diagnostics.Add(SkyrimDialogueDocumentSupport.Diagnostic(ex)); }
        return await Finish("npc dialogue analyze", request, proposal, proposal is not null ? request.Output : null, diagnostics, start, cancellationToken);
    }

    public async ValueTask<SkyrimDialogueStageResult<SkyrimDialogueOutputManifest>> ApplyAsync(SkyrimDialogueApplyRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); SkyrimDialogueOutputManifest? output = null; WorkspacePath? path = null;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); documents.Fresh(request.OutputRoot.Value);
            var proposal = documents.Read<SkyrimDialogueProposal>(request.Proposal.Value, request.ProposalSha256);
            var (source, authority, sourceSeqBytes) = BindProposal(proposal);
            var synthesis = documents.Read<SkyrimVoiceSynthesisManifest>(request.Synthesis.Value, request.SynthesisSha256);
            ValidateSynthesis(proposal, synthesis);
            FaceFxLipSyncAdapter? lip = request.LipTools is { } tools ? new(tools) : null;
            if (lip is null) diagnostics.Add(new(SkyrimNpcDialogueDiagnosticCodes.LipToolMissing, DiagnosticSeverity.Warning, "No lip tool was supplied; loose WAV output has no generated lip animation."));
            diagnostics.AddRange(proposal.Diagnostics);
            byte[] scratch = BethesdaSkyrimDialogueScratchWriter.Write(proposal, authority);
            byte[] plugin = BethesdaSkyrimDialogueSplicer.Splice(source, scratch, proposal);
            var readback = BethesdaSkyrimDialogueVerifier.Verify(source, plugin, proposal, authority);
            if (readback.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException(readback[0].Code + ": " + readback[0].Message);
            string parent = Path.GetDirectoryName(request.OutputRoot.Value)!; documents.Admit(parent); Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".dialogue-" + Guid.NewGuid().ToString("N")); documents.Fresh(staging); Directory.CreateDirectory(staging);
            // A failed transaction retains its private staging evidence; final output is published only after all checks pass.
            Sha256Hash pluginHash = documents.WriteBytes(documents.Relative(staging, proposal.SourcePlugin.Value).Value, plugin);
            var assets = await SkyrimDialogueVoiceAssetWriter.WriteAsync(proposal, synthesis, Path.GetDirectoryName(request.Synthesis.Value)!, staging, lip, documents, progress, cancellationToken);
            var quest = proposal.Records.Single(x => x.Signature == "QUST"); var voice = proposal.Records.Single(x => x.Signature == "VTYP");
            string seqRelative = "Seq/" + Path.GetFileNameWithoutExtension(proposal.SourcePlugin.Value) + ".seq";
            byte[] seq = SkyrimDialogueVoiceAssetWriter.Seq(sourceSeqBytes, (uint)proposal.MasterOrder.Length << 24 | quest.LocalFormId);
            Sha256Hash seqHash = documents.WriteBytes(documents.Relative(staging, seqRelative).Value, seq);
            Sha256Hash scriptHash = documents.WriteBytes(documents.Relative(staging, SkyrimDialogueVoiceAssetWriter.ScriptRelative).Value, SkyrimDialogueVoiceAssetWriter.Script());
            output = new(SkyrimNpcDialogueSchemas.OutputManifest, request.Proposal.Value, request.ProposalSha256, request.Synthesis.Value, request.SynthesisSha256,
                request.OutputRoot.Value, proposal.SourcePlugin, pluginHash, proposal.LightPlugin, voice.EditorId, voice.LocalFormId, quest.EditorId, quest.LocalFormId,
                proposal.Records, assets, seqRelative, seqHash, SkyrimDialogueVoiceAssetWriter.ScriptRelative, scriptHash, lip is null ? "none" : lip.SupportsFuz ? "FaceFXWrapper+xWMAEncode+fuz_extractor" : "FaceFXWrapper", diagnostics.ToImmutable());
            string stagedManifest = documents.Relative(staging, "dialogue-output-manifest.json").Value;
            documents.Write(stagedManifest, output);
            cancellationToken.ThrowIfCancellationRequested(); documents.Fresh(request.OutputRoot.Value); documents.Admit(staging);
            Directory.Move(staging, request.OutputRoot.Value);
            path = documents.Relative(request.OutputRoot.Value, "dialogue-output-manifest.json");
        }
        catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { output = null; diagnostics.Add(SkyrimDialogueDocumentSupport.Diagnostic(ex)); }
        return await Finish("npc dialogue apply", request, output, path, diagnostics, start, cancellationToken, alreadyWritten: true);
    }

    public async ValueTask<SkyrimDialogueStageResult<SkyrimDialogueVerification>> VerifyAsync(SkyrimDialogueVerifyRequest request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SkyrimDialogueBudget budget = new(0, 0, 64, 2048, false); int records = 0, infos = 0, audio = 0, lips = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = documents.Read<SkyrimDialogueOutputManifest>(request.Manifest.Value, request.ManifestSha256, SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding);
            if (output.Schema != SkyrimNpcDialogueSchemas.OutputManifest || output.Records.IsDefault || output.Assets.IsDefault || output.Assets.Any(x => x is null) ||
                output.LipTool is not ("none" or "FaceFXWrapper" or "FaceFXWrapper+xWMAEncode+fuz_extractor"))
                throw new InvalidDataException("dialogue-verify-document-binding: Invalid output schema or record/asset arrays.");
            if (output.LipTool == "none") diagnostics.Add(new(SkyrimNpcDialogueDiagnosticCodes.LipToolMissing, DiagnosticSeverity.Warning,
                "Audio-only output: no generated LIP animation. Static verification does not establish complete lip-sync delivery or game playback."));
            documents.Admit(output.PackageRoot);
            if (!new WorkspacePath(request.Manifest.Value).IsUnder(new WorkspacePath(output.PackageRoot))) throw new InvalidDataException("dialogue-verify-document-binding: Manifest is outside its package.");
            var proposal = documents.Read<SkyrimDialogueProposal>(output.ProposalPath, output.ProposalSha256, SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding);
            var (source, authority, sourceSeqBytes) = BindProposal(proposal);
            var synthesis = documents.Read<SkyrimVoiceSynthesisManifest>(output.SynthesisPath, output.SynthesisSha256, SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding);
            ValidateSynthesis(proposal, synthesis);
            budget = proposal.Budget; records = proposal.Budget.OwnedRecords + proposal.Records.Length; infos = proposal.Manifest.Lines.Length;
            var voice = proposal.Records.Single(x => x.Signature == "VTYP"); var quest = proposal.Records.Single(x => x.Signature == "QUST");
            if (output.Plugin != proposal.SourcePlugin || output.LightPlugin != proposal.LightPlugin || output.VoiceTypeLocalFormId != voice.LocalFormId || output.VoiceTypeEditorId != voice.EditorId ||
                output.QuestLocalFormId != quest.LocalFormId || output.QuestEditorId != quest.EditorId || !output.Records.SequenceEqual(proposal.Records) || output.Assets.Length != proposal.Assets.Length)
                throw new InvalidDataException("dialogue-verify-document-binding: Output identities differ from the proposal.");
            string pluginPath = documents.Relative(output.PackageRoot, output.Plugin.Value).Value;
            byte[] plugin = documents.Read(pluginPath);
            diagnostics.AddRange(BethesdaSkyrimDialogueVerifier.Verify(source, plugin, proposal, authority));
            if (SkyrimNpcVoiceDocumentCodec.Hash(plugin) != output.PluginSha256) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Output plugin hash changed.");
            foreach (var plan in proposal.Assets)
            {
                var assets = output.Assets.Where(x => x.LineId == plan.LineId).ToArray();
                if (assets.Length != 1) { Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Missing or duplicate asset row."); continue; }
                var asset = assets[0]; var synthesisLine = synthesis.Lines.Single(x => x.LineId == plan.LineId);
                string wav = plan.RelativeDirectory + "/" + plan.FileStem + ".wav";
                if (asset.InfoLocalFormId != plan.InfoLocalFormId || asset.Wav != wav)
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Audio identity differs from the synthesis and proposal.");
                byte[]? bytes = Asset(asset.Wav, asset.WavSha256, SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing);
                if (bytes is not null)
                {
                    SkyrimDialogueVoiceAssetWriter.CheckDelivery(bytes);
                    string original = documents.Relative(Path.GetDirectoryName(output.SynthesisPath)!, synthesisLine.OutputFile!).Value;
                    byte[] expectedAudio = SkyrimDialogueVoiceAssetWriter.PrepareDelivery(documents.Read(original, synthesisLine.OutputSha256, SkyrimNpcDialogueDiagnosticCodes.SynthesisIncomplete));
                    if (!bytes.AsSpan().SequenceEqual(expectedAudio))
                        Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Delivery audio differs from its hash-bound synthesis: " + plan.LineId);
                    audio++;
                }
                if (output.LipTool == "none" && (asset.Lip is not null || asset.Fuz is not null) || output.LipTool != "none" && asset.Lip is null)
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "Lip tool claim does not match assets.");
                if ((output.LipTool == "FaceFXWrapper+xWMAEncode+fuz_extractor") != (asset.Fuz is not null))
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "FUZ tool claim does not match assets.");
                if (asset.Lip is not null)
                {
                    if (asset.Lip != Path.ChangeExtension(wav, ".lip") || asset.LipSha256 is null) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "LIP identity mismatch.");
                    else if (Asset(asset.Lip, asset.LipSha256.Value, SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing) is not null) lips++;
                }
                if (asset.Fuz is not null)
                {
                    if (asset.Fuz != Path.ChangeExtension(wav, ".fuz") || asset.FuzSha256 is null) Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "FUZ identity mismatch.");
                    else
                    {
                        byte[]? fuz = Asset(asset.Fuz, asset.FuzSha256.Value, SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing);
                        byte[]? lip = asset.Lip is not null && asset.LipSha256 is { } lipHash ? Asset(asset.Lip, lipHash, SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing) : null;
                        if (fuz is not null && (lip is null || !FaceFxLipSyncAdapter.HasExpectedFuzPayload(fuz, lip)))
                            Error(SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding, "FUZ header or embedded LIP mismatch.");
                    }
                }
            }
            string seqName = "Seq/" + Path.GetFileNameWithoutExtension(output.Plugin.Value) + ".seq";
            if (output.SeqFile != seqName || output.SeqSha256 is null) Error(SkyrimNpcDialogueDiagnosticCodes.VerifySeqMissing, "SEQ binding is absent.");
            else
            {
                byte[]? seq = Asset(output.SeqFile, output.SeqSha256.Value, SkyrimNpcDialogueDiagnosticCodes.VerifySeqMissing);
                uint rawQuest = (uint)proposal.MasterOrder.Length << 24 | output.QuestLocalFormId;
                if (seq is not null && (seq.Length % 4 != 0 || !Enumerable.Range(0, seq.Length / 4).Any(i => BinaryPrimitives.ReadUInt32LittleEndian(seq.AsSpan(i * 4)) == rawQuest)))
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifySeqMissing, "SEQ does not include the quest.");
                if (seq is not null && !seq.SequenceEqual(SkyrimDialogueVoiceAssetWriter.Seq(sourceSeqBytes, rawQuest)))
                    Error(SkyrimNpcDialogueDiagnosticCodes.VerifySeqMissing, "SEQ did not preserve the hash-bound original entries.");
            }
            if (output.ScriptFile != SkyrimDialogueVoiceAssetWriter.ScriptRelative || output.ScriptSha256 != SkyrimDialogueVoiceAssetWriter.ScriptHash)
                Error(SkyrimNpcDialogueDiagnosticCodes.VerifyScriptMismatch, "Product script identity changed.");
            else _ = Asset(output.ScriptFile, SkyrimDialogueVoiceAssetWriter.ScriptHash, SkyrimNpcDialogueDiagnosticCodes.VerifyScriptMismatch);

            byte[]? Asset(string relative, Sha256Hash hash, string code)
            {
                try { return documents.Read(documents.Relative(output.PackageRoot, relative).Value, hash, code); }
                catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { Error(code, ex.Message); return null; }
            }
        }
        catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { diagnostics.Add(SkyrimDialogueDocumentSupport.Diagnostic(ex)); }
        bool verified = !diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
        var result = new SkyrimDialogueVerification(SkyrimNpcDialogueSchemas.Verification, request.Manifest.Value, request.ManifestSha256, verified, records, infos, audio, lips, budget, diagnostics.ToImmutable());
        return await Finish("npc dialogue verify", request, result, null, diagnostics, start, cancellationToken);
        void Error(string code, string message) => diagnostics.Add(new(code, DiagnosticSeverity.Error, message));
    }

    private Dictionary<string, byte[]> ReadMasters(byte[] source, PluginName plugin, string dataRoot, ImmutableArray<PluginName> loadOrder)
    {
        documents.Admit(dataRoot);
        using var stream = new MemoryStream(source, writable: false);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(stream, SkyrimRelease.SkyrimSE, ModKey.FromNameAndExtension(plugin.Value));
        string[] masters = mod.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToArray();
        var declared = loadOrder.Select(x => x.Value).ToArray();
        if (declared.Distinct(StringComparer.OrdinalIgnoreCase).Count() != declared.Length)
            throw new InvalidDataException("dialogue-condition-unresolved: Load order contains duplicate plugins.");
        var positions = declared.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var copied = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plugin.Value };
        var edges = new List<(string Parent, string Master)>();
        foreach (string master in masters) Visit(plugin.Value, master);
        var direct = masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!declared.Where(direct.Contains).SequenceEqual(masters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("dialogue-condition-unresolved: Load order must preserve the source plugin's declared direct master order.");
        foreach (var edge in edges)
            if (positions.TryGetValue(edge.Parent, out int parentIndex) && positions[edge.Master] >= parentIndex)
                throw new InvalidDataException($"dialogue-condition-unresolved: Out-of-order copied master edge {edge.Parent} -> {edge.Master}; dependency must precede its dependent.");
        return declared.Where(copied.ContainsKey).ToDictionary(name => name, name => copied[name], StringComparer.OrdinalIgnoreCase);

        void Visit(string parent, string name)
        {
            edges.Add((parent, name));
            if (visiting.Contains(name))
                throw new InvalidDataException($"dialogue-condition-unresolved: Cyclic copied master edge {parent} -> {name}.");
            if (!positions.ContainsKey(name))
                throw new InvalidDataException($"dialogue-condition-unresolved: Unlisted copied master edge {parent} -> {name}.");
            if (copied.ContainsKey(name)) return;
            byte[] bytes;
            try { bytes = documents.Read(documents.Relative(dataRoot, name).Value); }
            catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex))
            { throw new InvalidDataException($"dialogue-condition-unresolved: Cannot read copied master edge {parent} -> {name}: {ex.Message}", ex); }
            visiting.Add(name);
            using var masterStream = new MemoryStream(bytes, writable: false);
            using var master = SkyrimMod.CreateFromBinaryOverlay(masterStream, SkyrimRelease.SkyrimSE, ModKey.FromNameAndExtension(name));
            foreach (var dependency in master.ModHeader.MasterReferences) Visit(name, dependency.Master.FileName.String);
            visiting.Remove(name);
            copied.Add(name, bytes);
        }
    }
    private (byte[] Source, BethesdaSkyrimVanillaDialogueAuthority Authority, byte[]? SourceSeq) BindProposal(SkyrimDialogueProposal proposal)
    {
        if (proposal.Schema != SkyrimNpcDialogueSchemas.Proposal || proposal.Status != SkyrimDialogueProposalStatus.ReadyForReviewedWrite || proposal.Manifest is null ||
            proposal.CopiedMasterSha256 is null || proposal.Records.IsDefault || proposal.Assets.IsDefault)
            throw new InvalidDataException("dialogue-manifest-invalid: Apply and verify require an admitted proposal.");
        var manifest = documents.Read<SkyrimDialogueManifest>(proposal.ManifestPath, proposal.ManifestSha256);
        ValidateManifest(manifest);
        if (!SkyrimNpcVoiceDocumentCodec.Serialize(manifest).SequenceEqual(SkyrimNpcVoiceDocumentCodec.Serialize(proposal.Manifest))) throw new InvalidDataException("dialogue-manifest-hash-mismatch: Embedded manifest changed.");
        byte[] source = documents.Read(proposal.SourcePluginPath, proposal.SourcePluginSha256);
        var masters = ReadMasters(source, proposal.SourcePlugin, proposal.DataRoot, proposal.LoadOrder);
        if (masters.Count != proposal.CopiedMasterSha256.Count || masters.Any(x => !proposal.CopiedMasterSha256.TryGetValue(x.Key, out Sha256Hash hash) || hash != SkyrimNpcVoiceDocumentCodec.Hash(x.Value)))
            throw new InvalidDataException("dialogue-source-hash-mismatch: Copied master authority changed after analyze.");
        var authority = new BethesdaSkyrimVanillaDialogueAuthority(manifest, source, masters);
        var seq = ReadSourceSeq(proposal.SourcePluginPath, proposal.DataRoot);
        if (seq.Path != proposal.SourceSeqPath || seq.Hash != proposal.SourceSeqSha256)
            throw new InvalidDataException("dialogue-source-hash-mismatch: Source SEQ path, bytes, or reviewed absence changed after analyze.");
        var request = new SkyrimDialogueAnalyzeRequest(new(proposal.ManifestPath), proposal.ManifestSha256, new(proposal.SourcePluginPath), proposal.SourcePluginSha256,
            new(proposal.DataRoot), proposal.LoadOrder, new(proposal.SampleAuthorityPath), proposal.SampleAuthoritySha256, documents.Root);
        var replan = BethesdaSkyrimDialogueScratchWriter.Plan(request, manifest, source, authority) with
        { CopiedMasterSha256 = proposal.CopiedMasterSha256, SourceSeqPath = seq.Path, SourceSeqSha256 = seq.Hash };
        if (!SkyrimNpcVoiceDocumentCodec.Serialize(replan).SequenceEqual(SkyrimNpcVoiceDocumentCodec.Serialize(proposal)))
            throw new InvalidDataException("dialogue-verify-document-binding: Proposal differs from independently recalculated allocation.");
        ValidateSample(manifest, proposal.SampleAuthorityPath, proposal.SampleAuthoritySha256);
        return (source, authority, seq.Bytes);
    }
    private (string Path, Sha256Hash? Hash, byte[]? Bytes) ReadSourceSeq(string sourcePlugin, string dataRoot)
    {
        string relative = "Seq/" + Path.GetFileNameWithoutExtension(sourcePlugin) + ".seq";
        string adjacent = documents.Relative(Path.GetDirectoryName(sourcePlugin)!, relative).Value;
        string copied = documents.Relative(dataRoot, relative).Value;
        string selected = File.Exists(adjacent) || !File.Exists(copied) ? adjacent : copied;
        byte[]? bytes = File.Exists(selected) ? documents.Read(selected, allowEmpty: true) : null;
        if (bytes is not null && bytes.Length % 4 != 0) throw new InvalidDataException("dialogue-verify-seq-missing: Source SEQ is malformed.");
        return (selected, bytes is null ? null : SkyrimNpcVoiceDocumentCodec.Hash(bytes), bytes);
    }
    private SkyrimVoiceSampleAuthority ValidateSample(SkyrimDialogueManifest manifest, string path, Sha256Hash hash)
    {
        var sample = documents.Read<SkyrimVoiceSampleAuthority>(path, hash);
        if (sample.Schema != SkyrimNpcVoiceSchemas.Sample || sample.Plugin != manifest.Npc.Plugin || sample.FormId != manifest.Npc.FormId ||
            sample.EditorId != manifest.Npc.EditorId || sample.VoicePrefix != manifest.Npc.VoicePrefix)
            throw new InvalidDataException("dialogue-manifest-invalid: Sample authority belongs to another NPC identity.");
        SkyrimDialogueVoiceAssetWriter.CheckWave(documents.Read(sample.NormalizedPath, sample.NormalizedSha256)); return sample;
    }
    private static void ValidateManifest(SkyrimDialogueManifest manifest)
    {
        if (manifest.Schema != SkyrimNpcDialogueSchemas.Manifest || manifest.Npc is null || manifest.Profile is null || manifest.Lines.IsDefaultOrEmpty ||
            string.IsNullOrWhiteSpace(manifest.Npc.Plugin.Value) || string.IsNullOrWhiteSpace(manifest.Npc.VoicePrefix) || string.IsNullOrWhiteSpace(manifest.QuestEditorId) ||
            string.IsNullOrWhiteSpace(manifest.Language) || manifest.Lines.Any(x => x is null || x.Conditions.IsDefault || x.Conditions.Any(c => c is null)))
            throw new InvalidDataException("dialogue-manifest-invalid: Manifest requires its NPC identity, profile, language, quest, lines and condition arrays.");
    }
    private void ValidateSynthesis(SkyrimDialogueProposal proposal, SkyrimVoiceSynthesisManifest synthesis)
    {
        var sample = ValidateSample(proposal.Manifest, proposal.SampleAuthorityPath, proposal.SampleAuthoritySha256);
        if (synthesis.Schema != SkyrimNpcVoiceSchemas.Synthesis || synthesis.DialogueManifestSha256 != proposal.ManifestSha256 || synthesis.DialogueManifestPath != proposal.ManifestPath ||
            synthesis.SampleAuthoritySha256 != proposal.SampleAuthoritySha256 || synthesis.SampleAuthorityPath != proposal.SampleAuthorityPath || synthesis.SampleSha256 != sample.NormalizedSha256 ||
            synthesis.Lines.IsDefaultOrEmpty || synthesis.Lines.Any(x => x is null) ||
            synthesis.Lines.Length != proposal.Manifest.Lines.Length || synthesis.Lines.Select(x => x.LineId).Distinct(StringComparer.Ordinal).Count() != synthesis.Lines.Length)
            throw new InvalidDataException("dialogue-synthesis-incomplete: Synthesis manifest bindings do not match this proposal.");
        // The explicitly supplied synthesis hash reviews its effective language, including --language overrides.
        string effectiveLanguage = synthesis.Lines[0].Language;
        if (string.IsNullOrWhiteSpace(effectiveLanguage) || synthesis.Lines.Any(x => x.Language != effectiveLanguage))
            throw new InvalidDataException("dialogue-synthesis-incomplete: Synthesis lines must share one non-empty effective language.");
        foreach (var line in proposal.Manifest.Lines)
        {
            var match = synthesis.Lines.SingleOrDefault(x => x.LineId == line.Id);
            if (match is null || match.Status is not (SkyrimVoiceLineStatus.Succeeded or SkyrimVoiceLineStatus.Skipped) || match.Text != line.Text || match.TextSha256 != SkyrimNpcVoiceDocumentCodec.Hash(Encoding.UTF8.GetBytes(line.Text)) ||
                match.OutputFile is null || match.OutputSha256 is null)
                throw new InvalidDataException("dialogue-synthesis-incomplete: Successful or reused, unchanged synthesis required for line " + line.Id);
        }
    }
    private async ValueTask<SkyrimDialogueStageResult<T>> Finish<T>(string command, object request, T? document, WorkspacePath? path,
        ImmutableArray<Diagnostic>.Builder diagnostics, long start, CancellationToken token, bool alreadyWritten = false) where T : class
    {
        Sha256Hash? hash = null;
        if (document is not null && path is { } output)
        {
            try { hash = alreadyWritten ? SkyrimNpcVoiceDocumentCodec.Hash(documents.Read(output.Value)) : documents.Write(output.Value, document); }
            catch (Exception ex) when (SkyrimDialogueDocumentSupport.Handled(ex)) { diagnostics.Add(SkyrimDialogueDocumentSupport.Diagnostic(ex)); path = null; }
        }
        bool success = document is not null && !diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
        var entry = new OperationJournalRecord(command, SkyrimNpcVoiceDocumentCodec.Hash(SkyrimNpcVoiceDocumentCodec.Serialize(request)).Value.ToUpperInvariant(),
            path is not null && success ? [ProtocolEffect.Create(AgentEffectKind.WriteNewArtifact, ApplicationEffectStatus.Completed, ApplicationEffectScope.KLocalOutput)] : [],
            diagnostics.Select(x => x.Code).Distinct().ToImmutableArray(), [], hash is { } digest ? [digest.Value.ToUpperInvariant()] : [], (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            success ? "succeeded" : "refused", success ? 0 : 4);
        var append = await journal.Value.AppendAsync(entry, token);
        if (append.Warning is not null) diagnostics.Add(append.Warning);
        return new(success, document, path, hash, diagnostics.ToImmutable());
    }
}
