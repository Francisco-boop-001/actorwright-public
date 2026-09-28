using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Architecture.Tests;
using NpcManager.Domain;
using NpcManager.Application;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunOutputOwnedHeadPartsAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory, "artifacts", "test-work", "hdpt-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Exact binary discovery failed.");
        foreach (string command in new[] { "records propose", "plugin write", "npc face-patch", "plugin audit" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0, "Schema discovery failed.");
        string legacy = Path.Combine(root.Value, "legacy.record-proposal.json");
        var legacyNew = await RunCliAsync(root, "records", "propose", "--edition", "skyrimse", "--type", "HDPT", "--mode", "new",
            "--form-id", "0x81A", "--editor-id", "LyndaBrowPrivate", "--output", legacy, "--json");
        Require(legacyNew.ExitCode == 0 && File.Exists(legacy), "Previously supported generic HDPT new proposal was refused: " + legacyNew.Root + legacyNew.StdErr);
        Require(HashFile(new WorkspacePath(legacy)).Equals("30107310fecd5de46908c999a499e06bf1ec2737420736abc0c717bfc564df90", StringComparison.OrdinalIgnoreCase), "Legacy HDPT proposal JSON/hash bytes changed.");
        foreach (string mode in new[] { "template", "override" })
        {
            string legacyPath = Path.Combine(root.Value, mode + ".record-proposal.json");
            var oldMode = await RunCliAsync(root, "records", "propose", "--edition", "fallout4", "--type", "HDPT", "--mode", mode,
                "--form-id", "0x81A", "--source", "0x81A", "--editor-id", "LegacyHeadPart", "--output", legacyPath, "--json");
            Require(oldMode.ExitCode == 0 && File.Exists(legacyPath), "Legacy HDPT template/override behavior was removed.");
            using var oldDocument = JsonDocument.Parse(File.ReadAllBytes(legacyPath));
            Require(!oldDocument.RootElement.TryGetProperty("headPart", out _) && oldDocument.RootElement.GetProperty("mode").GetString() == mode,
                "Legacy proposal unexpectedly selected composition.");
        }
        string data = Path.Combine(root.Value, "Data");
        string source = Path.Combine(root.Value, "before", "Lynda.esp");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Lynda.esp"), SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew(new FormKey(mod.ModKey, 0x810)); race.EditorID = "FixtureRace";
        var races = mod.FormLists.AddNew(new FormKey(mod.ModKey, 0x811)); races.Items.Add(race.FormKey);
        var alternate = mod.FormLists.AddNew(new FormKey(mod.ModKey, 0x814)); alternate.Items.Add(race.FormKey);
        var master = ModKey.FromNameAndExtension("SyntheticMaster.esm");
        races.Items.Add(new FormKey(master, 0x1234)); // A real master slot exposes incorrect local-to-raw PNAM encoding.
        var brow = mod.HeadParts.AddNew(new FormKey(mod.ModKey, 0x812)); brow.EditorID = "OldBrow"; brow.Type = HeadPart.TypeEnum.Eyebrows;
        brow.Model = new Model { File = @"actors\character\old.nif" }; brow.ValidRaces.SetTo(races.FormKey);
        var npc = mod.Npcs.AddNew(new FormKey(mod.ModKey, 0x800)); npc.EditorID = "Lynda"; npc.Configuration.Flags = NpcConfiguration.Flag.Female;
        npc.Race.SetTo(race.FormKey); npc.HeadParts.Add(brow.FormKey);
        mod.WriteToBinary(source);
        byte[] before = File.ReadAllBytes(source);
        string[] assets = ["brow.nif", "race.tri", "chargen.tri", "dialogue.tri"];
        byte[] model = WriteHdptTopologyModel(141);
        byte[] tri = new byte[64 + 141 * 12]; "FRTRI003"u8.CopyTo(tri); BinaryPrimitives.WriteUInt32LittleEndian(tri.AsSpan(8), 141);
        var geometry = new SseSelectedHeadpartNifGeometryReader().Read(new SseSelectedHeadpartNifGeometryReadRequest(
            new AssetPath("meshes/brow.nif"), new Sha256Hash(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(model))), [.. model]));
        Require(geometry.Accepted && geometry.Document!.Shapes.Single().VertexCount == 141, "Repair model is not coherent synthetic geometry.");
        foreach (string asset in assets) File.WriteAllBytes(Path.Combine(data, asset), asset.EndsWith(".nif", StringComparison.Ordinal) ? model : tri);
        string proposal = Path.Combine(root.Value, "brow.record-proposal.json");
        var proposed = await RunCliAsync(root, "records", "propose", "--edition", "skyrimse", "--type", "HDPT", "--mode", "new",
            "--form-id", "0x81A", "--editor-id", "LyndaBrowPrivate", "--name", "Private brow", "--model", "brow.nif",
            "--tri-race", "race.tri", "--tri-chargen", "chargen.tri", "--tri-dialogue", "dialogue.tri",
            "--valid-races", "Lynda.esp|0x811", "--flags", "playable,female", "--part-type", "eyebrows", "--output", proposal, "--json");
        Require(proposed.ExitCode == 0, "HDPT proposal failed: " + proposed.Root + proposed.StdErr);
        using var proposalJson = JsonDocument.Parse(File.ReadAllText(proposal));
        Require(proposalJson.RootElement.TryGetProperty("headPart", out var part) && part.GetProperty("model").GetString() == "brow.nif" &&
            part.GetProperty("triRace").GetString() == "race.tri" && part.GetProperty("triChargen").GetString() == "chargen.tri" &&
            part.GetProperty("triDialogue").GetString() == "dialogue.tri", "HDPT proposal discarded model or morph roles.");
        string written = Path.Combine(root.Value, "composed", "Lynda.esp"); Directory.CreateDirectory(Path.GetDirectoryName(written)!);
        string missingData = Path.Combine(root.Value, "missing-data"); Directory.CreateDirectory(missingData);
        var missing = await RunCliAsync(root, "plugin", "write", "--edition", "skyrimse", "--proposal", proposal, "--plugin", source,
            "--expected-sha256", HashFile(new WorkspacePath(source)), "--output", written, "--data-root", missingData, "--private-root", data, "--json");
        Require(missing.ExitCode != 0 && !File.Exists(written) && !Directory.Exists(Path.Combine(data, "meshes", "actors")), "Missing assets published output or private files.");
        var write = await RunCliAsync(root, "plugin", "write", "--edition", "skyrimse", "--proposal", proposal, "--plugin", source,
            "--expected-sha256", HashFile(new WorkspacePath(source)), "--output", written, "--data-root", data, "--private-root", data, "--json");
        Require(write.ExitCode == 0 && File.Exists(written), "HDPT write failed: " + write.Root + write.StdErr);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(written, SkyrimRelease.SkyrimSE))
        {
            var added = read.HeadParts.Single(x => x.EditorID == "LyndaBrowPrivate");
            Require(added.FormKey.ID == 0x81A && added.Type == HeadPart.TypeEnum.Eyebrows && added.ValidRaces.FormKey.ID == 0x811,
                "Independent HDPT readback differs.");
            Require(added.Parts.Count == 3 && added.Parts.Select(item => Convert.ToInt32(item.PartType)).SequenceEqual([0, 1, 2]), "Independent HDPT readback lost or confused morph roles.");
            Require(added.Model!.File!.ToString() == @"Meshes\actors\character\LyndaBrowPrivate\brow.nif" &&
                added.Parts.Select(item => item.FileName!.ToString()).SequenceEqual([@"Meshes\actors\character\LyndaBrowPrivate\race.tri", @"Meshes\actors\character\LyndaBrowPrivate\dialogue.tri", @"Meshes\actors\character\LyndaBrowPrivate\chargen.tri"]), "Private HDPT paths were not rewritten by role.");
            foreach (string asset in assets)
                Require(File.ReadAllBytes(Path.Combine(data, "meshes", "actors", "character", "LyndaBrowPrivate", asset)).SequenceEqual(File.ReadAllBytes(Path.Combine(data, asset))), "Private copy differs.");
        }
        string final = Path.Combine(root.Value, "final", "Lynda.esp"); Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var patch = await RunCliAsync(root, "npc", "face-patch", "--edition", "skyrimse", "--plugin", written, "--output", final,
            "--data-root", data, "--npc", "0x800", "--headpart-replace", "Lynda.esp|0x812=0x81A", "--apply", "true",
            "--expected-sha256", HashFile(new WorkspacePath(written)), "--json");
        Require(patch.ExitCode == 0 && File.Exists(final), "PNAM replacement failed: " + patch.Root + patch.StdErr);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(final, SkyrimRelease.SkyrimSE))
            Require(read.Npcs.Single().HeadParts.Single().FormKey.ID == 0x81A, "NPC PNAM did not resolve output-owned HDPT.");
        string wrongTypeSource = Path.Combine(root.Value, "wrong-type", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongTypeSource)!);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(written, SkyrimRelease.SkyrimSE))
        {
            var wrongType = (SkyrimMod)read.DeepCopy();
            var eyes = wrongType.HeadParts.AddNew(new FormKey(wrongType.ModKey, 0x81C));
            eyes.EditorID = "LyndaEyesPrivate";
            eyes.Type = HeadPart.TypeEnum.Eyes;
            eyes.Flags = HeadPart.Flag.Female;
            eyes.Model = new Model { File = "brow.nif" };
            wrongType.WriteToBinary(wrongTypeSource);
        }
        string wrongTypeOutput = Path.Combine(root.Value, "wrong-type-output", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongTypeOutput)!);
        var wrongTypePatch = await RunCliAsync(root, "npc", "face-patch", "--edition", "skyrimse",
            "--plugin", wrongTypeSource, "--output", wrongTypeOutput, "--data-root", data,
            "--npc", "0x800", "--headpart-replace", "Lynda.esp|0x812=0x81C", "--apply", "true",
            "--expected-sha256", HashFile(new WorkspacePath(wrongTypeSource)), "--json");
        Require(wrongTypePatch.ExitCode != 0 && !File.Exists(wrongTypeOutput) &&
            wrongTypePatch.Root.ToString().Contains("headpart-replacement-type-mismatch", StringComparison.Ordinal),
            "A brow PNAM was replaced by an eyes HDPT: " + wrongTypePatch.Root + wrongTypePatch.StdErr);

        var externalKey = ModKey.FromNameAndExtension("ExternalParts.esp");
        string externalProvider = Path.Combine(data, externalKey.FileName.String);
        var externalMod = new SkyrimMod(externalKey, SkyrimRelease.SkyrimSE);
        var externalBrow = externalMod.HeadParts.AddNew(new FormKey(externalKey, 0x900));
        externalBrow.EditorID = "ExternalBrow";
        externalBrow.Type = HeadPart.TypeEnum.Eyebrows;
        var externalEyes = externalMod.HeadParts.AddNew(new FormKey(externalKey, 0x901));
        externalEyes.EditorID = "ExternalEyes";
        externalEyes.Type = HeadPart.TypeEnum.Eyes;
        externalMod.WriteToBinary(externalProvider);

        string externalOldSource = Path.Combine(root.Value, "external-old", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(externalOldSource)!);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(written, SkyrimRelease.SkyrimSE))
        {
            var externalOld = (SkyrimMod)read.DeepCopy();
            var externalNpc = externalOld.Npcs.Single();
            externalNpc.HeadParts.Clear();
            externalNpc.HeadParts.Add(externalBrow.FormKey);
            externalOld.WriteToBinary(externalOldSource);
        }
        string externalOldOutput = Path.Combine(root.Value, "external-old-output", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(externalOldOutput)!);
        var externalOldPatch = await RunCliAsync(root, "npc", "face-patch", "--edition", "skyrimse",
            "--plugin", externalOldSource, "--output", externalOldOutput, "--data-root", data,
            "--npc", "0x800", "--headpart-replace", "ExternalParts.esp|0x900=0x81A", "--apply", "true",
            "--expected-sha256", HashFile(new WorkspacePath(externalOldSource)), "--json");
        Require(externalOldPatch.ExitCode == 0 && File.Exists(externalOldOutput),
            "An externally owned brow PNAM could not be replaced by a local brow: " +
            externalOldPatch.Root + externalOldPatch.StdErr);

        string externalWrongSource = Path.Combine(root.Value, "external-wrong", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(externalWrongSource)!);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(written, SkyrimRelease.SkyrimSE))
        {
            var externalWrong = (SkyrimMod)read.DeepCopy();
            var externalNpc = externalWrong.Npcs.Single();
            externalNpc.HeadParts.Clear();
            externalNpc.HeadParts.Add(externalEyes.FormKey);
            externalWrong.WriteToBinary(externalWrongSource);
        }
        string externalWrongOutput = Path.Combine(root.Value, "external-wrong-output", "Lynda.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(externalWrongOutput)!);
        var externalWrongPatch = await RunCliAsync(root, "npc", "face-patch", "--edition", "skyrimse",
            "--plugin", externalWrongSource, "--output", externalWrongOutput, "--data-root", data,
            "--npc", "0x800", "--headpart-replace", "ExternalParts.esp|0x901=0x81A", "--apply", "true",
            "--expected-sha256", HashFile(new WorkspacePath(externalWrongSource)), "--json");
        Require(externalWrongPatch.ExitCode != 0 && !File.Exists(externalWrongOutput) &&
            externalWrongPatch.Root.ToString().Contains("headpart-replacement-type-mismatch", StringComparison.Ordinal),
            "An externally owned eyes PNAM was replaced by a local brow: " +
            externalWrongPatch.Root + externalWrongPatch.StdErr);

        string dataRoot = Path.GetPathRoot(data) ?? throw new InvalidOperationException("PNAM fixture has no volume root.");
        string filesystem = new DriveInfo(dataRoot).DriveFormat;
        if (string.IsNullOrWhiteSpace(filesystem) ||
            filesystem.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Could not establish filesystem for external PNAM reparse fixture; root={data}; filesystem=unknown.");
        Console.WriteLine($"EVIDENCE_OUTPUT_OWNED_HEADPART_FILESYSTEM={filesystem}; root={data}");
        string reparseProvider = Path.Combine(data, "ReparseParts.esp");
        if (PhysicalReparseFixture.TryCreateFileLink(
                reparseProvider,
                externalProvider,
                data))
        {
            try
            {
                Require((File.GetAttributes(reparseProvider) & FileAttributes.ReparsePoint) != 0,
                    "External PNAM symlink fixture was created without a reparse-point attribute.");
                var reparseKey = ModKey.FromNameAndExtension("ReparseParts.esp");
                string reparseSource = Path.Combine(root.Value, "external-reparse", "Lynda.esp");
                Directory.CreateDirectory(Path.GetDirectoryName(reparseSource)!);
                using (var read = SkyrimMod.CreateFromBinaryOverlay(written, SkyrimRelease.SkyrimSE))
                {
                    var reparseOld = (SkyrimMod)read.DeepCopy();
                    var reparseNpc = reparseOld.Npcs.Single();
                    reparseNpc.HeadParts.Clear();
                    reparseNpc.HeadParts.Add(new FormKey(reparseKey, 0x900));
                    reparseOld.WriteToBinary(reparseSource);
                }
                string reparseOutput = Path.Combine(root.Value, "external-reparse-output", "Lynda.esp");
                Directory.CreateDirectory(Path.GetDirectoryName(reparseOutput)!);
                var reparsePatch = await RunCliAsync(root, "npc", "face-patch", "--edition", "skyrimse",
                    "--plugin", reparseSource, "--output", reparseOutput, "--data-root", data,
                    "--npc", "0x800", "--headpart-replace", "ReparseParts.esp|0x900=0x81A", "--apply", "true",
                    "--expected-sha256", HashFile(new WorkspacePath(reparseSource)), "--json");
                Require(reparsePatch.ExitCode != 0 && !File.Exists(reparseOutput) &&
                    reparsePatch.Root.ToString().Contains("face-read-failed", StringComparison.Ordinal),
                    "A reparse-point old PNAM provider was followed: " + reparsePatch.Root + reparsePatch.StdErr);
            }
            finally
            {
                if (File.Exists(reparseProvider))
                    File.Delete(reparseProvider);
            }
        }
        var audit = await RunCliAsync(root, "plugin", "audit", "--edition", "skyrimse", "--before", source, "--after", final, "--json");
        Require(audit.ExitCode == 0, "Plugin audit failed: " + audit.Root + audit.StdErr);
        Require(audit.Root.GetProperty("addedRecordCount").GetInt32() == 1 && audit.Root.GetProperty("changedRecordCount").GetInt32() == 1 &&
            audit.Root.GetProperty("removedRecordCount").GetInt32() == 0 && audit.Root.GetProperty("changes").EnumerateArray().Any(item => item.GetProperty("signature").GetString() == "HDPT" && item.GetProperty("changeKind").GetString() == "added") &&
            audit.Root.GetProperty("beforeMasters").GetRawText() == audit.Root.GetProperty("afterMasters").GetRawText(), "Audit did not prove one added HDPT, one changed NPC and unchanged masters.");
        var oldRecords = HdptTestRecords(before); var finalRecords = HdptTestRecords(File.ReadAllBytes(final));
        foreach (var (key, bytes) in oldRecords.Where(item => item.Key.Item1 != "TES4" && item.Key.Item1 != "NPC_"))
            Require(finalRecords[key].SequenceEqual(bytes), "Unrelated record bytes changed: " + key);
        byte[] expectedNpc = oldRecords.Single(item => item.Key.Item1 == "NPC_").Value.ToArray();
        int pnam = 24;
        while (!expectedNpc.AsSpan(pnam, 4).SequenceEqual("PNAM"u8)) pnam += 6 + BinaryPrimitives.ReadUInt16LittleEndian(expectedNpc.AsSpan(pnam + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(expectedNpc.AsSpan(pnam + 6), 0x0100081A);
        Require(finalRecords.Single(item => item.Key.Item1 == "NPC_").Value.SequenceEqual(expectedNpc), "NPC fields or PNAM order changed outside the requested four bytes.");
        string cloneProposal = Path.Combine(root.Value, "clone.record-proposal.json");
        var clone = await RunCliAsync(root, "records", "propose", "--edition", "skyrimse", "--type", "HDPT", "--mode", "new", "--form-id", "0x81B",
            "--editor-id", "LyndaBrowRetargeted", "--clone-from", "Lynda.esp|0x81A", "--retarget-valid-races", "Lynda.esp|0x814", "--output", cloneProposal, "--json");
        Require(clone.ExitCode == 0, "Clone proposal failed: " + clone.Root + clone.StdErr);
        string cloneOutput = Path.Combine(root.Value, "clone", "Lynda.esp"); Directory.CreateDirectory(Path.GetDirectoryName(cloneOutput)!);
        var cloned = await RunCliAsync(root, "plugin", "write", "--edition", "skyrimse", "--proposal", cloneProposal, "--plugin", written,
            "--expected-sha256", HashFile(new WorkspacePath(written)), "--output", cloneOutput, "--json");
        Require(cloned.ExitCode == 0, "Clone write failed: " + cloned.Root + cloned.StdErr);
        using (var read = SkyrimMod.CreateFromBinaryOverlay(cloneOutput, SkyrimRelease.SkyrimSE))
        {
            var old = read.HeadParts.Single(item => item.FormKey.ID == 0x81A); var next = read.HeadParts.Single(item => item.FormKey.ID == 0x81B);
            Require(next.ValidRaces.FormKey.ID == 0x814 && old.ValidRaces.FormKey.ID == 0x811 && next.Model!.File!.ToString() == old.Model!.File!.ToString() && next.Parts.Count == 3,
                "Clone failed to preserve geometry or retarget only the new ValidRaces link.");
        }
        string collision = Path.Combine(root.Value, "collision", "Lynda.esp"); Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
        var collided = await RunCliAsync(root, "plugin", "write", "--edition", "skyrimse", "--proposal", proposal, "--plugin", written,
            "--expected-sha256", HashFile(new WorkspacePath(written)), "--output", collision, "--json");
        Require(collided.ExitCode != 0 && !File.Exists(collision), "Occupied local FormID was overwritten.");
        Require(File.ReadAllBytes(source).SequenceEqual(before), "Input plugin changed.");
        Console.WriteLine("HDPT CLI materialization, independent readback, private copies and PNAM replacement passed: " + root.Value);
    }

    private static Dictionary<(string, uint), byte[]> HdptTestRecords(byte[] bytes)
    {
        var records = new Dictionary<(string, uint), byte[]>();
        void Walk(int start, int end)
        {
            while (start < end)
            {
                string signature = Encoding.ASCII.GetString(bytes, start, 4);
                int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start + 4)));
                int next = start + size + (signature == "GRUP" ? 0 : 24);
                if (signature == "GRUP") Walk(start + 24, next);
                else records.Add((signature, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start + 12))), bytes[start..next]);
                start = next;
            }
        }
        Walk(0, bytes.Length); return records;
    }
    internal static byte[] WriteHdptTopologyModel(int count, string? diffuse = null, uint? hairTint = null,
        System.Numerics.Vector3[]? positions = null, string? shapeName = null,
        System.Numerics.Vector3 headTranslation = default, System.Numerics.Vector3 spineTranslation = default)
    {
        const ulong descriptor = 0x0040100000000004; // full-precision position, 16 bytes
        bool referenceGeometry = positions is not null;
        Require(positions is null || positions.Length == count, "Fixture position count must match topology.");
        ulong attributes = referenceGeometry ? 0x0040300000000405UL : descriptor;
        int stride = referenceGeometry ? 20 : 16;
        static byte[] Block(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            write(writer);
            return stream.ToArray();
        }
        static void Rotation(BinaryWriter w)
        {
            for (int i = 0; i < 9; i++) w.Write(i % 4 == 0 ? 1F : 0F);
        }
        static void Transform(BinaryWriter w, System.Numerics.Vector3 translation = default)
        {
            Rotation(w); w.Write(translation.X); w.Write(translation.Y); w.Write(translation.Z); w.Write(1F);
        }
        static void Av(BinaryWriter w, int name, System.Numerics.Vector3 translation = default)
        {
            w.Write(name); w.Write(0U); w.Write(-1); w.Write(0U);
            w.Write(translation.X); w.Write(translation.Y); w.Write(translation.Z);
            Rotation(w); w.Write(1F); w.Write(-1);
        }
        static void Text(BinaryWriter w, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value); w.Write(bytes.Length); w.Write(bytes);
        }
        byte[][] blocks =
        [
            Block(w => { Av(w, 0); w.Write(3U); w.Write(1); w.Write(2); w.Write(8); w.Write(0U); }),
            Block(w => { Av(w, 1, headTranslation); w.Write(0U); w.Write(0U); }),
            Block(w =>
            {
                Av(w, 2); w.Write(new byte[16]); w.Write(3); w.Write(6); w.Write(-1);
                w.Write(attributes); w.Write((ushort)0); w.Write((ushort)count);
                w.Write(0U); w.Write(0U); w.Write(count * 16);
                for (int i = 0; i < count; i++)
                {
                    w.Write(positions?[i].X ?? (i == 1 ? 1F : 0F));
                    w.Write(positions?[i].Y ?? (i == 2 ? 1F : 0F));
                    w.Write(positions?[i].Z ?? 0F); w.Write(0F);
                }
            }),
            Block(w => { w.Write(4); w.Write(5); w.Write(0); w.Write(1U); w.Write(1); }),
            Block(w =>
            {
                Transform(w); w.Write(1U); w.Write((byte)1);
                Transform(w, headTranslation == default ? default : -headTranslation);
                w.Write(new byte[16]); w.Write((ushort)count);
                for (int i = 0; i < count; i++) { w.Write((ushort)i); w.Write(1F); }
            }),
            Block(w =>
            {
                w.Write(1U); w.Write(count * stride); w.Write((uint)stride); w.Write(attributes);
                for (int i = 0; i < count; i++)
                {
                    w.Write(new byte[16]);
                    if (referenceGeometry)
                    {
                        w.Write(BitConverter.HalfToUInt16Bits((Half)(i == 1 ? 1 : 0)));
                        w.Write(BitConverter.HalfToUInt16Bits((Half)(i == 2 ? 1 : 0)));
                    }
                }
                w.Write((ushort)count); w.Write((ushort)1); w.Write((ushort)1);
                w.Write((ushort)0); w.Write((ushort)(referenceGeometry ? 4 : 1)); w.Write((ushort)0);
                w.Write((byte)1);
                for (int i = 0; i < count; i++) w.Write((ushort)i);
                w.Write((byte)1);
                for (int i = 0; i < count; i++)
                {
                    w.Write(1F);
                    if (referenceGeometry) w.Write(new byte[12]);
                }
                w.Write((byte)1); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
                w.Write((byte)1); w.Write(new byte[count * (referenceGeometry ? 4 : 1)]);
                w.Write((ushort)0); w.Write(attributes);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)2);
            }),
            Block(w =>
            {
                w.Write(hairTint.HasValue ? 6U : 5U); w.Write(uint.MaxValue); w.Write(0U); w.Write(-1);
                w.Write(new byte[24]); w.Write(7); w.Write(new byte[referenceGeometry ? 56 : 60]);
                if (hairTint is { } rgb)
                { w.Write(((rgb >> 16) & 255) / 255F); w.Write(((rgb >> 8) & 255) / 255F); w.Write((rgb & 255) / 255F); }
                else if (referenceGeometry) { w.Write(1F); w.Write(1F); w.Write(1F); }
            }),
            Block(w =>
            {
                w.Write(9U); Text(w, diffuse ?? "textures/actors/character/female/femalehead.dds");
                for (int i = 1; i < 9; i++) Text(w, string.Empty);
            }),
            Block(w => { Av(w, 3, spineTranslation); w.Write(0U); w.Write(0U); })
        ];
        return Block(w =>
        {
            w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            w.Write(0x14020007U); w.Write((byte)1); w.Write(12U); w.Write(9U); w.Write(100U);
            w.Write(new byte[3]); w.Write((ushort)7);
            ReadOnlySpan<string> types = ["NiNode", "BSDynamicTriShape", "NiSkinInstance",
                "NiSkinData", "NiSkinPartition", "BSLightingShaderProperty", "BSShaderTextureSet"];
            foreach (string type in types)
                Text(w, type);
            foreach (ushort index in new ushort[] { 0, 0, 1, 2, 3, 4, 5, 6, 0 }) w.Write(index);
            foreach (byte[] block in blocks) w.Write(block.Length);
            string[] names = ["FixtureRoot", "NPC Head [Head]", shapeName ?? "FixtureShape", "NPC Spine2 [Spn2]"];
            w.Write((uint)names.Length);
            w.Write(checked((uint)names.Max(name => Encoding.ASCII.GetByteCount(name))));
            foreach (string name in names) Text(w, name);
            w.Write(0U);
            foreach (byte[] block in blocks) w.Write(block);
            w.Write(1U); w.Write(0);
        });
    }
}
