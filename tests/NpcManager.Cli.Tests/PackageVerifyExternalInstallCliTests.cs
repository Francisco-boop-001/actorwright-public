using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Cli.Tests;

internal sealed class PackageVerifyExternalInstallCliTests
    : IPreview254ExternalSmpCliScenario
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Selector => "--test-package-verify-external-install-cli";

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        await RejectInstallInputsWithoutStrictMode(cancellationToken);
        await RejectMalformedStrictOptions(cancellationToken);
        await RefuseUnclosedExternalEvidence(cancellationToken);
        await ForwardExactTargetRaceAfterOrdinaryPass(cancellationToken);
        await RejectUnboundedLoadOrderShapes(cancellationToken);
        await RefuseMissingAndAmbiguousOutputRows(cancellationToken);
        await RefuseMissingTargetFormAndRace(cancellationToken);
        await PreserveDeclaredUnverifiedAndVerifiedStates(cancellationToken);
        await RefuseDisabledRacePlugin(cancellationToken);
        await RefuseBetweenPassPackageDrift(cancellationToken);
        await PreserveOrdinaryJsonAndHumanOutput(cancellationToken);
        Require(CommandCatalog.All.Length == 142,
            "command-count-includes-six-voice-dialogue-commands");
    }

    private static async Task RejectInstallInputsWithoutStrictMode(
        CancellationToken cancellationToken)
    {
        var run = await RunHandler(
            [
                "package", "verify",
                "--manifest", @"K:\\Actorwright\\fixtures\\npcmanager-package.json",
                "--data-root", @"K:\\Actorwright\\fixtures\\Data",
                "--plugins", "Skyrim.esm,ExternalHair.esp"
            ],
            (_, _) => RefusedResult(),
            cancellationToken);

        Require(run.Exit == CommandExitCode.UsageError,
            "data-root-and-plugins-without-strict-are-usage-errors");
        Require(run.Service.Requests.Count == 0,
            "usage-error-does-not-call-package-verifier");
    }

    private static async Task RejectMalformedStrictOptions(
        CancellationToken cancellationToken)
    {
        string manifest = @"K:\Actorwright\fixtures\npcmanager-package.json";
        string dataRoot = @"K:\Actorwright\fixtures\Data";
        string[] invalid =
        [
            "false",
            "maybe",
            "duplicate",
            "empty",
            "missing-data",
            "missing-plugins",
            "race-option"
        ];

        foreach (string caseName in invalid)
        {
            string[] args = caseName switch
            {
                "false" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "false",
                    "--data-root", dataRoot,
                    "--plugins", "Skyrim.esm"
                ],
                "maybe" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "maybe",
                    "--data-root", dataRoot,
                    "--plugins", "Skyrim.esm"
                ],
                "duplicate" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "true",
                    "--data-root", dataRoot,
                    "--plugins", "Skyrim.esm,Skyrim.esm"
                ],
                "empty" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "true",
                    "--data-root", dataRoot,
                    "--plugins", "Skyrim.esm,,ExternalHair.esp"
                ],
                "missing-data" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "true",
                    "--plugins", "Skyrim.esm"
                ],
                "missing-plugins" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "true",
                    "--data-root", dataRoot
                ],
                "race-option" => [
                    "package", "verify", "--manifest", manifest,
                    "--strict-install-dependencies", "true",
                    "--data-root", dataRoot,
                    "--plugins", "Skyrim.esm",
                    "--race", "WrongRace.esp|0x900"
                ],
                _ => throw new InvalidOperationException(caseName)
            };

            var run = await RunHandler(args, (_, _) => RefusedResult(), cancellationToken);
            Require(run.Exit == CommandExitCode.UsageError,
                "strict-option-usage:" + caseName);
            Require(run.Service.Requests.Count == 0,
                "strict-option-no-service-call:" + caseName);
        }
    }

    private static async Task RefuseUnclosedExternalEvidence(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        foreach (var invalid in new[] { "closure", "diagnostic" })
        {
            var service = new RecordingPackageVerifyService((_, _) =>
                ExternalResult(fixture, ExternalInstallDependencyState.DeclaredUnverified,
                    descriptorClosureValid: invalid != "closure",
                    diagnostics: invalid == "diagnostic"
                        ? [new Diagnostic("external-headpart-descriptor-lost",
                            DiagnosticSeverity.Error, "descriptor evidence lost")]
                        : []));
            var run = await RunHandler(StrictArgs(fixture, json: true), service,
                cancellationToken);
            Require(run.Exit != CommandExitCode.Success && service.Requests.Count == 1,
                "invalid-external-evidence-stops-before-race-read-and-second-pass:" + invalid);
            Require(run.Output.Contains("external-headpart", StringComparison.Ordinal),
                "invalid-external-evidence-is-diagnostic:" + invalid);
        }
    }

    private static async Task ForwardExactTargetRaceAfterOrdinaryPass(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        var expectedOrder = ImmutableArray.Create(
            new PluginName("Skyrim.esm"),
            fixture.Race.Plugin,
            new PluginName("ExternalHair.esp"));
        var service = new RecordingPackageVerifyService((index, request) =>
            ExternalResult(
                fixture,
                index == 1
                    ? ExternalInstallDependencyState.DeclaredUnverified
                    : ExternalInstallDependencyState.Verified));
        var run = await RunHandler(
            [
                "package", "verify",
                "--manifest", fixture.ManifestPath.Value,
                "--strict-install-dependencies", "true",
                "--data-root", fixture.DataRoot.Value,
                "--plugins", string.Join(',', expectedOrder.Select(item => item.Value)),
                "--json"
            ],
            service,
            cancellationToken);

        Require(run.Exit == CommandExitCode.Success,
            "strict-external-pass");
        Require(service.Requests.Count == 2,
            "strict-external-two-pass-service-calls");
        Require(service.Requests[0].InstallContext is null &&
                !service.Requests[0].RequireInstallDependencyAuthority,
            "first-pass-is-context-free");
        Require(service.Requests[1].RequireInstallDependencyAuthority &&
                service.Requests[1].InstallContext is not null,
            "second-pass-requires-install-authority");
        var context = service.Requests[1].InstallContext!;
        Require(context.DataRoot == fixture.DataRoot &&
                context.EnabledPluginOrder.SequenceEqual(expectedOrder) &&
                context.TargetRace == fixture.Race,
            "second-pass-forwards-exact-data-order-and-npc-race");
        Require(!run.Output.Contains(fixture.DataRoot.Value,
                StringComparison.OrdinalIgnoreCase),
            "data-root-is-not-serialized-as-install-context");
        using var response = JsonDocument.Parse(run.Output);
        Require(response.RootElement.GetProperty("verified").GetBoolean() &&
                response.RootElement
                    .GetProperty("externalInstallDependencyVerification")
                    .GetProperty("currentInstallDependencyState")
                    .GetString() == "verified",
            "verified-state-is-projected-separately-from-package-bytes");

        fixture.WriteSchema3ValidRace(new FormReference(
            new PluginName("Schema3DifferentRace.esp"), new FormId(0x901)));
        var schema3Service = DeclaredThenVerifiedService(fixture);
        var schema3Run = await RunHandler(StrictArgs(fixture), schema3Service,
            cancellationToken);
        Require(schema3Run.Exit == CommandExitCode.Success &&
                schema3Service.Requests[1].InstallContext!.TargetRace == fixture.Race,
            "persisted-npc-race-wins-over-schema3-valid-race");
        Require(File.ReadAllText(fixture.Schema3Path).Contains(
                "Schema3DifferentRace.esp|0x901", StringComparison.Ordinal),
            "schema3-valid-race-fixture-is-authentic-and-distinct");
    }

    private static async Task RejectUnboundedLoadOrderShapes(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        fixture.WriteProviderPlugin(new PluginName("Provider.esp"),
            new PluginName("Master.esp"));
        var providers = ImmutableArray.Create(new ExternalHeadPartInstallProviderObservation(
            new PluginName("Provider.esp"), Hash('3'), null, true));
        foreach (var item in new[]
        {
            (Name: "dependent-before-master", Plugins: new[] { "Provider.esp", "Master.esp" }),
            (Name: "missing-master", Plugins: new[] { "Provider.esp" })
        })
        {
            var service = new RecordingPackageVerifyService((_, _) =>
                ExternalResult(fixture, ExternalInstallDependencyState.DeclaredUnverified,
                    providerObservations: providers));
            var run = await RunHandler(StrictArgs(fixture, item.Plugins), service,
                cancellationToken);
            Require(run.Exit == CommandExitCode.UsageError && service.Requests.Count == 1,
                "load-order-shape-is-usage-error:" + item.Name);
        }

        var tooMany = Enumerable.Range(0, 65)
            .Select(index => $"Plugin{index}.esp").ToArray();
        var overLimitService = new RecordingPackageVerifyService((_, _) =>
            ExternalResult(fixture, ExternalInstallDependencyState.DeclaredUnverified));
        var overLimit = await RunHandler(StrictArgs(fixture, tooMany), overLimitService,
            cancellationToken);
        Require(overLimit.Exit == CommandExitCode.UsageError &&
                overLimitService.Requests.Count == 1,
            "load-order-over-limit-is-usage-error");
    }

    private static async Task RefuseMissingAndAmbiguousOutputRows(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        var cases = new Dictionary<string, ImmutableArray<PackageFileVerification>>
        {
            ["missing"] = [],
            ["duplicate"] = [
                PluginRow(fixture.OutputPlugin.Value),
                PluginRow("Data/" + fixture.OutputPlugin.Value)
            ],
            ["wrong-kind"] = [
                PluginRow(fixture.OutputPlugin.Value, "faceGeom")
            ]
        };

        foreach (var item in cases)
        {
            var service = new RecordingPackageVerifyService((_, _) =>
                ExternalResult(fixture,
                    ExternalInstallDependencyState.DeclaredUnverified,
                    item.Value));
            var run = await RunHandler(
                StrictArgs(fixture, json: true), service, cancellationToken);
            Require(run.Exit != CommandExitCode.Success,
                "output-row-refused:" + item.Key);
            Require(service.Requests.Count == 1,
                "output-row-refusal-stops-before-second-pass:" + item.Key);
            Require(run.Output.Contains("precheck-unavailable",
                    StringComparison.Ordinal),
                "output-row-refusal-diagnostic:" + item.Key);
        }
    }

    private static async Task RefuseMissingTargetFormAndRace(
        CancellationToken cancellationToken)
    {
        using (var missingForm = new Fixture(targetFormId: new FormId(0x801)))
        {
            var service = DeclaredThenVerifiedService(missingForm);
            var run = await RunHandler(
                StrictArgs(missingForm), service, cancellationToken);
            Require(run.Exit != CommandExitCode.Success &&
                    service.Requests.Count == 1,
                "missing-target-form-refuses-before-strict-pass");
        }

        using (var missingRace = new Fixture(includeRace: false))
        {
            var service = DeclaredThenVerifiedService(missingRace);
            var run = await RunHandler(
                StrictArgs(missingRace), service, cancellationToken);
            Require(run.Exit != CommandExitCode.Success &&
                    service.Requests.Count == 1,
                "null-race-target-refuses-before-strict-pass");
        }
    }

    private static async Task PreserveDeclaredUnverifiedAndVerifiedStates(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        var service = new RecordingPackageVerifyService((index, _) =>
            ExternalResult(fixture,
                index == 1
                    ? ExternalInstallDependencyState.DeclaredUnverified
                    : ExternalInstallDependencyState.Verified));

        var ordinary = await RunHandler(
            ["package", "verify", "--manifest", fixture.ManifestPath.Value,
                "--json"],
            service,
            cancellationToken);
        Require(ordinary.Exit == CommandExitCode.Success &&
                service.Requests.Count == 1,
            "ordinary-external-verification-remains-package-byte-success");
        using (var ordinaryJson = JsonDocument.Parse(ordinary.Output))
        {
            Require(ordinaryJson.RootElement.GetProperty("verified").GetBoolean() &&
                    ordinaryJson.RootElement
                        .GetProperty("externalInstallDependencyVerification")
                        .GetProperty("currentInstallDependencyState")
                        .GetString() == "declaredUnverified",
                "ordinary-external-state-is-declared-unverified");
        }

        var strict = await RunHandler(
            StrictArgs(fixture, json: true), service, cancellationToken);
        Require(strict.Exit == CommandExitCode.Success &&
                service.Requests.Count == 3,
            "strict-external-verification-establishes-current-state");
        using var strictJson = JsonDocument.Parse(strict.Output);
        Require(strictJson.RootElement
                    .GetProperty("externalInstallDependencyVerification")
                    .GetProperty("installReady").GetBoolean(),
            "strict-external-state-is-install-ready");
    }

    private static async Task RefuseDisabledRacePlugin(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        var service = new RecordingPackageVerifyService((index, request) =>
        {
            if (index == 1)
                return ExternalResult(fixture,
                    ExternalInstallDependencyState.DeclaredUnverified);

            var raceEnabled = request.InstallContext is { } context &&
                context.EnabledPluginOrder.Contains(fixture.Race.Plugin);
            return ExternalResult(fixture,
                raceEnabled
                    ? ExternalInstallDependencyState.Verified
                    : ExternalInstallDependencyState.DeclaredUnverified);
        });
        var run = await RunHandler(
            [
                "package", "verify",
                "--manifest", fixture.ManifestPath.Value,
                "--strict-install-dependencies", "true",
                "--data-root", fixture.DataRoot.Value,
                "--plugins", "Skyrim.esm,ExternalHair.esp"
            ],
            service,
            cancellationToken);

        Require(run.Exit != CommandExitCode.Success &&
                service.Requests.Count == 2,
            "disabled-race-plugin-refuses-strict-authority");
        Require(service.Requests[1].InstallContext!.TargetRace == fixture.Race,
            "disabled-race-test-still-uses-persisted-race");
    }

    private static async Task RefuseBetweenPassPackageDrift(
        CancellationToken cancellationToken)
    {
        using var fixture = new Fixture();
        var service = new ConcreteSecondPassVerificationService(fixture);
        var run = await RunHandler(
            StrictArgs(fixture, json: true), service, cancellationToken);
        Require(run.Exit != CommandExitCode.Success &&
                service.Requests.Count == 2,
            "between-pass-package-drift-refuses");
        Require(service.HashChecked && service.ParseChecked,
            "between-pass-concrete-verification-calculates-hash-and-parse-drift");
        using var response = JsonDocument.Parse(run.Output);
        Require(!response.RootElement.GetProperty("verified").GetBoolean(),
            "between-pass-package-drift-does-not-upgrade-first-pass");
    }

    private static async Task PreserveOrdinaryJsonAndHumanOutput(
        CancellationToken cancellationToken)
    {
        using var ordinaryFixture = new Fixture(includeExternal: false);
        var ordinaryResult = OrdinaryResult(ordinaryFixture.ManifestPath);
        var ordinary = await RunHandler(
            ["package", "verify", "--manifest", ordinaryFixture.ManifestPath.Value,
                "--json"],
            (_, _) => ordinaryResult,
            cancellationToken);
        string expectedLegacyJson = JsonSerializer.Serialize(
            new
            {
                Verified = ordinaryResult.Verified,
                Artifact = ordinaryResult.Artifact,
                Diagnostics = ordinaryResult.Diagnostics
            },
            LegacyJsonOptions) + Environment.NewLine;
        Require(ordinary.Output == expectedLegacyJson,
            "ordinary-json-is-byte-identical-to-legacy-projection");
        Require(!ordinary.Output.Contains(
                "externalInstallDependencyVerification",
                StringComparison.Ordinal),
            "ordinary-json-suppresses-external-projection");

        using var externalFixture = new Fixture();
        var external = await RunHandler(
            ["package", "verify", "--manifest", externalFixture.ManifestPath.Value],
            (_, _) => ExternalResult(externalFixture,
                ExternalInstallDependencyState.DeclaredUnverified),
            cancellationToken);
        Require(external.Exit == CommandExitCode.Success &&
                external.Output.Contains("package verify: PASS (package bytes PASS; " +
                    "install dependencies REQUIRED", StringComparison.Ordinal),
            "human-output-distinguishes-required-install-dependencies");

        var strictService = DeclaredThenVerifiedService(externalFixture);
        var strict = await RunHandler(
            StrictArgs(externalFixture), strictService, cancellationToken);
        Require(strict.Exit == CommandExitCode.Success &&
                strict.Output.Contains("install dependencies VERIFIED",
                    StringComparison.Ordinal),
            "human-output-distinguishes-verified-install-dependencies");
    }

    private static RecordingPackageVerifyService DeclaredThenVerifiedService(
        Fixture fixture) =>
        new((index, _) => ExternalResult(fixture,
            index == 1
                ? ExternalInstallDependencyState.DeclaredUnverified
                : ExternalInstallDependencyState.Verified));

    private static string[] StrictArgs(Fixture fixture, bool json = false)
        => StrictArgs(fixture, ["Skyrim.esm", fixture.Race.Plugin.Value, "ExternalHair.esp"], json);

    private static string[] StrictArgs(
        Fixture fixture, IEnumerable<string> plugins, bool json = false)
    {
        var args = new List<string>
        {
            "package", "verify",
            "--manifest", fixture.ManifestPath.Value,
            "--strict-install-dependencies", "true",
            "--data-root", fixture.DataRoot.Value,
            "--plugins", string.Join(',', plugins)
        };
        if (json) args.Add("--json");
        return args.ToArray();
    }

    private static PackageVerifyResult OrdinaryResult(
        WorkspacePath? manifestPath = null) =>
        new(
            true,
            manifestPath is null ? null : new PackageVerificationArtifact(
                "1",
                "npcmanager-package-verification",
                "skyrimse",
                "racemenu-jslot",
                "NpcManagerOutput.esp",
                new FormId(0x800),
                manifestPath.Value,
                Hash('1'),
                [],
                true,
                true,
                false),
            []);

    private static PackageVerifyResult RefusedResult(
        WorkspacePath? manifestPath = null,
        ImmutableArray<Diagnostic> diagnostics = default) =>
        new(
            false,
            manifestPath is null ? null : new PackageVerificationArtifact(
                "1",
                "npcmanager-package-verification",
                "skyrimse",
                "racemenu-jslot",
                "NpcManagerOutput.esp",
                new FormId(0x800),
                manifestPath.Value,
                Hash('1'),
                [],
                true,
                true,
                false),
            diagnostics.IsDefault
                ? [new Diagnostic("package-test-refused",
                    DiagnosticSeverity.Error, "package refused")]
                : diagnostics);

    private static PackageVerifyResult ExternalResult(
        Fixture fixture,
        ExternalInstallDependencyState state,
        ImmutableArray<PackageFileVerification> rows = default,
        bool descriptorClosureValid = true,
        ImmutableArray<ExternalHeadPartInstallProviderObservation> providerObservations = default,
        ImmutableArray<Diagnostic> diagnostics = default) =>
        new(
            true,
            new PackageVerificationArtifact(
                "1",
                "npcmanager-package-verification",
                "skyrimse",
                "racemenu-jslot",
                fixture.OutputPlugin.Value,
                fixture.TargetFormId,
                fixture.ManifestPath,
                Hash('1'),
                rows.IsDefault
                    ? ImmutableArray.Create(PluginRow(fixture.OutputPlugin.Value))
                    : rows,
                true,
                true,
                false),
            diagnostics.IsDefault ? [] : diagnostics)
        {
            ExternalInstallDependencyVerification =
                new ExternalHeadPartInstallVerificationArtifact(
                    ExternalHeadPartSchemaIdentifiers.InstallVerification,
                    true,
                    descriptorClosureValid,
                    null,
                    [],
                    null,
                    state,
                    state == ExternalInstallDependencyState.Verified,
                    state == ExternalInstallDependencyState.Verified,
                    false,
                    false,
                    providerObservations.IsDefault ? [] : providerObservations,
                    [])
        };

    private static PackageFileVerification PluginRow(
        string relativePath,
        string kind = "plugin") =>
        new(
            kind,
            new AssetPath(relativePath),
            1,
            1,
            Hash('2'),
            Hash('2'),
            true);

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static async Task<HandlerRun> RunHandler(
        string[] args,
        Func<int, PackageVerifyRequest, PackageVerifyResult> resultFactory,
        CancellationToken cancellationToken) =>
        await RunHandler(args,
            new RecordingPackageVerifyService(resultFactory), cancellationToken);

    private static async Task<HandlerRun> RunHandler(
        string[] args,
        RecordingPackageVerifyService service,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PackageCommandHandler(
            new NoopBuildService(),
            new NoopInspectService(),
            service,
            new NoopArchiveService(),
            new WorkspacePath(@"K:\Actorwright"),
            output,
            error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse(args), cancellationToken);
        return new HandlerRun(exit, output.ToString(), error.ToString(), service);
    }

    private static async Task<HandlerRun> RunHandler(
        string[] args,
        IRequestRecordingPackageVerifyService service,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PackageCommandHandler(
            new NoopBuildService(), new NoopInspectService(), service,
            new NoopArchiveService(), new WorkspacePath(@"K:\Actorwright"), output, error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse(args), cancellationToken);
        return new HandlerRun(exit, output.ToString(), error.ToString(), service);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record HandlerRun(
        CommandExitCode Exit,
        string Output,
        string Error,
        IRequestRecordingPackageVerifyService Service);

    private interface IRequestRecordingPackageVerifyService : IPackageVerifyService
    {
        internal List<PackageVerifyRequest> Requests { get; }
    }

    private sealed class RecordingPackageVerifyService(
        Func<int, PackageVerifyRequest, PackageVerifyResult> resultFactory)
        : IRequestRecordingPackageVerifyService
    {
        public List<PackageVerifyRequest> Requests { get; } = [];

        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(resultFactory(Requests.Count, request));
        }
    }

    private sealed class ConcreteSecondPassVerificationService(Fixture fixture)
        : IRequestRecordingPackageVerifyService
    {
        private readonly byte[] expectedBytes = File.ReadAllBytes(
            Path.Combine(fixture.Root, fixture.OutputPlugin.Value));

        internal bool HashChecked { get; private set; }
        internal bool ParseChecked { get; private set; }
        public List<PackageVerifyRequest> Requests { get; } = [];

        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
                return ValueTask.FromResult(ExternalResult(fixture,
                    ExternalInstallDependencyState.DeclaredUnverified));

            File.WriteAllBytes(Path.Combine(fixture.Root, fixture.OutputPlugin.Value),
                Encoding.UTF8.GetBytes("malformed plugin after first race read"));
            var current = File.ReadAllBytes(Path.Combine(fixture.Root, fixture.OutputPlugin.Value));
            HashChecked = !SHA256.HashData(current).AsSpan().SequenceEqual(
                SHA256.HashData(expectedBytes));
            try
            {
                _ = BethesdaNpcFaceAdapter.Read(GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(Path.Combine(fixture.Root, fixture.OutputPlugin.Value)),
                    fixture.TargetFormId);
            }
            catch (Exception)
            {
                ParseChecked = true;
            }

            return ValueTask.FromResult(RefusedResult(fixture.ManifestPath,
                [new Diagnostic("package-artifact-hash-mismatch",
                    DiagnosticSeverity.Error,
                    "The concrete second verification observed package drift.")]));
        }
    }

    private sealed class NoopBuildService : IPackageBuildService
    {
        public ValueTask<PackageBuildResult> BuildAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopInspectService : IPackageInspectService
    {
        public ValueTask<PackageInspectResult> InspectAsync(
            PackageInspectRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopArchiveService : IPackageArchiveService
    {
        public ValueTask<RuntimeArchiveVerificationResult> VerifyRuntimeAsync(
            RuntimeArchiveVerificationRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<PackageArchiveResult> ArchiveAsync(
            PackageArchiveRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(
            bool includeRace = true,
            bool includeExternal = true,
            FormId? targetFormId = null)
        {
            Root = Path.Combine(
                @"K:\Actorwright\artifacts\task-10-cli",
                "fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DataRoot = new WorkspacePath(Path.Combine(Root, "Data"));
            Directory.CreateDirectory(DataRoot.Value);
            OutputPlugin = new PluginName("NpcManagerOutput.esp");
            Race = new FormReference(
                new PluginName("NpcManagerRace.esp"),
                new FormId(0x900));
            TargetFormId = targetFormId ?? new FormId(0x800);
            ManifestPath = new WorkspacePath(
                Path.Combine(Root, "npcmanager-package.json"));
            if (includeExternal)
                WriteOutputPlugin(includeRace);
        }

        internal string Root { get; }
        internal WorkspacePath DataRoot { get; }
        internal WorkspacePath ManifestPath { get; }
        internal string Schema3Path => Path.Combine(Root, "selected-dependencies.schema3.json");
        internal PluginName OutputPlugin { get; }
        internal FormReference Race { get; }
        internal FormId TargetFormId { get; }

        private void WriteOutputPlugin(bool includeRace)
        {
            var outputKey = ModKey.FromNameAndExtension(OutputPlugin.Value);
            var mod = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(Race.Plugin.Value)
            });
            {
                var npc = new Npc(
                    new FormKey(outputKey, 0x800),
                    SkyrimRelease.SkyrimSE)
                {
                    EditorID = "Task10TargetNpc",
                    Configuration = new NpcConfiguration
                    {
                        Flags = NpcConfiguration.Flag.Female
                    },
                    Race = includeRace
                        ? new FormLink<IRaceGetter>(new FormKey(
                            ModKey.FromNameAndExtension(Race.Plugin.Value),
                            Race.FormId.Value))
                        : new FormLink<IRaceGetter>()
                };
                mod.Npcs.Add(npc);
            }

            mod.WriteToBinary(
                new FilePath(Path.Combine(Root, OutputPlugin.Value)),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
        }

        internal void WriteSchema3ValidRace(FormReference validRace)
        {
            File.WriteAllText(Schema3Path, $$"""{"schemaVersion":3,"validRace":"{{validRace.Plugin.Value}}|0x{{validRace.FormId.Value:X}}"}""");
        }

        internal void WriteProviderPlugin(PluginName provider, PluginName master)
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(provider.Value), SkyrimRelease.SkyrimSE);
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master.Value)
            });
            mod.WriteToBinary(new FilePath(Path.Combine(DataRoot.Value, provider.Value)),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            File.WriteAllBytes(Path.Combine(DataRoot.Value, master.Value), [0]);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
