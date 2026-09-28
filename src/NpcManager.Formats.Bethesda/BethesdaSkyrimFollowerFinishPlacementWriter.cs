using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Opaque proof that the fixed production marker capability was located and
/// raw-digest verified in a caller-bound, immutable provider snapshot.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishMarkerAuthority
{
    internal BethesdaSkyrimFollowerFinishMarkerAuthority(
        FormReference markerForm,
        Sha256Hash rawRecordDigest)
    {
        MarkerForm = markerForm;
        RawRecordDigest = rawRecordDigest;
    }

    internal FormReference MarkerForm { get; }

    public Sha256Hash RawRecordDigest { get; }
}

/// <summary>
/// Admits only the fixed, generic marker capability from an already verified
/// immutable Skyrim provider snapshot.
/// </summary>
internal sealed class
    BethesdaSkyrimFollowerFinishMarkerAuthorityAdmission
{
    private static readonly FormReference CanonicalMarkerForm =
        new(
            new PluginName("Skyrim.esm"),
            new FormId(0x0000003B));

    private static readonly Sha256Hash CanonicalMarkerRawDigest =
        new(
            "d53c990732e96dc825cc890e0e969415" +
            "b85e2ca15fb140981c96cf9793a49bae");

    public static BethesdaSkyrimFollowerFinishMarkerAuthority Admit(
        WorkspacePath verifiedProviderSnapshot)
    {
        ImmutableArray<RawMarkerOccurrence> matches =
            ScanMarkerOccurrences(
                verifiedProviderSnapshot.Value);
        if (matches.Length != 1)
            Refuse(
                "follower-finish-plugin-marker-authority",
                "The verified provider snapshot does not contain exactly one canonical marker record.");
        RawMarkerOccurrence occurrence = matches[0];
        if (!string.Equals(
                occurrence.GroupPath,
                "STAT:00000000",
                StringComparison.Ordinal))
            Refuse(
                "follower-finish-plugin-marker-authority",
                "The canonical marker record is outside its exact top-level group path.");
        Sha256Hash digest = occurrence.RawRecordDigest;
        if (digest != CanonicalMarkerRawDigest)
            Refuse(
                "follower-finish-plugin-marker-authority",
                "The canonical marker raw record differs from the fixed production capability.");
        return new BethesdaSkyrimFollowerFinishMarkerAuthority(
            CanonicalMarkerForm,
            digest);
    }

    private static ImmutableArray<RawMarkerOccurrence>
        ScanMarkerOccurrences(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "TES4")
            Refuse(
                "follower-finish-plugin-marker-authority",
                "The verified provider snapshot has no bounded TES4 envelope.");
        int tes4BodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        int firstGroup = checked(24 + tes4BodyLength);
        if (firstGroup > bytes.Length)
            Refuse(
                "follower-finish-plugin-marker-authority",
                "The verified provider TES4 record exceeds its snapshot.");

        var matches =
            ImmutableArray.CreateBuilder<RawMarkerOccurrence>();
        Walk(
            firstGroup,
            bytes.Length,
            string.Empty);
        return matches.ToImmutable();

        void Walk(
            int start,
            int end,
            string parentPath)
        {
            int position = start;
            while (position < end)
            {
                if (position + 24 > end)
                    Refuse(
                        "follower-finish-plugin-marker-authority",
                        "A provider group or record header is truncated.");
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
                        Refuse(
                            "follower-finish-plugin-marker-authority",
                            "A provider group exceeds its parent bounds.");
                    string label = Encoding.ASCII.GetString(
                        bytes,
                        position + 8,
                        4);
                    uint groupType =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(position + 12, 4));
                    string token =
                        $"{label}:{groupType:X8}";
                    string groupPath =
                        string.IsNullOrEmpty(parentPath)
                            ? token
                            : $"{parentPath}/{token}";
                    Walk(
                        position + 24,
                        groupEnd,
                        groupPath);
                    position = groupEnd;
                    continue;
                }

                int recordLength = checked(24 + (int)size);
                int recordEnd = checked(position + recordLength);
                if (recordEnd > end)
                    Refuse(
                        "follower-finish-plugin-marker-authority",
                        "A provider record exceeds its parent bounds.");
                uint formId =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                if (signature == "STAT" &&
                    (formId & 0x00FF_FFFFu) ==
                    CanonicalMarkerForm.FormId.Value)
                {
                    byte[] prefix = Encoding.UTF8.GetBytes(
                        parentPath + "\n");
                    byte[] digestInput =
                        new byte[prefix.Length + recordLength];
                    prefix.CopyTo(digestInput, 0);
                    bytes.AsSpan(position, recordLength)
                        .CopyTo(digestInput.AsSpan(prefix.Length));
                    matches.Add(new RawMarkerOccurrence(
                        parentPath,
                        new Sha256Hash(
                            Convert.ToHexString(
                                SHA256.HashData(digestInput)))));
                }
                position = recordEnd;
            }
            if (position != end)
                Refuse(
                    "follower-finish-plugin-marker-authority",
                    "Provider scanning did not end on its parent boundary.");
        }
    }

    private sealed record RawMarkerOccurrence(
        string GroupPath,
        Sha256Hash RawRecordDigest);

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");
}

