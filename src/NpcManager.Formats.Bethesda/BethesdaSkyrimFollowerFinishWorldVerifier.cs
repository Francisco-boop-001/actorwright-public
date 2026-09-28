using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaSkyrimFollowerFinishWorldVerification(
    bool Verified,
    ImmutableArray<string> RawGroupTreeSurface,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Independently reopens the emitted plugin and verifies both its typed
/// placement and its raw record/group surface.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishWorldVerifier
{
    private static readonly ImmutableHashSet<string> ForbiddenSignatures =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "LAND", "NAVM", "NAVI", "WATR", "LTEX",
            "LCTN", "REGN", "CLMT", "MUSC", "IMGS");

    private static readonly ImmutableHashSet<string> ForbiddenCellPayload =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "DATA", "LTMP",
            "XCLL", "XLCN", "XCLW", "XCWT", "XCAS",
            "XCCM", "XCIM", "XOWN", "XEZN");

    public BethesdaSkyrimFollowerFinishWorldVerification Verify(
        WorkspacePath outputPlugin,
        SkyrimFollowerFinishProposal proposal,
        P2Int expectedCellGrid)
    {
        _ = GetType();
        ArgumentNullException.ThrowIfNull(proposal);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var surface = ImmutableArray.CreateBuilder<string>();
        if (!File.Exists(outputPlugin.Value) ||
            Directory.Exists(outputPlugin.Value))
        {
            diagnostics.Add(Error(
                "follower-finish-world-output",
                "The output plugin is not an ordinary file."));
            return Result(surface, diagnostics);
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(outputPlugin.Value);
            RawAudit raw = AuditRaw(bytes, surface, diagnostics);
            VerifyRawIdentity(
                raw,
                proposal,
                expectedCellGrid,
                diagnostics);
            if (!HasErrors(diagnostics))
                VerifyTyped(
                    outputPlugin,
                    raw,
                    proposal,
                    expectedCellGrid,
                    diagnostics);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or OverflowException or
                RecordException)
        {
            diagnostics.Add(Error(
                "follower-finish-world-read",
                exception.Message));
        }
        return Result(surface, diagnostics);
    }

    private static RawAudit AuditRaw(
        byte[] bytes,
        ImmutableArray<string>.Builder surface,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (bytes.Length < 24 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "TES4")
            throw new InvalidDataException(
                "The plugin does not begin with one TES4 record.");
        int tes4BodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        int firstGroup = checked(24 + tes4BodyLength);
        if (firstGroup > bytes.Length)
            throw new InvalidDataException(
                "The TES4 record extends beyond the file.");

        ImmutableArray<string> masters = ReadSubrecords(
                bytes.AsSpan(24, tes4BodyLength))
            .Where(subrecord => subrecord.Signature == "MAST")
            .Select(subrecord => Encoding.UTF8
                .GetString(subrecord.Payload.AsSpan())
                .TrimEnd('\0'))
            .ToImmutableArray();

        var records = ImmutableArray.CreateBuilder<RawRecord>();
        var groupNodes = ImmutableArray.CreateBuilder<RawGroup>();
        Walk(firstGroup, bytes.Length, ImmutableArray<string>.Empty);
        return new RawAudit(
            masters,
            groupNodes.ToImmutable(),
            records.ToImmutable());

        void Walk(
            int start,
            int end,
            ImmutableArray<string> groups)
        {
            int position = start;
            while (position < end)
            {
                if (position + 24 > end)
                    throw new InvalidDataException(
                        "A raw group/record header is truncated.");
                string signature = Encoding.ASCII.GetString(
                    bytes,
                    position,
                    4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                if (signature == "GRUP")
                {
                    int groupEnd = checked(position + (int)size);
                    if (size < 24 || groupEnd > end)
                        throw new InvalidDataException(
                            "A raw GRUP size is invalid.");
                    string label = Convert.ToHexString(
                        bytes.AsSpan(position + 8, 4));
                    uint groupType =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(position + 12, 4));
                    string token = $"{groupType}:{label}";
                    ImmutableArray<string> fullPath =
                        groups.Add(token);
                    groupNodes.Add(new RawGroup(
                        groupType,
                        label,
                        groups,
                        fullPath,
                        position,
                        checked((int)size),
                        groupEnd));
                    surface.Add(
                        $"GRUP {string.Join("/", fullPath)}");
                    Walk(
                        position + 24,
                        groupEnd,
                        fullPath);
                    position = groupEnd;
                    continue;
                }

                int bodyLength = checked((int)size);
                int recordEnd = checked(position + 24 + bodyLength);
                if (recordEnd > end)
                    throw new InvalidDataException(
                        $"Raw {signature} size exceeds its parent group.");
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 8, 4));
                uint formId = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
                ImmutableArray<RawSubrecord> subrecords =
                    ReadSubrecords(
                        bytes.AsSpan(position + 24, bodyLength));
                uint versionControl =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 16, 4));
                ushort formVersion =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.AsSpan(position + 20, 2));
                ushort unknown =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.AsSpan(position + 22, 2));
                records.Add(new RawRecord(
                    signature,
                    formId,
                    flags,
                    bodyLength,
                    versionControl,
                    formVersion,
                    unknown,
                    groups,
                    subrecords));
                surface.Add(
                    $"{string.Join("/", groups)}:{signature}:" +
                    $"0x{formId:X8}");
                if (ForbiddenSignatures.Contains(signature))
                    diagnostics.Add(Error(
                        "follower-finish-world-forbidden-signature",
                        $"Forbidden {signature} appears at " +
                        $"{string.Join("/", groups)}."));
                if (signature == "CELL" &&
                    subrecords.Any(subrecord =>
                        ForbiddenCellPayload.Contains(
                            subrecord.Signature)))
                    diagnostics.Add(Error(
                        "follower-finish-world-cell-payload",
                        "The structural CELL contains copied lighting, location, water, or related payload."));
                position = recordEnd;
            }
            if (position != end)
                throw new InvalidDataException(
                    "Raw group walking did not end on its parent boundary.");
        }
    }

    private static ImmutableArray<RawSubrecord>
        ReadSubrecords(ReadOnlySpan<byte> body)
    {
        var result =
            ImmutableArray.CreateBuilder<RawSubrecord>();
        int position = 0;
        int? extendedLength = null;
        while (position < body.Length)
        {
            if (position + 6 > body.Length)
                throw new InvalidDataException(
                    "A raw subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                body.Slice(position, 4));
            int length =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.Slice(position + 4, 2));
            if (signature == "XXXX")
            {
                if (length != 4 || position + 10 > body.Length)
                    throw new InvalidDataException(
                        "A raw XXXX subrecord is malformed.");
                extendedLength = checked((int)
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(position + 6, 4)));
                result.Add(new RawSubrecord(
                    signature,
                    body.Slice(position + 6, length).ToArray()
                        .ToImmutableArray()));
                position += 10;
                continue;
            }
            if (extendedLength.HasValue)
            {
                length = extendedLength.Value;
                extendedLength = null;
            }
            if (position + 6 + length > body.Length)
                throw new InvalidDataException(
                    $"Raw {signature} subrecord exceeds its record.");
            result.Add(new RawSubrecord(
                signature,
                body.Slice(position + 6, length).ToArray()
                    .ToImmutableArray()));
            position += 6 + length;
        }
        return result.ToImmutable();
    }

    private static void VerifyRawIdentity(
        RawAudit audit,
        SkyrimFollowerFinishProposal proposal,
        P2Int expectedCellGrid,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimFollowerFinishRequest request = proposal.Request;
        uint? expectedWorldId = ResolveRawFormId(
            request.Placement.Worldspace,
            audit,
            diagnostics);
        uint? expectedCellId = ResolveRawFormId(
            request.Placement.Cell,
            audit,
            diagnostics);
        uint? expectedMarkerId = ResolveRawFormId(
            request.Placement.MarkerBase,
            audit,
            diagnostics);
        uint expectedPackageId = SelfRawFormId(
            request.Allocation.Package.Value,
            audit.MasterCount);
        uint expectedAnchorId = SelfRawFormId(
            request.Allocation.Anchor.Value,
            audit.MasterCount);
        uint expectedActorId = SelfRawFormId(
            request.Allocation.Actor.Value,
            audit.MasterCount);
        uint expectedNpcId = SelfRawFormId(
            request.NpcFormId.Value,
            audit.MasterCount);

        RawRecord[] packages = audit.Records
            .Where(record => record.Signature == "PACK")
            .ToArray();
        if (packages.Length != 1 ||
            packages[0].FormId != expectedPackageId)
            diagnostics.Add(Error(
                "follower-finish-world-group-tree",
                "The output lacks the one allocated package record."));
        else
            RequireGroups(
                packages[0],
                [GroupToken(0, SignatureLabel("PACK"))],
                diagnostics);

        RawRecord[] worlds = audit.Records
            .Where(record => record.Signature == "WRLD")
            .ToArray();
        if (worlds.Length != 0)
            diagnostics.Add(Error(
                "follower-finish-world-partial-master-record",
                "The output contains an actual master WRLD record; " +
                "only the exact world-child placement group is allowed."));

        RawRecord[] cells = audit.Records
            .Where(record => record.Signature == "CELL")
            .ToArray();
        if (cells.Length != 1 ||
            expectedCellId is null ||
            cells[0].FormId != expectedCellId.Value)
            diagnostics.Add(Error(
                "follower-finish-world-cell",
                "The output lacks the one proposal-selected structural CELL."));
        else if (expectedWorldId is not null)
        {
            RequireGroups(
                cells[0],
                ExpectedCellGroups(
                    expectedWorldId.Value,
                    expectedCellGrid),
                diagnostics);
            RequireExactRecord(
                cells[0],
                0,
                [
                    new RawSubrecord(
                        "XCLC",
                        CellGridPayload(expectedCellGrid))
                ],
                "follower-finish-world-cell-payload",
                diagnostics);
        }

        RawRecord[] anchors = audit.Records
            .Where(record => record.Signature == "REFR")
            .ToArray();
        RawRecord[] actors = audit.Records
            .Where(record => record.Signature == "ACHR")
            .ToArray();
        if (anchors.Length != 1 || actors.Length != 1 ||
            anchors.SingleOrDefault()?.FormId != expectedAnchorId ||
            actors.SingleOrDefault()?.FormId != expectedActorId)
            diagnostics.Add(Error(
                "follower-finish-world-reference-count",
                "The output does not contain exactly one allocated REFR and ACHR."));
        if (anchors.Any(record =>
                !IsSelfOwned(record.FormId, audit.MasterCount)) ||
            actors.Any(record =>
                !IsSelfOwned(record.FormId, audit.MasterCount)))
            diagnostics.Add(Error(
                "follower-finish-world-existing-reference-override",
                "A placed reference overrides an existing external FormID."));

        if (expectedWorldId is not null &&
            expectedCellId is not null)
        {
            ImmutableArray<string> persistentGroups =
                ExpectedCellGroups(
                        expectedWorldId.Value,
                        expectedCellGrid)
                    .Add(GroupToken(
                        6,
                        FormLabel(expectedCellId.Value)))
                    .Add(GroupToken(
                        8,
                        FormLabel(expectedCellId.Value)));
            if (anchors.Length == 1 &&
                expectedMarkerId is not null)
            {
                RequireGroups(
                    anchors[0],
                    persistentGroups,
                    diagnostics);
                RequireExactRecord(
                    anchors[0],
                    0x400,
                    [
                        new RawSubrecord(
                            "NAME",
                            UInt32Payload(
                                expectedMarkerId.Value)),
                        new RawSubrecord(
                            "DATA",
                            TransformPayload(
                                request.Placement.Anchor))
                    ],
                    "follower-finish-world-record-payload",
                    diagnostics);
            }
            if (actors.Length == 1)
            {
                RequireGroups(
                    actors[0],
                    persistentGroups,
                    diagnostics);
                RequireExactRecord(
                    actors[0],
                    0x400,
                    [
                        new RawSubrecord(
                            "NAME",
                            UInt32Payload(expectedNpcId)),
                        new RawSubrecord(
                            "DATA",
                            TransformPayload(
                                request.Placement.Actor))
                    ],
                    "follower-finish-world-record-payload",
                    diagnostics);
            }
        }

        foreach (RawRecord record in audit.Records)
        {
            if (IsSelfOwned(record.FormId, audit.MasterCount))
                continue;
            bool structuralParent =
                expectedCellId is not null &&
                record.Signature == "CELL" &&
                record.FormId == expectedCellId.Value;
            if (!structuralParent)
                diagnostics.Add(Error(
                    "follower-finish-world-existing-record-override",
                    "An output record overrides external ownership outside the exact structural parents."));
        }

        if (expectedWorldId is not null &&
            expectedCellId is not null)
            VerifyCompleteGroupTree(
                audit,
                proposal,
                expectedWorldId.Value,
                expectedCellId.Value,
                expectedCellGrid,
                diagnostics);
    }

    private static void VerifyCompleteGroupTree(
        RawAudit audit,
        SkyrimFollowerFinishProposal proposal,
        uint expectedWorldId,
        uint expectedCellId,
        P2Int expectedCellGrid,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expectedRecords =
            new Dictionary<
                (string Signature, uint FormId),
                ImmutableArray<string>>();
        foreach (string row in
                 proposal.SourceSnapshot.RecordInventory)
        {
            int separator = row.IndexOf(' ');
            int marker = row.LastIndexOf(
                "0x",
                StringComparison.Ordinal);
            if (separator <= 0 ||
                marker <= separator ||
                !uint.TryParse(
                    row.AsSpan(marker + 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint localFormId))
                throw new InvalidDataException(
                    "The proposal source inventory contains a malformed record identity.");
            string signature = row[..separator];
            expectedRecords.Add(
                (
                    signature,
                    SelfRawFormId(
                        localFormId,
                        audit.MasterCount)),
                [GroupToken(
                    0,
                    SignatureLabel(signature))]);
        }

        SkyrimFollowerFinishRequest request = proposal.Request;
        expectedRecords.Add(
            (
                "PACK",
                SelfRawFormId(
                    request.Allocation.Package.Value,
                    audit.MasterCount)),
            [GroupToken(0, SignatureLabel("PACK"))]);
        ImmutableArray<string> cellGroups =
            ExpectedCellGroups(
                expectedWorldId,
                expectedCellGrid);
        expectedRecords.Add(
            ("CELL", expectedCellId),
            cellGroups);
        ImmutableArray<string> persistentGroups =
            cellGroups
                .Add(GroupToken(
                    6,
                    FormLabel(expectedCellId)))
                .Add(GroupToken(
                    8,
                    FormLabel(expectedCellId)));
        expectedRecords.Add(
            (
                "REFR",
                SelfRawFormId(
                    request.Allocation.Anchor.Value,
                    audit.MasterCount)),
            persistentGroups);
        expectedRecords.Add(
            (
                "ACHR",
                SelfRawFormId(
                    request.Allocation.Actor.Value,
                    audit.MasterCount)),
            persistentGroups);

        bool exactRecords =
            audit.Records.Length == expectedRecords.Count;
        foreach (RawRecord record in audit.Records)
        {
            if (!expectedRecords.TryGetValue(
                    (record.Signature, record.FormId),
                    out ImmutableArray<string> expectedPath))
            {
                exactRecords = false;
                continue;
            }
            if (!record.Groups.SequenceEqual(expectedPath))
                exactRecords = false;
        }
        if (!exactRecords)
            diagnostics.Add(Error(
                "follower-finish-world-group-tree",
                "The raw record inventory does not match the complete allowed structural paths."));

        var expectedGroups =
            ImmutableHashSet.CreateBuilder<string>(
                StringComparer.Ordinal);
        foreach (ImmutableArray<string> recordPath in
                 expectedRecords.Values)
            for (int length = 1;
                 length <= recordPath.Length;
                 length++)
                expectedGroups.Add(
                    string.Join(
                        "/",
                        recordPath.Take(length)));

        string[] observedPaths = audit.Groups
            .Select(group =>
                string.Join("/", group.FullPath))
            .ToArray();
        bool exactGroups =
            observedPaths.Length == expectedGroups.Count &&
            observedPaths.Distinct(
                    StringComparer.Ordinal)
                .Count() == observedPaths.Length &&
            observedPaths.All(expectedGroups.Contains) &&
            audit.Groups.All(group =>
                group.Size >= 24 &&
                group.End - group.Start == group.Size &&
                group.FullPath.Length ==
                group.ParentPath.Length + 1);
        if (!exactGroups)
            diagnostics.Add(Error(
                "follower-finish-world-group-tree",
                "The raw plugin contains an extra, duplicate, empty, unknown, nested, or out-of-bounds group node."));
    }

    private static uint? ResolveRawFormId(
        FormReference reference,
        RawAudit audit,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int masterIndex = -1;
        for (int index = 0;
             index < audit.Masters.Length;
             index++)
            if (string.Equals(
                    audit.Masters[index],
                    reference.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                masterIndex = index;
                break;
            }
        if (masterIndex < 0 ||
            masterIndex > byte.MaxValue ||
            reference.FormId.Value > 0x00FF_FFFF)
        {
            diagnostics.Add(Error(
                "follower-finish-world-master-identity",
                "A proposal-selected external record cannot be resolved through the output master table."));
            return null;
        }
        return checked(
            ((uint)masterIndex << 24) |
            reference.FormId.Value);
    }

    private static uint SelfRawFormId(
        uint localFormId,
        int masterCount)
    {
        if (masterCount > byte.MaxValue ||
            localFormId > 0x00FF_FFFF)
            throw new InvalidDataException(
                "A local record cannot be encoded in the output master table.");
        return checked(
            ((uint)masterCount << 24) |
            localFormId);
    }

    private static void RequireGroups(
        RawRecord record,
        ImmutableArray<string> expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!record.Groups.SequenceEqual(expected))
            diagnostics.Add(Error(
                "follower-finish-world-group-tree",
                $"Raw {record.Signature} is outside its exact structural group path."));
    }

    private static void RequireExactRecord(
        RawRecord record,
        uint expectedFlags,
        ImmutableArray<RawSubrecord> expectedSubrecords,
        string code,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int expectedBodyLength = expectedSubrecords.Sum(
            subrecord => checked(6 + subrecord.Payload.Length));
        bool exact =
            record.Flags == expectedFlags &&
            record.BodyLength == expectedBodyLength &&
            record.VersionControl == 0 &&
            record.FormVersion == 44 &&
            record.Unknown == 0 &&
            record.Subrecords.Length ==
            expectedSubrecords.Length;
        if (exact)
        {
            for (int index = 0;
                 index < expectedSubrecords.Length;
                 index++)
            {
                RawSubrecord actual = record.Subrecords[index];
                RawSubrecord expected = expectedSubrecords[index];
                if (!string.Equals(
                        actual.Signature,
                        expected.Signature,
                        StringComparison.Ordinal) ||
                    !actual.Payload.SequenceEqual(expected.Payload))
                {
                    exact = false;
                    break;
                }
            }
        }
        if (!exact)
            diagnostics.Add(Error(
                code,
                $"Raw {record.Signature} header/subrecord bytes differ from the exact structural identity. " +
                $"expected={DescribeRecord(
                    expectedFlags,
                    expectedBodyLength,
                    0,
                    44,
                    0,
                    expectedSubrecords)}; " +
                $"actual={DescribeRecord(
                    record.Flags,
                    record.BodyLength,
                    record.VersionControl,
                    record.FormVersion,
                    record.Unknown,
                    record.Subrecords)}."));
    }

    private static string DescribeRecord(
        uint flags,
        int bodyLength,
        uint versionControl,
        ushort formVersion,
        ushort unknown,
        ImmutableArray<RawSubrecord> subrecords) =>
        $"flags=0x{flags:X8},body={bodyLength}," +
        $"vc=0x{versionControl:X8},fv={formVersion}," +
        $"u={unknown},subrecords=[" +
        string.Join(
            ",",
            subrecords.Select(subrecord =>
                $"{subrecord.Signature}:{subrecord.Payload.Length}:" +
                Convert.ToHexString(
                    subrecord.Payload.AsSpan()))) +
        "]";

    private static ImmutableArray<string> ExpectedCellGroups(
        uint worldRawFormId,
        P2Int grid)
    {
        short blockX = FloorDivide(grid.X, 32);
        short blockY = FloorDivide(grid.Y, 32);
        short subBlockX = FloorDivide(grid.X, 8);
        short subBlockY = FloorDivide(grid.Y, 8);
        return
        [
            GroupToken(0, SignatureLabel("WRLD")),
            GroupToken(1, FormLabel(worldRawFormId)),
            GroupToken(
                4,
                CoordinateLabel(blockX, blockY)),
            GroupToken(
                5,
                CoordinateLabel(subBlockX, subBlockY))
        ];
    }

    private static short FloorDivide(int value, int divisor)
    {
        int quotient = Math.DivRem(value, divisor, out int remainder);
        if (remainder < 0)
            quotient--;
        return checked((short)quotient);
    }

    private static string GroupToken(
        uint groupType,
        string label) =>
        $"{groupType}:{label}";

    private static string SignatureLabel(string signature) =>
        Convert.ToHexString(
            Encoding.ASCII.GetBytes(signature));

    private static string FormLabel(uint rawFormId) =>
        Convert.ToHexString(
            UInt32Payload(rawFormId).AsSpan());

    private static string CoordinateLabel(
        short x,
        short y)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(0, 2),
            y);
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(2, 2),
            x);
        return Convert.ToHexString(bytes);
    }

    private static ImmutableArray<byte> CellGridPayload(
        P2Int grid)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, 4),
            grid.X);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4, 4),
            grid.Y);
        return bytes.ToImmutableArray();
    }

    private static ImmutableArray<byte> UInt32Payload(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes.ToImmutableArray();
    }

    private static ImmutableArray<byte> TransformPayload(
        SkyrimExteriorTransform transform)
    {
        float[] values =
        [
            checked((float)transform.X),
            checked((float)transform.Y),
            checked((float)transform.Z),
            checked((float)transform.RotationX),
            checked((float)transform.RotationY),
            checked((float)transform.RotationZ)
        ];
        byte[] bytes = new byte[24];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(index * 4, 4),
                values[index]);
        return bytes.ToImmutableArray();
    }

    private static RawSubrecord Expect(
        string signature,
        params byte[] payload) =>
        new(signature, payload.ToImmutableArray());

    private static void VerifyTyped(
        WorkspacePath outputPlugin,
        RawAudit raw,
        SkyrimFollowerFinishProposal proposal,
        P2Int expectedCellGrid,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        uint? expectedWorldId = ResolveRawFormId(
            proposal.Request.Placement.Worldspace,
            raw,
            diagnostics);
        if (expectedWorldId is null)
            return;
        string temporary =
            outputPlugin.Value +
            $".typed-read-{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] hydrated = BuildTypedReadablePlugin(
                File.ReadAllBytes(outputPlugin.Value),
                raw,
                expectedWorldId.Value);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(hydrated);
                stream.Flush(flushToDisk: true);
            }
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension(
                        proposal.Request.Source.Plugin.Value),
                    new FilePath(temporary)),
                SkyrimRelease.SkyrimSE);
            if (output.ModHeader.MasterReferences.Any(master =>
                    master.Master.FileName.String.Contains(
                        "Lux",
                        StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(Error(
                    "follower-finish-world-lux-master",
                    "A Lux-family master appears in the output."));

            FormKey expectedWorld = ToFormKey(
                proposal.Request.Placement.Worldspace);
            FormKey expectedCell = ToFormKey(
                proposal.Request.Placement.Cell);
            IWorldspaceGetter? world = output.Worldspaces
                .SingleOrDefault();
            if (world is null || world.FormKey != expectedWorld)
            {
                diagnostics.Add(Error(
                    "follower-finish-world-world",
                    "Typed readback does not contain the selected world."));
                return;
            }
            if (world.EditorID is not null)
                diagnostics.Add(Error(
                    "follower-finish-world-world-payload",
                    "Typed WRLD readback contains copied payload."));

            ICellGetter[] cells = world.SubCells
                .SelectMany(block => block.Items)
                .SelectMany(subBlock => subBlock.Items)
                .ToArray();
            if (cells.Length != 1 ||
                cells[0].FormKey != expectedCell ||
                cells[0].Grid?.Point != expectedCellGrid)
            {
                diagnostics.Add(Error(
                    "follower-finish-world-cell",
                    "Typed readback does not contain the selected exterior CELL/grid."));
                return;
            }
            ICellGetter cell = cells[0];
            if (cell.EditorID is not null ||
                cell.Lighting is not null ||
                cell.Location.FormKeyNullable is not null ||
                cell.WaterHeight is not null ||
                cell.AcousticSpace.FormKeyNullable is not null ||
                cell.ImageSpace.FormKeyNullable is not null)
                diagnostics.Add(Error(
                    "follower-finish-world-cell-payload",
                    "Typed CELL readback contains copied payload."));

            IPlacedObjectGetter[] anchors = cell.Persistent
                .OfType<IPlacedObjectGetter>()
                .ToArray();
            IPlacedNpcGetter[] actors = cell.Persistent
                .OfType<IPlacedNpcGetter>()
                .ToArray();
            int allPlaced = output.EnumerateMajorRecords()
                .OfType<IPlacedGetter>()
                .Count();
            if (anchors.Length != 1 ||
                actors.Length != 1 ||
                allPlaced != 2)
                diagnostics.Add(Error(
                    "follower-finish-world-reference-count",
                    "Typed readback does not contain exactly one marker and actor."));
            if (anchors.Length == 1 &&
                anchors[0].Base.FormKey !=
                ToFormKey(proposal.Request.Placement.MarkerBase))
                diagnostics.Add(Error(
                    "follower-finish-world-anchor-base",
                    "The marker base differs from the proposal."));
            if (actors.Length == 1 &&
                actors[0].Base.FormKey != new FormKey(
                    output.ModKey,
                    proposal.Request.NpcFormId.Value))
                diagnostics.Add(Error(
                    "follower-finish-world-actor-base",
                    "The actor base differs from the proposal NPC."));
            if (anchors.Length == 1 &&
                !TransformMatches(
                    anchors[0].Placement,
                    proposal.Request.Placement.Anchor) ||
                actors.Length == 1 &&
                !TransformMatches(
                    actors[0].Placement,
                    proposal.Request.Placement.Actor))
                diagnostics.Add(Error(
                    "follower-finish-world-transform",
                    "A placed-reference transform differs from the proposal."));
            if (cell.Temporary.Count != 0)
                diagnostics.Add(Error(
                    "follower-finish-world-persistent",
                    "The structural CELL contains a temporary placed reference."));
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static byte[] BuildTypedReadablePlugin(
        byte[] source,
        RawAudit raw,
        uint rawWorldFormId)
    {
        RawGroup[] topWorldGroups = raw.Groups
            .Where(group =>
                group.GroupType == 0 &&
                group.ParentPath.IsDefaultOrEmpty &&
                group.Label == SignatureLabel("WRLD"))
            .ToArray();
        if (topWorldGroups.Length != 1)
            throw new InvalidDataException(
                "The raw output lacks one top-level WRLD group for typed readback.");
        RawGroup topWorld = topWorldGroups[0];
        byte[] record = new byte[81];
        Encoding.ASCII.GetBytes("WRLD")
            .CopyTo(record, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(4, 4),
            57);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(12, 4),
            rawWorldFormId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.AsSpan(20, 2),
            44);
        int body = 24;
        foreach ((string signature, int length) in
                 new[]
                 {
                     ("ONAM", 16),
                     ("DATA", 1),
                     ("NAM0", 8),
                     ("NAM9", 8)
                 })
        {
            Encoding.ASCII.GetBytes(signature)
                .CopyTo(record, body);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(body + 4, 2),
                checked((ushort)length));
            body += 6 + length;
        }
        if (body != record.Length)
            throw new InvalidDataException(
                "The typed-read WRLD envelope is malformed.");

        int insertAt = topWorld.Start + 24;
        byte[] hydrated = new byte[
            source.Length + record.Length];
        source.AsSpan(0, insertAt).CopyTo(hydrated);
        record.CopyTo(hydrated, insertAt);
        source.AsSpan(insertAt).CopyTo(
            hydrated.AsSpan(insertAt + record.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            hydrated.AsSpan(topWorld.Start + 4, 4),
            checked((uint)(topWorld.Size + record.Length)));

        int tes4BodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(4, 4)));
        int position = 24;
        int end = checked(24 + tes4BodyLength);
        while (position < end)
        {
            string signature = Encoding.ASCII.GetString(
                source,
                position,
                4);
            int length =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    source.AsSpan(position + 4, 2));
            if (signature == "HEDR")
            {
                if (length != 12)
                    throw new InvalidDataException(
                        "The typed-read TES4 HEDR envelope is malformed.");
                uint count =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.AsSpan(position + 10, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    hydrated.AsSpan(position + 10, 4),
                    checked(count + 1));
                return hydrated;
            }
            position = checked(position + 6 + length);
        }
        throw new InvalidDataException(
            "The typed-read TES4 record lacks HEDR.");
    }

    private static bool TransformMatches(
        IPlacementGetter? actual,
        SkyrimExteriorTransform expected) =>
        actual is not null &&
        Near(actual.Position.X, expected.X) &&
        Near(actual.Position.Y, expected.Y) &&
        Near(actual.Position.Z, expected.Z) &&
        Near(actual.Rotation.X, expected.RotationX) &&
        Near(actual.Rotation.Y, expected.RotationY) &&
        Near(actual.Rotation.Z, expected.RotationZ);

    private static bool Near(float actual, double expected) =>
        Math.Abs(actual - expected) < 0.001;

    private static uint LowId(uint rawFormId) =>
        rawFormId & 0x00FF_FFFFu;

    private static bool IsSelfOwned(
        uint rawFormId,
        int masterCount) =>
        (rawFormId >> 24) == (uint)masterCount;

    private static FormKey ToFormKey(FormReference reference) =>
        new(
            ModKey.FromNameAndExtension(reference.Plugin.Value),
            reference.FormId.Value);

    private static BethesdaSkyrimFollowerFinishWorldVerification Result(
        ImmutableArray<string>.Builder surface,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            !HasErrors(diagnostics),
            surface.ToImmutable(),
            diagnostics.ToImmutable());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record RawAudit(
        ImmutableArray<string> Masters,
        ImmutableArray<RawGroup> Groups,
        ImmutableArray<RawRecord> Records)
    {
        public int MasterCount => Masters.Length;
    }

    private sealed record RawGroup(
        uint GroupType,
        string Label,
        ImmutableArray<string> ParentPath,
        ImmutableArray<string> FullPath,
        int Start,
        int Size,
        int End);

    private sealed record RawRecord(
        string Signature,
        uint FormId,
        uint Flags,
        int BodyLength,
        uint VersionControl,
        ushort FormVersion,
        ushort Unknown,
        ImmutableArray<string> Groups,
        ImmutableArray<RawSubrecord> Subrecords);

    private sealed record RawSubrecord(
        string Signature,
        ImmutableArray<byte> Payload);
}
