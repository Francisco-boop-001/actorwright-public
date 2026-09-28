using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaObjectTemplateBinaryWriteService
{
    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        ObjectTemplateProposalArtifact proposal, ObjectTemplatePropertyProposalArtifact? properties,
        FormId sourceFormId, FormId targetFormId, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var source = overlay.Armors.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"ARMO source record {proposal.SourceFormId} was not found in the source plugin.");
        if (proposal.Mode == ObjectTemplateProposalMode.Override && !string.Equals(source.EditorID, proposal.EditorId, StringComparison.Ordinal)) throw new InvalidDataException("Override proposal EditorID does not match the source ARMO.");
        var references = proposal.Combinations.SelectMany(item => item.Keywords.Concat(item.Includes.Select(include => include.Mod)))
            .Concat(properties?.Properties.Where(item => item.Value1FormId is not null).Select(item => item.Value1FormId!) ?? [])
            .Select(value => FormReference.TryParse(value, out var reference) ? reference : throw new InvalidDataException($"Invalid OBTS reference '{value}'."))
            .ToImmutableArray();
        ValidateMasters(references, sourceModKey, overlay.ModHeader.MasterReferences.Select(item => item.Master));
        var formKey = proposal.Mode == ObjectTemplateProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var armor = proposal.Mode == ObjectTemplateProposalMode.Override ? source.DeepCopy() : new Fo4.Armor(formKey, Fo4.Fallout4Release.Fallout4);
        armor.EditorID = proposal.EditorId;
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, references, outputModKey,
            proposal.Mode == ObjectTemplateProposalMode.Override ? overlay.ModHeader.MasterReferences.Select(item => item.Master) : []);
        mod.Armors.Add(armor);
        mod.WriteToBinary(new FilePath(destination), new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck });
    }

    private static void PatchArmoObjectTemplate(string path, ObjectTemplateProposalArtifact proposal,
        ObjectTemplatePropertyProposalArtifact? properties,
        ModKey outputModKey, FormId targetFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var bytes = File.ReadAllBytes(path);
        var candidates = FindArmoRecords(bytes).Where(candidate => HasEditorId(bytes, candidate, proposal.EditorId)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("The materialized plugin did not contain exactly one identifiable ARMO record.");
        var candidate = candidates[0];
        var materializedLocalFormId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(candidate.Offset + 12, 4)) & 0x00FF_FFFF;
        var expectedLocalFormId = targetFormId.Value & 0x00FF_FFFF;
        if (materializedLocalFormId != expectedLocalFormId)
        {
            diagnostics.Add(new Diagnostic("object-template-binary-target-mismatch", DiagnosticSeverity.Error,
                $"The materialized ARMO FormID 0x{materializedLocalFormId:X6} does not match the requested target 0x{expectedLocalFormId:X6}."));
            return;
        }
        var masterNames = ReadMasterNames(bytes);
        var payload = bytes.AsSpan(candidate.Offset + 24, candidate.DataSize).ToArray();
        if (proposal.Mode == ObjectTemplateProposalMode.Override && properties is null && FindSubrecord(payload, "OBTE", 0) >= 0)
        {
            diagnostics.Add(new Diagnostic("object-template-binary-properties-required", DiagnosticSeverity.Error,
                "Override proposals for an ARMO that already has OBTS properties require a bound property proposal."));
            return;
        }
        var block = BuildObjectTemplateBlock(proposal, properties, masterNames, outputModKey, diagnostics);
        if (HasErrors(diagnostics)) return;
        var stripped = RemoveObjectTemplateBlock(payload);
        var replacement = new byte[stripped.Length + block.Length];
        Buffer.BlockCopy(stripped, 0, replacement, 0, stripped.Length);
        Buffer.BlockCopy(block, 0, replacement, stripped.Length, block.Length);
        var delta = replacement.Length - payload.Length;
        var oldEnd = candidate.Offset + 24 + candidate.DataSize;
        var output = new byte[bytes.Length + delta];
        Buffer.BlockCopy(bytes, 0, output, 0, candidate.Offset + 24);
        Buffer.BlockCopy(replacement, 0, output, candidate.Offset + 24, replacement.Length);
        Buffer.BlockCopy(bytes, oldEnd, output, candidate.Offset + 24 + replacement.Length, bytes.Length - oldEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(candidate.Offset + 4, 4), checked((uint)replacement.Length));
        foreach (var group in FindContainingGroups(bytes, candidate.Offset, oldEnd))
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(group.Offset + 4, 4), checked((uint)(group.Size + delta)));
        File.WriteAllBytes(path, output);
    }

    private static byte[] BuildObjectTemplateBlock(ObjectTemplateProposalArtifact proposal,
        ObjectTemplatePropertyProposalArtifact? properties, ImmutableArray<ModKey> masterNames,
        ModKey outputModKey, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteSubrecord(writer, "OBTE", UInt32Bytes((uint)proposal.Combinations.Length));
        for (var combinationIndex = 0; combinationIndex < proposal.Combinations.Length; combinationIndex++)
        {
            var combination = proposal.Combinations[combinationIndex];
            if (combination.IsEditorOnly) WriteSubrecord(writer, "OBTF", []);
            if (!string.IsNullOrEmpty(combination.DisplayName)) WriteSubrecord(writer, "FULL", Encoding.UTF8.GetBytes(combination.DisplayName + "\0"));
            using var payload = new MemoryStream();
            using var payloadWriter = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
            payloadWriter.Write((uint)combination.Includes.Length);
            var propertyRows = properties?.Properties.Where(item => item.CombinationIndex == combinationIndex).ToArray() ?? [];
            payloadWriter.Write((uint)propertyRows.Length);
            payloadWriter.Write(combination.LevelMin); payloadWriter.Write((byte)0);
            payloadWriter.Write(combination.LevelMax); payloadWriter.Write((byte)0);
            payloadWriter.Write((short)(combination.ParentCombinationIndex is { } parent ? parent : -1));
            payloadWriter.Write(combination.IsDefault ? (byte)1 : (byte)0);
            payloadWriter.Write((byte)combination.Keywords.Length);
            foreach (var keyword in combination.Keywords) payloadWriter.Write(MapReference(keyword, masterNames, outputModKey));
            payloadWriter.Write(combination.MinLevelForRanks); payloadWriter.Write(combination.AltLevelsPerTier);
            foreach (var include in combination.Includes)
            {
                payloadWriter.Write(MapReference(include.Mod, masterNames, outputModKey));
                payloadWriter.Write(include.AttachPointIndex); payloadWriter.Write(include.IsOptional ? (byte)1 : (byte)0); payloadWriter.Write(include.DontUseAll ? (byte)1 : (byte)0);
            }
            foreach (var property in propertyRows) WriteProperty(payloadWriter, property, masterNames, outputModKey);
            payloadWriter.Flush();
            if (payload.Length > ushort.MaxValue) diagnostics.Add(new Diagnostic("object-template-binary-payload-size", DiagnosticSeverity.Error, "An OBTS payload exceeds the ordinary 16-bit subrecord limit."));
            else WriteSubrecord(writer, "OBTS", payload.ToArray());
        }
        WriteSubrecord(writer, "STOP", []);
        writer.Flush();
        return stream.ToArray();
    }

    private static uint MapReference(string value, ImmutableArray<ModKey> masters, ModKey outputModKey)
    {
        if (!FormReference.TryParse(value, out var reference)) throw new InvalidDataException($"Invalid OBTS reference '{value}'.");
        var plugin = new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin);
        if (plugin == outputModKey) throw new InvalidDataException("OBTS references may not point at the output plugin.");
        var index = masters.IndexOf(plugin);
        if (index < 0) throw new InvalidDataException($"OBTS reference '{reference}' is not declared as an output master.");
        return checked((uint)((index + 1) << 24) | (reference.FormId.Value & 0x00FF_FFFF));
    }

    private static byte[] RemoveObjectTemplateBlock(byte[] payload)
    {
        var start = FindSubrecord(payload, "OBTE", 0);
        if (start < 0) return payload;
        var stop = FindSubrecord(payload, "STOP", start);
        if (stop < 0) throw new InvalidDataException("Existing ARMO OBTE block has no STOP terminator.");
        var stopLength = ReadSubrecordLength(payload, stop);
        var result = new byte[payload.Length - (stop + 6 + stopLength - start)];
        Buffer.BlockCopy(payload, 0, result, 0, start);
        Buffer.BlockCopy(payload, stop + 6 + stopLength, result, start, payload.Length - (stop + 6 + stopLength));
        return result;
    }

    private static bool HasEditorId(byte[] bytes, BinaryRecord candidate, string editorId)
    {
        var payload = bytes.AsSpan(candidate.Offset + 24, candidate.DataSize);
        var offset = 0;
        while (offset + 6 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 4, 2));
            offset += 6;
            if (offset + length > payload.Length) return false;
            if (Encoding.ASCII.GetString(payload.Slice(offset - 6, 4)) == "EDID")
            {
                var value = Encoding.UTF8.GetString(payload.Slice(offset, length)).TrimEnd('\0');
                return string.Equals(value, editorId, StringComparison.Ordinal);
            }
            offset += length;
        }
        return false;
    }

    private static int FindSubrecord(byte[] payload, string signature, int start)
    {
        var offset = start;
        while (offset + 6 <= payload.Length)
        {
            var length = ReadSubrecordLength(payload, offset);
            if (Encoding.ASCII.GetString(payload, offset, 4) == signature) return offset;
            offset += 6 + length;
        }
        return -1;
    }

    private static int ReadSubrecordLength(byte[] payload, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4, 2));

    private static void WriteSubrecord(BinaryWriter writer, string signature, byte[] payload)
    {
        if (signature.Length != 4 || payload.Length > ushort.MaxValue) throw new InvalidDataException($"Subrecord {signature} is not representable.");
        writer.Write(Encoding.ASCII.GetBytes(signature)); writer.Write((ushort)payload.Length); writer.Write(payload);
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes;
    }
}