/// <summary>
/// Adds only the proposal-selected structural exterior-cell branch and the two
/// proposal-owned persistent references to an admitted follower-finish core.
/// No winning world or cell payload is copied.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishPlacementWriter
{
    private static readonly ModKey Skyrim =
        ModKey.FromNameAndExtension("Skyrim.esm");

    public SkyrimMod Write(
        SkyrimMod core,
        SkyrimFollowerFinishProposal proposal,
        P2Int cellGrid,
        BethesdaSkyrimFollowerFinishMarkerAuthority markerAuthority)
    {
        _ = GetType();
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(markerAuthority);
        SkyrimFollowerFinishRequest request = proposal.Request;
        ValidateEnvelope(
            core,
            proposal,
            request,
            markerAuthority);

        var output = (SkyrimMod)core.DeepCopy();
        EnsureSingleSkyrimMaster(output);

        var world = new Worldspace(
            ToFormKey(request.Placement.Worldspace),
            SkyrimRelease.SkyrimSE);
        var block = new WorldspaceBlock
        {
            BlockNumberX = FloorDivide(cellGrid.X, 32),
            BlockNumberY = FloorDivide(cellGrid.Y, 32),
            GroupType = (GroupTypeEnum)4
        };
        var subBlock = new WorldspaceSubBlock
        {
            BlockNumberX = FloorDivide(cellGrid.X, 8),
            BlockNumberY = FloorDivide(cellGrid.Y, 8),
            GroupType = (GroupTypeEnum)5
        };
        var cell = new Cell(
            ToFormKey(request.Placement.Cell),
            SkyrimRelease.SkyrimSE)
        {
            Grid = new CellGrid
            {
                Point = cellGrid,
                Flags = default
            }
        };

        var anchor = new PlacedObject(
            new FormKey(
                output.ModKey,
                request.Allocation.Anchor.Value),
            SkyrimRelease.SkyrimSE)
        {
            Placement = ToPlacement(request.Placement.Anchor)
        };
        anchor.Base.SetTo(
            ToFormKey(markerAuthority.MarkerForm));
        var actor = new PlacedNpc(
            new FormKey(
                output.ModKey,
                request.Allocation.Actor.Value),
            SkyrimRelease.SkyrimSE)
        {
            Placement = ToPlacement(request.Placement.Actor)
        };
        actor.Base.SetTo(
            new FormKey(output.ModKey, request.NpcFormId.Value));
        SetPersistent(anchor);
        SetPersistent(actor);
        cell.Persistent.Add(anchor);
        cell.Persistent.Add(actor);
        subBlock.Items.Add(cell);
        block.Items.Add(subBlock);
        world.SubCells.Add(block);
        output.Worldspaces.Add(world);
        return output;
    }

    private static void ValidateEnvelope(
        SkyrimMod core,
        SkyrimFollowerFinishProposal proposal,
        SkyrimFollowerFinishRequest request,
        BethesdaSkyrimFollowerFinishMarkerAuthority markerAuthority)
    {
        if (!core.IsSmallMaster ||
            core.ModHeader.Stats.NextFormID !=
            request.Allocation.NextFormId.Value ||
            core.Packages.Count != 1 ||
            core.Packages.Single().FormKey.ID !=
            request.Allocation.Package.Value)
            Refuse(
                "follower-finish-placement-core",
                "Placement requires the admitted completed core model.");
        if (core.Worldspaces.Count != 0 ||
            core.Cells.Count != 0 ||
            core.EnumerateMajorRecords().Any(record =>
                record is IPlacedGetter))
            Refuse(
                "follower-finish-placement-source-world",
                "The core already contains world or placed-reference records.");
        if (proposal.NextFormId != request.Allocation.NextFormId)
            Refuse(
                "follower-finish-placement-allocation",
                "Placement requires the proposal and request to retain the same contiguous allocation.");
        if (request.Placement.MarkerBase !=
            markerAuthority.MarkerForm)
            Refuse(
                "follower-finish-placement-marker-authority",
                "The request marker differs from the admitted fixed marker capability.");
        if (request.Sandbox.Target !=
            new FormReference(
                request.Source.Plugin,
                request.Allocation.Anchor))
            Refuse(
                "follower-finish-placement-anchor-target",
                "The sandbox package does not target the allocated anchor.");
        if (core.ModHeader.MasterReferences.Any(master =>
                master.Master.FileName.String.Contains(
                    "Lux",
                    StringComparison.OrdinalIgnoreCase)))
            Refuse(
                "follower-finish-placement-lux-master",
                "A Lux-family master entered the placement core.");
    }

    private static short FloorDivide(int value, int divisor)
    {
        int quotient = Math.DivRem(value, divisor, out int remainder);
        if (remainder < 0)
            quotient--;
        return checked((short)quotient);
    }

    private static Placement ToPlacement(
        SkyrimExteriorTransform transform) =>
        new()
        {
            Position = new P3Float(
                checked((float)transform.X),
                checked((float)transform.Y),
                checked((float)transform.Z)),
            Rotation = new P3Float(
                checked((float)transform.RotationX),
                checked((float)transform.RotationY),
                checked((float)transform.RotationZ))
        };

    private static void EnsureSingleSkyrimMaster(SkyrimMod output)
    {
        ModKey[] masters = output.ModHeader.MasterReferences
            .Select(master => master.Master)
            .ToArray();
        if (!masters.Contains(Skyrim))
            output.ModHeader.MasterReferences.Add(
                new MasterReference { Master = Skyrim });
        if (output.ModHeader.MasterReferences.Count(master =>
                master.Master == Skyrim) != 1)
            Refuse(
                "follower-finish-placement-skyrim-master",
                "The output requires exactly one Skyrim.esm master.");
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
                "follower-finish-placement-persistent",
                "The placed record does not expose writable Skyrim flags.");
        property.SetValue(
            record,
            Enum.ToObject(current.GetType(), 0x400));
    }

    private static FormKey ToFormKey(FormReference reference) =>
        new(
            ModKey.FromNameAndExtension(reference.Plugin.Value),
            reference.FormId.Value);

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");
}
