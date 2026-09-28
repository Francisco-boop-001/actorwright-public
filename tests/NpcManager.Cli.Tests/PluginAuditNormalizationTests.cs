using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunAuditWorldParentsAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "task23-world", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var path = Child(root, "Audit.esp");
        WriteAuditFixture(path, ["Skyrim.esm"]);
        // A copied exterior override may contain world children without a
        // complete parent WRLD record; it remains a bounded raw record surface.
        byte[] cell = new byte[32];
        "CELL"u8.CopyTo(cell);
        BinaryPrimitives.WriteUInt32LittleEndian(cell.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(cell.AsSpan(12), 0x3d);
        "DATA"u8.CopyTo(cell.AsSpan(24));
        BinaryPrimitives.WriteUInt16LittleEndian(cell.AsSpan(28), 2);
        byte[] world = AuditGroup(0x444c5257, 0, AuditGroup(0x3c, 1, cell));
        using (var stream = new FileStream(path.Value, FileMode.Append)) stream.Write(world);
        byte[] counted = File.ReadAllBytes(path.Value);
        Require(counted.AsSpan(24, 4).SequenceEqual("HEDR"u8), "Synthetic TES4 HEDR layout changed.");
        BinaryPrimitives.WriteUInt32LittleEndian(counted.AsSpan(34),
            BinaryPrimitives.ReadUInt32LittleEndian(counted.AsSpan(34)) + 3);
        File.WriteAllBytes(path.Value, counted);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0,
                "Exact audit binary discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json",
            "--command", "plugin audit")).ExitCode == 0, "Audit schema discovery failed.");
        var result = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", path.Value, "--json");
        Require(result.ExitCode == 0 && result.Root.GetProperty("beforeRecordCount").GetInt32() == 5 &&
            result.Root.GetProperty("changes").GetArrayLength() == 0 &&
            result.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                row.GetProperty("code").GetString() == "plugin-audit-skeletal-world-parent"),
            "Skeletal WRLD parent must retain all records with an advisory: " + result.Root + result.StdErr);
        byte[] original = File.ReadAllBytes(path.Value);
        bool strictRefused = false;
        try { await new BethesdaPluginReader().ReadAsync(new PluginReadRequest(
            GameEdition.SkyrimSpecialEdition, path), CancellationToken.None); }
        catch (Mutagen.Bethesda.Plugins.Exceptions.RecordException) { strictRefused = true; }
        Require(strictRefused, "Audit-specific tolerance escaped into the ordinary plugin reader.");
        var changedPath = Child(root, "changed", "Audit.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(changedPath.Value)!);
        byte[] changed = original.ToArray();
        changed[^1] ^= 0x20;
        File.WriteAllBytes(changedPath.Value, changed);
        var changedResult = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", changedPath.Value, "--json");
        Require(changedResult.ExitCode == 0 && changedResult.Root.GetProperty("changedRecordCount").GetInt32() == 1,
            "Skeletal audit lost a real child record change: " + changedResult.Root);
        var normalized = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", path.Value, "--normalize-master-index", "--json");
        Require(normalized.ExitCode != 0 && normalized.Root.GetProperty("diagnostics").EnumerateArray()
            .Any(row => row.GetProperty("code").GetString() == "plugin-audit-normalization-unavailable"),
            "Incomplete typed world graphs must not claim proven FormID normalization.");
        byte[] invalidHeader = original.ToArray();
        invalidHeader[24] = (byte)'X';
        File.WriteAllBytes(changedPath.Value, invalidHeader);
        var invalidHeaderResult = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", changedPath.Value, "--json");
        var headerFailures = new List<string>();
        if (invalidHeaderResult.ExitCode == 0) headerFailures.Add("missing HEDR admitted");
        using var masterStream = new MemoryStream();
        using (var writer = new BinaryWriter(masterStream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("MAST"u8); writer.Write((ushort)11); writer.Write("skyrim.esm\0"u8);
            writer.Write("DATA"u8); writer.Write((ushort)8); writer.Write(0UL);
        }
        byte[] duplicateMaster = masterStream.ToArray();
        int headerEnd = 24 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(4)));
        byte[] duplicated = [.. original.AsSpan(0, headerEnd).ToArray(), .. duplicateMaster, .. world];
        BinaryPrimitives.WriteUInt32LittleEndian(duplicated.AsSpan(34), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(duplicated.AsSpan(4),
            checked((uint)(headerEnd - 24 + duplicateMaster.Length)));
        File.WriteAllBytes(changedPath.Value, duplicated);
        var duplicateResult = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", changedPath.Value, "--json");
        if (duplicateResult.ExitCode == 0) headerFailures.Add("case-insensitive duplicate master admitted");
        Require(headerFailures.Count == 0, "Skeletal TES4 header guards: " + string.Join("; ", headerFailures));
        // An unrelated malformed interior group remains a hard refusal even
        // when the same plugin also contains an admitted skeletal world parent.
        byte[] orphan = AuditGroup(0x4c4c4543, 0,
            AuditGroup(0, 2, AuditGroup(0, 3, AuditGroup(0x3d, 6, []))));
        byte[] orphanBytes = [.. original, .. orphan];
        BinaryPrimitives.WriteUInt32LittleEndian(orphanBytes.AsSpan(34),
            BinaryPrimitives.ReadUInt32LittleEndian(orphanBytes.AsSpan(34)) + 4);
        File.WriteAllBytes(changedPath.Value, orphanBytes);
        var refused = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse",
            "--before", path.Value, "--after", changedPath.Value, "--json");
        Require(refused.ExitCode != 0 && refused.Root.GetProperty("diagnostics").EnumerateArray()
            .Any(row => row.GetProperty("code").GetString() == "orphan-cell-children"),
            "Skeletal tolerance hid an unrelated orphan interior CELL group.");
    }

    private static byte[] AuditGroup(uint label, uint type, byte[] content)
    {
        byte[] group = new byte[24 + content.Length];
        "GRUP"u8.CopyTo(group);
        BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(4), checked((uint)group.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(8), label);
        BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(12), type);
        content.CopyTo(group, 24);
        return group;
    }

    internal static async Task RunAuditNormalizationAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "task23", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0,
                "Exact audit binary discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json",
            "--command", "plugin audit")).ExitCode == 0, "Audit schema discovery failed.");
        var before = Child(root, "before", "Audit.esp");
        var after = Child(root, "after", "Audit.esp");
        WriteAuditFixture(before, ["Unused.esm", "Skyrim.esm"]);
        WriteAuditFixture(after, ["Skyrim.esm"]);
        async Task<ProtocolInvocation> Audit(bool normalized) => await RunCliAsync(root,
            normalized
                ? ["plugin", "audit", "--edition", "skyrimse", "--before", before.Value,
                    "--after", after.Value, "--normalize-master-index", "--json"]
                : ["plugin", "audit", "--edition", "skyrimse", "--before", before.Value,
                    "--after", after.Value, "--json"]);
        var raw = await Audit(false);
        Require(raw.ExitCode == 0 && raw.Root.GetProperty("changedRecordCount").GetInt32() > 0,
            "Raw audit must retain physical master-index sensitivity: " + raw.Root + raw.StdErr);
        var normalized = await Audit(true);
        Require(normalized.ExitCode == 0 && normalized.Root.GetProperty("changes").GetArrayLength() == 0,
            "Unused-master removal changed normalized records: " + normalized.Root + normalized.StdErr);
        Require(normalized.Root.GetProperty("beforeRecordCount").GetInt32() == 4,
            "Owner-colliding local IDs were dropped from the audit.");

        // A field outside PluginRecordSummary must still change the raw digest.
        WriteAuditFixture(after, ["Skyrim.esm"], changedColor: true);
        var changed = await Audit(true);
        Require(changed.ExitCode == 0 && changed.Root.GetProperty("changedRecordCount").GetInt32() == 1,
            "Normalization hid an unmodeled record field change: " + changed.Root + changed.StdErr);
        WriteAuditFixture(after, ["Skyrim.esm"], changedLink: true);
        var relinked = await Audit(true);
        Require(relinked.ExitCode == 0 && relinked.Root.GetProperty("changedRecordCount").GetInt32() == 1,
            "Normalization hid a same-local-ID FormLink owner change: " + relinked.Root + relinked.StdErr);
    }

    private static void WriteAuditFixture(WorkspacePath path, string[] masters,
        bool changedColor = false, bool changedLink = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
        ModKey self = ModKey.FromNameAndExtension("Audit.esp");
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(self, SkyrimRelease.SkyrimSE);
        foreach (string master in masters)
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension(master) });
        mod.Keywords.Add(new Keyword(new FormKey(self, 0x800), SkyrimRelease.SkyrimSE)
            { EditorID = "OwnedKeyword" });
        mod.Keywords.Add(new Keyword(new FormKey(skyrim, 0x800), SkyrimRelease.SkyrimSE)
            { EditorID = "OverriddenKeyword" });
        mod.TextureSets.Add(new TextureSet(new FormKey(self, 0x801), SkyrimRelease.SkyrimSE)
            { EditorID = "OwnedTexture" });
        var npc = new Npc(new FormKey(self, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "AuditNpc" };
        npc.HeadTexture.SetTo(new FormKey(self, 0x801));
        npc.Keywords = [new FormLink<IKeywordGetter>(new FormKey(changedLink ? skyrim : self, 0x800))];
        mod.Npcs.Add(npc);
        mod.WriteToBinary(path.Value, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck
        });
        if (changedColor)
        {
            // Change the actual KYWD CNAM bytes, independently of typed summaries.
            byte[] bytes = File.ReadAllBytes(path.Value);
            int cnam = bytes.AsSpan().IndexOf("CNAM"u8);
            Require(cnam >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cnam + 4)) == 4,
                "Synthetic KYWD CNAM fixture changed.");
            bytes[cnam + 6] ^= 0x40;
            File.WriteAllBytes(path.Value, bytes);
        }
    }
}
