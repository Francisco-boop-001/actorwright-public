using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimRaceMenuPaintChoiceCatalog()
    {
        string fixtureRoot = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\tests",
            "paint-catalog-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(fixtureRoot, "Data");
        string scriptsRoot = Path.Combine(dataRoot, "Scripts");
        Directory.CreateDirectory(scriptsRoot);
        try
        {
            File.WriteAllBytes(Path.Combine(dataRoot, "Fixture.esp"), [0x01]);
            string pexPath = Path.Combine(scriptsRoot, "PaintFixture.pex");
            File.WriteAllBytes(pexPath, BuildPaintPex(
                new TestPaintCall("AddWarpaint",
                [
                    TestPexArgument.Literal("$Ash"),
                    TestPexArgument.Literal("actors\\paint\\ash.dds")
                ]),
                new TestPaintCall("AddWarpaint",
                [
                    TestPexArgument.Literal("$Ash"),
                    TestPexArgument.Literal("textures/actors/paint/ash.dds")
                ]),
                new TestPaintCall("AddWarpaintEx",
                [
                    TestPexArgument.Literal("$Rune"),
                    TestPexArgument.Literal("actors/paint/rune.dds"),
                    TestPexArgument.Literal("actors/paint/rune_n.dds"),
                    TestPexArgument.Literal("ignore"),
                    TestPexArgument.Literal(string.Empty),
                    TestPexArgument.Literal(string.Empty),
                    TestPexArgument.Literal(string.Empty),
                    TestPexArgument.Literal(string.Empty),
                    TestPexArgument.Identifier("::computed_slot")
                ]),
                new TestPaintCall("AddWarpaint",
                [
                    TestPexArgument.Literal("Computed primary"),
                    TestPexArgument.Identifier("::temp0")
                ]),
                new TestPaintCall("AddWarpaint",
                [
                    TestPexArgument.Identifier("::paint_name"),
                    TestPexArgument.Literal("actors/paint/identifier-name.dds")
                ]),
                new TestPaintCall("AddFacePaint",
                [
                    TestPexArgument.Literal("Face only"),
                    TestPexArgument.Literal("actors/paint/face.dds")
                ])));

            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath("K:\\ExampleWorkspace"),
                new WorkspacePath("F:\\ExampleGame"));
            var service = new BethesdaSkyrimRaceMenuPaintChoiceService(
                policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var request = new SkyrimRaceMenuPaintChoiceRequest(
                new WorkspacePath(dataRoot),
                [new PluginName("Fixture.esp")],
                SkyrimRaceMenuPaintCategory.Warpaint,
                null);
            SkyrimRaceMenuPaintChoiceResult result = await service.SearchAsync(
                request,
                CancellationToken.None);

            Assert(result.Accepted, "The loose compiled-Papyrus catalog was refused.");
            Assert(result.Candidates.Length == 3,
                "Normalized duplicate or computed primary registration was not handled correctly.");
            Assert(result.Summary is
            {
                AdmittedArchiveCount: 0,
                LooseScriptEntryCount: 1,
                WinningScriptCount: 1,
                PaintScriptCount: 1,
                MalformedPaintScriptCount: 0,
                RegistrationCount: 6,
                CandidateCount: 3
            }, "The compiled-script scan summary drifted.");
            SkyrimRaceMenuPaintChoiceCandidate ash = result.Candidates.Single(item =>
                item.DisplayName == "Ash");
            Assert(ash.CanonicalTexturePath.Value.Equals(
                    "textures/actors/paint/ash.dds",
                    StringComparison.OrdinalIgnoreCase),
                "Optional textures prefix normalization drifted.");
            SkyrimRaceMenuPaintChoiceCandidate rune = result.Candidates.Single(item =>
                item.DisplayName == "Rune");
            Assert(rune.TextureSlots.Length == 8 &&
                   rune.TextureSlots[2].Kind == SkyrimRaceMenuPaintSlotKind.Ignore &&
                   rune.TextureSlots[7].Kind == SkyrimRaceMenuPaintSlotKind.Computed,
                "Extended paint slots lost typed ignore/computed semantics.");
            Assert(result.Candidates.Any(item =>
                    item.RegisteredName == "::paint_name" &&
                    item.RegisteredPath.Value == "actors/paint/identifier-name.dds"),
                "A PEX identifier used as the registered display name lost upstream parity.");
            Assert(!result.RuntimeAuthority && !result.TextureRenderAuthority,
                "Static paint registration evidence claimed rendering or runtime authority.");

            SkyrimRaceMenuPaintChoiceResult filtered = await service.SearchAsync(
                request with { Search = "rune" },
                CancellationToken.None);
            Assert(filtered.Accepted && filtered.Candidates.Length == 1 &&
                   filtered.Summary?.CatalogSha256 == result.Summary?.CatalogSha256,
                "Search changed catalog identity or returned the wrong row.");

            File.WriteAllBytes(pexPath,
                [0xFA, 0x57, 0xC0, 0xDE, .. Encoding.ASCII.GetBytes("AddWarpaint")]);
            SkyrimRaceMenuPaintChoiceResult malformed = await service.SearchAsync(
                request,
                CancellationToken.None);
            Assert(!malformed.Accepted && malformed.Diagnostics.Any(item =>
                    item.Code == "paint-catalog-pex-malformed"),
                "A truncated paint-bearing PEX did not fail closed.");

            ImmutableArray<PaintArchivePlanEntry> plan =
                BethesdaSkyrimRaceMenuPaintCatalogScanner.BuildArchivePlan(
                [
                    Path.Combine(dataRoot, "Orphan.bsa"),
                    Path.Combine(dataRoot, "B.bsa"),
                    Path.Combine(dataRoot, "A - Textures.bsa"),
                    Path.Combine(dataRoot, "A.bsa")
                ],
                [new PluginName("A.esp"), new PluginName("B.esm")]);
            Assert(plan.Select(item => item.Name).SequenceEqual(
                    ["A - Textures.bsa", "A.bsa", "B.bsa"],
                    StringComparer.OrdinalIgnoreCase) &&
                   plan.Select(item => item.SourceOrder).SequenceEqual([0, 1, 2]),
                "Loaded-plugin archive binding or deterministic priority drifted.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static byte[] BuildPaintPex(params TestPaintCall[] calls)
    {
        var stringTable = new List<string> { string.Empty, "Fixture", "State", "Function", "self", "dest" };
        foreach (TestPaintCall call in calls)
        {
            AddString(call.Method);
            foreach (TestPexArgument argument in call.Arguments) AddString(argument.Value);
        }

        using var stream = new MemoryStream();
        WriteUInt32(stream, 0xFA57C0DE);
        stream.WriteByte(3);
        stream.WriteByte(2);
        WriteUInt16(stream, 1);
        stream.Write(new byte[8]);
        WriteWideString(stream, "Fixture.psc");
        WriteWideString(stream, "tester");
        WriteWideString(stream, "fixture");
        WriteUInt16(stream, checked((ushort)stringTable.Count));
        foreach (string value in stringTable) WriteWideString(stream, value);
        stream.WriteByte(0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, Index("Fixture"));
        WriteUInt32(stream, 0);
        WriteUInt16(stream, Index(string.Empty));
        WriteUInt16(stream, Index(string.Empty));
        WriteUInt32(stream, 0);
        WriteUInt16(stream, Index(string.Empty));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, Index("State"));
        WriteUInt16(stream, 1);
        WriteUInt16(stream, Index("Function"));
        WriteUInt16(stream, Index(string.Empty));
        WriteUInt16(stream, Index(string.Empty));
        WriteUInt32(stream, 0);
        stream.WriteByte(0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, checked((ushort)calls.Length));
        foreach (TestPaintCall call in calls)
        {
            stream.WriteByte(0x17);
            WriteIdentifier(call.Method);
            WriteIdentifier("self");
            WriteIdentifier("dest");
            stream.WriteByte(3);
            WriteUInt32(stream, checked((uint)call.Arguments.Length));
            foreach (TestPexArgument argument in call.Arguments)
            {
                stream.WriteByte(argument.IsIdentifier ? (byte)1 : (byte)2);
                WriteUInt16(stream, Index(argument.Value));
            }
        }
        return stream.ToArray();

        void AddString(string value)
        {
            if (!stringTable.Contains(value, StringComparer.Ordinal)) stringTable.Add(value);
        }

        ushort Index(string value) => checked((ushort)stringTable.IndexOf(value));

        void WriteIdentifier(string value)
        {
            stream.WriteByte(1);
            WriteUInt16(stream, Index(value));
        }
    }

    private static void WriteWideString(Stream stream, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed record TestPaintCall(
        string Method,
        ImmutableArray<TestPexArgument> Arguments);

    private readonly record struct TestPexArgument(bool IsIdentifier, string Value)
    {
        public static TestPexArgument Literal(string value) => new(false, value);
        public static TestPexArgument Identifier(string value) => new(true, value);
    }
}
