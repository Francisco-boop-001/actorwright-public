using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Cli.Tests;

internal static class FaceGeomExtendedMorphTests
{
    private static readonly string[] MorphNames =
        ["EYES_EyesLowerMiddleA", "Nose_NoseSellionLow", "CME_ElfEyesType"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task RunAdmissionAsync()
    {
        var fixture = CreateFixture("admission");
        int invocations = 0;
        ImmutableArray<FaceGeomBinaryMorph> observed = [];
        var exporter = CreateExporter(fixture, async (request, _, token) =>
        {
            invocations++;
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(request, token));
            observed = document.RootElement.GetProperty("morphs").EnumerateArray()
                .Select(row => new FaceGeomBinaryMorph(row.GetProperty("name").GetString()!,
                    row.GetProperty("value").GetSingle())).ToImmutableArray();
            return [new Diagnostic("test-dispatch-only", DiagnosticSeverity.Error,
                "Admission reached the process boundary; this is not a successful bake.")];
        });
        ImmutableArray<FaceGeomBinaryMorph> extended =
            [new(MorphNames[0], -2.159999847412109f), new(MorphNames[1], 1.509999990463257f), new(MorphNames[2], 21f)];
        var result = await exporter.BuildAsync(Request(fixture, "extended", extended), CancellationToken.None);
        Require(invocations == 1 && observed.SequenceEqual(extended),
            "Task8: every finite extended value must reach the bake unchanged: " + Diagnostics(result));
        Require(result.Diagnostics.Count(row => row.Code == "facegeom-binary-morph-extended-range" &&
            row.Severity == DiagnosticSeverity.Warning) == 3, "Task8: one warning is required per extended morph.");

        var ordinary = await exporter.BuildAsync(Request(fixture, "ordinary", [new(MorphNames[0], .5f)]), CancellationToken.None);
        Require(invocations == 2 && ordinary.Diagnostics.All(row => row.Code != "facegeom-binary-morph-extended-range"),
            "Task8: ordinary admission changed.");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            RequireRefusal(await exporter.BuildAsync(Request(fixture, "nonfinite", [new(MorphNames[0], invalid)]),
                CancellationToken.None), "facegeom-binary-morph-invalid");
        RequireRefusal(await exporter.BuildAsync(Request(fixture, "blank", [new(" ", 1f)]), CancellationToken.None),
            "facegeom-binary-morph-invalid");
        RequireRefusal(await exporter.BuildAsync(Request(fixture, "long", [new(new string('x', 256), 1f)]), CancellationToken.None),
            "facegeom-binary-morph-invalid");
        RequireRefusal(await exporter.BuildAsync(Request(fixture, "duplicate", [new("JawOpen", 1f), new("jawopen", .5f)]),
            CancellationToken.None), "facegeom-binary-morph-duplicate");
        RequireRefusal(await exporter.BuildAsync(Request(fixture, "zero", [new(MorphNames[0], 0f)]), CancellationToken.None),
            "facegeom-binary-morph-zero");
        Require(invocations == 2, "Invalid morphs must not invoke Blender.");
        var router = new FaceGeomBinaryBuildRouter(exporter, new BethesdaNifGeometryReadbackService(), Policy(fixture), fixture.Workspace);
        var transport = await router.BuildAsync(Request(fixture, "transport", [new(MorphNames[0], .5f)]) with
        {
            Operation = FaceGeomBinaryOperation.Transport,
            TransportProfile = FaceGeomTransportProfile.CompleteCarrier,
            SourceSha256 = new Sha256Hash(Hash(fixture.Source.Value))
        }, CancellationToken.None);
        Require(!transport.Written && invocations == 2 && transport.Diagnostics.Any(row => row.Code.Contains("morph", StringComparison.Ordinal)),
            "Transport must continue refusing nonzero morphs without Blender.");
        Console.WriteLine("PASS Task8 finite morph admission, exact values, warnings, invalid controls, and Transport guard.");
    }

