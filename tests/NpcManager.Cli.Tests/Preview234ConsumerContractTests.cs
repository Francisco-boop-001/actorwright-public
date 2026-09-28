using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class Preview234ConsumerContractTests
{
    private static readonly JsonSerializerOptions IntakeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static async Task RunAsync()
    {
        await ProviderMigrationCliTests.RunAsync();
        string root = Path.Combine(
            "K:\\Actorwright",
            ".tmp",
            "preview234-consumer-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath workspaceRoot = new(root);
            string dataRoot = Path.Combine(root, "Data");
            Directory.CreateDirectory(dataRoot);
            string firstPlugin = Path.Combine(dataRoot, "First.esp");
            string thirdPlugin = Path.Combine(dataRoot, "Third.esp");
            await File.WriteAllBytesAsync(firstPlugin, [1, 2, 3]);
            await File.WriteAllBytesAsync(thirdPlugin, [4, 5, 6]);
            await AssertLoadOrderBomContractAsync(workspaceRoot,
                new WorkspacePath(dataRoot));
            string loadOrderPath = Path.Combine(root, "loadorder.txt");
            await File.WriteAllTextAsync(
                loadOrderPath,
                "First.esp\nThird.esp\n");
            WorkspacePath outputRoot = new(
                Path.Combine(root, "reviewed-output"));
            var request = new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition,
                workspaceRoot,
                new WorkspacePath(dataRoot),
                new WorkspacePath(loadOrderPath),
                outputRoot,
                [new PluginName("First.esp"), new PluginName("Third.esp")]);
            ImmutableArray<PluginClosureReviewEntry> plugins =
            [
                Plugin("First.esp", 0, firstPlugin),
                Plugin("Third.esp", 3, thirdPlugin)
            ];
            Sha256Hash loadOrderHash = Hash(loadOrderPath);
            Sha256Hash assetFingerprint = new(new string('A', 64));
            Sha256Hash intakeFingerprint =
                ReviewedGameIntakeFingerprintAuthority.Fingerprint(
                    request.Edition,
                    request.WorkspaceRoot,
                    request.DataRoot,
                    request.LoadOrderPath,
                    request.OutputRoot,
                    loadOrderHash,
                    plugins,
                    [],
                    [],
                    [],
                    assetFingerprint);
            var intake = new ReviewedGameIntake(
                request.Edition,
                request.WorkspaceRoot,
                request.DataRoot,
                request.LoadOrderPath,
                request.OutputRoot,
                loadOrderHash,
                plugins,
                [],
                [],
                [],
                2,
                assetFingerprint,
                intakeFingerprint,
                false);
            var result = new ReviewedGameIntakeResult(
                request.Edition,
                intake.Plugins,
                intake,
                []);
            ReviewedGameIntakeResponse response =
                ReviewedGameIntakeResponse.From(request, result);
            WorkspacePath intakePath = new(
                Path.Combine(root, "reviewed-intake.json"));
            await File.WriteAllBytesAsync(
                intakePath.Value,
                JsonSerializer.SerializeToUtf8Bytes(
                    response,
                    IntakeJsonOptions));

            var composer = new RecordingComposer();
            var output = new StringWriter();
            var error = new StringWriter();
            var handler = new NpcVisualPreviewCommandHandler(
                new FixedComposerFactory(composer),
                new KOnlyWorkspacePolicy(
                    workspaceRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                workspaceRoot,
                new FaceGeomHairRegionsDocumentCodec(workspaceRoot),
                output,
                error);
            CommandExitCode exit = await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preview", "npc",
                    "--intake", intakePath.Value,
                    "--plugin", "First.esp",
                    "--form", "0x800",
                    "--output-root", Path.Combine(root, "preview-output"),
                    "--json"
                ]),
                CancellationToken.None);

            if (exit != CommandExitCode.Success)
                throw new InvalidOperationException(
                    "preview npc rejected the producer's reviewed intake. " +
                    $"Exit={exit}; Output={output}; Error={error}");
            Require(composer.Request!.Intake.Plugins.Select(p => p.Order)
                .SequenceEqual([0, 3]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PluginClosureReviewEntry Plugin(
        string name,
        int order,
        string path) => new(
        new PluginName(name),
        order,
        true,
        true,
        true,
        false,
        true,
        new WorkspacePath(path),
        Hash(path),
        []);

    private static Sha256Hash Hash(string path) => new(
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static Sha256Hash Hash(byte[] bytes) => new(
        Convert.ToHexString(SHA256.HashData(bytes)));

    private static async Task AssertLoadOrderBomContractAsync(
        WorkspacePath workspaceRoot,
        WorkspacePath pluginsRoot)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"First.esp\",\"order\":0,\"enabled\":true}]}");
        byte[] bomBytes = [0xEF, 0xBB, 0xBF, .. plainBytes];
        string plainPath = Path.Combine(workspaceRoot.Value, "load-order-plain.json");
        string bomPath = Path.Combine(workspaceRoot.Value, "load-order-bom.json");
        await File.WriteAllBytesAsync(plainPath, plainBytes);
        await File.WriteAllBytesAsync(bomPath, bomBytes);

        var service = new PluginLoadOrderService(new LocalPluginReader(),
            new KOnlyWorkspacePolicy(workspaceRoot,
                new WorkspacePath("F:\\ExampleGame")), workspaceRoot);
        var plain = await service.ResolveAsync(new PluginLoadOrderRequest(
            GameEdition.SkyrimSpecialEdition, pluginsRoot,
            new WorkspacePath(plainPath)), CancellationToken.None);
        var bom = await service.ResolveAsync(new PluginLoadOrderRequest(
            GameEdition.SkyrimSpecialEdition, pluginsRoot,
            new WorkspacePath(bomPath)), CancellationToken.None);

        Require(plain.IsValid, "The BOM-free load-order manifest was rejected.");
        Require(bom.IsValid, "The leading-BOM load-order manifest was rejected.");
        Require(plain.Entries.SequenceEqual(bom.Entries),
            "Leading UTF-8 BOM changed the load-order semantics.");
        Require(plain.SourceHash == Hash(plainBytes),
            "BOM-free load-order hash did not bind original bytes.");
        Require(bom.SourceHash == Hash(bomBytes),
            "BOM load-order hash did not bind original bytes.");
        Require(plain.SourceHash != bom.SourceHash,
            "BOM and BOM-free load-order hashes unexpectedly matched.");

        await RequireLoadOrderRefusalAsync(service, workspaceRoot, pluginsRoot,
            "load-order-two-boms.json", [0xEF, 0xBB, 0xBF, .. bomBytes],
            "Two leading BOMs were accepted.");
        await RequireLoadOrderRefusalAsync(service, workspaceRoot, pluginsRoot,
            "load-order-comment.json", Encoding.UTF8.GetBytes("// comment\n" + Encoding.UTF8.GetString(plainBytes)),
            "A commented load-order document was accepted.");
        await RequireLoadOrderRefusalAsync(service, workspaceRoot, pluginsRoot,
            "load-order-duplicate-key.json", Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"First.esp\",\"order\":0,\"enabled\":true}]}"),
            "A duplicate load-order key was accepted.");
        await RequireLoadOrderRefusalAsync(service, workspaceRoot, pluginsRoot,
            "load-order-trailing-garbage.json", [.. plainBytes, (byte)'x'],
            "Trailing load-order garbage was accepted.");
    }

    private static async Task RequireLoadOrderRefusalAsync(
        PluginLoadOrderService service,
        WorkspacePath workspaceRoot,
        WorkspacePath pluginsRoot,
        string fileName,
        byte[] bytes,
        string message)
    {
        string path = Path.Combine(workspaceRoot.Value, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        var result = await service.ResolveAsync(new PluginLoadOrderRequest(
            GameEdition.SkyrimSpecialEdition, pluginsRoot,
            new WorkspacePath(path)), CancellationToken.None);
        Require(!result.IsValid, message);
    }

    private static void Require(bool condition, string message =
        "preview npc did not consume the producer's reviewed intake.")
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class LocalPluginReader : IPluginReader
    {
        public ValueTask<PluginInspection> ReadAsync(
            PluginReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PluginInspection(request.Edition,
                new PluginName(Path.GetFileName(request.PluginPath.Value)), [], [], []));
        }
    }

    private sealed class FixedComposerFactory(
        INpcVisualPreviewComposer composer) :
        IPreviewServiceFactory<INpcVisualPreviewComposer>
    {
        public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(
                    composer));
        }
    }

    private sealed class RecordingComposer : INpcVisualPreviewComposer
    {
        public NpcVisualPreviewComposeRequest? Request { get; private set; }

        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(
                new NpcVisualPreviewComposeResult(true, null, []));
        }
    }
}
