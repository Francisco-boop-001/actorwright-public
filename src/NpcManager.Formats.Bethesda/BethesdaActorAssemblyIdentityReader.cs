using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class BethesdaActorAssemblyIdentityReader(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IActorAssemblyIdentityReader
{
    private const uint CompressedRecordFlag = 0x00040000;

    public ValueTask<ActorAssemblyIdentityReadResult> ReadAsync(
        ActorAssemblyIdentityReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.PluginPath.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("actor-assembly-plugin-outside-lab", DiagnosticSeverity.Error, "The identity plugin must remain under the K-only lab root."));
        var parent = Path.GetDirectoryName(request.PluginPath.Value);
        if (parent is not null) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.PluginPath.Value)) diagnostics.Add(new Diagnostic("actor-assembly-plugin-missing", DiagnosticSeverity.Error, "The identity plugin does not exist."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return ValueTask.FromResult(UnknownResult(request, diagnostics.ToImmutable(), "baseNpc"));

        try
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(request.PluginPath.Value, SkyrimRelease.SkyrimSE);
            var masters = mod.MasterReferences.Select(reference => reference.Master).ToImmutableArray();
            var self = mod.ModKey;
            var declaredPlugin = new PluginName(self.ToString());
            if (!string.Equals(declaredPlugin.Value, request.BaseNpc.Plugin.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("actor-assembly-plugin-identity-mismatch", DiagnosticSeverity.Error,
                    $"The opened plugin self key '{declaredPlugin.Value}' does not match the declared base plugin '{request.BaseNpc.Plugin.Value}'."));
                return ValueTask.FromResult(BlockedResult(request, diagnostics.ToImmutable(), "baseNpc"));
            }
            var typedNpcs = mod.Npcs.Where(npc => npc.FormKey.ModKey == self && npc.FormKey.ID == request.BaseNpc.FormId.Value).ToArray();
            var typedBase = RecordObservation(typedNpcs.Length, "NPC_", request.BaseNpc.FormId, typedNpcs.Length > 1 ? "multiple typed NPC_ records matched the declared base" : null);
            ActorAssemblyPlacedReferenceEvidence? placed = null;
            RawIdentity raw;
            try { raw = RawIdentityReader.Read(request.PluginPath.Value, masters, cancellationToken); }
            catch (PluginReadDiagnosticException exception) when (exception.Code == "actor-assembly-raw-parser-limitation")
            {
                diagnostics.Add(new Diagnostic(exception.Code, DiagnosticSeverity.Warning, exception.Message));
                raw = RawIdentity.Unknown;
            }
            var rawBase = raw.IsAvailable
                ? FindRawRecordObservation(raw, request.BaseNpc.FormId, masters.Length, "NPC_", out var rawBaseRecord)
                : UnknownRecord("Raw NPC_ parsing was unavailable.");
            var baseOutcome = CombineIdentity(typedBase, rawBase);
            var baseEvidence = new ActorAssemblyBaseNpcEvidence(request.BaseNpc.Plugin, request.BaseNpc.FormId, typedBase, rawBase, baseOutcome);
            if (request.Placement.Mode == ActorAssemblyPlacementMode.PersistentReference)
            {
                var placedId = request.Placement.PlacedReferenceFormId!.Value;
                var typedPlaced = mod.EnumerateMajorRecords().OfType<IPlacedNpcGetter>()
                    .Where(record => record.FormKey.ModKey == self && record.FormKey.ID == placedId.Value).ToArray();
                var typedRecord = RecordObservation(typedPlaced.Length, "ACHR", placedId, typedPlaced.Length > 1 ? "multiple typed ACHR records matched the declared reference" : null);
                RawRecord? rawPlacedRecord = null;
                var rawRecord = raw.IsAvailable
                    ? FindRawRecordObservation(raw, placedId, masters.Length, "ACHR", out rawPlacedRecord)
                    : UnknownRecord("Raw ACHR parsing was unavailable.");
                var typedBaseReference = typedPlaced.Length == 1
                    ? ReferenceObservation(typedPlaced[0].Base.FormKey, masters, self)
                    : typedPlaced.Length == 0 ? MissingReference("The typed ACHR was not found.") : UnknownReference("Multiple typed ACHRs matched the declared reference.");
                var rawBaseReference = raw.IsAvailable && rawPlacedRecord is not null
                    ? RawNameReference(rawPlacedRecord.NameValues, masters, self)
                    : raw.IsAvailable ? MissingReference("The raw ACHR has no NAME subrecord.") : UnknownReference("Raw ACHR parsing was unavailable.");
                var placedOutcome = CombinePlacedIdentity(typedRecord, rawRecord, typedBaseReference, rawBaseReference, request.BaseNpc);
                placed = new ActorAssemblyPlacedReferenceEvidence(request.BaseNpc.Plugin, placedId, typedRecord, rawRecord, typedBaseReference, rawBaseReference, placedOutcome);
            }
            return ValueTask.FromResult(new ActorAssemblyIdentityReadResult(baseEvidence, placed,
                request.Placement.Mode == ActorAssemblyPlacementMode.PersistentReference ? "placedReference" : "baseNpc", diagnostics.ToImmutable()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(new Diagnostic("actor-assembly-identity-read-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(UnknownResult(request, diagnostics.ToImmutable(), "baseNpc"));
        }
    }

    private static ActorAssemblyIdentityReadResult UnknownResult(ActorAssemblyIdentityReadRequest request, ImmutableArray<Diagnostic> diagnostics, string target)
    {
        var unknown = UnknownRecord("Identity could not be read.");
        var baseEvidence = new ActorAssemblyBaseNpcEvidence(request.BaseNpc.Plugin, request.BaseNpc.FormId, unknown, unknown, ActorAssemblyOutcome.Unknown);
        return new(baseEvidence, null, target, diagnostics);
    }

    private static ActorAssemblyRecordObservation FindRawRecordObservation(RawIdentity raw, FormId id, int masterCount, string expectedSignature, out RawRecord? matching)
    {
        var rawId = checked(((uint)masterCount << 24) | id.Value);
        var matches = raw.Records.Where(record => record.FormId == rawId).ToArray(); matching = matches.Length == 1 ? matches[0] : null;
        if (matches.Length == 0) return MissingRecord($"No raw record with local ID {id} was found.");
        if (matches.Length > 1) return UnknownRecord($"Multiple raw records with local ID {id} were found.");
        return new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Found, matches[0].Signature, id, null);
    }

    private static ActorAssemblyRecordObservation RecordObservation(int count, string signature, FormId id, string? multipleReason) => count switch
    {
        1 => new(ActorAssemblyObservationStatus.Found, signature, id, null),
        0 => MissingRecord($"No typed {signature} record with local ID {id} was found."),
        _ => UnknownRecord(multipleReason ?? $"Multiple typed {signature} records matched local ID {id}.")
    };

    private static ActorAssemblyOutcome CombineIdentity(ActorAssemblyRecordObservation typed, ActorAssemblyRecordObservation raw)
    {
        if (IsStructuralAmbiguity(typed) || IsStructuralAmbiguity(raw)) return ActorAssemblyOutcome.Blocked;
        if (typed.Status == ActorAssemblyObservationStatus.Unknown || raw.Status == ActorAssemblyObservationStatus.Unknown) return ActorAssemblyOutcome.Unknown;
        if (typed.Status != ActorAssemblyObservationStatus.Found || raw.Status != ActorAssemblyObservationStatus.Found || typed.Signature != "NPC_" || raw.Signature != "NPC_") return ActorAssemblyOutcome.Blocked;
        return ActorAssemblyOutcome.Pass;
    }

    private static ActorAssemblyOutcome CombinePlacedIdentity(ActorAssemblyRecordObservation typed, ActorAssemblyRecordObservation raw, ActorAssemblyReferenceObservation typedBase, ActorAssemblyReferenceObservation rawBase, ActorAssemblyActorIdentity expected)
    {
        if (new[] { typed, raw }.Any(IsStructuralAmbiguity) || new[] { typedBase, rawBase }.Any(IsStructuralAmbiguity)) return ActorAssemblyOutcome.Blocked;
        if (new[] { typed.Status, raw.Status, typedBase.Status, rawBase.Status }.Any(status => status == ActorAssemblyObservationStatus.Unknown)) return ActorAssemblyOutcome.Unknown;
        if (typed.Status != ActorAssemblyObservationStatus.Found || raw.Status != ActorAssemblyObservationStatus.Found || typed.Signature != "ACHR" || raw.Signature != "ACHR") return ActorAssemblyOutcome.Blocked;
        if (typedBase.Status != ActorAssemblyObservationStatus.Found || rawBase.Status != ActorAssemblyObservationStatus.Found || typedBase.Plugin != expected.Plugin || rawBase.Plugin != expected.Plugin || typedBase.FormId != expected.FormId || rawBase.FormId != expected.FormId) return ActorAssemblyOutcome.Blocked;
        return ActorAssemblyOutcome.Pass;
    }

    private static ActorAssemblyRecordObservation MissingRecord(string reason) => new(ActorAssemblyObservationStatus.Missing, null, null, reason);
    private static ActorAssemblyRecordObservation UnknownRecord(string reason) => new(ActorAssemblyObservationStatus.Unknown, null, null, reason);
    private static ActorAssemblyReferenceObservation MissingReference(string reason) => new(ActorAssemblyObservationStatus.Missing, null, null, reason);
    private static ActorAssemblyReferenceObservation UnknownReference(string reason) => new(ActorAssemblyObservationStatus.Unknown, null, null, reason);

    private static bool IsStructuralAmbiguity(ActorAssemblyRecordObservation observation) =>
        observation.Status == ActorAssemblyObservationStatus.Unknown &&
        observation.Reason?.Contains("multiple", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsStructuralAmbiguity(ActorAssemblyReferenceObservation observation) =>
        observation.Status == ActorAssemblyObservationStatus.Unknown &&
        observation.Reason?.Contains("multiple", StringComparison.OrdinalIgnoreCase) == true;

    private static ActorAssemblyIdentityReadResult BlockedResult(
        ActorAssemblyIdentityReadRequest request, ImmutableArray<Diagnostic> diagnostics, string target)
    {
        var blocked = new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Found, "NPC_", request.BaseNpc.FormId,
            "The plugin self key does not match the declared plugin.");
        return new(new ActorAssemblyBaseNpcEvidence(request.BaseNpc.Plugin, request.BaseNpc.FormId, blocked, blocked, ActorAssemblyOutcome.Blocked),
            null, target, diagnostics);
    }

    private static ActorAssemblyReferenceObservation ReferenceObservation(FormKey key, ImmutableArray<ModKey> masters, ModKey self)
    {
        if (key.IsNull) return MissingReference("The ACHR has a null base reference.");
        return new(ActorAssemblyObservationStatus.Found, new PluginName(key.ModKey.ToString()), new FormId(key.ID), null);
    }

    private static ActorAssemblyReferenceObservation RawNameReference(ImmutableArray<uint> values, ImmutableArray<ModKey> masters, ModKey self)
    {
        if (values.Length == 0) return MissingReference("The ACHR has no NAME subrecord.");
        if (values.Length > 1) return UnknownReference("The ACHR has multiple NAME subrecords.");
        var raw = values[0]; var ownerIndex = raw >> 24; var id = raw & 0x00FF_FFFF;
        if (ownerIndex > masters.Length || id == 0) return UnknownReference("The raw NAME does not resolve through the plugin master table.");
        var plugin = ownerIndex == masters.Length ? new PluginName(self.ToString()) : new PluginName(masters[(int)ownerIndex].ToString());
        return new(ActorAssemblyObservationStatus.Found, plugin, new FormId(id), null);
    }

    private sealed record RawIdentity(bool IsAvailable, ImmutableArray<RawRecord> Records)
    {
        internal static RawIdentity Unknown { get; } = new(false, ImmutableArray<RawRecord>.Empty);
    }

    private sealed record RawRecord(uint FormId, string Signature, ImmutableArray<uint> NameValues);

    private static class RawIdentityReader
    {
        internal static RawIdentity Read(string path, ImmutableArray<ModKey> masters, CancellationToken cancellationToken)
        {
            var bytes = File.ReadAllBytes(path); if (bytes.Length < 24 || Encoding.ASCII.GetString(bytes, 0, 4) != "TES4") throw new InvalidDataException("Plugin does not begin with TES4.");
            var tes4Size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))); var start = checked(24 + tes4Size); if (start > bytes.Length) throw new InvalidDataException("TES4 extends beyond the plugin.");
            var records = ImmutableArray.CreateBuilder<RawRecord>(); Walk(bytes, start, bytes.Length, records, cancellationToken); return new RawIdentity(true, records.ToImmutable());
        }
        private static void Walk(byte[] bytes, int start, int end, ImmutableArray<RawRecord>.Builder records, CancellationToken token)
        {
            var position = start;
            while (position < end)
            {
                token.ThrowIfCancellationRequested(); if (end - position < 8) throw new InvalidDataException("Plugin record header is truncated.");
                var signature = Encoding.ASCII.GetString(bytes, position, 4); var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4)); var recordEnd = signature == "GRUP" ? checked(position + (int)size) : checked(position + 24 + (int)size); if (recordEnd > end || recordEnd < position) throw new InvalidDataException("Plugin record extends beyond its parent.");
                if (signature == "GRUP") { if (recordEnd - position < 24) throw new InvalidDataException("Plugin group header is truncated."); Walk(bytes, position + 24, recordEnd, records, token); }
                else if (recordEnd - position >= 24 && signature is "NPC_" or "ACHR")
                {
                    var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)); var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)); if ((flags & CompressedRecordFlag) != 0) throw new PluginReadDiagnosticException("actor-assembly-raw-parser-limitation", $"Compressed {signature} record 0x{formId:X8} cannot be independently inspected.");
                    var names = signature == "ACHR" ? ReadNames(bytes, position + 24, recordEnd) : ImmutableArray<uint>.Empty; records.Add(new RawRecord(formId, signature, names));
                }
                position = recordEnd;
            }
        }
        private static ImmutableArray<uint> ReadNames(byte[] bytes, int start, int end)
        {
            var values = ImmutableArray.CreateBuilder<uint>(); var position = start; uint? extended = null;
            while (position < end)
            {
                if (end - position < 6) throw new PluginReadDiagnosticException("actor-assembly-raw-parser-limitation", "ACHR subrecord header is truncated."); var signature = Encoding.ASCII.GetString(bytes, position, 4); var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2)); var actual = extended ?? size; extended = null;
                if (signature == "XXXX" && size == 4) { if (position + 10 > end) throw new PluginReadDiagnosticException("actor-assembly-raw-parser-limitation", "ACHR XXXX subrecord is truncated."); extended = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 6, 4)); position += 10; continue; }
                var fieldEnd = checked(position + 6 + (int)actual); if (fieldEnd > end) throw new PluginReadDiagnosticException("actor-assembly-raw-parser-limitation", "ACHR subrecord extends beyond its record."); if (signature == "NAME" && actual >= 4) values.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 6, 4))); position = fieldEnd;
            }
            return values.ToImmutable();
        }
    }
}