    public static async Task RunSourceAdmissionAsync()
    {
        var fixture = CreateFixture("source");
        var reader = new BethesdaNifGeometryReadbackService();
        var plain = await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, fixture.Source), CancellationToken.None);
        Require(plain.Accepted && plain.Document is not null, "Plain synthetic source must be admitted.");
        var metadataPath = new WorkspacePath(Path.Combine(fixture.Root.Value, "metadata.nif"));
        File.WriteAllBytes(metadataPath.Value, CreateNif(metadata: true));
        var metadata = await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, metadataPath), CancellationToken.None);
        var failures = new List<string>();
        if (!metadata.Accepted || metadata.Document is null) failures.Add("Task9 complete-carrier metadata rejected: " +
            string.Join("; ", metadata.Diagnostics.Select(row => row.Message)));
        else
        {
            Require(metadata.Document.BlockCount == 3 && metadata.Document.ReachableBlockCount == 3 &&
                metadata.Document.AggregateGeometrySha256 == plain.Document!.AggregateGeometrySha256 &&
                metadata.Document.Shapes[0].TriangleTopologySha256 == plain.Document.Shapes[0].TriangleTopologySha256,
                "The reachable metadata leaf must not alter geometry/topology evidence.");
            var parsed = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(metadataPath.Value));
            Require(parsed.Blocks[2].Type == "NiStringExtraData" && parsed.Blocks[2].Size == 8 &&
                parsed.Blocks[2].GeometryPayload is null && parsed.Blocks[2].Name == "FixtureMetadata" &&
                parsed.Data.AsSpan(parsed.Blocks[2].Offset, 8).SequenceEqual(new byte[] { 2, 0, 0, 0, 3, 0, 0, 0 }),
                "Metadata must remain a bounded non-geometry leaf with unchanged payload bytes.");
        }
        foreach (var bad in new[] { CreateNif(true, 4), CreateNif(true, 8, 50), CreateNif(true, 8, 3, "UnrecognizedExtraData") })
        {
            File.WriteAllBytes(metadataPath.Value, bad);
            Require(!(await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, metadataPath), CancellationToken.None)).Accepted,
                "Malformed metadata or unknown block types must remain refused.");
        }
        File.WriteAllBytes(metadataPath.Value, CreateNif(shapeTail: [0, 0, 0, 0]));
        var emittedLayout = await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, metadataPath), CancellationToken.None);
        if (!emittedLayout.Accepted || emittedLayout.Document?.AggregateGeometrySha256 != plain.Document!.AggregateGeometrySha256)
            failures.Add("Task9 actual PyNifly BSTriShape zero particle-data-size field rejected: " +
                string.Join("; ", emittedLayout.Diagnostics.Select(row => row.Message)));
        foreach (byte[] tail in new byte[][] { [1, 0, 0, 0], [0], [0, 0, 0, 0, 0] })
        {
            File.WriteAllBytes(metadataPath.Value, CreateNif(shapeTail: tail));
            Require(!(await reader.ReadAsync(new(GameEdition.SkyrimSpecialEdition, metadataPath), CancellationToken.None)).Accepted,
                "Nonzero particle data or unknown triangle-shape tails must remain refused.");
        }
        foreach (string code in new[] { "facegeom-binary-tri-import", "facegeom-binary-morph-missing" })
        {
            var exporter = CreateExporter(fixture, async (_, status, token) =>
            {
                await File.WriteAllTextAsync(status, JsonSerializer.Serialize(new
                {
                    exported = false, errorCode = code,
                    error = code == "facegeom-binary-tri-import" ? "TRI import failed for FemaleHead.tri: no shapes" : "Requested morph JawMissing is absent."
                }), token);
                return [new Diagnostic("facegeom-binary-process", DiagnosticSeverity.Error, "Blender exited with code 1.")];
            });
            var result = await exporter.BuildAsync(Request(fixture, code, [new(MorphNames[0], 1f)]), CancellationToken.None);
            if (result.Written || result.Diagnostics.All(row => row.Code != code)) failures.Add("Task9 nonzero process exit hid " + code + ": " + Diagnostics(result));
        }
        var unknownFailure = CreateExporter(fixture, async (_, status, token) =>
        {
            await File.WriteAllTextAsync(status, "{\"exported\":false,\"error\":\"bounded adapter error detail\"}", token);
            return [new Diagnostic("facegeom-binary-process", DiagnosticSeverity.Error, "Blender exited with code 1.")];
        });
        var unknown = await unknownFailure.BuildAsync(Request(fixture, "unknown-error", [new(MorphNames[0], 1f)]), CancellationToken.None);
        if (unknown.Diagnostics.All(row => row.Code != "facegeom-binary-export-failed" || row.Message != "bounded adapter error detail"))
            failures.Add("Task9 nonzero process exit hid its bounded adapter failure status.");
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS Task9 metadata geometry invariance, bounded negatives, and nonzero-exit typed status.");
    }

    public static async Task RunActualBakeAsync()
    {
        var fixture = CreateFixture("actual");
        try
        {
            await RunActualBakeAsync(fixture);
        }
        finally
        {
            if (Directory.Exists(fixture.Root.Value))
                Directory.Delete(fixture.Root.Value, recursive: true);
        }
    }

    private static async Task RunActualBakeAsync(Fixture fixture)
    {
        File.WriteAllBytes(fixture.Source.Value, CreateNif(shapeTail: [0, 0, 0, 0]));
        string externalRoot = RendererAuthorityRoot(fixture);
        // Short owned names keep Blender's embedded Python DLL paths below the Windows limit.
        string localBlenderRoot = Path.Combine(fixture.Root.Value, "b");
        CopyAuthorityDirectory(Path.Combine(externalRoot, "blender-4.5.1-windows-x64",
            "blender-4.5.1-windows-x64"), localBlenderRoot);
        var executable = new WorkspacePath(Path.Combine(localBlenderRoot, "blender.exe"));
        var profile = new WorkspacePath(Path.Combine(fixture.Root.Value, "p"));
        CopyAuthorityDirectory(Path.Combine(externalRoot, "blender-4.5.1-pynifly-profile"), profile.Value);
        WorkspacePath labRoot = fixture.Root;
        var embedded = EmbeddedBlenderScriptBundle.Load("export_facegeom_nif");
        string scriptHash = Hash(Path.Combine(fixture.Workspace.Value, "runtime/rendering/export_facegeom_nif.py"));
        string helperHash = Hash(Path.Combine(fixture.Workspace.Value, "runtime/rendering/nif_geometry_readback.py"));
        Require(embedded.Sha256.Value == scriptHash &&
            Convert.ToHexStringLower(SHA256.HashData(embedded.HelperModules["nif_geometry_readback"].AsSpan())) == helperHash,
            "Actual bake must execute the current embedded script and readback helper.");
        var exporter = new BlenderFaceGeomNifExporter(executable, profile, "export_facegeom_nif",
            new KOnlyWorkspacePolicy(labRoot, new WorkspacePath("F:/ExampleGame")), labRoot,
            new Sha256Hash("B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"), new BethesdaNifGeometryReadbackService());
        ImmutableArray<FaceGeomBinaryMorph> extended = [new(MorphNames[0], -2.16f), new(MorphNames[1], 1.51f), new(MorphNames[2], 21f)];
        var sourceHash = Hash(fixture.Source.Value);
        var triHash = Hash(Path.ChangeExtension(fixture.Source.Value, ".tri"));
        var result = await exporter.BuildAsync(Request(fixture, "extended", extended), CancellationToken.None);
        Require(result.Written && result.Artifact is not null, "Real extended bake failed: " + Diagnostics(result));
        Require(result.Artifact!.Morphs.SequenceEqual(extended) && result.Artifact.TriFiles.Single().Sha256 == triHash &&
            result.Artifact.InputSourceSha256 == sourceHash && !result.Artifact.RuntimeAuthority, "Real bake lost exact input binding.");
        var control = await exporter.BuildAsync(Request(fixture, "clamped-control", [new(MorphNames[0], -1f), new(MorphNames[1], 1f), new(MorphNames[2], 1f)]), CancellationToken.None);
        Require(control.Written && result.Artifact.BakedVertexSha256 != control.Artifact!.BakedVertexSha256,
            "Real extended geometry must differ from the clamped control: " + Diagnostics(control));
        await RequireCoordinatesAsync(Request(fixture, "extended", extended).OutputPath,
            [[-2.16f, 0f, 0f], [1f, 1.51f, 0f], [0f, 1f, 21f]]);
        await RequireCoordinatesAsync(Request(fixture, "clamped-control", extended).OutputPath,
            [[-1f, 0f, 0f], [1f, 1f, 0f], [0f, 1f, 1f]]);
        File.WriteAllBytes(fixture.Source.Value, CreateNif(metadata: true, shapeTail: [0, 0, 0, 0]));
        var metadata = await exporter.BuildAsync(Request(fixture, "metadata", extended), CancellationToken.None);
        Require(metadata.Written && metadata.Artifact!.BakedVertexSha256 == result.Artifact.BakedVertexSha256,
            "Real metadata-bearing source bake failed or changed geometry: " + Diagnostics(metadata));
        var missing = await exporter.BuildAsync(Request(fixture, "missing", [new("MissingRequestedMorph", 1f)]), CancellationToken.None);
        RequireRefusal(missing, "facegeom-binary-morph-missing");
        File.WriteAllBytes(Path.ChangeExtension(fixture.Source.Value, ".tri"), [1, 2, 3, 4]);
        var malformed = await exporter.BuildAsync(Request(fixture, "malformed", [new(MorphNames[0], 1f)]), CancellationToken.None);
        RequireRefusal(malformed, "facegeom-binary-tri-import");
        Require(malformed.Diagnostics.Any(row => row.Code == "facegeom-binary-tri-import" && row.Message.Contains("FemaleHead.tri", StringComparison.Ordinal)),
            "Actual TRI failure must name the bound file.");
        Require(scriptHash == Hash(Path.Combine(fixture.Workspace.Value, "tools/rendering/export_facegeom_nif.py")) &&
            helperHash == Hash(Path.Combine(fixture.Workspace.Value, "tools/rendering/nif_geometry_readback.py")), "Tracked FaceGeom scripts/helpers differ.");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root.Value, "actual-bake-evidence.json"),
            JsonSerializer.Serialize(new { blenderSha256 = Hash(executable.Value), scriptHash, helperHash,
                extended = result.Artifact, clampedControl = control.Artifact, metadata = metadata.Artifact,
                missing = missing.Diagnostics, malformed = malformed.Diagnostics }, JsonOptions));
        Console.WriteLine("PASS Tasks8+9 real pinned Blender/PyNifly bake: exact -2.16, 1.51, 21.0 coordinates; clamped control; metadata; actual TRI/missing errors.");
    }

    private static string RendererAuthorityRoot(Fixture fixture)
    {
        const string variable = "ACTORWRIGHT_TEST_RENDERER_AUTHORITY_ROOT";
        string? configured = Environment.GetEnvironmentVariable(variable);
        Require(string.IsNullOrWhiteSpace(configured) || Path.IsPathFullyQualified(configured),
            $"{variable} must be one absolute directory.");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(fixture.Workspace.Value, "tools", "external")
            : Path.GetFullPath(configured);
    }

    private static void CopyAuthorityDirectory(string source, string destination)
    {
        const int maximumFiles = 20_000;
        const long maximumBytes = 4L * 1024 * 1024 * 1024;
        Require(!Directory.Exists(destination) && !File.Exists(destination),
            $"Renderer authority destination is occupied: {destination}");
        var count = 0;
        long bytes = 0;
        Copy(source, destination);

        void Copy(string currentSource, string currentDestination)
        {
            Require(Directory.Exists(currentSource) &&
                    (File.GetAttributes(currentSource) & FileAttributes.ReparsePoint) == 0,
                $"Renderer authority is absent or reparsed: {currentSource}");
            Directory.CreateDirectory(currentDestination);
            foreach (string file in Directory.EnumerateFiles(currentSource, "*", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(file);
                Require((info.Attributes & FileAttributes.ReparsePoint) == 0,
                    $"Renderer authority contains a reparse point: {file}");
                count = checked(count + 1);
                bytes = checked(bytes + info.Length);
                Require(count <= maximumFiles && bytes <= maximumBytes,
                    "Renderer authority copy exceeded its bounded inventory.");
                File.Copy(file, Path.Combine(currentDestination, info.Name), overwrite: false);
            }
            foreach (string directory in Directory.EnumerateDirectories(currentSource, "*", SearchOption.TopDirectoryOnly))
                Copy(directory, Path.Combine(currentDestination, Path.GetFileName(directory)));
        }
    }

    private static async Task RequireCoordinatesAsync(WorkspacePath path, float[][] expected)
    {
        var read = await new BethesdaNifGeometryReadbackService().ReadAsync(new(GameEdition.SkyrimSpecialEdition, path), CancellationToken.None);
        Require(read.Accepted && read.Document is { Shapes.Length: 1 }, "Actual output must independently reopen as one shape.");
        var shape = read.Document!.Shapes[0];
        byte[] bytes = await File.ReadAllBytesAsync(path.Value);
        Require(shape.VertexCount == expected.Length, "Actual output vertex count changed.");
        for (int vertex = 0; vertex < expected.Length; vertex++)
            for (int axis = 0; axis < 3; axis++)
            {
                float actual = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(checked((int)shape.VertexPayloadOffset) + vertex * shape.VertexStride + axis * 4, 4));
                Require(Math.Abs(actual - expected[vertex][axis]) < .001f,
                    $"Actual morph evaluation mismatch at vertex {vertex} axis {axis}: expected {expected[vertex][axis]}, got {actual}.");
            }
    }

    private static Fixture CreateFixture(string label)
    {
        var workspace = new WorkspacePath(Environment.CurrentDirectory);
        var root = new WorkspacePath(Path.Combine(workspace.Value, "artifacts", "task8-9", label + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "FemaleHead.nif"));
        File.WriteAllBytes(source.Value, CreateNif());
        File.WriteAllBytes(Path.ChangeExtension(source.Value, ".tri"), CreateTri());
        return new(workspace, root, source);
    }

    private static BlenderFaceGeomNifExporter CreateExporter(Fixture fixture,
        Func<string, string, CancellationToken, ValueTask<ImmutableArray<Diagnostic>>> runner)
    {
        var executable = new WorkspacePath(Path.Combine(fixture.Root.Value, "test.exe"));
        File.WriteAllBytes(executable.Value, [1, 2, 3]);
        var profile = new WorkspacePath(Path.Combine(fixture.Root.Value, "profile"));
        Directory.CreateDirectory(profile.Value);
        return new(executable, profile, "export_facegeom_nif", Policy(fixture), fixture.Workspace,
            new Sha256Hash(Hash(executable.Value)), new BethesdaNifGeometryReadbackService(), runner);
    }

    private static KOnlyWorkspacePolicy Policy(Fixture fixture) => new(fixture.Workspace, new WorkspacePath("F:/ExampleGame"));
    private static FaceGeomBinaryBuildRequest Request(Fixture fixture, string output, ImmutableArray<FaceGeomBinaryMorph> morphs) =>
        new(GameEdition.SkyrimSpecialEdition, fixture.Root, fixture.Source, new WorkspacePath(Path.Combine(fixture.Root.Value, output + ".nif")), morphs);
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Diagnostics(FaceGeomBinaryBuildResult result) => string.Join("; ", result.Diagnostics.Select(row => row.Code + ": " + row.Message));
    private static void RequireRefusal(FaceGeomBinaryBuildResult result, string code) =>
        Require(!result.Written && result.Diagnostics.Any(row => row.Code == code), "Expected " + code + ": " + Diagnostics(result));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Fixture(WorkspacePath Workspace, WorkspacePath Root, WorkspacePath Source);

    private static byte[] CreateNif(bool metadata = false, int metadataSize = 8, uint metadataValue = 3,
        string metadataType = "NiStringExtraData", byte[]? shapeTail = null)
    {
        static byte[] Block(Action<BinaryWriter> action)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            action(writer);
            return stream.ToArray();
        }
        static void AvObject(BinaryWriter writer, uint name, bool extra)
        {
            writer.Write(name); writer.Write(extra ? 1U : 0U); if (extra) writer.Write(2);
            writer.Write(-1); writer.Write(0U);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            for (int i = 0; i < 9; i++) writer.Write(i % 4 == 0 ? 1f : 0f);
            writer.Write(1f); writer.Write(-1);
        }
        byte[] node = Block(writer => { AvObject(writer, 0, metadata); writer.Write(1U); writer.Write(1); writer.Write(0U); });
        byte[] shape = Block(writer =>
        {
            AvObject(writer, 1, false); writer.Write(new byte[16]);
            writer.Write(-1); writer.Write(-1); writer.Write(-1);
            writer.Write((0x401UL << 44) | 4UL); writer.Write((ushort)1); writer.Write((ushort)3); writer.Write(54U);
            foreach (float[] point in new float[][] { [0, 0, 0], [1, 0, 0], [0, 1, 0] })
            { foreach (float coordinate in point) writer.Write(coordinate); writer.Write(0U); }
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
            if (shapeTail is not null) writer.Write(shapeTail);
        });
        return Block(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            writer.Write(0x14020007U); writer.Write((byte)1); writer.Write(12U); writer.Write(metadata ? 3U : 2U); writer.Write(100U);
            writer.Write(new byte[3]); writer.Write((ushort)(metadata ? 3 : 2));
            Sized(writer, "NiNode"); Sized(writer, "BSTriShape"); if (metadata) Sized(writer, metadataType);
            writer.Write((ushort)0); writer.Write((ushort)1); if (metadata) writer.Write((ushort)2);
            writer.Write((uint)node.Length); writer.Write((uint)shape.Length); if (metadata) writer.Write((uint)metadataSize);
            string[] strings = ["Root", "Head", "FixtureMetadata", "RetainedValue"];
            writer.Write((uint)strings.Length); writer.Write((uint)strings.Max(value => value.Length));
            foreach (string value in strings) Sized(writer, value);
            writer.Write(0U); writer.Write(node); writer.Write(shape);
            if (metadata) { writer.Write(2U); if (metadataSize == 8) writer.Write(metadataValue); }
            writer.Write(1U); writer.Write(0);
        });
    }

    private static byte[] CreateTri()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        foreach (uint value in new uint[] { 3, 1, 0, 0, 0, 3, 1, 3, 0, 0, 0, 0, 0, 0 }) writer.Write(value);
        foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
        writer.Write(0U); writer.Write(1U); writer.Write(2U);
        foreach (float value in new float[] { 0, 0, 1, 0, 0, 1 }) writer.Write(value);
        writer.Write(0U); writer.Write(1U); writer.Write(2U);
        for (int morph = 0; morph < MorphNames.Length; morph++)
        {
            Sized(writer, MorphNames[morph] + '\0'); writer.Write(1f);
            for (int vertex = 0; vertex < 3; vertex++)
                for (int axis = 0; axis < 3; axis++) writer.Write((short)(vertex == morph && axis == morph ? 1 : 0));
        }
        return stream.ToArray();
    }

    private static void Sized(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.ASCII.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes); }
}
