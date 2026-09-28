using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaSkyrimFollowerFinishCoreVerification(
    bool Verified,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Independently reopens the core candidate and proves the bounded typed and
/// raw record surface. Placement and runtime appearance are deliberately
/// outside this verifier.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishCoreVerifier
{
    private static readonly Regex ConditionPattern = new(
        "^GetFactionRank\\((?<plugin>[^|()]+)\\|0x(?<id>[0-9A-Fa-f]{8})\\) < 0$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public BethesdaSkyrimFollowerFinishCoreVerification Verify(
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        SkyrimFollowerFinishProposal proposal,
        BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        _ = GetType();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            Require(
                File.Exists(sourcePlugin.Value) &&
                !Directory.Exists(sourcePlugin.Value),
                "follower-finish-core-verify-source",
                "The source plugin file does not exist.",
                diagnostics);
            Require(
                File.Exists(outputPlugin.Value) &&
                !Directory.Exists(outputPlugin.Value),
                "follower-finish-core-verify-output",
                "The core output plugin file does not exist.",
                diagnostics);
            if (HasErrors(diagnostics))
                return Result(diagnostics);

            Sha256Hash sourceHash = HashFile(sourcePlugin.Value);
            Require(
                sourceHash == proposal.SourceSnapshot.PluginSha256 &&
                sourceHash == proposal.Request.Source.PluginSha256,
                "follower-finish-core-verify-source-hash",
                "The verifier source bytes differ from the admitted snapshot.",
                diagnostics);

            SkyrimFollowerFinishRequest request = proposal.Request;
            ModKey pluginKey = ModKey.FromNameAndExtension(
                request.Source.Plugin.Value);
            SkyrimMod source = SkyrimMod.CreateFromBinary(
                new ModPath(
                    pluginKey,
                    new FilePath(sourcePlugin.Value)),
                SkyrimRelease.SkyrimSE);
            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(
                    pluginKey,
                    new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);

            Require(
                source.ModKey == output.ModKey &&
                string.Equals(
                    output.ModKey.ToString(),
                    request.Source.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase),
                "follower-finish-core-verify-plugin",
                "Source, output, and proposal plugin identities differ.",
                diagnostics);
            Require(
                !source.IsSmallMaster && output.IsSmallMaster &&
                request.SetEslFlag &&
                !request.CompactFormIds,
                "follower-finish-core-verify-esl",
                "TES4 ESL mechanics differ from the closed proposal.",
                diagnostics);
            Require(
                source.ModHeader.Stats.NextFormID ==
                request.Allocation.Package.Value &&
                output.ModHeader.Stats.NextFormID ==
                proposal.NextFormId.Value,
                "follower-finish-core-verify-next-form-id",
                "TES4 NextFormID mechanics differ from the allocation.",
                diagnostics);
            Require(
                source.ModHeader.MasterReferences
                    .Select(master => master.Master)
                    .SequenceEqual(
                        output.ModHeader.MasterReferences.Select(
                            master => master.Master)),
                "follower-finish-core-verify-masters",
                "The core output changed the source master list.",
                diagnostics);

            ColorRecord? sourceColor = FindColor(
                source,
                request.Hair.ColorFormId.Value);
            ColorRecord? outputColor = FindColor(
                output,
                request.Hair.ColorFormId.Value);
            Require(
                sourceColor is not null &&
                PackedRgb(sourceColor.Color) ==
                request.Hair.OldPackedRgb.Value &&
                outputColor is not null &&
                PackedRgb(outputColor.Color) ==
                request.Hair.NewPackedRgb.Value,
                "follower-finish-core-verify-color",
                "The source/output CLFM values do not equal the proposal old/new RGB.",
                diagnostics);

            Npc? sourceNpc = FindNpc(
                source,
                request.NpcFormId.Value);
            Npc? outputNpc = FindNpc(
                output,
                request.NpcFormId.Value);
            FormKey packageKey = new(
                output.ModKey,
                request.Allocation.Package.Value);
            Require(
                sourceNpc is not null &&
                sourceNpc.Packages.Count == 0 &&
                outputNpc is not null &&
                outputNpc.Packages.Count == 1 &&
                outputNpc.Packages[0].FormKey == packageKey,
                "follower-finish-core-verify-pkid",
                "The only output NPC package link is not the proposal allocation.",
                diagnostics);

            Package? package = output.Packages
                .SingleOrDefault(record =>
                    record.FormKey == packageKey);
            Require(
                package is not null &&
                output.Packages.Count == 1,
                "follower-finish-core-verify-package",
                "The output does not contain exactly one self-owned proposal PACK.",
                diagnostics);
            if (package is not null)
                VerifyPackage(
                    package,
                    request,
                    authority,
                    diagnostics);

            VerifyRawSurface(
                sourcePlugin.Value,
                outputPlugin.Value,
                proposal,
                diagnostics);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or OverflowException)
        {
            diagnostics.Add(Error(
                "follower-finish-core-verify-failed",
                exception.Message));
        }

        if (!HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic(
                "follower-finish-core-verified",
                DiagnosticSeverity.Info,
                "The reopened core candidate contains only TES4 mechanics, CLFM RGB, NPC PKID, and one PACK delta."));
        return Result(diagnostics);
    }

    private static void VerifyPackage(
        Package package,
        SkyrimFollowerFinishRequest request,
        BethesdaSkyrimFollowerFinishSandboxAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Package template = authority.CreateTemplateCopy();
        Require(
            package.VirtualMachineAdapter is null &&
            package.OwnerQuest.FormKeyNullable is null &&
            IsCanonicalEmptyVanillaEventBlock(
                package.OnBegin) &&
            IsCanonicalEmptyVanillaEventBlock(
                package.OnChange) &&
            IsCanonicalEmptyVanillaEventBlock(
                package.OnEnd),
            "follower-finish-core-verify-package-shape",
            "The output package contains VMAD, owner, or noncanonical event content.",
            diagnostics);
        Require(
            package.DataInputVersion == template.DataInputVersion &&
            package.Flags == template.Flags &&
            package.Type == template.Type &&
            package.InterruptOverride == template.InterruptOverride &&
            package.PreferredSpeed == template.PreferredSpeed &&
            unchecked((ushort)package.InteruptFlags) ==
            unchecked((ushort)template.InteruptFlags),
            "follower-finish-core-verify-template-copy",
            "The output package drifted from admitted non-procedure template mechanics: " +
            $"data-input-version={package.DataInputVersion}/{template.DataInputVersion}, " +
            $"flags={package.Flags}/{template.Flags}, " +
            $"type={package.Type}/{template.Type}, " +
            $"interrupt-override={package.InterruptOverride}/{template.InterruptOverride}, " +
            $"preferred-speed={package.PreferredSpeed}/{template.PreferredSpeed}, " +
            $"interrupt-flags={package.InteruptFlags}/{template.InteruptFlags}.",
            diagnostics);
        Require(
            package.PackageTemplate.FormKey ==
            template.PackageTemplate.FormKey &&
            package.ProcedureTree.Count == 0 &&
            string.Equals(
                authority.ProcedureType,
                "Sandbox",
                StringComparison.Ordinal) &&
            string.Equals(
                request.Sandbox.Procedure,
                authority.ProcedureType,
                StringComparison.Ordinal),
            "follower-finish-core-verify-procedure",
            "The output package procedure differs from the actual admitted template.",
            diagnostics);
        Require(
            package.ScheduleMonth == authority.ScheduleMonth &&
            package.ScheduleDayOfWeek ==
            authority.ScheduleDayOfWeek &&
            package.ScheduleDate == authority.ScheduleDate &&
            package.ScheduleHour == authority.ScheduleHour &&
            package.ScheduleMinute == authority.ScheduleMinute &&
            package.ScheduleDurationInMinutes ==
            authority.ScheduleDurationInMinutes,
            "follower-finish-core-verify-schedule",
            "The output package drifted from the admitted continuous schedule.",
            diagnostics);

        PackageDataLocation[] locations = package.Data.Values
            .OfType<PackageDataLocation>()
            .ToArray();
        Require(
            locations.Length == 1,
            "follower-finish-core-verify-location-count",
            "The output package does not contain exactly one location data value.",
            diagnostics);
        PackageDataLocation? location =
            locations.Length == 1 ? locations[0] : null;
        LocationTarget? target =
            location?.Location.Target as LocationTarget;
        Require(
            location is not null &&
            location.Location.Radius ==
            checked((uint)request.Sandbox.Radius) &&
            target?.Link.FormKey ==
            BethesdaSkyrimFollowerFinishCoreWriter.ToFormKey(
                request.Sandbox.Target),
            "follower-finish-core-verify-location",
            "The output package radius or local marker differs from the proposal.",
            diagnostics);

        Match conditionMatch = ConditionPattern.Match(
            request.Sandbox.Condition);
        FormKey expectedFaction = FormKey.Null;
        uint factionId = 0;
        bool parsed = conditionMatch.Success &&
                      uint.TryParse(
                          conditionMatch.Groups["id"].Value,
                          NumberStyles.AllowHexSpecifier,
                          CultureInfo.InvariantCulture,
                          out factionId);
        if (parsed)
            expectedFaction = new FormKey(
                ModKey.FromNameAndExtension(
                    conditionMatch.Groups["plugin"].Value),
                factionId);
        Require(
            package.Conditions.Count == 1,
            "follower-finish-core-verify-condition-count",
            "The output package does not contain exactly one condition.",
            diagnostics);
        ConditionFloat? condition =
            package.Conditions.Count == 1
                ? package.Conditions[0] as ConditionFloat
                : null;
        GetFactionRankConditionData? data =
            condition?.Data as GetFactionRankConditionData;
        Require(
            parsed &&
            condition is not null &&
            condition.CompareOperator == CompareOperator.LessThan &&
            condition.ComparisonValue == 0f &&
            data is not null &&
            data.Faction.Link.FormKey == expectedFaction &&
            data.RunOnType == Condition.RunOnType.Subject,
            "follower-finish-core-verify-condition",
            "The output package does not contain exactly the proposal GetFactionRank < 0 condition.",
            diagnostics);
    }

    private static bool IsCanonicalEmptyVanillaEventBlock(
        PackageEvent? packageEvent) =>
        packageEvent is not null &&
        packageEvent.Topics.Count == 1 &&
        packageEvent.Topics[0] is TopicReference topic &&
        topic.Reference.FormKey.IsNull &&
        packageEvent.Idle.IsNull &&
        packageEvent.SCHR is null &&
        packageEvent.SCDA is null &&
        packageEvent.SCTX is null &&
        packageEvent.QNAM is null &&
        packageEvent.TNAM is null;

    private static void VerifyRawSurface(
        string sourcePath,
        string outputPath,
        SkyrimFollowerFinishProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimFollowerFinishRequest request = proposal.Request;
        ImmutableArray<RawRecord> sourceRecords =
            ReadRawRecords(sourcePath);
        ImmutableArray<RawRecord> outputRecords =
            ReadRawRecords(outputPath);
        ImmutableArray<RawRecordIdentity> actualSourceShape =
            RawInventory(sourceRecords);
        ImmutableArray<RawRecordIdentity> actualOutputShape =
            RawInventory(outputRecords);
        SkyrimFollowerFinishPluginSnapshot inspected =
            BethesdaSkyrimFollowerFinishSourceReader.Inspect(
                request, new WorkspacePath(sourcePath), CancellationToken.None);
        uint ownerPrefix = checked((uint)inspected.Masters.Length << 24);
        ImmutableArray<RawRecordIdentity> expectedOutputShape =
            WithPack(actualSourceShape, request.Allocation.Package.Value);
        bool inventoryValid = inspected.Valid &&
            inspected.RecordInventory.SequenceEqual(proposal.SourceSnapshot.RecordInventory) &&
            sourceRecords.Concat(outputRecords).Where(record => record.Signature != "TES4")
                .All(record => (record.RawFormId & 0xFF00_0000u) == ownerPrefix) &&
            actualOutputShape.SequenceEqual(expectedOutputShape);
        if (!inventoryValid)
        {
            diagnostics.Add(Error(
                "follower-finish-core-verify-raw-inventory",
                "The raw source/output inventory must equal the independently admitted source roles plus the allocated PACK. " +
                $"Actual source: {DescribeInventory(actualSourceShape)}. " +
                $"Actual output: {DescribeInventory(actualOutputShape)}. " +
                string.Join(" | ", inspected.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message))));
            return;
        }

        RawRecord sourceTes4 = FindRaw(
            sourceRecords,
            "TES4",
            0);
        RawRecord outputTes4 = FindRaw(
            outputRecords,
            "TES4",
            0);
        VerifyTes4Raw(
            sourceTes4,
            outputTes4,
            proposal,
            diagnostics);

        foreach (RawRecordIdentity identity in actualSourceShape)
        {
            if (identity.Signature is "NPC_" or "CLFM")
                continue;
            RawRecord before = FindRaw(
                sourceRecords,
                identity.Signature,
                identity.LocalFormId);
            RawRecord after = FindRaw(
                outputRecords,
                identity.Signature,
                identity.LocalFormId);
            RequireSameHeader(
                before,
                after,
                allowDataSize: false,
                diagnostics);
            Require(
                before.GroupPath == after.GroupPath &&
                before.Payload.SequenceEqual(after.Payload),
                "follower-finish-core-verify-preservation",
                $"{identity.Signature} 0x{identity.LocalFormId:X8} " +
                "payload/group placement did not remain byte-exact.",
                diagnostics);
        }

        VerifyClfmRaw(
            FindRaw(
                sourceRecords,
                "CLFM",
                request.Hair.ColorFormId.Value),
            FindRaw(
                outputRecords,
                "CLFM",
                request.Hair.ColorFormId.Value),
            diagnostics);
        VerifyNpcRaw(
            FindRaw(
                sourceRecords,
                "NPC_",
                request.NpcFormId.Value),
            FindRaw(
                outputRecords,
                "NPC_",
                request.NpcFormId.Value),
            request.Allocation.Package.Value,
            diagnostics);
        _ = FindRaw(
            outputRecords,
            "PACK",
            request.Allocation.Package.Value);
    }

    private static void VerifyTes4Raw(
        RawRecord before,
        RawRecord after,
        SkyrimFollowerFinishProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        byte[] normalizedHeader = after.Header.ToArray();
        before.Header.AsSpan(8, 4).CopyTo(
            normalizedHeader.AsSpan(8, 4));
        Require(
            before.GroupPath == after.GroupPath &&
            before.Header.SequenceEqual(normalizedHeader),
            "follower-finish-core-verify-record-header-drift",
            "TES4 record header changed outside the ESL flag.",
            diagnostics);
        uint beforeFlags =
            BinaryPrimitives.ReadUInt32LittleEndian(
                before.Header.AsSpan(8, 4));
        uint afterFlags =
            BinaryPrimitives.ReadUInt32LittleEndian(
                after.Header.AsSpan(8, 4));
        Require(
            afterFlags == (beforeFlags | 0x200u),
            "follower-finish-core-verify-esl",
            "Raw TES4 flags do not contain exactly the added ESL bit.",
            diagnostics);

        SubrecordSlice beforeHedr = FindSingleSubrecord(
            before.Payload,
            "HEDR");
        SubrecordSlice afterHedr = FindSingleSubrecord(
            after.Payload,
            "HEDR");
        Require(
            beforeHedr.DataLength >= 12 &&
            afterHedr.DataLength == beforeHedr.DataLength,
            "follower-finish-core-verify-tes4-hedr",
            "TES4 HEDR shape changed.",
            diagnostics);
        byte[] normalizedPayload = after.Payload.ToArray();
        before.Payload.AsSpan(
                beforeHedr.DataStart + 4,
                8)
            .CopyTo(normalizedPayload.AsSpan(
                afterHedr.DataStart + 4,
                8));
        Require(
            before.Payload.SequenceEqual(normalizedPayload),
            "follower-finish-core-verify-tes4-hedr",
            "TES4 payload changed outside HEDR record-count/NextFormID mechanics.",
            diagnostics);
        uint beforeCount =
            BinaryPrimitives.ReadUInt32LittleEndian(
                before.Payload.AsSpan(
                    beforeHedr.DataStart + 4,
                    4));
        uint afterCount =
            BinaryPrimitives.ReadUInt32LittleEndian(
                after.Payload.AsSpan(
                    afterHedr.DataStart + 4,
                    4));
        uint afterNext =
            BinaryPrimitives.ReadUInt32LittleEndian(
                after.Payload.AsSpan(
                    afterHedr.DataStart + 8,
                    4));
        Require(
            afterCount == beforeCount + 2 &&
            afterNext == proposal.NextFormId.Value,
            "follower-finish-core-verify-next-form-id",
            "Raw HEDR count/NextFormID does not match the one-PACK group/record allocation: " +
            $"count {beforeCount}->{afterCount}, " +
            $"NextFormID 0x{afterNext:X8} expected 0x{proposal.NextFormId.Value:X8}.",
            diagnostics);
    }

    private static void VerifyClfmRaw(
        RawRecord before,
        RawRecord after,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        RequireSameHeader(
            before,
            after,
            allowDataSize: false,
            diagnostics);
        SubrecordSlice beforeCnam = FindSingleSubrecord(
            before.Payload,
            "CNAM");
        SubrecordSlice afterCnam = FindSingleSubrecord(
            after.Payload,
            "CNAM");
        Require(
            beforeCnam.DataLength == afterCnam.DataLength,
            "follower-finish-core-verify-color",
            "CLFM CNAM encoded size changed.",
            diagnostics);
        byte[] normalized = after.Payload.ToArray();
        before.Payload.AsSpan(
                beforeCnam.DataStart,
                beforeCnam.DataLength)
            .CopyTo(normalized.AsSpan(
                afterCnam.DataStart,
                afterCnam.DataLength));
        Require(
            before.GroupPath == after.GroupPath &&
            before.Payload.SequenceEqual(normalized),
            "follower-finish-core-verify-subrecord-preservation",
            "CLFM changed outside the CNAM payload bytes.",
            diagnostics);
    }

    private static void VerifyNpcRaw(
        RawRecord before,
        RawRecord after,
        uint packageLocalId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        RequireSameHeader(
            before,
            after,
            allowDataSize: true,
            diagnostics);
        ImmutableArray<SubrecordSlice> beforeSlices =
            ParseSubrecords(before.Payload);
        ImmutableArray<SubrecordSlice> afterSlices =
            ParseSubrecords(after.Payload);
        SubrecordSlice[] pkidSlices = afterSlices
            .Where(slice => slice.Signature == "PKID")
            .ToArray();
        Require(
            beforeSlices.All(slice =>
                slice.Signature != "PKID") &&
            pkidSlices.Length == 1,
            "follower-finish-core-verify-pkid",
            "NPC raw payload does not add exactly one PKID.",
            diagnostics);
        SubrecordSlice? pkid =
            pkidSlices.Length == 1 ? pkidSlices[0] : null;
        if (pkid is null)
            return;
        Require(
            pkid.Value.DataLength == 4 &&
            (BinaryPrimitives.ReadUInt32LittleEndian(
                 after.Payload.AsSpan(
                     pkid.Value.DataStart,
                     4)) &
             0x00FF_FFFFu) == packageLocalId,
            "follower-finish-core-verify-pkid",
            "NPC PKID payload does not target the allocated PACK.",
            diagnostics);
        byte[] withoutPkid = new byte[
            after.Payload.Length -
            pkid.Value.EncodedLength];
        after.Payload.AsSpan(
                0,
                pkid.Value.EncodedStart)
            .CopyTo(withoutPkid);
        after.Payload.AsSpan(
                pkid.Value.EncodedStart +
                pkid.Value.EncodedLength)
            .CopyTo(withoutPkid.AsSpan(
                pkid.Value.EncodedStart));
        Require(
            before.GroupPath == after.GroupPath &&
            before.Payload.SequenceEqual(withoutPkid),
            "follower-finish-core-verify-subrecord-preservation",
            "NPC changed outside one encoded PKID subrecord.",
            diagnostics);
    }

    private static void RequireSameHeader(
        RawRecord before,
        RawRecord after,
        bool allowDataSize,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        byte[] normalized = after.Header.ToArray();
        if (allowDataSize)
            before.Header.AsSpan(4, 4).CopyTo(
                normalized.AsSpan(4, 4));
        Require(
            before.Header.SequenceEqual(normalized),
            "follower-finish-core-verify-record-header-drift",
            $"{before.Signature} record header changed outside its allowed data-size field.",
            diagnostics);
    }

    private static ImmutableArray<RawRecordIdentity> RawInventory(
        ImmutableArray<RawRecord> records) =>
        records
            .Where(record => record.Signature != "TES4")
            .Select(record => new RawRecordIdentity(
                record.Signature,
                record.RawFormId & 0x00FF_FFFFu))
            .OrderBy(record => record.LocalFormId)
            .ThenBy(record => record.Signature, StringComparer.Ordinal)
            .ToImmutableArray();

    private static string DescribeInventory(
        IEnumerable<RawRecordIdentity> inventory) =>
        "[" + string.Join(
            ", ",
            inventory.Select(record =>
                $"{record.Signature} 0x{record.LocalFormId:X8}")) + "]";

    private static ImmutableArray<RawRecordIdentity> WithPack(
        ImmutableArray<RawRecordIdentity> source,
        uint packageLocalFormId) =>
        source
            .Add(new RawRecordIdentity("PACK", packageLocalFormId))
            .OrderBy(record => record.LocalFormId)
            .ThenBy(record => record.Signature, StringComparer.Ordinal)
            .ToImmutableArray();

    private static RawRecord FindRaw(
        ImmutableArray<RawRecord> records,
        string signature,
        uint localFormId)
    {
        RawRecord[] matches = records
            .Where(record =>
                record.Signature == signature &&
                (record.RawFormId & 0x00FF_FFFFu) ==
                localFormId)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"Raw inventory found {matches.Length} {signature} records for 0x{localFormId:X8}.");
        return matches[0];
    }

    private static SubrecordSlice FindSingleSubrecord(
        byte[] payload,
        string signature)
    {
        SubrecordSlice[] matches = ParseSubrecords(payload)
            .Where(slice => slice.Signature == signature)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"Record contains {matches.Length} {signature} subrecords.");
        return matches[0];
    }

    private static ImmutableArray<SubrecordSlice> ParseSubrecords(
        byte[] payload)
    {
        var result = ImmutableArray.CreateBuilder<SubrecordSlice>();
        int position = 0;
        uint? extended = null;
        int encodedStart = 0;
        while (position < payload.Length)
        {
            if (position + 6 > payload.Length)
                throw new InvalidDataException(
                    "A raw subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                payload,
                position,
                4);
            ushort shortSize =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    payload.AsSpan(position + 4, 2));
            if (signature == "XXXX")
            {
                encodedStart = position;
                if (shortSize != 4 ||
                    position + 10 > payload.Length)
                    throw new InvalidDataException(
                        "A raw XXXX subrecord is malformed.");
                extended =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        payload.AsSpan(position + 6, 4));
                position += 10;
                continue;
            }
            int start = extended is null ? position : encodedStart;
            uint size = extended ?? shortSize;
            extended = null;
            int dataStart = position + 6;
            if (size > int.MaxValue ||
                dataStart + (int)size > payload.Length)
                throw new InvalidDataException(
                    "A raw subrecord exceeds its record.");
            int encodedLength =
                dataStart + (int)size - start;
            result.Add(new SubrecordSlice(
                signature,
                start,
                dataStart,
                (int)size,
                encodedLength));
            position = dataStart + (int)size;
        }
        if (extended is not null)
            throw new InvalidDataException(
                "A raw XXXX subrecord has no payload subrecord.");
        return result.ToImmutable();
    }

    private static ImmutableArray<RawRecord> ReadRawRecords(
        string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var records = ImmutableArray.CreateBuilder<RawRecord>();
        Walk(0, bytes.Length, string.Empty);
        return records.ToImmutable();

        void Walk(int start, int end, string groupPath)
        {
            int position = start;
            while (position < end)
            {
                if (position + 24 > end)
                    throw new InvalidDataException(
                        "A TES4 record header is truncated.");
                string signature = Encoding.ASCII.GetString(
                    bytes,
                    position,
                    4);
                uint size =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 4, 4));
                if (signature == "GRUP")
                {
                    int groupEnd = checked(position + (int)size);
                    if (size < 24 || groupEnd > end)
                        throw new InvalidDataException(
                            "A TES4 group exceeds its parent.");
                    string label = Encoding.ASCII.GetString(
                        bytes,
                        position + 8,
                        4);
                    uint groupType =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(position + 12, 4));
                    string childPath =
                        string.IsNullOrEmpty(groupPath)
                            ? $"{label}:{groupType:X8}"
                            : $"{groupPath}/{label}:{groupType:X8}";
                    Walk(position + 24, groupEnd, childPath);
                    position = groupEnd;
                    continue;
                }
                int recordEnd =
                    checked(position + 24 + (int)size);
                if (recordEnd > end)
                    throw new InvalidDataException(
                        "A TES4 record exceeds its parent.");
                records.Add(new RawRecord(
                    signature,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4)),
                    groupPath,
                    bytes.AsSpan(position, 24).ToArray(),
                    bytes.AsSpan(
                        position + 24,
                        (int)size).ToArray()));
                position = recordEnd;
            }
        }
    }

    private sealed record RawRecord(
        string Signature,
        uint RawFormId,
        string GroupPath,
        byte[] Header,
        byte[] Payload);

    private readonly record struct RawRecordIdentity(
        string Signature,
        uint LocalFormId);

    private readonly record struct SubrecordSlice(
        string Signature,
        int EncodedStart,
        int DataStart,
        int DataLength,
        int EncodedLength);

    private static ColorRecord? FindColor(
        SkyrimMod mod,
        uint localFormId) =>
        mod.Colors.SingleOrDefault(record =>
            record.FormKey.ModKey == mod.ModKey &&
            record.FormKey.ID == localFormId);

    private static Npc? FindNpc(
        SkyrimMod mod,
        uint localFormId) =>
        mod.Npcs.SingleOrDefault(record =>
            record.FormKey.ModKey == mod.ModKey &&
            record.FormKey.ID == localFormId);

    private static uint PackedRgb(System.Drawing.Color color) =>
        ((uint)color.R << 16) |
        ((uint)color.G << 8) |
        color.B;

    private static Sha256Hash HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Require(
        bool condition,
        string code,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!condition)
            diagnostics.Add(Error(code, message));
    }

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);

    private static BethesdaSkyrimFollowerFinishCoreVerification
        Result(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            !HasErrors(diagnostics),
            diagnostics.ToImmutable());

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
