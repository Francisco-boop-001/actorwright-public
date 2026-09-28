using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class Preview261LegacyDiscoveryTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed record DiscoveryCase(
        string Name,
        ImmutableArray<string> Options,
        ImmutableHashSet<string> Required);

    private sealed record ExpectedFact(
        string ValueSyntax,
        ImmutableArray<string> AcceptedValues,
        ImmutableArray<string> DescriptionFragments);

    private sealed record Task3Profile(
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        AgentDeterminism Determinism,
        ImmutableArray<AgentAuthorityState> Authority,
        string ResultDescription);

    private static readonly ImmutableDictionary<(string Command, string Option), ExpectedFact>
        ExpectedFacts = new Dictionary<(string, string), ExpectedFact>
        {
            [("npc face-patch", "dry-run")] = Fact(
                "true|false|1", ["true", "false", "1"], "true or 1"),
            [("npc face-patch", "apply")] = Fact(
                "true|false|1", ["true", "false", "1"], "true or 1"),
            [("npc finish analyze", "validate-all")] = Fact(
                "true|false|1", ["true", "false", "1"], "true or 1"),
            [("npc finish apply", "validate-all")] = Fact(
                "true|false|1", ["true", "false", "1"], "true or 1"),
            [("npc finish apply", "data-root")] = Fact(
                "<absolute-K-local-path>", [],
                "Optional for ordinary requests",
                "External-schema non-validation apply requires both --data-root and --plugins",
                "--validate-all true or 1 may omit both",
                "supplying either still requires the pair"),
            [("npc finish apply", "plugins")] = Fact(
                "<plugin,...>", [],
                "Optional for ordinary requests",
                "External-schema non-validation apply requires both --data-root and --plugins",
                "--validate-all true or 1 may omit both",
                "supplying either still requires the pair")
        }.ToImmutableDictionary();

    private static readonly ImmutableArray<DiscoveryCase> Cases =
    [
        Case("workspace preflight",
            ["edition", "game", "workspace-root", "data-root", "output-root", "plugin", "asset-index", "load-order", "loadorder", "selected", "intake-output", "npc-editor-id", "workflow-output"]),
        Case("npc face-patch",
            ["game", "edition", "plugin", "output", "data-root", "npc", "form-id", "headparts", "headpart-replace", "hair-color", "expected-sha256", "proposal", "dry-run", "apply"],
            "plugin", "output", "data-root"),
        Case("facegen hair-regions analyze",
            ["source", "expected-source-sha256", "analysis", "assignment-template"],
            "source", "expected-source-sha256", "analysis", "assignment-template"),
        Case("facegen hair-regions propose",
            ["analysis", "analysis-sha256", "request", "request-sha256", "proposal"],
            "analysis", "analysis-sha256", "request", "request-sha256", "proposal"),
        Case("facegen hair-regions preview",
            ["request", "request-sha256", "proposal", "proposal-sha256", "intake", "output-root"],
            "request", "request-sha256", "proposal", "proposal-sha256", "intake", "output-root"),
        Case("facegen hair-regions apply",
            ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            "request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"),
        Case("facegen hair-regions verify",
            ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            "request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"),
        Case("preview render", PreviewOptions(), "manifest", "output"),
        Case("render npc", PreviewOptions(), "manifest", "output"),
        Case("preview npc", PreviewNpcOptions(), "intake", "plugin", "form", "output-root"),
        Case("preview reroll", ["edition", "game", "manifest", "npc", "seed", "output"],
            "manifest", "npc", "seed", "output"),
        Case("preview export-nif", ["edition", "game", "scene", "output", "asset-root"],
            "scene", "output"),
        Case("plugin write", ["edition", "game", "proposal", "plugin", "expected-sha256", "data-root", "private-root", "output", "no-overwrite"],
            "proposal", "output"),
        Case("plugin verify", PluginVerifyOptions()),
        Case("plugin audit", ["edition", "game", "normalize-master-index", "before", "after", "plugins-root", "data-root", "load-order", "loadorder"],
            "before", "after"),
        Case("plugin deploy", ["edition", "game", "plugin", "input-plugin", "data-root", "expected-sha256", "sha256"],
            "data-root"),
        Case("package build", ["source-root", "output-root"], "source-root", "output-root"),
        Case("package inspect", ["manifest"], "manifest"),
        Case("package verify", ["manifest", "archive", "payload", "include-runtime-preset", "strict-install-dependencies", "data-root", "plugins", "workflow-bundle", "workflow-bundle-sha256"]),
        Case("package archive", ["source-root", "package", "payload", "include-runtime-preset", "output", "workflow-bundle", "workflow-bundle-sha256"], "output"),
        Case("runtime-script propose", ["edition", "game", "npc", "appearance", "plugin", "output"],
            "npc", "appearance", "output"),
        Case("runtime-script build", ["edition", "game", "source-root", "output"], "source-root", "output"),
        Case("runtime-script write", ["edition", "game", "source", "proposal", "output"], "source", "proposal", "output"),
        Case("runtime-script package", ["edition", "game", "source-root", "output-root"], "source-root", "output-root"),
        Case("runtime-script deploy", ["edition", "game", "package", "data-root"], "package", "data-root"),
        Case("runtime-script inspect-vmad", ["edition", "game", "plugin", "npc", "script"], "plugin", "npc"),
        Case("preset design-propose", ["intake", "intake-sha256", "output", "template-output"]),
        Case("preset create-from-reference",
            ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "jslot-output", "evidence-root", "apply", "accepted-proposal-sha256"],
            "proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "jslot-output", "evidence-root"),
        Case("npc create-from-reference",
            ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "accepted-proposal-sha256", "request", "request-sha256", "data-root", "plugins", "transaction-root", "apply"],
            "proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "accepted-proposal-sha256", "request", "request-sha256", "data-root", "plugins", "transaction-root", "apply"),
        Case("npc finish analyze",
            ["request", "request-sha256", "proposal", "validate-all", "data-root", "plugins"],
            "request", "request-sha256", "proposal"),
        Case("npc finish apply",
            ["request", "request-sha256", "proposal", "proposal-sha256", "validate-all", "data-root", "plugins"],
            "request", "request-sha256", "proposal", "proposal-sha256"),
        Case("npc finish verify",
            ["manifest", "manifest-sha256", "data-root", "plugins"],
            "manifest", "manifest-sha256"),
        Case("npc create", NpcCreateOptions(),
            "provider-manifest", "provider-manifest-sha256", "template-plugin",
            "template-sha256", "template-npc", "facegeom-carrier",
            "facegeom-sha256", "facetint-manifest", "provider-root",
            "dependencies", "output-root", "plugin", "editor-id", "name",
            "role", "sex", "race", "voice", "class", "combat-style",
            "default-outfit"),
        Case("npc create-from-preset", ["request", "request-sha256"],
            "request", "request-sha256"),
        Case("npc create-from-jslot",
            ["request", "request-sha256", "preset", "preset-sha256",
             "data-root", "plugins", "companion-root", "preflight-output", "face-bake-authority-output",
             "reviewed-preflight", "reviewed-preflight-sha256",
             "migrated-request-root", "provider-migration-output",
             "reviewed-provider-migration",
             "reviewed-provider-migration-sha256", "provider-migration",
             "provider-migration-sha256"],
            "request", "request-sha256")
    ];

    public static async Task RunAsync()
    {
        Assert(CommandCatalog.All.Length == 142 &&
               CommandCatalog.All.Select(item => item.Name)
                   .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 142,
            "The command catalogue is not exactly 142 unique names.");

        string root = Path.Combine(AppContext.BaseDirectory,
            "preview261-legacy-discovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (DiscoveryCase testCase in Cases)
                await AssertLegacyDiscoveryAsync(testCase, root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Assert(LegacyCommandOptionCatalog.For("preview render")
                .SequenceEqual(LegacyCommandOptionCatalog.For("render npc")),
            "preview render and render npc do not publish identical option rows.");

        await AssertTask13ExhaustiveGateAsync();

        ImmutableArray<string> registryErrors = AgentCommandRegistry.Validate();
        Assert(registryErrors.IsEmpty,
            "The production registry did not validate: " + string.Join(" | ", registryErrors));

        await AssertRepresentativeContractsAsync();
        await AssertTask2ContractsAsync();
        await AssertTask3ContractsAsync();
        await AssertTask4ContractsAsync();
        await AssertTask5ContractsAsync();
        await AssertTask6ContractsAsync();
        await AssertTask7ContractsAsync();
        await AssertTask8ContractsAsync();
        await AssertTask9ContractsAsync();
        await AssertTask10ContractsAsync();
        await AssertTask11ContractsAsync();
        await AssertTask12ContractsAsync();
        await AssertProtocolV2BoundaryAsync();
    }

    private static async Task AssertTask13ExhaustiveGateAsync()
    {
        string[] names = CommandCatalog.All.Select(item => item.Name).ToArray();
        string[] expectedNames =
        [
            "capabilities", "schema export", "version", "diagnose",
            "workspace preflight", "workspace scan-generated",
            "body sliders resolve", "body sliders inspect-preset",
            "body sidecar inspect", "body sidecar write", "bodygen write",
            "body weight resolve", "body overlay patch", "body overlay bake",
            "body transforms apply", "body reset", "profile scan",
            "load-order validate", "plugins resolve-load-order",
            "plugins validate", "assets index", "assets search",
            "headpart choices", "paint choices", "forms search", "outfit list",
            "outfit propose", "outfit write", "leveled-list propose",
            "leveled-list write", "leveled-list resolve", "armor propose",
            "armor write", "armor-addon write", "armor damage-resist",
            "armor-addon propose", "material-swap propose", "material-swap write",
            "object-template propose", "object-template write", "changes list",
            "changes update", "records propose", "plugin write", "npc create",
            "npc create-from-preset", "npc create-from-jslot", "npc edit-package",
            "npc list", "npc search", "records list", "npc inspect", "npc patch",
            "body patch", "npc reset", "npc face-patch",
            "npc materialize-template", "preset inspect", "preset design-propose",
            "preset create-from-reference", "npc create-from-reference",
            "npc follower-finish analyze", "npc follower-finish apply",
            "npc follower-finish verify", "npc finish analyze", "npc finish apply",
            "npc finish verify", "npc placement interior analyze",
            "npc placement interior apply", "npc placement interior verify",
            "npc follower-finish pair-analyze", "npc follower-finish pair-apply",
            "npc follower-finish pair-verify", "preset catalog", "preset export",
            "preset diff", "preset resolve", "appearance copy", "facegen analyze",
            "facegen diagnose", "facegen verify", "facegen hair-regions analyze",
            "facegen hair-regions propose", "facegen hair-regions preview",
            "facegen hair-regions apply", "facegen hair-regions verify",
            "facegen build-geom", "facegen build-geom-nif",
            "facegen build-geom-bound", "facegen build-tint",
            "facegen build-tint-bound", "facegen build-tint-native",
            "facegen options", "face tint patch", "face morph patch",
            "face morph extended", "face sculpt patch", "face pose resolve",
            "face reset", "body weight normalize", "body weight redistribute",
            "facegen build", "facegen bake-all", "facegen bake-all-native",
            "facegen build-plugin", "facegen resolve-providers",
            "facegen plan-pack", "facegen pack", "facegen deploy",
            "preview render", "preview npc", "preview reroll", "preview export-nif",
            "animation list", "animation tree", "bodygen build",
            "runtime-script propose", "runtime-script build",
            "runtime-script write", "runtime-script package",
            "runtime-script deploy", "runtime-script inspect-vmad", "outfit create",
            "plugin verify", "plugin audit", "plugin deploy",
            "runtime smoke verify", "runtime smoke verify-all",
            "npc assembly preflight", "package build", "package inspect",
            "package verify", "package archive", "render npc",
            "pipeline preset-to-npc", "npc voice discover", "npc voice import",
            "npc voice synthesize", "npc dialogue analyze", "npc dialogue apply",
            "npc dialogue verify", "gui"
        ];
        Assert(names.SequenceEqual(expectedNames, StringComparer.Ordinal),
            "The exact ordered 142-command vocabulary changed.");
        Assert(names.SequenceEqual(AgentCommandRegistry.All.Select(item => item.Name),
                   StringComparer.Ordinal),
            "The production registry order diverged from the command catalogue.");

        await AssertOrderedV1HelpHashAsync(names);

        string[] incompleteV1 = names.Where(name =>
                AgentCommandRegistry.GetLegacyDiscoveryRequired(name)
                    .ContractStatus != AgentContractStatus.Complete)
            .ToArray();
        Assert(incompleteV1.Length == 0,
            $"V1 scoped discovery remains incomplete for {incompleteV1.Length} command(s): {string.Join(", ", incompleteV1)}.");

        string[] expectedV2 =
        [
            "capabilities", "schema export", "version", "workspace preflight",
            "npc create-from-jslot", "preset inspect", "npc finish analyze", "npc finish apply", "npc finish verify", "preview npc",
            "npc assembly preflight"
        ];
        string[] actualV2 = AgentCommandRegistry.All
            .Where(item => item.Readiness == ProtocolReadiness.V2)
            .Select(item => item.Name).ToArray();
        Assert(actualV2.SequenceEqual(expectedV2, StringComparer.Ordinal),
            $"The strict V2 callability set changed: {string.Join(",", actualV2)}.");
        Assert(expectedV2.All(name => AgentCommandRegistry.GetRequired(name)
                .ContractStatus == AgentContractStatus.Complete),
            "One or more strict V2 primary contracts remains incomplete.");

        var expectedStrictOptions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["npc finish analyze"] = ["request", "request-sha256", "proposal", "validate-all", "data-root", "plugins", "workflow-bundle", "workflow-bundle-sha256", "workflow-output", "review-receipt", "review-receipt-sha256"],
            ["npc finish apply"] = ["request", "request-sha256", "proposal", "proposal-sha256", "validate-all", "data-root", "plugins", "workflow-bundle", "workflow-bundle-sha256", "workflow-output", "review-receipt", "review-receipt-sha256"],
            ["npc finish verify"] = ["manifest", "manifest-sha256", "verification-output", "workflow-bundle", "workflow-bundle-sha256", "workflow-output", "data-root", "plugins"],
            ["npc create-from-jslot"] = ["request", "request-sha256", "preset", "preset-sha256", "data-root", "plugins", "companion-root", "preflight-output", "face-bake-authority-output", "reviewed-preflight", "reviewed-preflight-sha256", "workflow-bundle", "workflow-bundle-sha256", "workflow-output"],
            ["preset inspect"] = ["format", "edition", "input", "input-sha256", "inspection-output", "workflow-bundle", "workflow-bundle-sha256", "workflow-output"],
            ["preview npc"] = ["intake", "plugin", "form", "package-manifest", "expected-package-sha256", "output-root", "workflow-bundle", "workflow-bundle-sha256", "workflow-output"],
            ["npc assembly preflight"] = ["contract", "contract-sha256", "output"]
        };
        foreach ((string name, string[] options) in expectedStrictOptions)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.Options.Select(item => item.CliName)
                       .SequenceEqual(options, StringComparer.Ordinal),
                $"Strict V2 options changed for {name}.");
            Assert(contract.Options.All(item =>
                       !string.IsNullOrWhiteSpace(item.ValueSyntax) &&
                       !string.IsNullOrWhiteSpace(item.Description)) &&
                   !string.IsNullOrWhiteSpace(contract.ResultShape) &&
                   contract.ResultShape != "unspecified" &&
                   !string.IsNullOrWhiteSpace(contract.ResultDescription) &&
                   contract.InputArtifacts.Select(item => item.Kind)
                       .SequenceEqual(contract.InputArtifactKinds, StringComparer.Ordinal) &&
                   contract.Effects.Length >= 2 && contract.Authority.Length == 8,
                $"Strict V2 profile remains incomplete for {name}.");
        }

        var expectedStrictArtifacts = new Dictionary<string, (string[] Inputs, string[] Outputs)>(StringComparer.Ordinal)
        {
            ["preset inspect"] = new(
                [
                    "racemenu-jslot:schemas=application/json:always",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ],
                [
                    "preset-inspection:schemas=actorwright-preset-inspection/2:always",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ]),
            ["npc create-from-jslot"] = new(
                [
                    "npc-create-request:schemas=npc.create-from-jslot.request.v1:always",
                    "racemenu-jslot:schemas=application/json:always",
                    "npc-build-preflight:schemas=actorwright-npc-build-preflight/1:all[reviewed-preflight=noneOf(__absent__)/present]",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ],
                [
                    "npc-build-preflight:schemas=actorwright-npc-build-preflight/1:all[preflight-output=noneOf(__absent__)/present]",
                    "skyrim-face-bake-authority:schemas=skyrim-face-bake-authority/1:all[face-bake-authority-output=noneOf(__absent__)/present]",
                    "npc-package-manifest:schemas=application/json:all[reviewed-preflight=noneOf(__absent__)/present]",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ]),
            ["preview npc"] = new(
                [
                    "reviewed-workspace-intake:schemas=npcmanager-reviewed-game-intake/2:always",
                    "npc-package-manifest:schemas=application/json:always",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ],
                [
                    "npc-preview-manifest:schemas=npc-preview-bundle/1:always",
                    "workflow-bundle:schemas=actorwright.agent-workflow-bundle.v1:always"
                ]),
            ["npc assembly preflight"] = new(
                [
                    "actor-assembly-preflight-contract:schemas=npc.actor-assembly-preflight.contract.v1:always"
                ],
                [
                    "actor-assembly-preflight:schemas=npc.actor-assembly-preflight.result.v1:always"
                ])
        };
        foreach ((string name, (string[] inputs, string[] outputs)) in expectedStrictArtifacts)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.InputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(outputs, StringComparer.Ordinal),
                $"Strict V2 literal artifact profile drifted for {name}. " +
                $"inputs=[{string.Join(", ", contract.InputArtifacts.Select(Task6ArtifactProfile))}] " +
                $"outputs=[{string.Join(", ", contract.OutputArtifacts.Select(Task6ArtifactProfile))}].");
        }

        AgentCommandContract jslot = AgentCommandRegistry.GetRequired(
            "npc create-from-jslot");
        Assert(jslot.OptionRelationships.Any(item =>
                   item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                   item.Options.SequenceEqual(
                       ["preflight-output", "reviewed-preflight"],
                       StringComparer.Ordinal)) &&
               jslot.OptionRelationships.Any(item =>
                   item.Kind == AgentOptionRelationshipKind.RequiresTogether &&
                   item.Options.SequenceEqual(
                       ["reviewed-preflight", "reviewed-preflight-sha256"],
                       StringComparer.Ordinal)),
            "Strict V2 JSlot mode relationships are incomplete.");

        AgentCommandContract verify = AgentCommandRegistry
            .GetLegacyDiscoveryRequired("plugin verify");
        AgentOptionRelationshipContract expectationGate = verify.OptionRelationships
            .Single(item => item.Condition.Contains(
                "parser-producing expectation", StringComparison.Ordinal));
        foreach (string flag in new[] { "set-flag", "clear-flag" })
        {
            AgentRelationshipPredicate predicate = expectationGate.Trigger!.Predicates
                .Single(item => item.Subject == flag);
            Assert(predicate.Source == AgentPredicateSource.OptionValue &&
                   predicate.Match == AgentPredicateMatch.AnyOf &&
                   predicate.MatchesWhenAbsent &&
                   predicate.Values.SequenceEqual(["__absent__", ""],
                       StringComparer.Ordinal),
                $"plugin verify does not model absent-or-empty --{flag} as nonproducing.");
        }

        Assert(BuildInfo.ProductVersion == "1.0.0-preview.281" &&
               BuildInfo.SourceLine == "preview.281-public",
            "The current production identity is not Preview.281.");
        string readme = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory, "README.md"));
        string capabilities = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory, "docs", "product-capabilities.md"))
            .ReplaceLineEndings("\n");
        Assert(readme.Contains(
                   "Current source line: `1.0.0-preview.281` / `preview.281-public`.",
                   StringComparison.Ordinal) &&
               capabilities.Contains(
                   "The current source contract is `1.0.0-preview.281` with source line\n`preview.281-public`",
                   StringComparison.Ordinal) &&
               capabilities.Contains(
                   "| `1.0.0-preview.265` source/tag | Failed unpublished final-verifier checkpoint; never final-rooted, zipped, candidate-published, or promoted |",
                   StringComparison.Ordinal),
            "Current Preview.281 prose or preserved Preview.265 failed-tag history drifted.");

        foreach (string name in names)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            CommandExitCode exit = await NpcManager.Cli.Program.RunAsync(
                [.. name.Split(' '), "--help"], output, error,
                _ => throw new InvalidOperationException(
                    "Scoped V1 help constructed a protocol-v2 journal."),
                CancellationToken.None);
            string text = output.ToString();
            Assert(exit == CommandExitCode.Success &&
                   text.StartsWith($"{name} — ", StringComparison.Ordinal) &&
                   !text.Contains("Actorwright commands", StringComparison.Ordinal),
                $"{name} did not produce command-scoped human help. stdout={text} stderr={error}");
        }
    }

    private static async Task AssertOrderedV1HelpHashAsync(IEnumerable<string> names)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in names)
        {
            var (runner, output, error) = Program.CreateRunner();
            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
                [.. name.Split(' '), "--help", "--json"]), CancellationToken.None);
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0,
                $"V1 JSON help failed while hashing the fixed 142-command surface: {name}.");
            string normalizedJson = output.ToString().Replace("\r\n", "\n",
                StringComparison.Ordinal);
            hash.AppendData(Encoding.UTF8.GetBytes(normalizedJson));
        }

        // Compatibility-first repair descriptions clarify optional Finish receipts
        // and admitted paths; command, option, and protocol assertions stay fixed.
        const string expectedSha256 =
            "582825A1FC8E162C79340DA9B5AECA4CF62D4F9750F9F7E5B61DBC45CDB4E07B";
        string actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        Assert(actualSha256 == expectedSha256,
            $"The ordered 142-command V1 JSON help surface changed: {actualSha256}.");
    }

    private static ImmutableArray<string> PreviewNpcOptions() =>
    [
        "intake", "plugin", "form", "package-manifest",
        "expected-package-sha256", "output-root", "workflow-bundle",
        "workflow-bundle-sha256", "workflow-output"
    ];

    private static ImmutableArray<string> PluginVerifyOptions() =>
    [
        "edition", "game", "before", "after", "proposal", "source-plugin",
        "output-plugin", "form-id", "npc", "editor-id", "name", "sex",
        "race", "voice", "class", "combat-style", "level", "level-mult",
        "magicka-offset", "stamina-offset", "health-offset", "calc-min",
        "calc-max", "speed-multiplier", "disposition", "bleedout",
        "player-health", "player-magicka", "player-stamina", "skill-values",
        "skill-offsets", "height", "far-model-distance", "geared-weapons",
        "xp-offset", "set-flag", "clear-flag", "keywords", "add-keyword",
        "remove-keyword", "factions", "add-faction", "update-faction",
        "remove-faction", "inventory", "add-inventory", "update-inventory",
        "remove-inventory", "default-outfit", "sleep-outfit", "perks",
        "add-perk", "update-perk", "remove-perk", "actor-effects",
        "add-actor-effect", "remove-actor-effect", "properties", "add-property",
        "update-property", "remove-property", "appr", "add-appr",
        "remove-appr", "skin", "clear-skin", "regions",
        "weight", "weight-triangle", "whole-skin", "whole-skin-sha256", "aidt"
    ];

    private static ImmutableArray<string> PluginVerifyPresentExpectations() =>
    [
        "editor-id", "name", "sex", "race", "voice", "class",
        "combat-style", "level", "level-mult", "magicka-offset",
        "stamina-offset", "health-offset", "calc-min", "calc-max",
        "speed-multiplier", "disposition", "bleedout", "player-health",
        "player-magicka", "player-stamina", "skill-values", "skill-offsets",
        "height", "far-model-distance", "geared-weapons", "xp-offset",
        "set-flag", "clear-flag", "keywords", "add-keyword",
        "remove-keyword", "factions", "add-faction", "update-faction",
        "remove-faction", "inventory", "add-inventory", "update-inventory",
        "remove-inventory", "default-outfit", "sleep-outfit", "perks",
        "add-perk", "update-perk", "remove-perk", "actor-effects",
        "add-actor-effect", "remove-actor-effect", "properties",
        "add-property", "update-property", "remove-property", "appr",
        "add-appr", "remove-appr", "skin", "regions", "weight",
        "weight-triangle", "whole-skin", "aidt"
    ];

    private static ImmutableArray<string> NpcCreateOptions() =>
    [
        "edition", "game", "provider-manifest", "provider-manifest-sha256",
        "template-plugin", "template-sha256", "template-npc",
        "facegeom-carrier", "facegeom-sha256", "facetint-manifest",
        "provider-root", "dependencies", "output-root", "plugin",
        "editor-id", "name", "role", "sex", "race", "voice", "class",
        "combat-style", "default-outfit", "unique", "essential", "protected",
        "respawns", "auto-calc-stats", "level", "magicka-offset",
        "stamina-offset", "health-offset", "calc-min-level", "calc-max-level",
        "speed", "disposition", "bleedout", "base-health", "base-magicka",
        "base-stamina", "height", "weight", "nam5"
    ];

    private static async Task AssertTask6ContractsAsync()
    {
        Assert(Enum.GetNames<AgentOptionRelationshipKind>().SequenceEqual(
                   ["RequiredWhen", "RequiresTogether", "ForbiddenWhen", "AtLeastOne"], StringComparer.Ordinal),
            "Task 6 expanded the public relationship-kind/wire vocabulary.");
        var expectedOptions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["npc create"] =
            [
                "edition:optional:enum:skyrimse:skyrimse:-", "game:optional:enum:skyrimse:skyrimse:edition",
                "provider-manifest:required:path:<provider-manifest.json>::-", "provider-manifest-sha256:required:sha256:<64-hex-sha256>::-",
                "template-plugin:required:path:<K-local-template-plugin>::-", "template-sha256:required:sha256:<64-hex-sha256>::-",
                "template-npc:required:artifactReference:<nonzero-plugin-local-24-bit-form-id>::-", "facegeom-carrier:required:path:<K-local-carrier.nif>::-",
                "facegeom-sha256:required:sha256:<64-hex-sha256>::-", "facetint-manifest:required:path:<facetint-manifest.json>::-",
                "provider-root:required:path:<K-local-provider-root>::-", "dependencies:required:path:<dependency-manifest.json>::-",
                "output-root:required:path:<new-K-local-package-root>::-", "plugin:required:identifier:<new-plugin.esp>::-",
                "editor-id:required:identifier:<editor-id>::-", "name:required:string:<display-name>::-",
                "role:required:enum:static-validation:static-validation:-", "sex:required:enum:female:female:-",
                "race:required:artifactReference:<Plugin.esp|nonzero-plugin-local-24-bit-form-id>::-", "voice:required:artifactReference:<Plugin.esp|nonzero-plugin-local-24-bit-form-id>::-",
                "class:required:artifactReference:<Plugin.esp|nonzero-plugin-local-24-bit-form-id>::-", "combat-style:required:artifactReference:<Plugin.esp|nonzero-plugin-local-24-bit-form-id>::-",
                "default-outfit:required:artifactReference:<Plugin.esp|nonzero-plugin-local-24-bit-form-id>::-",
                "unique:optional:boolean:true|false:true|false:-", "essential:optional:boolean:true|false:true|false:-",
                "protected:optional:boolean:true|false:true|false:-", "respawns:optional:boolean:true|false:true|false:-",
                "auto-calc-stats:optional:boolean:true|false:true|false:-", "level:optional:integer:<0..32767>::-",
                "magicka-offset:optional:integer:<-32768..32767>::-", "stamina-offset:optional:integer:<-32768..32767>::-",
                "health-offset:optional:integer:<-32768..32767>::-", "calc-min-level:optional:integer:<0..32767;value<=calc-max-level>::-",
                "calc-max-level:optional:integer:<0..32767;value>=calc-min-level>::-", "speed:optional:integer:<1..32767>::-",
                "disposition:optional:integer:<-32768..32767>::-", "bleedout:optional:integer:<-32768..32767>::-",
                "base-health:optional:integer:<0..65535>::-", "base-magicka:optional:integer:<0..65535>::-",
                "base-stamina:optional:integer:<0..65535>::-", "height:optional:string:<finite-number-equal-to-1.0>::-",
                "weight:optional:string:<finite-0..100>::-", "nam5:optional:integer:<integer-equal-to-255>::-"
            ],
            ["npc create-from-preset"] =
            [
                "request:required:path:<@optional-K-local-request.json>::-",
                "request-sha256:required:sha256:<64-hex-sha256>::-"
            ],
            ["npc create-from-jslot"] =
            [
                "request:required:path:<@optional-K-local-request.json>::-", "request-sha256:required:sha256:<64-hex-sha256>::-",
                "preset:optional:path:<K-local-preset.jslot>::-", "preset-sha256:optional:sha256:<64-hex-sha256>::-",
                "data-root:optional:path:<K-local-data-root>::-", "plugins:optional:string:<plugin,...>::-",
                "companion-root:optional:path:<new-K-local-companion-root>::-", "preflight-output:optional:path:<new-preflight.json>::-",
                "face-bake-authority-output:optional:path:<new-face-bake-authority.json>::-",
                "reviewed-preflight:optional:path:<reviewed-preflight.json>::-", "reviewed-preflight-sha256:optional:sha256:<64-hex-sha256>::-",
                "migrated-request-root:optional:path:<new-K-local-migrated-root>::-", "provider-migration-output:optional:path:<new-provider-migration-review.json>::-",
                "reviewed-provider-migration:optional:path:<reviewed-provider-migration.json>::-", "reviewed-provider-migration-sha256:optional:sha256:<64-hex-sha256>::-",
                "provider-migration:optional:path:<reviewed-provider-migration.json>::reviewed-provider-migration", "provider-migration-sha256:optional:sha256:<64-hex-sha256>::reviewed-provider-migration-sha256"
            ]
        };
        foreach ((string name, string[] profile) in expectedOptions)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete,
                $"Task 6 contract stayed incomplete: {name}.");
            Assert(contract.Options.Select(Task6OptionProfile).SequenceEqual(profile, StringComparer.Ordinal),
                $"Task 6 independent option profile drifted: {name}.");
            Assert(contract.SupportedGames.SequenceEqual([GameEdition.SkyrimSpecialEdition]) &&
                   contract.Authority.Length == 8 && !contract.SupportsDryRun &&
                   contract.ResultShape == "object" && contract.ResultSchemaIds.IsEmpty,
                $"Task 6 full contract metadata is incomplete: {name}.");
        }

        AgentCommandContract direct = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc create");
        Assert(direct.Readiness == ProtocolReadiness.Legacy &&
               direct.InputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["blank-npc-provider-manifest", "template-plugin", "facegeom-carrier", "facetint-layer-manifest", "dependency-manifest"], StringComparer.Ordinal) &&
               direct.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["npc-plugin", "facegeom-nif", "facetint-dds", "npc-package-manifest"], StringComparer.Ordinal) &&
               direct.RetryPolicy == AgentRetryPolicy.RequiresFreshOutput &&
               direct.Determinism == AgentDeterminism.PinnedInputsAndTools &&
               direct.OptionRelationships.IsEmpty &&
               direct.Effects.Select(Task6EffectProfile).SequenceEqual(
                   ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], StringComparer.Ordinal) &&
               direct.Authority.Select(item => item.State).SequenceEqual(Task6Authority(verified: true)),
            "npc create full profile drifted.");
        Assert(direct.Options.Single(item => item.CliName == "level").Description.Contains("fixed", StringComparison.OrdinalIgnoreCase) &&
               DirectOptionDescriptionContains(direct, "calc-min-level", "less than or equal to --calc-max-level") &&
               DirectOptionDescriptionContains(direct, "calc-max-level", "greater than or equal to --calc-min-level") &&
               direct.Options.Single(item => item.CliName == "speed").Description.Contains("positive", StringComparison.OrdinalIgnoreCase) &&
               direct.Options.Single(item => item.CliName == "height").Description.Contains("exactly 1.0", StringComparison.OrdinalIgnoreCase) &&
               direct.Options.Single(item => item.CliName == "weight").Description.Contains("finite", StringComparison.OrdinalIgnoreCase) &&
               direct.Options.Single(item => item.CliName == "nam5").Description.Contains("exactly 255", StringComparison.OrdinalIgnoreCase) &&
               DirectOptionDescriptionContains(direct, "template-npc", "nonzero plugin-local 24-bit") &&
               DirectOptionDescriptionContains(direct, "race", "nonzero plugin-local 24-bit") &&
               DirectOptionDescriptionContains(direct, "voice", "nonzero plugin-local 24-bit") &&
               DirectOptionDescriptionContains(direct, "class", "nonzero plugin-local 24-bit") &&
               DirectOptionDescriptionContains(direct, "combat-style", "nonzero plugin-local 24-bit") &&
               DirectOptionDescriptionContains(direct, "default-outfit", "nonzero plugin-local 24-bit"),
            "npc create admission descriptions drifted from transaction validation.");

        AgentCommandContract preset = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc create-from-preset");
        Assert(preset.Readiness == ProtocolReadiness.Legacy &&
               preset.InputArtifacts.Select(ArtifactProfile).SequenceEqual(["npc-creation-request:" + RaceMenuNpcExecutionRequestSchemas.Request], StringComparer.Ordinal) &&
               preset.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["npc-plugin", "facegeom-nif", "facetint-dds", "npc-package-manifest", "bodygen-configuration"], StringComparer.Ordinal),
            "npc create-from-preset exposed document fields or omitted its package outputs.");
        Assert(preset.OptionRelationships.IsEmpty &&
               preset.Effects.Select(Task6EffectProfile).SequenceEqual(
                   ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], StringComparer.Ordinal) &&
               preset.Authority.Select(item => item.State).SequenceEqual(Task6Authority(verified: true)),
            "npc create-from-preset full mode profile drifted.");

        AgentCommandContract jslot = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc create-from-jslot");
        Assert(jslot.Readiness == ProtocolReadiness.Legacy &&
               jslot.InputArtifacts.Any(item => item.Kind == "npc-creation-request" &&
                   item.SchemaIds.SequenceEqual([RaceMenuNpcExecutionRequestSchemas.Request]) &&
                   item.Description.Contains("schemaVersion 1, 2, or 3", StringComparison.Ordinal)) &&
               jslot.InputArtifacts.Any(item => item.Kind == "npc-build-preflight" && item.SchemaIds.SequenceEqual([NpcBuildPreflightSchemas.Artifact])) &&
               jslot.InputArtifacts.Any(item => item.Kind == "provider-migration-review" && item.SchemaIds.SequenceEqual([ProviderMigrationSchemas.Review])) &&
               jslot.OutputArtifacts.Any(item => item.Kind == "npc-build-preflight" && item.SchemaIds.SequenceEqual([NpcBuildPreflightSchemas.Artifact])) &&
               jslot.OutputArtifacts.Any(item => item.Kind == "provider-migration-review" && item.SchemaIds.SequenceEqual([ProviderMigrationSchemas.Review])) &&
               jslot.OutputArtifacts.Any(item => item.Kind == "provider-migration-receipt" && item.SchemaIds.SequenceEqual([ProviderMigrationSchemas.Receipt])) &&
               jslot.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.RequiresTogether && item.Options.SequenceEqual(["reviewed-preflight", "reviewed-preflight-sha256"])) &&
               jslot.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.RequiresTogether && item.Options.SequenceEqual(["reviewed-provider-migration", "reviewed-provider-migration-sha256"])) &&
               jslot.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.RequiresTogether && item.Options.SequenceEqual(["provider-migration", "provider-migration-sha256"])),
            "npc create-from-jslot did not publish its complete V1 mode/alias/artifact union.");
        Assert(jslot.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(
            [
                "requiredWhen:preflight-output>face-bake-authority-output:all[face-bake-authority-output=noneOf(__absent__)/present]",
                "requiredWhen:preset,preset-sha256,data-root,plugins,companion-root>provider-migration-output,reviewed-provider-migration,reviewed-provider-migration-sha256,provider-migration,provider-migration-sha256:all[provider-migration-output=anyOf(__absent__)/absent;reviewed-provider-migration=anyOf(__absent__)/absent;reviewed-provider-migration-sha256=anyOf(__absent__)/absent;provider-migration=anyOf(__absent__)/absent;provider-migration-sha256=anyOf(__absent__)/absent]",
                "requiresTogether:reviewed-preflight,reviewed-preflight-sha256>:none",
                "forbiddenWhen:preflight-output>reviewed-preflight,reviewed-preflight-sha256:any[reviewed-preflight=noneOf(__absent__)/present;reviewed-preflight-sha256=noneOf(__absent__)/present]",
                "requiresTogether:reviewed-provider-migration,reviewed-provider-migration-sha256>:none",
                "requiresTogether:provider-migration,provider-migration-sha256>:none",
                "requiredWhen:migrated-request-root>provider-migration-output,reviewed-provider-migration,reviewed-provider-migration-sha256,provider-migration,provider-migration-sha256:any[provider-migration-output=noneOf(__absent__)/present;reviewed-provider-migration=noneOf(__absent__)/present;reviewed-provider-migration-sha256=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present;provider-migration-sha256=noneOf(__absent__)/present]",
                "forbiddenWhen:preset,preset-sha256,data-root,plugins,companion-root,preflight-output,face-bake-authority-output,reviewed-preflight,reviewed-preflight-sha256>provider-migration-output,reviewed-provider-migration,reviewed-provider-migration-sha256,provider-migration,provider-migration-sha256:any[provider-migration-output=noneOf(__absent__)/present;reviewed-provider-migration=noneOf(__absent__)/present;reviewed-provider-migration-sha256=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present;provider-migration-sha256=noneOf(__absent__)/present]",
                "forbiddenWhen:provider-migration-output>reviewed-provider-migration,reviewed-provider-migration-sha256,provider-migration,provider-migration-sha256:any[reviewed-provider-migration=noneOf(__absent__)/present;reviewed-provider-migration-sha256=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present;provider-migration-sha256=noneOf(__absent__)/present]",
                "forbiddenWhen:reviewed-provider-migration,reviewed-provider-migration-sha256>provider-migration,provider-migration-sha256:any[provider-migration=noneOf(__absent__)/present;provider-migration-sha256=noneOf(__absent__)/present]"
            ], StringComparer.Ordinal),
            "npc create-from-jslot typed mode relationships drifted.");
        Assert(jslot.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
            [
                $"npc-creation-request:schemas={RaceMenuNpcExecutionRequestSchemas.Request}:always", "racemenu-jslot:schemas=none:all[preset=noneOf(__absent__)/present]",
                "copied-data-root:schemas=none:all[data-root=noneOf(__absent__)/present]",
                $"npc-build-preflight:schemas={NpcBuildPreflightSchemas.Artifact}:all[reviewed-preflight=noneOf(__absent__)/present]",
                $"provider-migration-review:schemas={ProviderMigrationSchemas.Review}:any[reviewed-provider-migration=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present]"
            ], StringComparer.Ordinal) &&
            jslot.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
            [
                $"npc-build-preflight:schemas={NpcBuildPreflightSchemas.Artifact}:all[preflight-output=noneOf(__absent__)/present]",
                "skyrim-face-bake-authority:schemas=skyrim-face-bake-authority/1:all[face-bake-authority-output=noneOf(__absent__)/present]",
                $"provider-migration-review:schemas={ProviderMigrationSchemas.Review}:all[provider-migration-output=noneOf(__absent__)/present]",
                $"provider-migration-receipt:schemas={ProviderMigrationSchemas.Receipt}:any[reviewed-provider-migration=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present]",
                "racemenu-companion-assets:schemas=none:all[preset=noneOf(__absent__)/present;preflight-output=anyOf(__absent__)/absent]",
                "npc-package-manifest:schemas=none:all[preset=noneOf(__absent__)/present;preflight-output=anyOf(__absent__)/absent]"
            ], StringComparer.Ordinal) &&
            jslot.Effects.Select(Task6EffectProfile).SequenceEqual(
            [
                "readWorkspace:always", "writeNewArtifact:all[preflight-output=noneOf(__absent__)/present]",
                "writeNewArtifact:all[provider-migration-output=noneOf(__absent__)/present]",
                "writeNewArtifact:any[reviewed-provider-migration=noneOf(__absent__)/present;provider-migration=noneOf(__absent__)/present]",
                "writeNewArtifact:all[preset=noneOf(__absent__)/present;preflight-output=anyOf(__absent__)/absent]",
                "appendLocalOperationJournal:always"
            ], StringComparer.Ordinal) &&
            jslot.Authority.Select(item => item.State).SequenceEqual(Task6Authority(verified: false)),
            "npc create-from-jslot conditional artifacts/effects/authority drifted.");
        ImmutableArray<AgentCommandContract> legacyDiscoveryRegistry =
            AgentCommandRegistry.All.Select(item => item.Name == jslot.Name
                ? jslot
                : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(legacyDiscoveryRegistry).IsEmpty,
            "The independently composed V1 JSlot discovery contract did not validate.");

        foreach (string name in expectedOptions.Keys)
        {
            var (runner, output, error) = Program.CreateRunner();
            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
                [.. name.Split(' '), "--help", "--json"]), CancellationToken.None);
            using JsonDocument help = JsonDocument.Parse(output.ToString());
            string expected = JsonSerializer.Serialize(
                AgentCommandRegistry.GetLegacyDiscoveryRequired(name),
                CamelCaseJson);
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(help.RootElement.GetRawText())),
                $"V1 JSON help did not serialize the legacy Task 6 contract: {name}.");
        }

        CliBoundaryResult strict = await ProtocolV2TestHost.RunAsync(
            ["npc", "create-from-jslot", "--help", "--protocol", "2", "--json"]);
        using JsonDocument strictEnvelope = JsonDocument.Parse(strict.StandardOutput);
        JsonElement strictContract = strictEnvelope.RootElement.GetProperty("result").GetProperty("contract");
        Assert(strictContract.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("cliName").GetString()).SequenceEqual(
                   ["request", "request-sha256", "preset", "preset-sha256", "data-root", "plugins", "companion-root", "preflight-output", "face-bake-authority-output", "reviewed-preflight", "reviewed-preflight-sha256", "workflow-bundle", "workflow-bundle-sha256", "workflow-output"], StringComparer.Ordinal) &&
               !strictContract.GetProperty("options").EnumerateArray().Any(item => item.GetProperty("cliName").GetString() == "provider-migration-output"),
            "The strict V2 JSlot contract was changed or contaminated by V1 migration options.");
    }

    private static string Task6OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}:{option.AliasFor ?? "-"}";

    private static bool DirectOptionDescriptionContains(
        AgentCommandContract contract, string option, string fragment) =>
        contract.Options.Single(item => item.CliName == option).Description.Contains(
            fragment, StringComparison.OrdinalIgnoreCase);

    private static ImmutableArray<AgentAuthorityState> Task6Authority(bool verified) =>
    [
        AgentAuthorityState.Established, AgentAuthorityState.Established,
        AgentAuthorityState.Established,
        verified ? AgentAuthorityState.Established : AgentAuthorityState.Required,
        AgentAuthorityState.NotApplicable, AgentAuthorityState.Required,
        AgentAuthorityState.Required, AgentAuthorityState.Required
    ];

    private static string Task6ArtifactProfile(AgentArtifactContract artifact) =>
        $"{artifact.Kind}:schemas={(artifact.SchemaIds.IsEmpty ? "none" : string.Join('|', artifact.SchemaIds))}:{Task6TriggerProfile(artifact.Trigger)}";

    private static string Task6EffectProfile(AgentEffectContract effect) =>
        $"{ValueKindWireName(effect.Kind.ToString())}:{Task6TriggerProfile(effect.Trigger)}";

    private static string Task6RelationshipProfile(AgentOptionRelationshipContract relationship) =>
        $"{ValueKindWireName(relationship.Kind.ToString())}:{string.Join(',', relationship.Options)}>{string.Join(',', relationship.ReferencedOptions)}:" +
        (relationship.Trigger is null ? "none" : Task6TriggerProfile(relationship.Trigger));

    private static string Task6TriggerProfile(AgentOptionRelationshipTrigger? trigger) =>
        trigger is null ? "always" :
        $"{ValueKindWireName(trigger.Operator.ToString())}[{string.Join(';', trigger.Predicates.Select(predicate =>
            $"{predicate.Subject}={ValueKindWireName(predicate.Match.ToString())}({string.Join('|', predicate.Values)})/{(predicate.MatchesWhenAbsent ? "absent" : "present")}"))}]";

    private sealed record Task7Profile(
        ImmutableArray<string> Games,
        ImmutableArray<string> Relationships,
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        AgentDeterminism Determinism,
        ImmutableArray<AgentAuthorityState> Authority,
        string Result);

    private static async Task AssertTask7ContractsAsync()
    {
        string[] names =
        [
            "preview render", "render npc", "preview npc", "preview reroll",
            "preview export-nif", "plugin write", "plugin verify", "plugin audit",
            "plugin deploy"
        ];
        foreach (string name in names)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            AgentContractStatus expectedStatus = AgentContractStatus.Complete;
            Assert(contract.ContractStatus == expectedStatus &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty && contract.ResultShape == "object" &&
                   !contract.SupportsDryRun,
                $"Task 7 contract stayed incomplete or changed dialect: {name}.");
            ImmutableArray<string> expectedOptions = name switch
            {
                "preview render" or "render npc" => PreviewOptions(),
                "preview npc" => PreviewNpcOptions(),
                "preview reroll" => ["edition", "game", "manifest", "npc", "seed", "output"],
                "preview export-nif" => ["edition", "game", "scene", "output", "asset-root"],
                "plugin write" => ["edition", "game", "proposal", "plugin", "expected-sha256", "data-root", "private-root", "output", "no-overwrite"],
                "plugin verify" => PluginVerifyOptions(),
                "plugin audit" => ["edition", "game", "normalize-master-index", "before", "after", "plugins-root", "data-root", "load-order", "loadorder"],
                _ => ["edition", "game", "plugin", "input-plugin", "data-root", "expected-sha256", "sha256"]
            };
            Assert(contract.Options.Select(item => item.CliName).SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 7 option order drifted: {name}.");
            Assert(contract.Options.Select(item => Task7OptionProfile(name, item))
                    .SequenceEqual(expectedOptions.Select(option => ExpectedTask7OptionProfile(name, option)), StringComparer.Ordinal),
                $"Task 7 independent option profile drifted: {name}: {string.Join(';', contract.Options.Select(item => Task7OptionProfile(name, item)))}");
            Task7Profile expected = Task7Profiles[name];
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(expected.Games, StringComparer.Ordinal) &&
                   contract.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(expected.Relationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(expected.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == expected.Retry && contract.Determinism == expected.Determinism &&
                   contract.Authority.Select(item => item.State).SequenceEqual(expected.Authority) &&
                   contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.ResultDescription == expected.Result &&
                   contract.InputArtifactKinds.SequenceEqual(contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal),
                $"Task 7 independent full-mode profile drifted: {name}.");
        }

        AgentCommandContract preview = AgentCommandRegistry.GetLegacyDiscoveryRequired("preview render");
        AgentCommandContract alias = AgentCommandRegistry.GetLegacyDiscoveryRequired("render npc");
        Assert(alias.CanonicalCommand == "preview render" && preview.CanonicalCommand is null &&
               alias.Options.Select(item => Task7OptionProfile("preview render", item))
                   .SequenceEqual(preview.Options.Select(item => Task7OptionProfile("preview render", item)), StringComparer.Ordinal) &&
               alias.OptionRelationships.Select(Task6RelationshipProfile)
                   .SequenceEqual(preview.OptionRelationships.Select(Task6RelationshipProfile), StringComparer.Ordinal) &&
               alias.InputArtifacts.Select(Task6ArtifactProfile)
                   .SequenceEqual(preview.InputArtifacts.Select(Task6ArtifactProfile), StringComparer.Ordinal) &&
               alias.OutputArtifacts.Select(Task6ArtifactProfile)
                   .SequenceEqual(preview.OutputArtifacts.Select(Task6ArtifactProfile), StringComparer.Ordinal) &&
               alias.Effects.Select(Task6EffectProfile)
                   .SequenceEqual(preview.Effects.Select(Task6EffectProfile), StringComparer.Ordinal) &&
               alias.Authority.Select(item => $"{item.Kind}:{item.State}:{item.Reason}")
                   .SequenceEqual(preview.Authority.Select(item => $"{item.Kind}:{item.State}:{item.Reason}"), StringComparer.Ordinal),
            "render npc is not an exact structural projection of preview render.");
        Assert(preview.Options.Single(item => item.CliName == "visible").AllowedValues.SequenceEqual(
                   ["face", "body", "hair", "outfit", "underarmor", "armor", "headwear", "gore", "accessory", "all"], StringComparer.Ordinal) &&
               preview.Options.Single(item => item.CliName == "fps").ValueKind == AgentValueKind.String &&
               preview.Options.Single(item => item.CliName == "time").ValueKind == AgentValueKind.String,
            "Preview categories or floating-point time/FPS grammar drifted.");

        AgentCommandContract legacyNpc = AgentCommandRegistry.GetLegacyDiscoveryRequired("preview npc");
        AgentCommandContract strictNpc = AgentCommandRegistry.GetRequired("preview npc");
        Assert(legacyNpc.Readiness == ProtocolReadiness.Legacy &&
               strictNpc.Readiness == ProtocolReadiness.V2 &&
               legacyNpc.Options.Single(item => item.CliName == "package-manifest").Required == false &&
               strictNpc.Options.Single(item => item.CliName == "package-manifest").Required &&
               legacyNpc.Options.Single(item => item.CliName == "workflow-bundle-sha256").ValueKind == AgentValueKind.String &&
               legacyNpc.Options.Where(item => item.CliName.StartsWith("workflow-", StringComparison.Ordinal))
                   .All(item => item.Description.Contains("unconsumed", StringComparison.OrdinalIgnoreCase)),
            "V1 preview npc was tightened to the protocol-2 package/hash/workflow dialect.");

        AgentCommandContract verify = AgentCommandRegistry.GetLegacyDiscoveryRequired("plugin verify");
        Assert(verify.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                   item.Options.SequenceEqual(["proposal", "source-plugin"], StringComparer.Ordinal)) &&
               verify.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                   item.Options.SequenceEqual(["proposal", "output-plugin"], StringComparer.Ordinal)) &&
               verify.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                   item.Options.SequenceEqual(["proposal", "form-id", "npc"], StringComparer.Ordinal)) &&
               verify.Options.All(item => item.CliName != "preset-skin") &&
               verify.Options.Single(item => item.CliName == "level").ConflictsWith.SequenceEqual(["level-mult"], StringComparer.Ordinal) &&
               verify.Options.Single(item => item.CliName == "level-mult").ConflictsWith.SequenceEqual(["level"], StringComparer.Ordinal) &&
               verify.Options.Single(item => item.CliName == "weight").ConflictsWith.SequenceEqual(["weight-triangle"], StringComparer.Ordinal) &&
               verify.Options.Single(item => item.CliName == "weight-triangle").ConflictsWith.SequenceEqual(["weight"], StringComparer.Ordinal) &&
               verify.Options.Where(item => item.CliName is "set-flag" or "clear-flag")
                   .All(item => item.Description.Contains("empty token", StringComparison.Ordinal) &&
                                item.Description.Contains("no expectation", StringComparison.Ordinal)) &&
               verify.Limitations.Any(item =>
                   item.Contains("empty --set-flag or --clear-flag token",
                       StringComparison.Ordinal) &&
                   item.Contains("no direct verification expectation",
                       StringComparison.Ordinal)),
            "plugin verify did not publish both presence-selected dialects and the expectation alternative.");

        var (falseRunner, falseOutput, falseError) = Program.CreateRunner();
        CommandExitCode falseExit = await falseRunner.RunAsync(CommandLine.Parse(
        [
            "plugin", "verify", "--edition", "fallout4",
            "--source-plugin", "K:\\Actorwright\\source.esp",
            "--output-plugin", "K:\\Actorwright\\output.esp",
            "--form-id", "0x00000001", "--clear-skin", "false", "--json"
        ]), CancellationToken.None);
        Assert(falseExit == CommandExitCode.UsageError && falseOutput.ToString().Length == 0 &&
               falseError.ToString().Contains("plugin verify requires at least one expected", StringComparison.Ordinal),
            "Inactive --clear-skin false incorrectly satisfied the direct-dialect expectation requirement.");

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode jsonExit = await runner.RunAsync(CommandLine.Parse(
            ["plugin", "verify", "--help", "--json"]), CancellationToken.None);
        using JsonDocument jsonHelp = JsonDocument.Parse(output.ToString());
        Assert(jsonExit == CommandExitCode.Success && error.ToString().Length == 0 &&
               jsonHelp.RootElement.GetProperty("options").GetArrayLength() == PluginVerifyOptions().Length,
            "Task 7 representative V1 JSON help did not expose both plugin verify dialects.");
        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["preview", "render", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("underarmor", StringComparison.Ordinal) &&
               humanOutput.ToString().Contains("--asset-root", StringComparison.Ordinal),
            "Task 7 representative human help omitted the complete preview grammar.");
    }

    private static string Task7OptionProfile(string command, AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}:{option.AliasFor ?? "-"}:{(option.ConflictsWith.IsEmpty ? "-" : string.Join('|', option.ConflictsWith))}";

    private static string ExpectedTask7OptionProfile(string command, string option)
    {
        string family = command == "render npc" ? "preview render" : command;
        string profile = (family, option) switch
        {
            (_, "edition") => "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-",
            (_, "game") => "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
            ("preview render", "manifest") => "manifest:required:path:<preview-manifest.json>::-",
            ("preview render", "output") => "output:required:path:<new-preview-artifact.json>::-",
            ("preview render", "visible") => "visible:optional:enumList:<face,body,hair,outfit,underarmor,armor,headwear,gore,accessory|all>:face|body|hair|outfit|underarmor|armor|headwear|gore|accessory|all:-",
            ("preview render", "morphs") => "morphs:optional:enumList:<bone,vertex,weight,sculpt|all>:bone|vertex|weight|sculpt|all:-",
            ("preview render", "outfit") => "outfit:optional:artifactReference:<Plugin.esp|0xFormID>::-",
            ("preview render", "variant" or "camera" or "lighting" or "animation") => $"{option}:optional:identifier:<identifier>::-",
            ("preview render", "frame") => "frame:optional:integer:<non-negative-integer>::-",
            ("preview render", "time") => "time:optional:string:<finite-non-negative-seconds>::-",
            ("preview render", "fps") => "fps:optional:string:<finite-1..240>::-",
            ("preview render", "play" or "render-headwear") => $"{option}:optional:boolean:true|false:true|false:-",
            ("preview render", "asset-root") => "asset-root:optional:path:<K-local-data-root>::-",
            ("preview render", "image-output") => "image-output:optional:path:<new-png>::-",
            ("preview render", "width" or "height") => $"{option}:optional:integer:<64..2048>::-",
            ("preview render", "hair-slots") => "hair-slots:optional:string:<integer-0..255,...>::-",
            ("preview npc", "intake") => "intake:required:path:<reviewed-intake.json>::-",
            ("preview npc", "plugin") => "plugin:required:identifier:<plugin-name>::-",
            ("preview npc", "form") => "form:required:artifactReference:<nonzero-hexadecimal-form-id>::-",
            ("preview npc", "package-manifest") => "package-manifest:optional:path:<npc-package-manifest.json>::-",
            ("preview npc", "expected-package-sha256") => "expected-package-sha256:optional:sha256:<64-hex-sha256>::-",
            ("preview npc", "output-root") => "output-root:required:path:<new-K-local-output-root>::-",
            ("preview npc", "workflow-bundle") => "workflow-bundle:optional:path:<accepted-but-unconsumed-workflow-bundle.json>::-",
            ("preview npc", "workflow-bundle-sha256") => "workflow-bundle-sha256:optional:string:<accepted-but-unconsumed-string>::-",
            ("preview npc", "workflow-output") => "workflow-output:optional:path:<accepted-but-unconsumed-output-path>::-",
            ("preview reroll", "manifest") => "manifest:required:path:<preview-manifest.json>::-",
            ("preview reroll", "npc") => "npc:required:artifactReference:<hexadecimal-form-id>::-",
            ("preview reroll", "seed") => "seed:required:integer:<signed-64-bit-integer>::-",
            ("preview reroll", "output") => "output:required:path:<new-preview-reroll.json>::-",
            ("preview export-nif", "scene") => "scene:required:path:<preview-scene.json>::-",
            ("preview export-nif", "output") => "output:required:path:<new-*.nif.plan.json|new-*.nif>::-",
            ("preview export-nif", "asset-root") => "asset-root:optional:path:<K-local-asset-root>::-",
            ("plugin write", "proposal") => "proposal:required:path:<proposal.json>::-",
            ("plugin write", "plugin") => "plugin:optional:path:<K-local-plugin>::-",
            ("plugin write", "expected-sha256") => "expected-sha256:optional:sha256:<64-hex-sha256>::-",
            ("plugin write", "data-root" or "private-root") => $"{option}:optional:path:<K-local-data-root>::-",
            ("plugin write", "output") => "output:required:path:<new-plugin>::-",
            ("plugin write", "no-overwrite") => "no-overwrite:optional:boolean:true|1:true|1:-",
            ("plugin verify", "before" or "after" or "proposal" or "source-plugin" or "output-plugin") => $"{option}:optional:path:<K-local-{option}>::-",
            ("plugin verify", "form-id") => "form-id:optional:artifactReference:<hexadecimal-form-id>::-",
            ("plugin verify", "npc") => "npc:optional:artifactReference:<hexadecimal-form-id>::form-id",
            ("plugin verify", "sex") => "sex:optional:enum:female|male:female|male:-",
            ("plugin verify", "clear-skin") => "clear-skin:optional:boolean:true|false|1:true|false|1:-",
            ("plugin verify", "race" or "voice" or "class" or "combat-style" or "default-outfit" or "sleep-outfit") => $"{option}:optional:artifactReference:<Plugin.esp|0xFormID|none>::-",
            ("plugin verify", "skin") => "skin:optional:artifactReference:<Plugin.esp|0xFormID>::-",
            ("plugin verify", "magicka-offset" or "stamina-offset" or "health-offset" or "speed-multiplier" or "disposition" or "bleedout" or "xp-offset") => $"{option}:optional:integer:<signed-16-bit-integer>::-",
            ("plugin verify", "calc-min" or "calc-max" or "player-health" or "player-magicka" or "player-stamina") => $"{option}:optional:integer:<unsigned-16-bit-integer>::-",
            ("plugin verify", "geared-weapons") => "geared-weapons:optional:integer:<byte-0..255>::-",
            ("plugin verify", "level" or "level-mult") => $"{option}:optional:string:<invariant-decimal>::-",
            ("plugin verify", "height" or "far-model-distance" or "weight") => $"{option}:optional:string:<invariant-float>::-",
            ("plugin verify", "weight-triangle") => "weight-triangle:optional:string:thin=<n>,muscular=<n>,fat=<n>::-",
            ("plugin verify", "regions") => "regions:optional:json:<JSON-object|@K-local-file>::-",
            ("plugin verify", "whole-skin") => "whole-skin:optional:json:<JSON-object|@K-local-file>::-",
            ("plugin verify", "whole-skin-sha256") => "whole-skin-sha256:optional:sha256:<64-hex-sha256>::-",
            ("plugin verify", "aidt") => "aidt:optional:json:<JSON-object|@K-local-file>::-",
            ("plugin verify", "set-flag" or "clear-flag") => $"{option}:optional:enumList:<edition-compatible-flag,...> (empty string accepted but nonproducing):female|essential|ischargenfacepreset|respawn|autocalcstats|unique|doesntaffectstealthmeter|fallout4calcforeachtemplate|skyrimusetemplate|protected|summonable|doesnotbleed|bleedoutoverride|oppositegenderanims|simpleactor|fallout4noactivationorhellos|fallout4diffusealphatest|skyrimloopedscript|skyrimloopedaudio|isghost|invulnerable|pc-level-mult:-",
            ("plugin verify", "factions" or "add-faction" or "update-faction") => $"{option}:optional:string:<Plugin.esp|0xFormID=signed-byte-rank,...>::-",
            ("plugin verify", "inventory" or "add-inventory" or "update-inventory") => $"{option}:optional:string:<Plugin.esp|0xFormID=signed-32-bit-count,...>::-",
            ("plugin verify", "perks" or "add-perk" or "update-perk") => $"{option}:optional:string:<Plugin.esp|0xFormID=unsigned-byte-rank,...>::-",
            ("plugin verify", "properties" or "add-property" or "update-property") => $"{option}:optional:string:<Plugin.esp|0xFormID=invariant-float,...>::-",
            ("plugin verify", "skill-values" or "skill-offsets") => $"{option}:optional:string:<skill=byte,...>::-",
            ("plugin verify", "editor-id" or "name") => $"{option}:optional:{(option == "editor-id" ? "identifier" : "string")}:<non-empty-string>::-",
            ("plugin verify", _) => $"{option}:optional:string:<Plugin.esp|0xFormID,...>::-",
            ("plugin audit", "normalize-master-index") => "normalize-master-index:optional:boolean:true|1:true|1:-",
            ("plugin audit", "before" or "after") => $"{option}:required:path:<K-local-{option}-plugin>::-",
            ("plugin audit", "plugins-root") => "plugins-root:optional:path:<K-local-plugins-root>::-",
            ("plugin audit", "data-root") => "data-root:optional:path:<K-local-plugins-root>::plugins-root",
            ("plugin audit", "load-order") => "load-order:optional:path:<K-local-load-order.json>::-",
            ("plugin audit", "loadorder") => "loadorder:optional:path:<K-local-load-order.json>::load-order",
            ("plugin deploy", "plugin") => "plugin:optional:path:<K-local-plugin>::-",
            ("plugin deploy", "input-plugin") => "input-plugin:optional:path:<K-local-plugin>::plugin",
            ("plugin deploy", "data-root") => "data-root:required:path:<existing-K-local-copied-Data-root>::-",
            ("plugin deploy", "expected-sha256") => "expected-sha256:optional:sha256:<64-hex-sha256>::-",
            ("plugin deploy", "sha256") => "sha256:optional:sha256:<64-hex-sha256>::expected-sha256",
            _ => throw new InvalidOperationException($"Missing Task 7 option fixture: {command} --{option}")
        };
        string conflicts = (family, option) switch
        {
            ("preview render", "frame") => "time",
            ("preview render", "time") => "frame",
            ("plugin verify", "level") => "level-mult",
            ("plugin verify", "level-mult") => "level",
            ("plugin verify", "weight") => "weight-triangle",
            ("plugin verify", "weight-triangle") => "weight",
            _ => "-"
        };
        return $"{profile}:{conflicts}";
    }

    private static readonly ImmutableDictionary<string, Task7Profile> Task7Profiles =
        new Dictionary<string, Task7Profile>(StringComparer.Ordinal)
        {
            ["preview render"] = Task7PreviewRenderProfile(),
            ["render npc"] = Task7PreviewRenderProfile(),
            ["preview npc"] = new(["skyrimse"],
                ["requiresTogether:package-manifest,expected-package-sha256>:none"],
                ["reviewed-game-intake:schemas=npcmanager-reviewed-game-intake/2:always", "npc-package-manifest:schemas=none:all[package-manifest=noneOf(__absent__)/present]"],
                ["npc-preview-bundle:schemas=none:always"],
                ["readWorkspace:always", "invokeAdmittedProcess:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                Task7Authority("preview npc"),
                "Composed state, persisted preview bundle/scene versions, source route, off-engine label, contact-sheet path/hash, six view paths/hashes, and diagnostics; V1 workflow options are accepted but unconsumed and no grounded result schema identifier is published."),
            ["preview reroll"] = new(["fallout4", "skyrimse"],
                ["atLeastOne:edition,game>:none"],
                ["preview-scene-manifest:schemas=none:always"], ["preview-reroll-artifact:schemas=none:always"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                Task7Authority("preview reroll"),
                "Written state, semantic reroll artifact kind, NPC FormID, signed seed, candidate counts, selected variant/index/outfit, output SHA-256, and diagnostics; the SplitMix64 choice is reproducible and no grounded result schema identifier is published."),
            ["preview export-nif"] = new(["fallout4", "skyrimse"],
                ["atLeastOne:edition,game>:none"],
                ["preview-scene-artifact:schemas=none:always", "copied-asset-root:schemas=none:all[asset-root=noneOf(__absent__)/present]"],
                ["preview-nif-export-plan:schemas=none:all[asset-root=anyOf(__absent__)/absent]", "preview-nif-binary:schemas=none:all[asset-root=noneOf(__absent__)/present]"],
                ["readWorkspace:always", "invokeAdmittedProcess:all[asset-root=noneOf(__absent__)/present]", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                Task7Authority("preview export-nif"),
                "Written state plus plan-mode scene/NPC/asset metadata or binary-mode output path/hash/byte length/mesh/exporter/import/morph/armature/hair-zap/face-cull evidence and diagnostics; neither mode grants runtime authority and no grounded result schema identifier is published."),
            ["plugin write"] = new(["fallout4", "skyrimse"],
                ["atLeastOne:edition,game>:none"],
                ["npc-patch-proposal:schemas=npc.patch.proposal.v1|record-proposal.hdpt.v1:always"], ["written-plugin:schemas=none:always"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                Task7Authority("plugin write"),
                "Applied state, proposal/output paths, optional output SHA-256, selected changes, and diagnostics; output is always fresh and no grounded result schema identifier is published."),
            ["plugin verify"] = new(["fallout4", "skyrimse"], PluginVerifyRelationships(),
                ["plugin-verification-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]", "before-plugin:schemas=none:all[proposal=noneOf(__absent__)/present]", "after-plugin:schemas=none:all[proposal=noneOf(__absent__)/present]", "source-plugin:schemas=none:all[proposal=anyOf(__absent__)/absent]", "output-plugin:schemas=none:all[proposal=anyOf(__absent__)/absent]"],
                [], ["readWorkspace:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
                Task7Authority("plugin verify"),
                "Validity, observed typed changes, and diagnostics from either the proposal-bound before/after route or the direct mutation-expectation route; no grounded result schema identifier is published."),
            ["plugin audit"] = new(["fallout4", "skyrimse"],
                ["atLeastOne:edition,game>:none", "forbiddenWhen:plugins-root>plugins-root,load-order,loadorder:all[plugins-root=noneOf(__absent__)/present;load-order=anyOf(__absent__)/absent;loadorder=anyOf(__absent__)/absent]", "forbiddenWhen:data-root>data-root,load-order,loadorder:all[data-root=noneOf(__absent__)/present;load-order=anyOf(__absent__)/absent;loadorder=anyOf(__absent__)/absent]", "forbiddenWhen:load-order>load-order,plugins-root,data-root:all[load-order=noneOf(__absent__)/present;plugins-root=anyOf(__absent__)/absent;data-root=anyOf(__absent__)/absent]", "forbiddenWhen:loadorder>loadorder,plugins-root,data-root:all[loadorder=noneOf(__absent__)/present;plugins-root=anyOf(__absent__)/absent;data-root=anyOf(__absent__)/absent]"],
                ["before-plugin:schemas=none:always", "after-plugin:schemas=none:always", "copied-plugin-provider-context:schemas=none:any[plugins-root=noneOf(__absent__)/present;data-root=noneOf(__absent__)/present]"],
                [], ["readWorkspace:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
                Task7Authority("plugin audit"),
                "Validity, schemaVersion 1 surface counts, masters, risky signatures, record changes, optional provider resolutions, and diagnostics; no grounded result schema identifier is published."),
            ["plugin deploy"] = new(["fallout4", "skyrimse"],
                ["atLeastOne:edition,game>:none", "atLeastOne:plugin,input-plugin>:none", "atLeastOne:expected-sha256,sha256>:none"],
                ["hash-bound-plugin:schemas=none:always"], ["copied-data-plugin:schemas=none:always"],
                ["readWorkspace:always", "deployToCopiedData:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
                Task7Authority("plugin deploy"),
                "Deployed/alreadyPresent state, edition, source/destination paths and hashes, byte length, and diagnostics; an identical destination is idempotent, conflicts are refused, and no grounded result schema identifier is published.")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static Task7Profile Task7PreviewRenderProfile() => new(
        ["fallout4", "skyrimse"], PreviewRenderRelationships(),
        ["preview-scene-manifest:schemas=none:always", "copied-asset-root:schemas=none:all[asset-root=noneOf(__absent__)/present]"],
        ["preview-scene-artifact:schemas=none:always", "rendered-preview-png:schemas=none:all[image-output=noneOf(__absent__)/present]"],
        ["readWorkspace:always", "writeNewArtifact:always", "invokeAdmittedProcess:all[image-output=noneOf(__absent__)/present]", "writeNewArtifact:all[image-output=noneOf(__absent__)/present]", "appendLocalOperationJournal:always"],
        AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
        Task7Authority("preview render"),
        "Written state, semantic scene/variant/camera/lighting/animation/hair-zap evidence, optional off-engine PNG metadata, hashes, and diagnostics; no grounded result schema identifier is published.");

    private static async Task AssertTask8ContractsAsync()
    {
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task8OptionProfiles)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty && contract.ResultShape == "object" &&
                   !contract.SupportsDryRun,
                $"Task 8 contract stayed incomplete or changed dialect: {name}.");
            Assert(contract.Options.Select(Task8OptionProfile)
                    .SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 8 independent option profile drifted: {name}: {string.Join(';', contract.Options.Select(Task8OptionProfile))}");
            Task7Profile expected = Task8Profiles[name];
            Assert(contract.SupportedGames.Select(item => item.ToWireName())
                       .SequenceEqual(expected.Games, StringComparer.Ordinal) &&
                   contract.OptionRelationships.Select(Task6RelationshipProfile)
                       .SequenceEqual(expected.Relationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(expected.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(expected.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task6EffectProfile)
                       .SequenceEqual(expected.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == expected.Retry &&
                   contract.Determinism == expected.Determinism &&
                   contract.Authority.Select(item => item.Kind)
                       .SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Select(item => item.State)
                       .SequenceEqual(expected.Authority) &&
                   contract.ResultDescription == expected.Result &&
                   contract.InputArtifactKinds.SequenceEqual(
                       contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal),
                $"Task 8 independent full profile drifted: {name}.");
        }

        AgentCommandContract verify = AgentCommandRegistry.GetLegacyDiscoveryRequired("package verify");
        Assert(verify.Options.Single(item => item.CliName == "strict-install-dependencies") is
                   { ValueKind: AgentValueKind.Boolean, ValueSyntax: "true|false" } strict &&
               strict.AllowedValues.SequenceEqual(["true", "false"], StringComparer.Ordinal) &&
               verify.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(
               [
                   "atLeastOne:manifest,archive>:none",
                   "forbiddenWhen:manifest,strict-install-dependencies,data-root,plugins>archive:all[archive=noneOf(__absent__)/present]",
                   "requiredWhen:payload>archive:all[archive=noneOf(__absent__)/present]",
                   "forbiddenWhen:payload,include-runtime-preset>manifest:all[manifest=noneOf(__absent__)/present]",
                   "requiredWhen:data-root,plugins>strict-install-dependencies:all[strict-install-dependencies=anyOf(true)/present]",
                   "forbiddenWhen:data-root>data-root,strict-install-dependencies:all[data-root=noneOf(__absent__)/present;strict-install-dependencies=noneOf(true)/absent]",
                   "forbiddenWhen:plugins>plugins,strict-install-dependencies:all[plugins=noneOf(__absent__)/present;strict-install-dependencies=noneOf(true)/absent]",
                   "requiresTogether:workflow-bundle,workflow-bundle-sha256>:none"
               ], StringComparer.Ordinal),
            "package verify did not publish its exact typed strict-install mode predicates.");
        Assert(verify.Options.Single(item => item.CliName == "plugins").Description.Contains(
                   "limited to 64", StringComparison.Ordinal) &&
               verify.Options.Single(item => item.CliName == "plugins").Description.Contains(
                   "ordinary packages do not enter", StringComparison.Ordinal) &&
               verify.Limitations.Any(item => item.Contains(
                   "master-before-dependent", StringComparison.Ordinal)),
            "package verify did not distinguish external list/order enforcement from the ordinary strict branch.");

        AgentCommandContract propose = AgentCommandRegistry.GetLegacyDiscoveryRequired("runtime-script propose");
        Assert(propose.Options.Single(item => item.CliName == "appearance").Description.Contains(
                   "BoolValue, IntValue, FloatValue, StringValue, BoolArray, IntArray, FloatArray, or StringArray",
                   StringComparison.Ordinal) &&
               propose.Options.Single(item => item.CliName == "appearance").Description.Contains(
                   "numeric enum tokens", StringComparison.Ordinal) &&
               propose.Options.Single(item => item.CliName == "appearance").Description.Contains(
                   "not supported names", StringComparison.Ordinal),
            "runtime appearance discovery omitted the exact named types or accidental numeric admission.");
        AgentCommandContract write = AgentCommandRegistry.GetLegacyDiscoveryRequired("runtime-script write");
        foreach (AgentCommandContract contract in new[] { propose, write })
        {
            Assert(contract.Limitations.Contains(Task8Fo4PropertyProfile, StringComparer.Ordinal) &&
                   contract.Limitations.Contains(Task8SsePropertyProfile, StringComparer.Ordinal),
                $"{contract.Name} did not publish the independent literal property name/type profiles.");
        }
        AgentCommandContract build = AgentCommandRegistry.GetLegacyDiscoveryRequired("runtime-script build");
        Assert(build.Options.Single(item => item.CliName == "output").Description ==
                   "Fresh static build-evidence document; <output>.pex-inspect.json is a second retained fresh output, and preexistence of either path is refused." &&
               build.OutputArtifacts.Single(item => item.Kind == "runtime-script-pex-inspection-sidecar").Description ==
                   "Retained fresh <output>.pex-inspect.json emitted by the pinned PEX inspector; existing sidecars are refused." &&
               build.Effects.Count(item => item.Kind == AgentEffectKind.WriteNewArtifact) == 2 &&
               build.RetryPolicy == AgentRetryPolicy.RequiresFreshOutput,
            "runtime-script build omitted the retained fresh PEX inspection sidecar contract.");
        Assert(write.OutputArtifacts.Single(item => item.Kind == "vmad-plugin").Description ==
                   "Fresh source-bound plugin. The writer preserves unrelated non-reserved VMAD scripts by construction; output readback checks only selected-script existence and property count." &&
               write.Authority.Single(item => item.Kind == AgentAuthorityKind.IndependentStaticVerification).State ==
                   AgentAuthorityState.Required &&
               write.ResultDescription ==
                   "Written state, fresh plugin path, target FormID, output SHA-256 and diagnostics after readback confirms only selected-script existence and property count; property semantic equality and unrelated-VMAD preservation are not independently verified, and no runtime proof is published.",
            "runtime-script write overclaimed property semantics or unrelated-VMAD readback.");

        var (jsonRunner, jsonOutput, jsonError) = Program.CreateRunner();
        CommandExitCode jsonExit = await jsonRunner.RunAsync(CommandLine.Parse(
            ["package", "verify", "--help", "--json"]), CancellationToken.None);
        using JsonDocument jsonHelp = JsonDocument.Parse(jsonOutput.ToString());
        Assert(jsonExit == CommandExitCode.Success && jsonError.ToString().Length == 0 &&
               jsonHelp.RootElement.GetProperty("options").EnumerateArray()
                   .Single(option => option.GetProperty("cliName").GetString() == "strict-install-dependencies")
                   .GetProperty("valueSyntax").GetString() == "true|false",
            "Task 8 representative V1 JSON help omitted the strict package-verify dialect.");

        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["runtime-script", "propose", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("--appearance", StringComparison.Ordinal) &&
               humanOutput.ToString().Contains("BoolValue", StringComparison.Ordinal),
            "Task 8 representative human help omitted the appearance grammar.");
    }

    private static string Task8OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}:{option.AliasFor ?? "-"}";

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task8OptionProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["package build"] = ["source-root:required:path:<verified-K-local-package-root>::-", "output-root:required:path:<new-K-local-package-root>::-"],
            ["package inspect"] = ["manifest:required:path:<K-local-npcmanager-package.json>::-"],
            ["package verify"] = ["manifest:optional:path:<K-local-npcmanager-package.json>::-", "archive:optional:path:<K-local-runtime.zip>::-", "payload:optional:enum:runtime-only:runtime-only:-", "include-runtime-preset:optional:path:<Data-relative-SKSE-or-F4SE-preset-path>::-", "strict-install-dependencies:optional:boolean:true|false:true|false:-", "data-root:optional:path:<absolute-K-local-copied-Data-root>::-", "plugins:optional:string:<plugin,...>::-", "workflow-bundle:optional:path:<reviewed-finish-workflow.json>::-", "workflow-bundle-sha256:optional:sha256:<sha256>::-"],
            ["package archive"] = ["source-root:optional:path:<verified-K-local-package-root>::-", "package:optional:path:<verified-K-local-package-root>::source-root", "payload:optional:enum:full|runtime-only:full|runtime-only:-", "include-runtime-preset:optional:path:<Data-relative-SKSE-or-F4SE-preset-path>::-", "output:required:path:<new-K-local-install.zip>::-", "workflow-bundle:optional:path:<reviewed-finish-workflow.json>::-", "workflow-bundle-sha256:optional:sha256:<sha256>::-"],
            ["runtime-script propose"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "npc:required:artifactReference:<hexadecimal-form-id>::-", "appearance:required:json:<inline-JSON|@K-local-file>::-", "plugin:optional:path:<K-local-source-plugin>::-", "output:required:path:<new-runtime-script-proposal.json>::-"],
            ["runtime-script build"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "source-root:required:path:<K-local-runtime-script-source-root>::-", "output:required:path:<new-runtime-script-build.json>::-"],
            ["runtime-script write"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "source:required:path:<K-local-source-plugin>::-", "proposal:required:path:<runtime-script-proposal.json>::-", "output:required:path:<new-plugin.esp>::-"],
            ["runtime-script package"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "source-root:required:path:<K-local-runtime-script-source-root>::-", "output-root:required:path:<new-K-local-package-root>::-"],
            ["runtime-script deploy"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "package:required:path:<runtime-script-package-manifest.json>::-", "data-root:required:path:<existing-K-local-copied-Data-root>::-"],
            ["runtime-script inspect-vmad"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition", "plugin:required:path:<K-local-plugin>::-", "npc:required:artifactReference:<hexadecimal-form-id>::-", "script:optional:identifier:<script-name>::-"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, Task7Profile> Task8Profiles =
        new Dictionary<string, Task7Profile>(StringComparer.Ordinal)
        {
            ["package build"] = new(["fallout4", "skyrimse"], [], ["verified-package-root:schemas=none:always"], ["rebuilt-package-root:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, copied package-root identity, output artifact inventory and diagnostics after source verification and output byte readback; no grounded result schema identifier or runtime proof is published."),
            ["package inspect"] = new(["fallout4", "skyrimse"], [], ["package-manifest:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Validity, manifest identity fields, declared artifact paths/sizes/hashes and diagnostics without reading artifact bytes; no grounded result schema identifier is published."),
            ["package verify"] = new(["fallout4", "skyrimse"], ["atLeastOne:manifest,archive>:none", "forbiddenWhen:manifest,strict-install-dependencies,data-root,plugins>archive:all[archive=noneOf(__absent__)/present]", "requiredWhen:payload>archive:all[archive=noneOf(__absent__)/present]", "forbiddenWhen:payload,include-runtime-preset>manifest:all[manifest=noneOf(__absent__)/present]", "requiredWhen:data-root,plugins>strict-install-dependencies:all[strict-install-dependencies=anyOf(true)/present]", "forbiddenWhen:data-root>data-root,strict-install-dependencies:all[data-root=noneOf(__absent__)/present;strict-install-dependencies=noneOf(true)/absent]", "forbiddenWhen:plugins>plugins,strict-install-dependencies:all[plugins=noneOf(__absent__)/present;strict-install-dependencies=noneOf(true)/absent]", "requiresTogether:workflow-bundle,workflow-bundle-sha256>:none"], ["package-manifest:schemas=none:all[manifest=noneOf(__absent__)/present]", "copied-install-context:schemas=none:all[strict-install-dependencies=anyOf(true)/present]", "runtime-install-archive:schemas=none:all[archive=noneOf(__absent__)/present]"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Manifest mode reports source-bound byte/hash/size/closure evidence and optional strict install authority. Archive mode reports runtime layout and streamed hash inventory with sourceManifestBound=false, installDependencyAuthority=false and runtimeProof=false; readable byte changes cannot be authenticated without expected source authority."),
            ["package archive"] = new(["fallout4", "skyrimse"], ["atLeastOne:source-root,package>:none", "requiresTogether:workflow-bundle,workflow-bundle-sha256>:none", "requiredWhen:payload>include-runtime-preset:all[include-runtime-preset=noneOf(__absent__)/present]"], ["verified-package-root:schemas=none:always"], ["deterministic-install-archive:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, deterministic no-wrapper ZIP path/hash/entries and independent source-plan readback. Default full includes runtime instructions; optional runtime-only emits admitted install files without evidence or generated instructions. No deployment or runtime proof."),
            ["runtime-script propose"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["runtime-appearance-document:schemas=none:always", "source-plugin:schemas=none:all[plugin=noneOf(__absent__)/present]"], ["runtime-script-proposal:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, typed proposal artifact, source/emitter hash binding, output SHA-256 and diagnostics; schemaVersion and artifactKind values are not promoted to formal schema identifiers."),
            ["runtime-script build"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["runtime-script-source-root:schemas=none:always"], ["runtime-script-build-evidence:schemas=none:always", "runtime-script-pex-inspection-sidecar:schemas=none:always"], ["readWorkspace:always", "invokeAdmittedProcess:always", "writeNewArtifact:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, pinned PSC/PEX/API/compiler-manifest hashes, retained fresh <output>.pex-inspect.json path/hash, output SHA-256 and diagnostics; preexisting evidence or sidecar paths are refused, and valid PEX evidence is static rather than runtime proof."),
            ["runtime-script write"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["source-plugin:schemas=none:always", "runtime-script-proposal:schemas=none:always"], ["vmad-plugin:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, fresh plugin path, target FormID, output SHA-256 and diagnostics after readback confirms only selected-script existence and property count; property semantic equality and unrelated-VMAD preservation are not independently verified, and no runtime proof is published."),
            ["runtime-script package"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["runtime-script-source-root:schemas=none:always"], ["runtime-script-package:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Written state, fresh package root, game-specific PEX source/install paths and hashes, manifest SHA-256 and diagnostics; packaging does not establish PEX validity or runtime proof."),
            ["runtime-script deploy"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["runtime-script-package:schemas=none:always"], ["copied-data-runtime-script:schemas=none:always"], ["readWorkspace:always", "deployToCopiedData:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Installed/alreadyPresent state, copied Data root, destination path/hash and diagnostics; identical bytes are idempotent, conflicts are refused, and deployment is not runtime proof."),
            ["runtime-script inspect-vmad"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["vmad-plugin:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required], "Resolved state, copied plugin hash, target NPC, selected/default game-specific script, property names/count, no-write and runtime-proof flags, and diagnostics; inspection is read-only and static.")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private const string Task8Fo4PropertyProfile =
        "Fallout 4 property profile for NPCM_Manolov_ApplyFO4: IsFemale=bool; SchemaVersion=int; OvlTemplate=string[]; OvlPriority=int[]; OvlRed=float[]; OvlGreen=float[]; OvlBlue=float[]; OvlAlpha=float[]; OvlOffsetU=float[]; OvlOffsetV=float[]; OvlScaleU=float[]; OvlScaleV=float[]; SkinTemplate=string.";

    private const string Task8SsePropertyProfile =
        "Skyrim SE property profile for NPCM_Manolov_ApplySSE: IsFemale=bool; SchemaVersion=int; OvlNode=string[]; OvlDiffuse=string[]; OvlNormal=string[]; OvlHasTint=bool[]; OvlTint=int[]; OvlHasAlpha=bool[]; OvlAlpha=float[]; SkinSlot=int[]; SkinDiffuse=string[]; SkinNormal=string[]; SkinHasTint=bool[]; SkinTint=int[]; NodeName=string[]; NodeHasScale=bool[]; NodeScale=float[]; NodeHasPos=bool[]; NodePosX=float[]; NodePosY=float[]; NodePosZ=float[]; NodeHasRot=bool[]; NodeRotM0=float[]; NodeRotM1=float[]; NodeRotM2=float[]; NodeRotM3=float[]; NodeRotM4=float[]; NodeRotM5=float[]; NodeRotM6=float[]; NodeRotM7=float[]; NodeRotM8=float[]; NodeScaleMode=int[].";

    private sealed record Task10Profile(
        ImmutableArray<string> Games,
        ImmutableArray<string> Relationships,
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        ImmutableArray<AgentAuthorityState> Authority,
        string Result);

    private static async Task AssertTask10ContractsAsync()
    {
        foreach ((string name, ImmutableArray<string> options) in Task10Options)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty && contract.ResultShape == "object" &&
                   contract.Determinism == AgentDeterminism.Deterministic && !contract.SupportsDryRun,
                $"Task 10 contract stayed incomplete or changed protocol vocabulary: {name}.");
            Assert(contract.Options.Select(Task9OptionProfile).SequenceEqual(options, StringComparer.Ordinal),
                $"Task 10 independent option profile drifted: {name}: {string.Join(';', contract.Options.Select(Task9OptionProfile))}");
            Task10Profile expected = Task10Profiles[name];
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(expected.Games, StringComparer.Ordinal) &&
                   contract.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(expected.Relationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(expected.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == expected.Retry &&
                   contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Select(item => item.State).SequenceEqual(expected.Authority) &&
                   contract.ResultDescription == expected.Result &&
                   contract.InputArtifactKinds.SequenceEqual(contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal),
                $"Task 10 independent full profile drifted: {name}.");
        }

        AgentCommandContract propose = AgentCommandRegistry.GetLegacyDiscoveryRequired("object-template propose");
        AgentCommandContract write = AgentCommandRegistry.GetLegacyDiscoveryRequired("object-template write");
        Assert(Task6ArtifactProfile(propose.OutputArtifacts.Single(item => item.Kind == "object-template-combinations-proposal")) ==
                   "object-template-combinations-proposal:schemas=none:all[properties=anyOf(__absent__)/absent]" &&
               Task6ArtifactProfile(propose.OutputArtifacts.Single(item => item.Kind == "object-template-properties-proposal")) ==
                   "object-template-properties-proposal:schemas=none:all[properties=noneOf(__absent__)/present]" &&
               propose.Limitations.Contains(Task10ObjectCombinationDialect, StringComparer.Ordinal) &&
               propose.Limitations.Contains(Task10ObjectPropertyDialect, StringComparer.Ordinal) &&
               propose.Limitations.Contains(Task10ObjectProposePathAdmissionLimitation, StringComparer.Ordinal) &&
               !propose.Limitations.Contains(Task10ObjectWritePathAdmissionLimitation, StringComparer.Ordinal) &&
               write.Limitations.Contains(Task10ObjectWritePathAdmissionLimitation, StringComparer.Ordinal) &&
               !write.Limitations.Contains(Task10ObjectProposePathAdmissionLimitation, StringComparer.Ordinal) &&
               propose.Authority.Single(item => item.Kind == AgentAuthorityKind.InputAdmission) ==
                   new AgentAuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Required, Task10ObjectProposeInputAdmissionReason) &&
               write.Authority.Single(item => item.Kind == AgentAuthorityKind.InputAdmission) ==
                   new AgentAuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Required, Task10ObjectWriteInputAdmissionReason),
            "Object-template discovery lost properties-presence precedence or exact JSON dialects.");

        AgentCommandContract copy = AgentCommandRegistry.GetLegacyDiscoveryRequired("appearance copy");
        Assert(copy.Options.Single(item => item.CliName == "sections").AllowedValues
                   .SequenceEqual(Task10Sections, StringComparer.Ordinal) &&
               copy.Limitations.Contains(Task10AppearanceExpansion, StringComparer.Ordinal),
            "Appearance copy lost its literal section list or game-specific all expansion.");

        AgentCommandContract v1Inspect = AgentCommandRegistry.GetLegacyDiscoveryRequired("preset inspect");
        AgentCommandContract v2Inspect = AgentCommandRegistry.GetRequired("preset inspect");
        Assert(v1Inspect.Options.Select(item => item.CliName).SequenceEqual(["format", "edition", "input"], StringComparer.Ordinal) &&
               v1Inspect.OutputArtifacts.IsEmpty && v1Inspect.RetryPolicy == AgentRetryPolicy.SafeUnchanged &&
               v2Inspect.Readiness == ProtocolReadiness.V2 &&
               v2Inspect.Options.Select(item => item.CliName).SequenceEqual(
                   ["format", "edition", "input", "input-sha256", "inspection-output", "workflow-bundle", "workflow-bundle-sha256", "workflow-output"], StringComparer.Ordinal) &&
               v2Inspect.ResultSchemaIds.SequenceEqual([AgentProtocolSchemaIds.PresetInspectResult], StringComparer.Ordinal),
            "V1 preset inspect discovery overwrote or inherited the strict V2 contract.");

        foreach (string name in Task10Options.Keys.Where(item => item != "preset inspect"))
        {
            CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync([.. name.Split(' '), "--protocol", "2", "--json"]);
            Assert(refused.ExitCode != 0 && refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
                $"Task 10 strict protocol-2 invocation did not refuse {name}.");
        }

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(["object-template", "propose", "--help"]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
               output.ToString().Contains("--properties", StringComparison.Ordinal) &&
               output.ToString().Contains("--combinations", StringComparison.Ordinal),
            "Representative Task 10 human help omitted object-template branches.");
    }

    private static ImmutableArray<string> Task10PresetPair() =>
    [
        "format:required:enum:looksmenu|racemenu-jslot|jslot:looksmenu|racemenu-jslot|jslot:-:conflicts=",
        "edition:required:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts="
    ];

    private static ImmutableArray<string> Task10PresetInput() =>
        [.. Task10PresetPair(), "input:required:path:<K-local-preset.json>::-:conflicts="];

    private static readonly ImmutableArray<string> Task10Sections =
        ["body-weight", "body-regions", "body-sliders", "overlays", "skin-override", "lm-skin-template", "outfit", "face-parts", "hair-color", "face-tints", "face-morphs", "face-bone-regions", "sculpt", "chargen-flag", "all"];

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task10Options =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["object-template propose"] = ["edition:optional:enum:fallout4:fallout4:-:conflicts=", "game:optional:enum:fallout4:fallout4:edition:conflicts=", "plugin:required:path:<K-local-plugin>::-:conflicts=", "source:required:artifactReference:<non-null-hexadecimal-form-id>::-:conflicts=", "combinations:optional:json:<inline-JSON-object|@K-local-file>::-:conflicts=", "includes:optional:json:<inline-JSON-array|@K-local-file>::-:conflicts=", "properties:optional:json:<inline-JSON-array|@K-local-file>::-:conflicts=", "output:required:path:<name.object-template-proposal.json|name.object-template-properties-proposal.json>::-:conflicts="],
            ["object-template write"] = ["edition:optional:enum:fallout4:fallout4:-:conflicts=", "game:optional:enum:fallout4:fallout4:edition:conflicts=", "proposal:required:path:<name.object-template-proposal.json>::-:conflicts=", "properties:optional:path:<name.object-template-properties-proposal.json>::-:conflicts=", "output:required:path:<new-plugin.esp>::-:conflicts="],
            ["preset inspect"] = Task10PresetInput(),
            ["preset export"] = [.. Task10PresetInput(), "output:required:path:<new-preset.json>::-:conflicts="],
            ["preset diff"] = [.. Task10PresetPair(), "left:required:path:<K-local-left-preset.json>::-:conflicts=", "right:required:path:<K-local-right-preset.json>::-:conflicts="],
            ["preset resolve"] = ["identifier:required:artifactReference:<Plugin|FormID>::-:conflicts=", "load-order:required:path:<K-local-load-order.json>::-:conflicts=", "data-root:optional:path:<K-local-data-root>::-:conflicts="],
            ["appearance copy"] = [.. Task10PresetPair(), "from:required:path:<K-local-source-preset.json>::-:conflicts=", "to:required:path:<K-local-target-preset.json>::-:conflicts=", "output:required:path:<new-preset.json>::-:conflicts=", $"sections:required:enumList:<section,...|all>:{string.Join('|', Task10Sections)}:-:conflicts="]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableArray<string> Task10PairRelationships =
        ["forbiddenWhen:format,edition>format,edition:all[format=anyOf(looksmenu)/present;edition=anyOf(skyrimse)/present]", "forbiddenWhen:format,edition>format,edition:all[format=anyOf(racemenu-jslot|jslot)/present;edition=anyOf(fallout4)/present]"];
    private static readonly ImmutableArray<AgentAuthorityState> Task10ObjectProposalAuthority = [AgentAuthorityState.Required, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task10ObjectWriterAuthority = [AgentAuthorityState.Required, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task10ProposalAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task10ReadAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task10InspectAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable];

    private static readonly ImmutableDictionary<string, Task10Profile> Task10Profiles =
        new Dictionary<string, Task10Profile>(StringComparer.Ordinal)
        {
            ["object-template propose"] = new(["fallout4"], ["atLeastOne:edition,game>:none", "requiredWhen:combinations,includes>properties:all[properties=anyOf(__absent__)/absent]"], ["object-template-source-plugin:schemas=none:always", "object-template-combinations-and-includes:schemas=none:all[properties=anyOf(__absent__)/absent]", "object-template-properties:schemas=none:all[properties=noneOf(__absent__)/present]"], ["object-template-combinations-proposal:schemas=none:all[properties=anyOf(__absent__)/absent]", "object-template-properties-proposal:schemas=none:all[properties=noneOf(__absent__)/present]"], ["readWorkspace:always", "writeNewArtifact:all[properties=anyOf(__absent__)/absent]", "writeNewArtifact:all[properties=noneOf(__absent__)/present]", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, Task10ObjectProposalAuthority, "Written state and diagnostics for exactly one Fallout 4 proposal branch: properties presence wins and returns typed OMOD property rows; otherwise combinations plus includes return a typed hash-bound OBTS proposal. Artifact kind/schemaVersion metadata is not a formal schema identifier."),
            ["object-template write"] = new(["fallout4"], ["atLeastOne:edition,game>:none"], ["object-template-combinations-proposal:schemas=none:always", "object-template-properties-proposal:schemas=none:all[properties=noneOf(__absent__)/present]"], ["object-template-plugin:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, Task10ObjectWriterAuthority, "Written state, fresh ordinary-plugin path, target ARMO FormID, output SHA-256, and diagnostics after materialization; the final OBTS/OMOD structure is not independently reparsed, and static, visual, game-runtime, and promotion verification remain separate."),
            ["preset inspect"] = new(["fallout4", "skyrimse"], Task10PairRelationships, ["preset-file:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, Task10InspectAuthority, "Format, edition, source SHA-256, validity, typed in-memory appearance, and diagnostics from one explicit preset; V1 writes no receipt, workflow, or artifact and publishes no formal result schema identifier."),
            ["preset export"] = new(["fallout4", "skyrimse"], Task10PairRelationships, ["preset-file:schemas=none:always"], ["preset-copy:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, Task10ProposalAuthority, "Written state, format, source SHA-256, fresh output SHA-256, and diagnostics for a deterministic typed preset reserialization; the output is not independently reopened and has no formal result schema identifier."),
            ["preset diff"] = new(["fallout4", "skyrimse"], Task10PairRelationships, ["preset-left-and-right:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, Task10ReadAuthority, "Equality, ordered typed field differences, and diagnostics from two explicit read-only preset inputs; no artifact, visual, runtime, or formal result-schema claim is published."),
            ["preset resolve"] = new(["fallout4", "skyrimse"], [], ["portable-preset-identifier-and-load-order:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, Task10ReadAuthority, "Portable identifier, optional resolved FormID, resolution state, and diagnostics from the exact --load-order document; no live profile, artifact, runtime, or formal result-schema claim is published."),
            ["appearance copy"] = new(["fallout4", "skyrimse"], Task10PairRelationships, ["preset-copy-source-and-target:schemas=none:always"], ["appearance-copied-preset:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, Task10ProposalAuthority, "Written state, format, edition, expanded selected sections, source/target SHA-256 values, fresh output SHA-256, and diagnostics; preset-only copying is not visual, game-runtime, or promotion authority and publishes no formal result schema identifier.")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private const string Task10ObjectCombinationDialect = "When --properties is absent, --combinations is a no-comments/no-trailing-commas, maximum-depth-16, duplicate-free case-sensitive inline or @file JSON object; @files must be lexically beneath the configured K root, exist, have a non-reparse leaf, and be at most 262144 bytes. Exact root fields are mode,editorId,targetFormId,items, where mode is new|override and items contains 1..4096 objects with only displayName,isDefault,isEditorOnly,parentCombinationIndex,levelMin,levelMax,minLevelForRanks,altLevelsPerTier,keywords. --includes has the same file/JSON bounds and is an array containing only {combinationIndex,mod,attachPointIndex,optional,dontUseAll}. Byte fields are 0..255; parentCombinationIndex is a nonnegative Int16 referring to an item; combinationIndex is a nonnegative Int32 referring to an item. New requires editorId and a nonzero plugin-local 24-bit targetFormId; override inherits EditorID and any targetFormId must equal source. Names are at most 4096 characters without controls; per-combination keywords/includes are at most 4096 unique non-null source/master references; levelMin may not exceed levelMax; at least one supported field must change.";
    private const string Task10ObjectPropertyDialect = "--properties presence wins over --combinations/--includes and selects a no-comments/no-trailing-commas, maximum-depth-12, duplicate-free case-sensitive inline or @file JSON array; @files must be lexically beneath the configured K root, exist, have a non-reparse leaf, and be at most 262144 bytes. It contains 1..4096 objects with only valueType,functionType,propertyIndex,value1Integer,value1Float,value1FormId,value2Integer,value2Float,stepValue,combinationIndex. valueType is IntType|FloatType|BoolType|StringType|FormIDInt|EnumType|FormIDFloat; functionType is byte, propertyIndex is UInt16, integer values are Int32, combinationIndex is 0..4095, and floating values are finite. At proposal time FormID types require a parsed source/master value1FormId and admit Plugin|0 when Plugin is the source or a master; the writer later refuses a zero FormID. Other types forbid value1FormId.";
    private const string Task10ObjectProposePathAdmissionLimitation = "Object-template propose @file checks establish lexical containment beneath the configured K root and reject a reparse-point leaf, but do not independently refuse or pin a junction or symbolic link in the parent chain; ordinary-path input admission therefore remains outstanding.";
    private const string Task10ObjectWritePathAdmissionLimitation = "Object-template write walks existing proposal, properties, and source path ancestry and refuses observed reparses, and evaluates output-parent ancestry. Those paths are not handle-pinned against a later swap, and the proposal/property documents are not admitted by an independent expected hash, so input admission remains outstanding.";
    private const string Task10ObjectProposeInputAdmissionReason = "Parent-chain junctions or symbolic links are not independently refused or pinned by object-template propose @file checks, so ordinary-path input admission remains required.";
    private const string Task10ObjectWriteInputAdmissionReason = "Observed reparses in existing proposal, properties, and source ancestry are refused, but the paths are not handle-pinned against a later swap and proposal/property documents lack independent expected-hash admission.";
    private const string Task10AppearanceExpansion = "Sections are a nonempty comma-separated case-insensitive list. all expands for Fallout 4 to body-weight,body-regions,body-sliders,overlays,lm-skin-template,face-parts,hair-color,face-tints,face-morphs,face-bone-regions; for Skyrim SE it expands to body-weight,body-sliders,overlays,face-parts,hair-color,face-tints,face-morphs,sculpt. skin-override, outfit, and chargen-flag require an NPC record carrier and are refused by preset-file copying; game-incompatible sections fail visibly and duplicates are refused by the service.";

    private static async Task AssertTask11ContractsAsync()
    {
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task11OptionProfiles)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty &&
                   contract.ResultShape == "object",
                $"Task 11 contract stayed incomplete or invented a result schema: {name}.");
            Assert(contract.Options.Select(Task9OptionProfile)
                    .SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 11 independent option profile drifted: {name}: {string.Join(';', contract.Options.Select(Task9OptionProfile))}");
            Assert(contract.Authority.Select(item => item.Kind)
                    .SequenceEqual(Enum.GetValues<AgentAuthorityKind>()),
                $"Task 11 omitted an authority state: {name}.");
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(
                       Task11SupportedGames[name], StringComparer.Ordinal),
                $"Task 11 independent supported-games profile drifted: {name}.");
            Assert(contract.Effects.All(item => item.Kind != AgentEffectKind.AppendLocalOperationJournal),
                $"Task 11 falsely published a local journal effect: {name}.");
            Assert(contract.InputArtifacts.Select(Task11ArtifactProfile).SequenceEqual(
                       Task11InputProfiles[name], StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task11ArtifactProfile).SequenceEqual(
                       Task11OutputProfiles[name], StringComparer.Ordinal) &&
                   contract.ResultDescription == Task11Results[name] &&
                   contract.Limitations[^1] == Task11Limitations[name],
                $"Task 11 independent artifacts/result/limitation profile drifted: {name}.");
        }

        AgentCommandContract list = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc list");
        AgentCommandContract search = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc search");
        AgentCommandContract inspect = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc inspect");
        Assert(list.CanonicalCommand is null && search.CanonicalCommand is null &&
               list.Options.Select(Task9OptionProfile).SequenceEqual(
                   search.Options.Select(Task9OptionProfile), StringComparer.Ordinal) &&
               !search.Options.Single(item => item.CliName == "search").Required &&
               inspect.Options.Single(item => item.CliName == "npc").Required &&
               inspect.Options.Single(item => item.CliName == "form-id").AliasFor is null &&
               inspect.Limitations.Any(item => item.Contains("overwritten", StringComparison.Ordinal)),
            "NPC inventory discovery lost distinct list/search identities or inspect overwrite semantics.");

        string[] inventoryRelationships =
        [
            "atLeastOne:edition,game>:none",
            "atLeastOne:data-root,plugin>:none"
        ];
        foreach (AgentCommandContract contract in new[] { list, search, inspect })
        {
            Assert(contract.OptionRelationships.Select(Task6RelationshipProfile)
                       .SequenceEqual(inventoryRelationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
                       ["copied-game-inventory:schemas=none:always"], StringComparer.Ordinal) &&
                   contract.OutputArtifacts.IsEmpty &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(
                       ["readWorkspace:always"], StringComparer.Ordinal) &&
                   contract.RetryPolicy == AgentRetryPolicy.SafeUnchanged &&
                   contract.Determinism == AgentDeterminism.EnvironmentDependent &&
                   contract.Authority.Select(item => item.State).SequenceEqual(
                       [AgentAuthorityState.Established, AgentAuthorityState.Established,
                        AgentAuthorityState.NotApplicable, AgentAuthorityState.Established,
                        AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable,
                        AgentAuthorityState.Required, AgentAuthorityState.NotApplicable]),
                $"Task 11 inventory full profile drifted: {contract.Name}.");
        }

        AgentCommandContract reset = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc reset");
        AgentCommandContract template = AgentCommandRegistry.GetLegacyDiscoveryRequired("npc materialize-template");
        Assert(reset.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(
                   ["atLeastOne:game,edition>:none", "atLeastOne:current-plugin,plugin>:none", "atLeastOne:npc,form-id>:none", "forbiddenWhen:dry-run,apply>dry-run,apply:all[dry-run=anyOf(true|1)/present;apply=anyOf(true|1)/present]", "requiredWhen:expected-sha256>apply:all[apply=anyOf(true|1)/present]"], StringComparer.Ordinal) &&
               template.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(
                   ["atLeastOne:game,edition>:none", "atLeastOne:input-plugin,plugin>:none", "forbiddenWhen:dry-run,apply>dry-run,apply:all[dry-run=anyOf(true|1)/present;apply=anyOf(true|1)/present]", "requiredWhen:expected-sha256>apply:all[apply=anyOf(true|1)/present]"], StringComparer.Ordinal) &&
               reset.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
                   ["npc-reset-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]", "npc-reset-plugin:schemas=none:all[apply=anyOf(true|1)/present]"], StringComparer.Ordinal) &&
               template.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
                   ["npc-template-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]", "npc-template-plugin:schemas=none:all[apply=anyOf(true|1)/present]"], StringComparer.Ordinal) &&
               new[] { reset, template }.All(contract =>
                   contract.RetryPolicy == AgentRetryPolicy.RequiresReanalysis &&
                   contract.Determinism == AgentDeterminism.Deterministic && contract.SupportsDryRun &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(
                       ["readWorkspace:always", "writeNewArtifact:all[proposal=noneOf(__absent__)/present]", "writeNewArtifact:all[apply=anyOf(true|1)/present]"], StringComparer.Ordinal) &&
                   contract.Authority.Select(item => item.State).SequenceEqual(
                       [AgentAuthorityState.Established, AgentAuthorityState.Required,
                        AgentAuthorityState.Required, AgentAuthorityState.Required,
                        AgentAuthorityState.NotApplicable, AgentAuthorityState.Required,
                        AgentAuthorityState.Required, AgentAuthorityState.Required])),
            "Task 11 reset/template conditional mode profile drifted.");

        AgentCommandContract catalog = AgentCommandRegistry.GetLegacyDiscoveryRequired("preset catalog");
        Assert(catalog.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(
                   ["requiresTogether:data-root,plugins,race,sex>:none", "requiredWhen:data-root,plugins,race,sex>compatible-only:all[compatible-only=noneOf(__absent__)/present]"], StringComparer.Ordinal) &&
               catalog.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
                   ["racemenu-preset-directory:schemas=none:always"], StringComparer.Ordinal) &&
               catalog.OutputArtifacts.IsEmpty &&
               catalog.Effects.Select(Task6EffectProfile).SequenceEqual(["readWorkspace:always"], StringComparer.Ordinal) &&
               catalog.RetryPolicy == AgentRetryPolicy.SafeUnchanged &&
               catalog.Determinism == AgentDeterminism.EnvironmentDependent &&
               catalog.Authority.Select(item => item.State).SequenceEqual(
                   [AgentAuthorityState.Established, AgentAuthorityState.Required,
                    AgentAuthorityState.NotApplicable, AgentAuthorityState.Established,
                    AgentAuthorityState.NotApplicable, AgentAuthorityState.Required,
                    AgentAuthorityState.Required, AgentAuthorityState.NotApplicable]),
            "Task 11 preset-catalog presence/quartet profile drifted.");

        foreach (string name in Task11OptionProfiles.Keys.Where(item =>
                     item.Contains("follower-finish", StringComparison.Ordinal) ||
                     item.Contains("placement interior", StringComparison.Ordinal)))
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            bool verify = name.EndsWith("verify", StringComparison.Ordinal);
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(["skyrimse"], StringComparer.Ordinal) &&
                   contract.OptionRelationships.IsEmpty && !contract.SupportsDryRun &&
                   contract.Determinism == AgentDeterminism.Deterministic &&
                   contract.RetryPolicy == (verify ? AgentRetryPolicy.SafeUnchanged : AgentRetryPolicy.RequiresFreshOutput) &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(
                       verify ? ["readWorkspace:always"] : ["readWorkspace:always", "writeNewArtifact:always"], StringComparer.Ordinal) &&
                   contract.Authority.Select(item => item.State).SequenceEqual(
                       verify
                           ? [AgentAuthorityState.Established, AgentAuthorityState.Established,
                              AgentAuthorityState.NotApplicable, AgentAuthorityState.Established,
                              AgentAuthorityState.NotApplicable, AgentAuthorityState.Required,
                              AgentAuthorityState.Required, AgentAuthorityState.Required]
                           : [AgentAuthorityState.Established, AgentAuthorityState.Established,
                              AgentAuthorityState.Established, AgentAuthorityState.Required,
                              AgentAuthorityState.NotApplicable, AgentAuthorityState.Required,
                              AgentAuthorityState.Required, AgentAuthorityState.Required]),
                $"Task 11 phase full profile drifted: {name}.");
            if (name.Contains("follower-finish", StringComparison.Ordinal))
                Assert(contract.InputArtifacts.All(item => item.SchemaIds.IsEmpty) &&
                       contract.OutputArtifacts.All(item => item.SchemaIds.IsEmpty),
                    $"Task 11 invented a follower/pair artifact schema: {name}.");
        }

        Assert(Task6ArtifactProfile(AgentCommandRegistry.GetLegacyDiscoveryRequired("npc placement interior analyze").InputArtifacts.Single()) ==
                   "interior-placement-request:schemas=npc.interior-placement.request.v1:always" &&
               Task6ArtifactProfile(AgentCommandRegistry.GetLegacyDiscoveryRequired("npc placement interior analyze").OutputArtifacts.Single()) ==
                   "interior-placement-proposal:schemas=npc.interior-placement.proposal.v1:always" &&
               AgentCommandRegistry.GetLegacyDiscoveryRequired("npc placement interior verify").InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(
                   ["interior-placement-manifest:schemas=npc.interior-placement.manifest.v1:always"], StringComparer.Ordinal),
            "Task 11 interior grounded artifact schemas drifted.");

        foreach (string name in Task11OptionProfiles.Keys)
        {
            CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
                [.. name.Split(' '), "--protocol", "2", "--json"]);
            Assert(refused.ExitCode != 0 &&
                   refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
                $"Task 11 strict protocol-2 invocation did not refuse {name}.");

            var (jsonRunner, jsonOutput, jsonError) = Program.CreateRunner();
            CommandExitCode jsonExit = await jsonRunner.RunAsync(CommandLine.Parse(
                [.. name.Split(' '), "--help", "--json"]), CancellationToken.None);
            using JsonDocument jsonHelp = JsonDocument.Parse(jsonOutput.ToString());
            Assert(jsonExit == CommandExitCode.Success && jsonError.ToString().Length == 0 &&
                   jsonHelp.RootElement.GetProperty("name").GetString() == name &&
                   jsonHelp.RootElement.GetProperty("options").EnumerateArray()
                       .Select(row =>
                           $"{row.GetProperty("cliName").GetString()}:" +
                           $"{(row.GetProperty("required").GetBoolean() ? "required" : "optional")}:" +
                           $"{row.GetProperty("valueKind").GetString()}:" +
                           $"{row.GetProperty("valueSyntax").GetString()}:" +
                           $"{string.Join('|', row.GetProperty("allowedValues").EnumerateArray().Select(item => item.GetString()))}:" +
                           $"{(row.TryGetProperty("aliasFor", out JsonElement alias) && alias.ValueKind != JsonValueKind.Null ? alias.GetString() : "-")}:" +
                           $"conflicts={string.Join('|', row.GetProperty("conflictsWith").EnumerateArray().Select(item => item.GetString()))}")
                       .SequenceEqual(Task11OptionProfiles[name], StringComparer.Ordinal),
                $"Task 11 scoped JSON help drifted: {name}.");

            var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
            CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
                [.. name.Split(' '), "--help"]), CancellationToken.None);
            string human = humanOutput.ToString();
            Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
                   human.Contains($"{name} — legacy command options", StringComparison.Ordinal) &&
                   AgentCommandRegistry.GetLegacyDiscoveryRequired(name).Options.All(option =>
                       human.Contains($"--{option.CliName} {option.ValueSyntax}", StringComparison.Ordinal)),
                $"Task 11 scoped human help drifted: {name}.");
        }

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
            ["npc", "follower-finish", "pair-verify", "--help"]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
               output.ToString().Contains("--manifest", StringComparison.Ordinal) &&
               !output.ToString().Contains("--manifest-sha256", StringComparison.Ordinal),
            "Task 11 scoped help did not preserve the paired verify manifest dialect.");
    }

    private static ImmutableArray<string> Task11InventoryOptions() =>
    [
        "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=",
        "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=",
        "data-root:optional:path:<K-local-copied-Data-root>::-:conflicts=",
        "plugin:optional:path:<K-local-plugin>::-:conflicts=",
        "plugins:optional:string:<plugin,...>::-:conflicts=",
        "search:optional:string:<search-text>::-:conflicts=",
        "form-id:optional:artifactReference:<hexadecimal-form-id>::-:conflicts=",
        "filter:optional:enumList:<unique,generic,template,unused>:unique|generic|template|unused:-:conflicts=",
        "category:optional:enumList:<unique,generic,template,unused>:unique|generic|template|unused:filter:conflicts=",
        "changed-only:optional:boolean:<value>::-:conflicts="
    ];

    private static ImmutableArray<string> Task11FollowerOptions(string phase) => phase switch
    {
        "analyze" => ["request:required:path:<request.json>::-:conflicts=", "request-sha256:required:sha256:<64-hex-sha256>::-:conflicts=", "proposal:required:path:<new-proposal.json>::-:conflicts="],
        "apply" => ["request:required:path:<request.json>::-:conflicts=", "request-sha256:required:sha256:<64-hex-sha256>::-:conflicts=", "proposal:required:path:<proposal.json>::-:conflicts=", "proposal-sha256:required:sha256:<64-hex-sha256>::-:conflicts="],
        "verify" => ["request:required:path:<request.json>::-:conflicts=", "request-sha256:required:sha256:<64-hex-sha256>::-:conflicts=", "proposal:required:path:<proposal.json>::-:conflicts=", "proposal-sha256:required:sha256:<64-hex-sha256>::-:conflicts=", "manifest:required:path:<manifest.json>::-:conflicts="],
        _ => throw new InvalidOperationException()
    };

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task11OptionProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc list"] = Task11InventoryOptions(),
            ["npc search"] = Task11InventoryOptions(),
            ["npc inspect"] = [.. Task11InventoryOptions(), "npc:required:artifactReference:<hexadecimal-form-id>::-:conflicts="],
            ["npc reset"] = ["game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:game:conflicts=", "current-plugin:optional:path:<K-local-current-plugin>::-:conflicts=", "plugin:optional:path:<K-local-current-plugin>::-:conflicts=", "baseline:required:path:<K-local-baseline-plugin>::-:conflicts=", "output:required:path:<new-plugin>::-:conflicts=", "section:required:enum:identity|archetype|weight|stats|keywords|factions|inventory|outfits|perks|actor-effects|properties:identity|archetype|weight|stats|keywords|factions|inventory|outfits|perks|actor-effects|properties:-:conflicts=", "npc:optional:artifactReference:<hexadecimal-form-id>::-:conflicts=", "form-id:optional:artifactReference:<hexadecimal-form-id>::-:conflicts=", "expected-sha256:optional:sha256:<64-hex-sha256>::-:conflicts=", "proposal:optional:path:<new-proposal.json>::-:conflicts=", "dry-run:optional:boolean:true|false|1:true|false|1:-:conflicts=", "apply:optional:boolean:true|false|1:true|false|1:-:conflicts="],
            ["npc materialize-template"] = ["game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:game:conflicts=", "input-plugin:optional:path:<K-local-input-plugin>::-:conflicts=", "plugin:optional:path:<K-local-input-plugin>::-:conflicts=", "output:required:path:<new-plugin>::-:conflicts=", "form-id:required:artifactReference:<hexadecimal-form-id>::-:conflicts=", "categories:optional:enumList:<category,...>:traits|stats|factions|spell-list|ai-data|ai-packages|model-animation|base-data|inventory|script|default-package-list|attack-data|keywords:-:conflicts=", "expected-sha256:optional:sha256:<64-hex-sha256>::-:conflicts=", "proposal:optional:path:<new-proposal.json>::-:conflicts=", "dry-run:optional:boolean:true|false|1:true|false|1:-:conflicts=", "apply:optional:boolean:true|false|1:true|false|1:-:conflicts="],
            ["npc follower-finish analyze"] = Task11FollowerOptions("analyze"),
            ["npc follower-finish apply"] = Task11FollowerOptions("apply"),
            ["npc follower-finish verify"] = Task11FollowerOptions("verify"),
            ["npc follower-finish pair-analyze"] = Task11FollowerOptions("analyze"),
            ["npc follower-finish pair-apply"] = Task11FollowerOptions("apply"),
            ["npc follower-finish pair-verify"] = Task11FollowerOptions("verify"),
            ["npc placement interior analyze"] = Task11FollowerOptions("analyze"),
            ["npc placement interior apply"] = Task11FollowerOptions("apply"),
            ["npc placement interior verify"] = ["manifest:required:path:<manifest.json>::-:conflicts=", "manifest-sha256:required:sha256:<64-hex-sha256>::-:conflicts="],
            ["preset catalog"] = ["directory:required:path:<K-local-preset-directory>::-:conflicts=", "filter:optional:string:<display-name-filter>::-:conflicts=", "data-root:optional:path:<K-local-copied-Data-root>::-:conflicts=", "plugins:optional:string:<plugin,...>::-:conflicts=", "race:optional:artifactReference:<Plugin|FormID>::-:conflicts=", "sex:optional:enum:female|male:female|male:-:conflicts=", "compatible-only:optional:boolean:<presence>::-:conflicts="]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static string Task11ArtifactProfile(AgentArtifactContract artifact) =>
        $"{Task6ArtifactProfile(artifact)}|{artifact.Description}";

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task11SupportedGames =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc list"] = ["fallout4", "skyrimse"],
            ["npc search"] = ["fallout4", "skyrimse"],
            ["npc inspect"] = ["fallout4", "skyrimse"],
            ["npc reset"] = ["fallout4", "skyrimse"],
            ["npc materialize-template"] = ["fallout4", "skyrimse"],
            ["npc follower-finish analyze"] = ["skyrimse"],
            ["npc follower-finish apply"] = ["skyrimse"],
            ["npc follower-finish verify"] = ["skyrimse"],
            ["npc follower-finish pair-analyze"] = ["skyrimse"],
            ["npc follower-finish pair-apply"] = ["skyrimse"],
            ["npc follower-finish pair-verify"] = ["skyrimse"],
            ["npc placement interior analyze"] = ["skyrimse"],
            ["npc placement interior apply"] = ["skyrimse"],
            ["npc placement interior verify"] = ["skyrimse"],
            ["preset catalog"] = ["skyrimse"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task11InputProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc list"] = ["copied-game-inventory:schemas=none:always|Explicit copied Data root or plugin-derived inventory root and optional plugin selection."],
            ["npc search"] = ["copied-game-inventory:schemas=none:always|Explicit copied Data root or plugin-derived inventory root and optional plugin selection."],
            ["npc inspect"] = ["copied-game-inventory:schemas=none:always|Explicit copied Data root or plugin-derived inventory root and optional plugin selection."],
            ["npc reset"] = ["npc-current-and-baseline-plugins:schemas=none:always|Explicit current and same-plugin baseline inputs for one NPC section."],
            ["npc materialize-template"] = ["npc-template-source-plugin:schemas=none:always|Explicit input plugin containing the target NPC and followed template chain."],
            ["npc follower-finish analyze"] = ["follower-finish-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 1 follower-finish request."],
            ["npc follower-finish apply"] = ["follower-finish-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 1 follower-finish request.", "follower-finish-proposal:schemas=none:always|Exact hash-bound numeric-private schemaVersion 1 follower-finish proposal."],
            ["npc follower-finish verify"] = ["follower-finish-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 1 follower-finish request.", "follower-finish-proposal:schemas=none:always|Exact hash-bound numeric-private schemaVersion 1 follower-finish proposal.", "follower-finish-manifest:schemas=none:always|Exact manifest consumed by post-write verification; it uses a private numeric schemaVersion and declares no public schema identifier."],
            ["npc follower-finish pair-analyze"] = ["follower-finish-pair-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 2 or 3 paired request."],
            ["npc follower-finish pair-apply"] = ["follower-finish-pair-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 2 or 3 paired request.", "follower-finish-pair-proposal:schemas=none:always|Exact hash-bound numeric-private schemaVersion 2 or 3 paired proposal."],
            ["npc follower-finish pair-verify"] = ["follower-finish-pair-request:schemas=none:always|Exact hash-bound numeric-private schemaVersion 2 or 3 paired request.", "follower-finish-pair-proposal:schemas=none:always|Exact hash-bound numeric-private schemaVersion 2 or 3 paired proposal.", "follower-finish-pair-manifest:schemas=none:always|Exact paired manifest consumed by verification; it uses private numeric schemaVersion 2 or 3 and declares no public schema identifier."],
            ["npc placement interior analyze"] = ["interior-placement-request:schemas=npc.interior-placement.request.v1:always|Exact hash-bound interior-placement request."],
            ["npc placement interior apply"] = ["interior-placement-request:schemas=npc.interior-placement.request.v1:always|Exact hash-bound interior-placement request.", "interior-placement-proposal:schemas=npc.interior-placement.proposal.v1:always|Exact hash-bound interior-placement proposal."],
            ["npc placement interior verify"] = ["interior-placement-manifest:schemas=npc.interior-placement.manifest.v1:always|Exact separately hash-bound interior-placement manifest."],
            ["preset catalog"] = ["racemenu-preset-directory:schemas=none:always|Explicit flat K-local RaceMenu JSlot directory and, conditionally, a copied provider closure."]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task11OutputProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc list"] = [], ["npc search"] = [], ["npc inspect"] = [],
            ["npc reset"] = ["npc-reset-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]|Fresh proposal when --proposal is present.", "npc-reset-plugin:schemas=none:all[apply=anyOf(true|1)/present]|Fresh verified output plugin when --apply is true or 1."],
            ["npc materialize-template"] = ["npc-template-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]|Fresh proposal when --proposal is present.", "npc-template-plugin:schemas=none:all[apply=anyOf(true|1)/present]|Fresh verified output plugin when --apply is true or 1."],
            ["npc follower-finish analyze"] = ["follower-finish-proposal:schemas=none:always|Fresh follower-finish proposal; no formal schema identifier is published."],
            ["npc follower-finish apply"] = ["follower-finish-package-and-archive:schemas=none:always|Fresh statically verified follower package and archive."],
            ["npc follower-finish verify"] = [],
            ["npc follower-finish pair-analyze"] = ["follower-finish-pair-proposal:schemas=none:always|Fresh paired proposal; no formal schema identifier is published."],
            ["npc follower-finish pair-apply"] = ["follower-finish-pair-package:schemas=none:always|Fresh deterministic two-plugin package and archive."],
            ["npc follower-finish pair-verify"] = [],
            ["npc placement interior analyze"] = ["interior-placement-proposal:schemas=npc.interior-placement.proposal.v1:always|Fresh interior-placement proposal."],
            ["npc placement interior apply"] = ["interior-placement-manifest:schemas=npc.interior-placement.manifest.v1:always|Fresh optional light-patch package manifest and archive."],
            ["npc placement interior verify"] = [], ["preset catalog"] = []
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> Task11Results =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npc list"] = "Edition, copied provider inventory, filtered NPC summaries, and diagnostics; no artifact, journal entry, formal result schema, runtime, or promotion claim is produced.",
            ["npc search"] = "Edition, copied provider inventory, filtered NPC summaries, and diagnostics; no artifact, journal entry, formal result schema, runtime, or promotion claim is produced.",
            ["npc inspect"] = "Schema-version-1 structured NPC summary, diagnostics, authenticated blank-npc-v1 inheritedDefaults equality list for Skyrim, and the explicit unsupported sections list; the required --npc value overwrites any parsed --form-id filter.",
            ["npc reset"] = "Applicable/applied state, section, input/output identities and hashes, changes, preserved fields, and diagnostics for proposal-only or active apply mode; no formal result schema identifier is published.",
            ["npc materialize-template"] = "Applicable/applied state, input/output identities and hashes, per-category evidence, changes, preserved fields, and diagnostics for proposal-only or active apply mode.",
            ["npc follower-finish analyze"] = "Proposal state/path/hash, allocation and static verdict with diagnostics; no follower response schema identifier or runtime authority is published.",
            ["npc follower-finish apply"] = "Completed state, package/plugin/archive evidence, static verdict and diagnostics; runtime authority remains false.",
            ["npc follower-finish verify"] = "Verified state, package/plugin evidence, static verdict and diagnostics from the distinct private-numeric manifest input; the command writes no artifact and runtime authority remains false.",
            ["npc follower-finish pair-analyze"] = "Succeeded state, verdict, proposal path/hash and diagnostics for the exact paired request; no pair response schema identifier is published.",
            ["npc follower-finish pair-apply"] = "Succeeded state, verdict, manifest/plugin/ZIP paths and hashes, runtimeAuthority=false, and diagnostics for the reviewed paired transaction.",
            ["npc follower-finish pair-verify"] = "Succeeded state, verdict, manifest/plugin/ZIP evidence, runtimeAuthority=false, and diagnostics from the distinct private-numeric paired manifest input; the command writes no artifact.",
            ["npc placement interior analyze"] = "Proposed state, status, proposal path/hash, diagnostics, and response schema discriminator npc.interior-placement.proposal.v1; the fresh proposal is the only output artifact.",
            ["npc placement interior apply"] = "Applied state, status, output root, archive, manifest path, diagnostics, and response schema discriminator npc.interior-placement.manifest.v1.",
            ["npc placement interior verify"] = "Verified state, status, typed verification object, diagnostics, and response schema discriminator npc.interior-placement.verification.v1; no persistent verification artifact is written and no registered protocol result schema is claimed.",
            ["preset catalog"] = "Accepted state, directory/filter, presence-active compatible-only state, conditional target authority, admitted hashed entries, omitted count and diagnostics; no formal result schema is published."
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> Task11Limitations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npc list"] = "List and search share one inventory handler but remain distinct command names, not aliases. Search terms are optional. Explicit --data-root wins over a --plugin-derived root; --plugins wins over the plugin-derived filename. Only the literal true activates --changed-only.",
            ["npc search"] = "List and search share one inventory handler but remain distinct command names, not aliases. Search terms are optional. Explicit --data-root wins over a --plugin-derived root; --plugins wins over the plugin-derived filename. Only the literal true activates --changed-only.",
            ["npc inspect"] = "The handler requires --npc. The shared optional --form-id is parsed first, then its filter is overwritten by the parsed --npc value. The two options are not aliases.",
            ["npc reset"] = "One mixed proposal/apply command: --current-plugin wins over --plugin and --form-id wins over --npc. Only true or 1 activates --dry-run/--apply; inactive supplied values are not conflicts. Proposal persistence is conditional on --proposal; apply requires --expected-sha256. Conservative mixed-mode authority and retry metadata do not claim one uniform mutation mode.",
            ["npc materialize-template"] = "One mixed proposal/apply command: --input-plugin wins over --plugin. Only simultaneously active true or 1 values conflict for --dry-run/--apply. The model admits traits, stats, factions, spell-list, ai-data, ai-packages, model-animation, base-data, inventory, script, default-package-list, attack-data, and keywords, but the current service supports only stats, factions, spell-list, and keywords and fails closed for unsupported inherited categories.",
            ["npc follower-finish analyze"] = "Follower phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No follower response schema identifier is published.",
            ["npc follower-finish apply"] = "Follower phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No follower response schema identifier is published.",
            ["npc follower-finish verify"] = "Follower phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No follower response schema identifier is published.",
            ["npc follower-finish pair-analyze"] = "Paired phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No pair response schema identifier is published.",
            ["npc follower-finish pair-apply"] = "Paired phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No pair response schema identifier is published.",
            ["npc follower-finish pair-verify"] = "Paired phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No pair response schema identifier is published.",
            ["npc placement interior analyze"] = "Interior phases are distinct Skyrim-only handlers. Analyze/apply bind request/proposal hashes; verify accepts only --manifest/--manifest-sha256 and writes no artifact. The handler response discriminators are npc.interior-placement.proposal.v1, npc.interior-placement.manifest.v1, and npc.interior-placement.verification.v1 respectively; only request/proposal/manifest documents are projected as artifacts, and no registered protocol result schema is claimed.",
            ["npc placement interior apply"] = "Interior phases are distinct Skyrim-only handlers. Analyze/apply bind request/proposal hashes; verify accepts only --manifest/--manifest-sha256 and writes no artifact. The handler response discriminators are npc.interior-placement.proposal.v1, npc.interior-placement.manifest.v1, and npc.interior-placement.verification.v1 respectively; only request/proposal/manifest documents are projected as artifacts, and no registered protocol result schema is claimed.",
            ["npc placement interior verify"] = "Interior phases are distinct Skyrim-only handlers. Analyze/apply bind request/proposal hashes; verify accepts only --manifest/--manifest-sha256 and writes no artifact. The handler response discriminators are npc.interior-placement.proposal.v1, npc.interior-placement.manifest.v1, and npc.interior-placement.verification.v1 respectively; only request/proposal/manifest documents are projected as artifacts, and no registered protocol result schema is claimed.",
            ["preset catalog"] = "--data-root, --plugins, --race, and --sex are an all-or-none compatibility quartet. --compatible-only is activated by option presence, not by a boolean value, and requires that quartet. Without it, provider identity is not established and no compatibility claim is made."
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private sealed record Task12Profile(
        ImmutableArray<string> Games,
        ImmutableArray<string> Relationships,
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        AgentDeterminism Determinism,
        ImmutableArray<AgentAuthorityState> Authority,
        string ResultFragment);

    private static async Task AssertTask12ContractsAsync()
    {
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task12Options)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Task12Profile expected = Task12Profiles[name];
            Assert(contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty && contract.ResultShape == "object" &&
                   contract.Options.Select(Task9OptionProfile).SequenceEqual(expectedOptions, StringComparer.Ordinal) &&
                   contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(expected.Games, StringComparer.Ordinal) &&
                   contract.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(expected.Relationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(expected.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task6EffectProfile).SequenceEqual(expected.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == expected.Retry && contract.Determinism == expected.Determinism &&
                   contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Select(item => item.State).SequenceEqual(expected.Authority) &&
                   contract.ResultDescription.Contains(expected.ResultFragment, StringComparison.Ordinal) &&
                   contract.InputArtifactKinds.SequenceEqual(contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal),
                $"Task 12 independent full profile drifted: {name}.");

            CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync([.. name.Split(' '), "--protocol", "2", "--json"]);
            Assert(refused.ExitCode != 0 && refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
                $"Task 12 strict protocol-2 invocation did not refuse {name}.");

            var (humanHelpRunner, humanHelpOutput, humanHelpError) = Program.CreateRunner();
            CommandExitCode humanHelpExit = await humanHelpRunner.RunAsync(
                CommandLine.Parse([.. name.Split(' '), "--help"]), CancellationToken.None);
            Assert(humanHelpExit == CommandExitCode.Success && humanHelpError.ToString().Length == 0 &&
                   humanHelpOutput.ToString().Contains($"{name} — legacy command options", StringComparison.Ordinal) &&
                   contract.Options.All(option => humanHelpOutput.ToString().Contains($"--{option.CliName} {option.ValueSyntax}", StringComparison.Ordinal)),
                $"Task 12 human help drifted: {name}.");

            var (jsonHelpRunner, jsonHelpOutput, jsonHelpError) = Program.CreateRunner();
            CommandExitCode jsonHelpExit = await jsonHelpRunner.RunAsync(
                CommandLine.Parse([.. name.Split(' '), "--help", "--json"]), CancellationToken.None);
            using JsonDocument scopedJson = JsonDocument.Parse(jsonHelpOutput.ToString());
            Assert(jsonHelpExit == CommandExitCode.Success && jsonHelpError.ToString().Length == 0 &&
                   scopedJson.RootElement.GetProperty("name").GetString() == name &&
                   scopedJson.RootElement.GetProperty("options").EnumerateArray()
                       .Select(item => item.GetProperty("cliName").GetString())
                       .SequenceEqual(contract.Options.Select(item => item.CliName), StringComparer.Ordinal),
                $"Task 12 JSON help drifted: {name}.");
        }

        AgentCommandContract tint = AgentCommandRegistry.GetLegacyDiscoveryRequired("face tint patch");
        AgentCommandContract vanilla = AgentCommandRegistry.GetLegacyDiscoveryRequired("face morph patch");
        AgentCommandContract extended = AgentCommandRegistry.GetLegacyDiscoveryRequired("face morph extended");
        AgentCommandContract sculpt = AgentCommandRegistry.GetLegacyDiscoveryRequired("face sculpt patch");
        AgentCommandContract pose = AgentCommandRegistry.GetLegacyDiscoveryRequired("face pose resolve");
        AgentCommandContract reset = AgentCommandRegistry.GetLegacyDiscoveryRequired("face reset");
        AgentCommandContract pipeline = AgentCommandRegistry.GetLegacyDiscoveryRequired("pipeline preset-to-npc");
        Assert(tint.Limitations.Contains(Task12TintDialect, StringComparer.Ordinal) &&
               vanilla.Limitations.Contains(Task12NativeMorphDialect, StringComparer.Ordinal) &&
               extended.Limitations.Contains(Task12ExtendedMorphDialect, StringComparer.Ordinal) &&
               sculpt.Limitations.Contains(Task12SculptDialect, StringComparer.Ordinal) &&
               pose.Limitations.Contains(Task12PoseDialect, StringComparer.Ordinal) &&
               reset.Limitations.Contains(Task12ResetDialect, StringComparer.Ordinal) &&
               pipeline.Limitations.Contains(Task12PipelineDialect, StringComparer.Ordinal),
            "Task 12 exact JSON dialect constraints were not published independently.");

        foreach (string command in new[] { "animation list", "animation tree" })
        {
            AgentCommandContract animation = AgentCommandRegistry.GetLegacyDiscoveryRequired(command);
            Assert(animation.Options.Single(item => item.CliName == "female").AllowedValues.SequenceEqual(["true", "false"], StringComparer.Ordinal) &&
                   animation.Options.Single(item => item.CliName == "first-person").AllowedValues.SequenceEqual(["true", "false"], StringComparer.Ordinal) &&
                   animation.Limitations.Any(item => item.Contains("256 characters", StringComparison.Ordinal) && item.Contains("in-memory artifactKind", StringComparison.Ordinal)),
                $"{command} lost Boolean/filter or non-schema artifact-kind constraints.");
        }

        AgentCommandContract smoke = AgentCommandRegistry.GetLegacyDiscoveryRequired("runtime smoke verify");
        AgentCommandContract aggregate = AgentCommandRegistry.GetLegacyDiscoveryRequired("runtime smoke verify-all");
        Assert(smoke.Options.Select(item => item.CliName).SequenceEqual(["edition", "game", "runtime-report", "package-acceptance"], StringComparer.Ordinal) &&
               smoke.SupportedGames.Length == 2 && aggregate.InputArtifacts.Length == 3 &&
               aggregate.Limitations.Any(item => item.Contains("three-report dialect", StringComparison.Ordinal)),
            "Runtime-smoke legacy projection lost dual-game or aggregate-dialect discovery.");

    }

    private static readonly ImmutableArray<string> Task12MixedRelationships =
    [
        "atLeastOne:edition,game>:none",
        "forbiddenWhen:dry-run,apply>dry-run,apply:all[dry-run=anyOf(true|1)/present;apply=anyOf(true|1)/present]",
        "requiredWhen:expected-sha256>apply:all[apply=anyOf(true|1)/present]"
    ];

    private static readonly ImmutableArray<string> Task12MixedTargetRelationships =
    [
        "atLeastOne:edition,game>:none",
        "atLeastOne:npc,form-id>:none",
        "forbiddenWhen:dry-run,apply>dry-run,apply:all[dry-run=anyOf(true|1)/present;apply=anyOf(true|1)/present]",
        "requiredWhen:expected-sha256>apply:all[apply=anyOf(true|1)/present]"
    ];

    private static readonly ImmutableArray<string> Task12NativeMorphRelationships =
    [
        "atLeastOne:edition,game>:none",
        "atLeastOne:npc,form-id>:none",
        "atLeastOne:vanilla,morphs>:none",
        "forbiddenWhen:dry-run,apply>dry-run,apply:all[dry-run=anyOf(true|1)/present;apply=anyOf(true|1)/present]",
        "requiredWhen:expected-sha256>apply:all[apply=anyOf(true|1)/present]"
    ];

    private const string Task12TintDialect =
        "--layers is inline or @existing-K-local-file JSON: at most 1 MiB, maximum depth 16, comments/trailing commas refused, root array at most 256. Fallout 4 rows are case-sensitive duplicate-free closed objects with dataType,optionIndex,value,color,templateColorIndex,rawTendBase64: dataType aliases value-color|valuecolor|palette and texture-set|textureset; optionIndex is unique UInt16; value is 0..100; raw base64 text is at most 64 characters and decodes to exactly 1, 5, or 7 bytes. Texture-set requires raw and forbids color/template; value-color effective length >=5 requires color and <5 forbids it, >=7 requires templateColorIndex and <7 forbids it; color is a closed duplicate-free red,green,blue byte object and templateColorIndex is -1..32767. Skyrim SE rows are case-sensitive duplicate-free closed index,red,green,blue,alpha,coverage,presetIndex objects: index is unique UInt16, color/alpha are bytes, coverage is 0..100, presetIndex is Int16, and texture fields are explicitly refused.";

    private const string Task12NativeMorphDialect =
        "Canonical --vanilla wins over --morphs; both are optional aliases but at least one is required. The selected inline or @existing-K-local-file JSON is at most 1 MiB, maximum depth 8, comments/trailing commas refused, with one case-sensitive duplicate-free closed root containing exactly nam9,nam9Trailing,nama. Skyrim service validation requires exactly 18 finite nam9 numbers in [-1,1], one finite nam9Trailing number, and exactly four UInt32 nama integers.";

    private const string Task12ExtendedMorphDialect =
        "--extended is inline or @existing-K-local-file JSON: at most 1 MiB, maximum depth 8, comments/trailing commas refused, with a case-sensitive duplicate-free closed root containing only morphs. morphs has at most 2048 entries; every entry requires string name and finite numeric value, while unknown and duplicate entry members are tolerated by current last-property lookup. Service validation requires a nonempty printable name at most 256 characters, names unique case-insensitively, and finite values in [-1,1]; absolute values below 0.0001 remove an existing morph, and uncatalogued nonzero names are admitted with a warning.";

    private const string Task12SculptDialect =
        "--sculpt is inline or @existing-K-local-file JSON: at most 16 MiB, maximum depth 16, comments/trailing commas refused. Case-sensitive closed fields are root divisor,parts; part host,vertices,verts; vertex index,dx,dy,dz; duplicate allowed fields are tolerated by current last-property lookup. Service validation requires divisor 1..1000000, at most 64 parts, printable nonempty host at most 512 characters, host vertex count 1..2147483647, 1..200000 vertices per part, unique indices in 0..hostVertexCount-1, finite deltas in [-1000,1000], and every delta times divisor within signed Int32.";

    private const string Task12PoseDialect =
        "Face-pose JSON accepts a UTF-8 BOM but refuses comments/trailing commas; it is at most 4 MiB and depth 16. Every object is case-sensitive, duplicate-free, closed, and requires exactly its listed fields: root version,game,npc,facialMorphIntensity,regions,faceMorphs,vertexMorphs; region id,name,default,bones; transform position,rotation,scale; bone bone,min,max; face morph regionId,position,rotation,scale; vertex morph resolver,name,weight,vertices; vertex index,delta. version is exactly 1; game is fallout4|skyrimse and npc a matching nonzero FormID; strings are printable/nonempty with game/npc at most 32 and names at most 256; intensity and face-morph scale are finite [-100,100]. regions has 1..4096 unique ids in 0..1000000; each has 1..4096 case-insensitively unique bones and total bones <=100000; all vectors contain exactly three finite values in [-100000,100000]. faceMorphs may be empty and has at most 4096 rows; unresolved regionId rows are skipped with warnings. vertexMorphs may be empty and has at most 2048 channels; weight is finite [-1,1], each channel and the total have at most 200000 vertices, with unique indices per channel in 0..10000000. Resolution is read-only and writes no persistent artifact.";

    private const string Task12ResetDialect =
        "Face reset snapshots are existing .face.json files at most 4 MiB, no comments/trailing commas, depth at most 64, object/array collections at most 8192, duplicate keys refused case-insensitively, and UTF-8 BOM accepted. Root lookup is case-insensitive and requires schemaVersion 1 plus matching game and npcFormId; unknown root values are preserved, and any recognized section must be an object or array. Fallout 4 sections are face-parts,tints,vertex-morphs,bone-regions; Skyrim SE sections are face-parts,skyrim-morphs,skyrim-tints. Current, baseline, and fresh output are distinct same-filename paths; reset replaces only the selected section, active apply is current/baseline/proposal/hash bound, and never overwrites output or proposal artifacts.";

    private const string Task12PipelineDialect =
        "Exact pairs are looksmenu+fallout4 and racemenu-jslot+skyrimse. The K-local output root must already exist; it is not required to be empty or fresh. The output plugin and root npcmanager-package.json must be absent, and generated FaceGen files, copied runtime-script evidence, runtime-test-instructions.json, and other generated artifact destinations use no-overwrite writes; optional runtime-script deployment may report an identical artifact already present. Static composition and hash binding do not establish human visual acceptance, game runtime, or promotion approval; unknown options and positional tokens retain legacy parser behavior.";

    private static ImmutableArray<string> Task12FacePluginOptions(string payload, string syntax) =>
    [
        "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:game:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=",
        "plugin:required:path:<K-local-source-plugin>::-:conflicts=", "output:required:path:<new-plugin>::-:conflicts=",
        "npc:optional:artifactReference:<hexadecimal-form-id>::form-id:conflicts=", "form-id:optional:artifactReference:<hexadecimal-form-id>::-:conflicts=",
        $"{payload}:required:json:{syntax}::-:conflicts=", "expected-sha256:optional:sha256:<64-hex-sha256>::-:conflicts=",
        "proposal:optional:path:<new-proposal.json>::-:conflicts=", "dry-run:optional:boolean:true|false|1:true|false|1:-:conflicts=",
        "apply:optional:boolean:true|false|1:true|false|1:-:conflicts="
    ];

    private static ImmutableArray<string> Task12FacePresetOptions(string payload, string syntax) =>
    [
        "edition:optional:enum:skyrimse:skyrimse:game:conflicts=", "game:optional:enum:skyrimse:skyrimse:-:conflicts=",
        "input:required:path:<K-local-source.jslot>::-:conflicts=", "output:required:path:<new-preset.jslot>::-:conflicts=",
        $"{payload}:required:json:{syntax}::-:conflicts=", "expected-sha256:optional:sha256:<64-hex-sha256>::-:conflicts=",
        "proposal:optional:path:<new-proposal.json>::-:conflicts=", "dry-run:optional:boolean:true|false|1:true|false|1:-:conflicts=",
        "apply:optional:boolean:true|false|1:true|false|1:-:conflicts="
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task12Options =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["face tint patch"] = Task12FacePluginOptions("layers", "<inline-JSON-array|@K-local-file>"),
            ["face morph patch"] = Task12FacePluginOptions("vanilla", "<inline-JSON-object|@K-local-file>")
                .SetItem(6, "vanilla:optional:json:<inline-JSON-object|@K-local-file>::-:conflicts=")
                .Insert(7, "morphs:optional:json:<inline-JSON-object|@K-local-file>::vanilla:conflicts="),
            ["face morph extended"] = Task12FacePresetOptions("extended", "<inline-JSON-object|@K-local-file>"),
            ["face sculpt patch"] = Task12FacePresetOptions("sculpt", "<inline-JSON-object|@K-local-file>"),
            ["face pose resolve"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:game:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "npc:required:artifactReference:<nonzero-hexadecimal-form-id>::-:conflicts=", "preset:required:path:<K-local-face-pose.json>::-:conflicts="],
            ["face reset"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:game:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "npc:optional:artifactReference:<hexadecimal-form-id>::-:conflicts=", "form-id:optional:artifactReference:<hexadecimal-form-id>::npc:conflicts=", "current:required:path:<K-local-current.face.json>::-:conflicts=", "baseline:required:path:<K-local-baseline.face.json>::-:conflicts=", "output:required:path:<new-face.json>::-:conflicts=", "section:required:enum:face-parts|tints|vertex-morphs|bone-regions|skyrim-morphs|skyrim-tints:face-parts|tints|vertex-morphs|bone-regions|skyrim-morphs|skyrim-tints:-:conflicts=", "expected-sha256:optional:sha256:<64-hex-sha256>::-:conflicts=", "proposal:optional:path:<new-proposal.json>::-:conflicts=", "dry-run:optional:boolean:true|false|1:true|false|1:-:conflicts=", "apply:optional:boolean:true|false|1:true|false|1:-:conflicts="],
            ["animation list"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=", "manifest:required:path:<K-local-preview-manifest.json>::-:conflicts=", "female:optional:boolean:true|false:true|false:-:conflicts=", "first-person:optional:boolean:true|false:true|false:-:conflicts=", "filter:optional:string:<0..256-printable-trimmed-characters>::-:conflicts="],
            ["animation tree"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=", "manifest:required:path:<K-local-preview-manifest.json>::-:conflicts=", "female:optional:boolean:true|false:true|false:-:conflicts=", "first-person:optional:boolean:true|false:true|false:-:conflicts=", "filter:optional:string:<0..256-printable-trimmed-characters>::-:conflicts="],
            ["runtime smoke verify"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=", "runtime-report:required:path:<K-local-runtime-report.json>::-:conflicts=", "package-acceptance:required:path:<K-local-package-acceptance.json>::-:conflicts="],
            ["runtime smoke verify-all"] = ["fallout4-report:required:path:<K-local-fallout4-runtime-report.json>::-:conflicts=", "skyrimse-report:required:path:<K-local-skyrimse-runtime-report.json>::-:conflicts=", "package-acceptance:required:path:<K-local-package-acceptance.json>::-:conflicts="],
            ["pipeline preset-to-npc"] = ["format:required:enum:looksmenu|racemenu-jslot:looksmenu|racemenu-jslot:-:conflicts=", "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=", "preset:required:path:<K-local-preset>::-:conflicts=", "source-plugin:required:path:<K-local-source-plugin>::-:conflicts=", "plugin:required:identifier:<output-plugin-name>::-:conflicts=", "npc:required:artifactReference:<hexadecimal-form-id>::-:conflicts=", "mod-name:required:string:<mod-name>::-:conflicts=", "output-root:required:path:<existing-K-local-output-root>::-:conflicts=", "editor-id:optional:identifier:<editor-id>::-:conflicts=", "name:optional:string:<npc-name>::-:conflicts=", "facegeom-manifest:optional:path:<K-local-facegeom-manifest.json>::-:conflicts=", "facetint-manifest:optional:path:<K-local-facetint-manifest.json>::-:conflicts=", "runtime-script-build:optional:path:<K-local-runtime-script-build.json>::-:conflicts=", "runtime-script-package:optional:path:<K-local-runtime-script-package.json>::-:conflicts="]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableArray<AgentAuthorityState> Task12MixedAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task12ReadAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable];
    private static readonly ImmutableArray<AgentAuthorityState> Task12SmokeAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Established, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task12PipelineAuthority = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];

    private static readonly ImmutableDictionary<string, Task12Profile> Task12Profiles = BuildTask12Profiles();

    private static ImmutableDictionary<string, Task12Profile> BuildTask12Profiles()
    {
        var rows = new Dictionary<string, Task12Profile>(StringComparer.Ordinal);
        foreach (string name in new[] { "face tint patch", "face morph patch", "face reset" })
            rows[name] = new(name == "face morph patch" ? ["skyrimse"] : ["fallout4", "skyrimse"], name == "face morph patch" ? Task12NativeMorphRelationships : Task12MixedTargetRelationships, [name == "face reset" ? "face-reset-current-and-baseline:schemas=none:always" : "face-patch-source:schemas=none:always"], ["face-patch-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]", "face-patch-output:schemas=none:all[apply=anyOf(true|1)/present]"], ["readWorkspace:always", "writeNewArtifact:all[proposal=noneOf(__absent__)/present]", "writeNewArtifact:all[apply=anyOf(true|1)/present]"], AgentRetryPolicy.RequiresReanalysis, AgentDeterminism.Deterministic, Task12MixedAuthority, "Applicable/applied state");
        rows["face tint patch"] = rows["face tint patch"] with
        {
            Effects = rows["face tint patch"].Effects.Add("appendLocalOperationJournal:always")
        };
        foreach (string name in new[] { "face morph extended", "face sculpt patch" })
            rows[name] = new(["skyrimse"], Task12MixedRelationships, ["racemenu-preset:schemas=none:always"], ["face-patch-proposal:schemas=none:all[proposal=noneOf(__absent__)/present]", "face-patch-output:schemas=none:all[apply=anyOf(true|1)/present]"], ["readWorkspace:always", "writeNewArtifact:all[proposal=noneOf(__absent__)/present]", "writeNewArtifact:all[apply=anyOf(true|1)/present]"], AgentRetryPolicy.RequiresReanalysis, AgentDeterminism.Deterministic, Task12MixedAuthority, "Applicable/applied state");
        rows["face pose resolve"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["face-pose-preset:schemas=none:always"], [], ["readWorkspace:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, Task12ReadAuthority, "Resolved state");
        foreach (string name in new[] { "animation list", "animation tree" })
            rows[name] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["preview-animation-manifest:schemas=none:always"], [], ["readWorkspace:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, Task12ReadAuthority, "in-memory");
        rows["runtime smoke verify"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none"], ["runtime-smoke-report:schemas=none:always", "package-acceptance-report:schemas=none:always"], [], ["readWorkspace:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.EnvironmentDependent, Task12SmokeAuthority, "Validated runtime evidence");
        rows["runtime smoke verify-all"] = new(["fallout4", "skyrimse"], [], ["fallout4-runtime-smoke-report:schemas=none:always", "skyrimse-runtime-smoke-report:schemas=none:always", "package-acceptance-report:schemas=none:always"], [], ["readWorkspace:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.EnvironmentDependent, Task12SmokeAuthority, "aggregate");
        rows["pipeline preset-to-npc"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none", "forbiddenWhen:format,edition>format,edition:all[format=anyOf(looksmenu)/present;edition=anyOf(skyrimse)/present]", "forbiddenWhen:format,edition>format,edition:all[format=anyOf(racemenu-jslot)/present;edition=anyOf(fallout4)/present]"], ["preset-and-source-plugin:schemas=none:always", "optional-facegeom-evidence:schemas=none:all[facegeom-manifest=noneOf(__absent__)/present]", "optional-facetint-evidence:schemas=none:all[facetint-manifest=noneOf(__absent__)/present]", "optional-runtime-script-build:schemas=none:all[runtime-script-build=noneOf(__absent__)/present]", "optional-runtime-script-package:schemas=none:all[runtime-script-package=noneOf(__absent__)/present]"], ["preset-to-npc-package:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, Task12PipelineAuthority, "Completed state");
        return rows.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private sealed record Task9Profile(
        ImmutableArray<string> Games,
        ImmutableArray<string> Relationships,
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        AgentDeterminism Determinism,
        ImmutableArray<AgentAuthorityState> Authority,
        string Result,
        string? CanonicalCommand = null);

    private static async Task AssertTask9ContractsAsync()
    {
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task9OptionProfiles)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete &&
                   contract.Readiness == ProtocolReadiness.Legacy &&
                   contract.ResultSchemaIds.IsEmpty && contract.ResultShape == "object" &&
                   !contract.SupportsDryRun,
                $"Task 9 contract stayed incomplete or changed protocol vocabulary: {name}.");
            Assert(contract.Options.Select(Task9OptionProfile)
                    .SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 9 independent option profile drifted: {name}: {string.Join(';', contract.Options.Select(Task9OptionProfile))}");
            Task9Profile expected = Task9Profiles[name];
            Assert(contract.SupportedGames.Select(item => item.ToWireName())
                       .SequenceEqual(expected.Games, StringComparer.Ordinal) &&
                   contract.OptionRelationships.Select(Task6RelationshipProfile)
                       .SequenceEqual(expected.Relationships, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(expected.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(Task6ArtifactProfile)
                       .SequenceEqual(expected.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task6EffectProfile)
                       .SequenceEqual(expected.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == expected.Retry &&
                   contract.Determinism == expected.Determinism &&
                   contract.Authority.Select(item => item.Kind)
                       .SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Select(item => item.State)
                       .SequenceEqual(expected.Authority) &&
                   contract.ResultDescription == expected.Result &&
                   contract.CanonicalCommand == expected.CanonicalCommand &&
                   contract.InputArtifactKinds.SequenceEqual(
                       contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal),
                $"Task 9 independent full profile drifted: {name}.");

            CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
                [.. name.Split(' '), "--protocol", "2", "--json"]);
            Assert(refused.ExitCode != 0 &&
                   refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
                $"Task 9 strict protocol-2 invocation did not refuse {name}.");
        }

        AgentCommandContract propose = AgentCommandRegistry.GetLegacyDiscoveryRequired("outfit propose");
        AgentCommandContract create = AgentCommandRegistry.GetLegacyDiscoveryRequired("outfit create");
        Assert(create.CanonicalCommand == "outfit propose" && propose.CanonicalCommand is null &&
               create.Purpose == propose.Purpose && create.Limitations.SequenceEqual(propose.Limitations) &&
               create.Options.Select(Task9OptionProfile).SequenceEqual(propose.Options.Select(Task9OptionProfile), StringComparer.Ordinal) &&
               create.OptionRelationships.Select(Task6RelationshipProfile).SequenceEqual(propose.OptionRelationships.Select(Task6RelationshipProfile), StringComparer.Ordinal) &&
               create.InputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(propose.InputArtifacts.Select(Task6ArtifactProfile), StringComparer.Ordinal) &&
               create.OutputArtifacts.Select(Task6ArtifactProfile).SequenceEqual(propose.OutputArtifacts.Select(Task6ArtifactProfile), StringComparer.Ordinal) &&
               create.Effects.Select(Task6EffectProfile).SequenceEqual(propose.Effects.Select(Task6EffectProfile), StringComparer.Ordinal) &&
               create.Authority.Select(item => $"{item.Kind}:{item.State}:{item.Reason}").SequenceEqual(
                   propose.Authority.Select(item => $"{item.Kind}:{item.State}:{item.Reason}"), StringComparer.Ordinal),
            "outfit create did not publish the genuine outfit propose structural alias contract.");

        AgentCommandContract list = AgentCommandRegistry.GetLegacyDiscoveryRequired("outfit list");
        Assert(list.Options.Single(item => item.CliName == "game").AliasFor == "edition" &&
               list.Options.Single(item => item.CliName == "search").AliasFor == "query" &&
               list.Options.Single(item => item.CliName == "plugin").Description.Contains(
                   "fallback data root", StringComparison.Ordinal) &&
               list.Options.Single(item => item.CliName == "plugins").Description.Contains(
                   "wins over", StringComparison.Ordinal),
            "outfit list did not publish its alternative roots, load-order aliases, and precedence.");

        Assert(propose.Limitations.Any(item => item == Task9OutfitProposalConstraints),
            "outfit propose did not publish its behavior-bearing item and mode constraints.");
        AgentCommandContract leveled = AgentCommandRegistry.GetLegacyDiscoveryRequired("leveled-list propose");
        Assert(leveled.Limitations.Any(item => item == Task9LeveledListProposalConstraints),
            "leveled-list propose did not publish its behavior-bearing entry constraints.");
        AgentCommandContract resolve = AgentCommandRegistry.GetLegacyDiscoveryRequired("leveled-list resolve");
        Assert(resolve.Limitations.Any(item => item == Task9LeveledListResolutionConstraints),
            "leveled-list resolve did not publish its bounded input and resolution constraints.");

        AgentCommandContract armor = AgentCommandRegistry.GetLegacyDiscoveryRequired("armor propose");
        Assert(armor.Limitations.Any(item => item == Task9ArmorPatchDialect),
            "armor propose did not publish the exact case-sensitive patch dialect.");
        AgentCommandContract damage = AgentCommandRegistry.GetLegacyDiscoveryRequired("armor damage-resist");
        Assert(damage.Limitations.Any(item => item == Task9DamageDialect),
            "armor damage-resist did not publish its exact Fallout-only entry dialect.");
        AgentCommandContract addon = AgentCommandRegistry.GetLegacyDiscoveryRequired("armor-addon propose");
        Assert(addon.Limitations.Any(item => item == Task9ArmorAddonPatchDialect) &&
               addon.Limitations.Any(item => item == Task9ArmorAddonModelsDialect),
            "armor-addon propose did not publish its exact patch/models dialects.");
        Assert(Task6ArtifactProfile(addon.InputArtifacts.Single(item => item.Kind == "armor-addon-patch")) ==
                   "armor-addon-patch:schemas=none:all[models=anyOf(__absent__)/absent]" &&
               Task6ArtifactProfile(addon.OutputArtifacts.Single(item => item.Kind == "armor-addon-record-proposal")) ==
                   "armor-addon-record-proposal:schemas=none:all[models=anyOf(__absent__)/absent]" &&
               Task6EffectProfile(addon.Effects.Single(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact &&
                   item.Trigger?.Predicates.Single().MatchesWhenAbsent == true)) ==
                   "writeNewArtifact:all[models=anyOf(__absent__)/absent]",
            "armor-addon absent-model branch did not use the established AnyOf __absent__ predicate.");
        AgentCommandContract material = AgentCommandRegistry.GetLegacyDiscoveryRequired("material-swap propose");
        Assert(material.Limitations.Any(item => item == Task9MaterialSwapDialect),
            "material-swap propose did not publish its exact Fallout-only patch dialect.");

        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["armor-addon", "propose", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("--patch", StringComparison.Ordinal) &&
               humanOutput.ToString().Contains("--models", StringComparison.Ordinal),
            "Representative Task 9 human help omitted the armor-addon branches.");
        var (jsonRunner, jsonOutput, jsonError) = Program.CreateRunner();
        CommandExitCode jsonExit = await jsonRunner.RunAsync(CommandLine.Parse(
            ["armor-addon", "propose", "--help", "--json"]), CancellationToken.None);
        using JsonDocument jsonHelp = JsonDocument.Parse(jsonOutput.ToString());
        Assert(jsonExit == CommandExitCode.Success && jsonError.ToString().Length == 0 &&
               jsonHelp.RootElement.GetProperty("name").GetString() == "armor-addon propose" &&
               jsonHelp.RootElement.GetProperty("options").GetArrayLength() == 7,
            "Representative Task 9 JSON help did not project the complete armor-addon contract.");
    }

    private static string Task9OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}:{option.AliasFor ?? "-"}:conflicts={string.Join('|', option.ConflictsWith)}";

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task9OptionProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["outfit list"] = ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts=", "data-root:optional:path:<K-local-copied-Data-root>::-:conflicts=", "plugin:optional:path:<K-local-plugin>::-:conflicts=", "plugins:optional:string:<plugin,...>::-:conflicts=", "query:optional:string:<search-text>::-:conflicts=", "search:optional:string:<search-text>::query:conflicts="],
            ["outfit propose"] = OutfitProposalOptionProfile(),
            ["outfit create"] = OutfitProposalOptionProfile(),
            ["outfit write"] = WriterOptionProfile("<name.outfit-proposal.json>"),
            ["leveled-list propose"] = [.. GameOptionProfile(), "plugin:required:path:<K-local-plugin>::-:conflicts=", "list:required:artifactReference:<non-null-hexadecimal-form-id>::-:conflicts=", "entries:required:json:<inline-JSON-array>::-:conflicts=", "output:required:path:<name.leveled-list-proposal.json>::-:conflicts=", "editor-id:optional:identifier:<editor-id>::-:conflicts=", "chance-none:optional:integer:<0..255>::-:conflicts=", "max-count:optional:integer:<0..255>::-:conflicts=", "calc-all-levels:optional:boolean:true|false:true|false:-:conflicts=", "calc-each-in-count:optional:boolean:true|false:true|false:-:conflicts=", "use-all:optional:boolean:true|false:true|false:-:conflicts="],
            ["leveled-list resolve"] = [.. GameOptionProfile(), "list:required:path:<name.leveled-list-proposal.json>::-:conflicts=", "seed:required:integer:<signed-64-bit-integer>::-:conflicts=", "output:required:path:<name.leveled-list-resolution.json>::-:conflicts="],
            ["leveled-list write"] = WriterOptionProfile("<name.leveled-list-proposal.json>"),
            ["armor propose"] = ProposalPatchOptionProfile("<inline-JSON-object|@K-local-file>", "<name.armor-proposal.json>"),
            ["armor damage-resist"] = ProposalPatchOptionProfile("<inline-JSON-array|@K-local-file>", "<name.armor-damage-resist-proposal.json>", "damage-resist"),
            ["armor-addon propose"] = [.. GameOptionProfile(), "plugin:required:path:<K-local-plugin>::-:conflicts=", "source:required:artifactReference:<non-null-hexadecimal-form-id>::-:conflicts=", "patch:optional:json:<inline-JSON-object|@K-local-file>::-:conflicts=", "models:optional:json:<inline-JSON-array|@K-local-file>::-:conflicts=", "output:required:path:<name.armor-addon-proposal.json|name.armor-addon-model-proposal.json>::-:conflicts="],
            ["armor write"] = WriterOptionProfile("<name.armor-proposal.json>"),
            ["armor-addon write"] = WriterOptionProfile("<name.armor-addon-proposal.json>"),
            ["material-swap propose"] = ProposalPatchOptionProfile("<inline-JSON-object|@K-local-file>", "<name.material-swap-proposal.json>"),
            ["material-swap write"] = WriterOptionProfile("<name.material-swap-proposal.json>")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableArray<string> GameOptionProfile() =>
    [
        "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-:conflicts=",
        "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition:conflicts="
    ];

    private static ImmutableArray<string> OutfitProposalOptionProfile() =>
    [
        .. GameOptionProfile(), "plugin:required:path:<K-local-plugin>::-:conflicts=",
        "source:required:artifactReference:<non-null-hexadecimal-form-id>::-:conflicts=",
        "actor-race:optional:artifactReference:<Plugin|FormID>::-:conflicts=",
        "items:required:json:<JSON-array|comma-separated-Plugin|FormID-list>::-:conflicts=",
        "output:required:path:<name.outfit-proposal.json>::-:conflicts=",
        "mode:required:enum:new|override:new|override:-:conflicts=",
        "target-form:optional:artifactReference:<plugin-local-hexadecimal-form-id>::-:conflicts=",
        "target-form-id:optional:artifactReference:<plugin-local-hexadecimal-form-id>::target-form:conflicts=",
        "editor-id:optional:identifier:<editor-id>::-:conflicts="
    ];

    private static ImmutableArray<string> WriterOptionProfile(string proposal) =>
    [
        .. GameOptionProfile(), $"proposal:required:path:{proposal}::-:conflicts=",
        "output:required:path:<new-plugin.esp>::-:conflicts="
    ];

    private static ImmutableArray<string> ProposalPatchOptionProfile(
        string syntax, string output, string option = "patch") =>
    [
        .. GameOptionProfile(), "plugin:required:path:<K-local-plugin>::-:conflicts=",
        "source:required:artifactReference:<non-null-hexadecimal-form-id>::-:conflicts=",
        $"{option}:required:json:{syntax}::-:conflicts=",
        $"output:required:path:{output}::-:conflicts="
    ];

    private static readonly ImmutableArray<AgentAuthorityState> Task9ReadAuthority =
        [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task9ProposalAuthority =
        [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];
    private static readonly ImmutableArray<AgentAuthorityState> Task9WriterAuthority =
        [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required];

    private static readonly ImmutableArray<string> Task9GameRelationship = ["atLeastOne:edition,game>:none"];
    private static readonly ImmutableArray<string> Task9OutfitProposalRelationships =
    [
        "atLeastOne:edition,game>:none",
        "requiredWhen:editor-id>mode:all[mode=anyOf(new)/present]",
        "forbiddenWhen:target-form,target-form-id>mode,target-form,target-form-id:all[mode=anyOf(new)/present;target-form=anyOf(__absent__)/absent;target-form-id=anyOf(__absent__)/absent]",
        "forbiddenWhen:editor-id>editor-id,mode:all[editor-id=noneOf(__absent__)/present;mode=anyOf(override)/present]",
        "forbiddenWhen:target-form>target-form,mode:all[target-form=noneOf(__absent__)/present;mode=anyOf(override)/present]",
        "forbiddenWhen:target-form-id>target-form-id,mode:all[target-form-id=noneOf(__absent__)/present;mode=anyOf(override)/present]"
    ];

    private static readonly ImmutableDictionary<string, Task9Profile> Task9Profiles =
        new Dictionary<string, Task9Profile>(StringComparer.Ordinal)
        {
            ["outfit list"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none", "atLeastOne:data-root,plugin>:none"], ["copied-data-load-order:schemas=none:always"], [], ["readWorkspace:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic, Task9ReadAuthority, "Edition, deterministic candidate rows, source/override-chain provenance, item references, and diagnostics from an explicit copied Data root with either an explicit plugin order or deterministic filename ordering; no live-profile, visual, runtime, or formal result-schema claim is published."),
            ["outfit propose"] = OutfitProposalProfile(),
            ["outfit create"] = OutfitProposalProfile("outfit propose"),
            ["outfit write"] = WriterProfile("outfit-record-proposal", "outfit-plugin", "Written state, fresh ordinary-plugin path, target OTFT FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority."),
            ["leveled-list propose"] = ProposalProfile("leveled-list-source-and-inline-entries", "leveled-list-record-proposal", "Written state, typed hash-bound LVLI proposal fields, source/input/output hashes, ordered entries, master dependencies, no-unrelated-records claim, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier."),
            ["leveled-list resolve"] = new(["fallout4", "skyrimse"], Task9GameRelationship, ["leveled-list-record-proposal:schemas=none:always"], ["leveled-list-resolution:schemas=none:always"], ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, Task9ProposalAuthority, "Written state, source proposal/hash, signed seed, list chance roll/suppression, ordered selections and resolved items, and diagnostics; the seed makes this static preview resolution deterministic without level-gate, binary, visual, or runtime proof."),
            ["leveled-list write"] = WriterProfile("leveled-list-record-proposal", "leveled-list-plugin", "Written state, fresh ordinary-plugin path, target LVLI FormID, output SHA-256, and diagnostics after independent binary readback; Skyrim SE requires maxCount and every entry chanceNone to be zero, and static verification is not runtime or promotion authority."),
            ["armor propose"] = ProposalProfile("armor-source-and-patch", "armor-record-proposal", "Written state, typed hash-bound ARMO proposal mode/source/editor identity, input/patch hashes, changed fields, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier."),
            ["armor damage-resist"] = ProposalProfile("armor-source-and-damage-resist", "armor-damage-resistance-proposal", "Written state, Fallout 4 source/input identity, typed unique DMGT/value rows, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.", ["fallout4"]),
            ["armor-addon propose"] = new(["fallout4", "skyrimse"], ["atLeastOne:edition,game>:none", "atLeastOne:patch,models>:none"], ["armor-addon-source-plugin:schemas=none:always", "armor-addon-patch:schemas=none:all[models=anyOf(__absent__)/absent]", "armor-addon-model-entries:schemas=none:all[models=noneOf(__absent__)/present]"], ["armor-addon-record-proposal:schemas=none:all[models=anyOf(__absent__)/absent]", "armor-addon-model-entries-proposal:schemas=none:all[models=noneOf(__absent__)/present]"], ["readWorkspace:always", "writeNewArtifact:all[models=anyOf(__absent__)/absent]", "writeNewArtifact:all[models=noneOf(__absent__)/present]", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, Task9ProposalAuthority, "Written state and diagnostics for exactly one branch: models presence wins and returns an ordered armor-addon-model proposal; otherwise patch returns a typed ARMA record proposal with source/input/patch identity and changed fields. Neither artifact metadata value is a formal schema identifier."),
            ["armor write"] = WriterProfile("armor-record-proposal", "armor-plugin", "Written state, fresh ordinary-plugin path, target ARMO FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority."),
            ["armor-addon write"] = WriterProfile("armor-addon-record-proposal", "armor-addon-plugin", "Written state, fresh ordinary-plugin path, target ARMA FormID, output SHA-256, and diagnostics after independent binary readback; only the record-proposal branch is accepted, and static verification is not runtime or promotion authority."),
            ["material-swap propose"] = ProposalProfile("material-swap-source-and-patch", "material-swap-record-proposal", "Written state, Fallout 4 typed hash-bound MSWP proposal mode/source/editor identity, input/patch hashes, ordered entries, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.", ["fallout4"]),
            ["material-swap write"] = WriterProfile("material-swap-record-proposal", "material-swap-plugin", "Written state, fresh Fallout 4 ordinary-plugin path, target MSWP FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority.", ["fallout4"])
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static Task9Profile OutfitProposalProfile(string? canonical = null) =>
        new(["fallout4", "skyrimse"], Task9OutfitProposalRelationships,
            ["outfit-source-and-items:schemas=none:always"], ["outfit-record-proposal:schemas=none:always"],
            ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic, Task9ProposalAuthority,
            "Written state, typed hash-bound OTFT proposal mode/source/target/editor identity, ordered items, master dependencies, input/output hashes, no-unrelated-records claim, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.", canonical);

    private static Task9Profile ProposalProfile(string input, string output, string result,
        ImmutableArray<string>? games = null) =>
        new(games ?? ["fallout4", "skyrimse"], Task9GameRelationship,
            [$"{input}:schemas=none:always"], [$"{output}:schemas=none:always"],
            ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
            Task9ProposalAuthority, result);

    private static Task9Profile WriterProfile(string input, string output, string result,
        ImmutableArray<string>? games = null) =>
        new(games ?? ["fallout4", "skyrimse"], Task9GameRelationship,
            [$"{input}:schemas=none:always"], [$"{output}:schemas=none:always"],
            ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
            Task9WriterAuthority, result);

    private const string Task9OutfitProposalConstraints =
        "Outfit items contain 1..4096 unique, non-null ARMO/LVLI FormReferences provided by the source plugin or its masters. New mode requires editor-id and one nonzero plugin-local 24-bit target spelling; override inherits a nonempty source EditorID and forbids editor-id and either target spelling.";
    private const string Task9LeveledListProposalConstraints =
        "Leveled-list entries are a strict no-comments/no-trailing-commas JSON array (maximum depth 8) containing 1..4096 case-sensitive {item,level,count,chanceNone} objects: item is a non-null source/master FormReference, level/count are nonzero uint16, and chanceNone is 0..100. List chance-none is 0..100; max-count is uint8; flags are exact true|false.";
    private const string Task9LeveledListResolutionConstraints =
        "Resolution accepts at most 8 MiB of duplicate-free schemaVersion 1, artifactKind leveled-list-record-proposal JSON, requires current source-plugin hash and matching edition, validates 1..4096 entries and chance ranges, and refuses any realization exceeding 100000 selections. Seed, ChanceNone, UseAll, and CalculateEachInCount determine output; player level and CalculateAllLevels are preserved but not gate-applied.";
    private const string Task9ArmorPatchDialect =
        "Strict duplicate-free case-sensitive armor patch object (no comments/trailing commas, maximum depth 12; @file at most 65536 bytes): mode=new|override; optional editorId, targetFormId, name, slotMask, race, maleWorldModel, femaleWorldModel, value, weight, health, armorRating, keywords, armorAddons[{index:uint16,addon:Plugin|FormID}], description, nonPlayable, enchantment, pickupSound, dropSound, equipmentType, alternateBlockMaterial, templateArmor, objectBounds{minimumX,minimumY,minimumZ,maximumX,maximumY,maximumZ}, completeDocument. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. At least one field must change. Names/descriptions are at most 4096 and descriptions allow only tab/newline controls; models are safe .nif paths; value is Skyrim uint32 or Fallout int32; weight is finite 0..1000000; rating is finite 0..65535 and integral in Fallout 4; health is Fallout 4-only; extended fields and completeDocument are Skyrim-only. Keywords are at most 255 unique non-null references; armorAddons are at most 255 non-null references and complete Skyrim rows use index 0. Complete Skyrim documents initialize name, slotMask, race, value, weight, armorRating, keywords, armorAddons, description, nonPlayable, and objectBounds. objectBounds has exactly six int16 properties whose minima do not exceed maxima; all references must resolve to source or masters.";
    private const string Task9DamageDialect =
        "Fallout 4 damage-resist JSON is a strict no-comments/no-trailing-commas array (maximum depth 8; @file at most 65536 bytes) containing 1..4096 duplicate-free case-sensitive {damageType:Plugin|FormID,value:uint32} objects; damageType references are unique, non-null, and provided by the source plugin or its masters.";
    private const string Task9ArmorAddonPatchDialect =
        "Strict duplicate-free case-sensitive armor-addon patch object (no comments/trailing commas, maximum depth 16; @file at most 131072 bytes): mode=new|override; optional editorId, targetFormId, slotMask:uint32, race, footstepSet, malePriority:uint8, femalePriority:uint8, maleWeightSliderFlags:uint8, femaleWeightSliderFlags:uint8, detectionSound:uint8, weaponAdjust, maleModel, femaleModel, maleFirstPersonModel, femaleFirstPersonModel, maleModelFlags:uint8, femaleModelFlags:uint8, maleColorRemapIndex, femaleColorRemapIndex, maleSkinTexture, femaleSkinTexture, maleSkinTextureSwapList, femaleSkinTextureSwapList, maleMaterialSwap, femaleMaterialSwap, maleFirstPersonMaterialSwap, femaleFirstPersonMaterialSwap, artObject, additionalRaces, sculpt[{gender:uint8,bone,x,y,z}], noUnderarmorScaling, hasSculptData, hiResFirstPersonOnly. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. At least one field must change. Model paths are safe assets; weaponAdjust is finite -100000..100000; color-remap indexes are finite 0..255. Additional races are unique non-null references. Sculpt has at most 4096 duplicate-free-property rows with gender 0|1, nonempty bone names at most 256 characters, and finite x/y/z in -100..100. Fallout-only fields are refused for Skyrim; all references must resolve to source or masters.";
    private const string Task9ArmorAddonModelsDialect =
        "Case-sensitive models JSON is a strict no-comments/no-trailing-commas array (maximum depth 8; @file at most 65536 bytes) containing 1..4096 duplicate-free-property {index:uint16,addon:Plugin|FormID} objects; indexes are unique, addon references are non-null and provided by source or masters, and Skyrim requires every index to be 0. --models presence selects this branch and takes precedence over --patch.";
    private const string Task9MaterialSwapDialect =
        "Fallout 4 strict duplicate-free case-sensitive material-swap patch object (no comments/trailing commas, maximum depth 12; @file at most 131072 bytes): mode=new|override; optional editorId, targetFormId, treeFolder; required entries array of 1..4096 duplicate-free-property {originalMaterial,replacementMaterial,colorRemapIndex,treeFolder} objects. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. Each entry supplies at least originalMaterial or replacementMaterial as a safe asset path; colorRemapIndex is finite 0..1; treeFolder values are at most 4096 characters without controls.";

    private static ImmutableArray<string> PreviewRenderRelationships() =>
    [
        "atLeastOne:edition,game>:none", "requiresTogether:outfit,variant>:none",
        "requiresTogether:asset-root,image-output>:none", "forbiddenWhen:frame,time>frame,time:all[frame=noneOf(__absent__)/present;time=noneOf(__absent__)/present]",
        "requiredWhen:animation>frame,time,fps,play:any[frame=noneOf(__absent__)/present;time=noneOf(__absent__)/present;fps=noneOf(__absent__)/present;play=noneOf(__absent__)/present]",
        "forbiddenWhen:animation>animation,frame,time:all[animation=noneOf(__absent__)/present;frame=anyOf(__absent__)/absent;time=anyOf(__absent__)/absent]",
        "requiredWhen:asset-root,image-output>width,height:any[width=noneOf(__absent__)/present;height=noneOf(__absent__)/present]",
        "forbiddenWhen:render-headwear>render-headwear,hair-slots:all[render-headwear=anyOf(true)/present;hair-slots=anyOf(__absent__)/absent]",
        "forbiddenWhen:hair-slots>hair-slots,render-headwear:all[hair-slots=noneOf(__absent__)/present;render-headwear=anyOf(__absent__)/absent]"
    ];

    private static ImmutableArray<string> PluginVerifyRelationships() =>
    [
        "atLeastOne:edition,game>:none", "atLeastOne:proposal,source-plugin>:none",
        "atLeastOne:proposal,output-plugin>:none", "atLeastOne:proposal,form-id,npc>:none",
        "requiresTogether:whole-skin,whole-skin-sha256>:none",
        "requiredWhen:before,after>proposal:all[proposal=noneOf(__absent__)/present]",
        "forbiddenWhen:level,level-mult>level,level-mult:all[level=noneOf(__absent__)/present;level-mult=noneOf(__absent__)/present]",
        "forbiddenWhen:weight,weight-triangle>weight,weight-triangle:all[weight=noneOf(__absent__)/present;weight-triangle=noneOf(__absent__)/present]",
        "forbiddenWhen:skin,clear-skin>skin,clear-skin:all[skin=noneOf(__absent__)/present;clear-skin=anyOf(true|1)/present]",
        MissingPluginVerifyExpectationProfile()
    ];

    private static string MissingPluginVerifyExpectationProfile()
    {
        ImmutableArray<string> ordinary = PluginVerifyPresentExpectations()
            .Where(option => option is not ("set-flag" or "clear-flag"))
            .ToImmutableArray();
        string absent = string.Join(';', ordinary
            .Select(option => $"{option}=anyOf(__absent__)/absent"));
        return "forbiddenWhen:source-plugin,output-plugin,form-id,npc>proposal," +
               string.Join(',', ordinary.Concat(["set-flag", "clear-flag"])) +
               ",clear-skin:all[proposal=anyOf(__absent__)/absent;" + absent +
               ";set-flag=anyOf(__absent__|)/absent;clear-flag=anyOf(__absent__|)/absent" +
               ";clear-skin=noneOf(true|1)/absent]";
    }

    private static ImmutableArray<AgentAuthorityState> Task7Authority(string name)
    {
        bool readOnly = name is "plugin verify" or "plugin audit";
        bool preview = name is "preview render" or "preview npc";
        bool staticallyVerified = name is "preview npc" or "plugin write" or
            "plugin verify" or "plugin audit" or "plugin deploy";
        return
        [
            AgentAuthorityState.Established,
            name == "plugin audit" ? AgentAuthorityState.Required : AgentAuthorityState.Established,
            readOnly ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Established,
            staticallyVerified ? AgentAuthorityState.Established : AgentAuthorityState.Required,
            preview ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required
        ];
    }

    private sealed record Task5Profile(
        ImmutableArray<string> Games,
        ImmutableArray<string> Inputs,
        ImmutableArray<string> Outputs,
        ImmutableArray<string> Effects,
        AgentRetryPolicy Retry,
        AgentDeterminism Determinism,
        ImmutableArray<AgentAuthorityState> Authority,
        string Result);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task5OptionProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["facegen build-geom"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "manifest:required:path:<K-local-manifest.json>::-", "output:required:path:<new-semantic-facegeom.json>::-",
                "npc:optional:artifactReference:<hexadecimal-form-id>::-", "allow-poison:optional:string:<presence-toggle>::-"],
            ["facegen build-geom-nif"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "asset-root:required:path:<K-local-root>::-", "source:required:path:<relative-or-K-local-NIF>::-", "output:required:path:<new-NIF>::-",
                "mode:optional:enum:bake|transport:bake|transport:-", "morphs:optional:json:<JSON-array|@K-local-file>::-",
                "source-sha256:optional:sha256:<64-hex-sha256>::-", "transport-profile:optional:enum:complete-carrier|geometry-into-carrier:complete-carrier|geometry-into-carrier:-",
                "carrier:optional:path:<relative-or-K-local-NIF>::-", "carrier-sha256:optional:sha256:<64-hex-sha256>::-", "shape:optional:identifier:<exact-shape-name>::-"],
            ["facegen build-geom-bound"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "data-root:required:path:<copied-Data-root>::-", "output-root:required:path:<K-local-output-root>::-", "npc:required:artifactReference:<hexadecimal-form-id>::-",
                "plugins:required:string:<plugin,...>::-", "morphs:required:json:<JSON-array|@K-local-file>::-"],
            ["facegen build-tint"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "manifest:required:path:<K-local-manifest.json>::-", "output:required:path:<new-semantic-facetint.json>::-", "npc:optional:artifactReference:<hexadecimal-form-id>::-",
                "resolution:optional:integer:512|1024|2048|4096|8192:512|1024|2048|4096|8192:-", "format:optional:enum:bgra8|bc3|bc7|uncompressed:bgra8|bc3|bc7|uncompressed:-",
                "mips:optional:integer:1:1:-", "alpha:optional:enum:preserve|opaque:preserve|opaque:-",
                "dds-output:optional:path:<new-dds>::-", "provider-root:optional:path:<K-local-provider-root>::-"],
            ["facegen build-tint-bound"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "data-root:required:path:<copied-Data-root>::-", "manifest:required:path:<K-local-manifest.json>::-", "output:required:path:<new-semantic-facetint.json>::-",
                "output-root:required:path:<K-local-output-root>::-", "npc:required:artifactReference:<hexadecimal-form-id>::-", "plugins:required:string:<plugin,...>::-",
                "format:optional:enum:bgra8|bc3|bc7|uncompressed:bgra8|bc3|bc7|uncompressed:-", "mips:optional:integer:1:1:-", "alpha:optional:enum:preserve|opaque:preserve|opaque:-"],
            ["facegen build-tint-native"] = [
                "edition:optional:enum:skyrimse:skyrimse:-", "game:optional:enum:skyrimse:skyrimse:edition", "data-root:required:path:<copied-Data-root>::-",
                "plugins:required:string:<plugin,...>::-", "npc:required:artifactReference:<Plugin.esp|0xXXXXXXXX>::-", "race:required:artifactReference:<Plugin.esm|0xXXXXXXXX>::-",
                "sex:required:enum:male|female:male|female:-", "output:required:path:<new-K-local-dds>::-"],
            ["facegen options"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "input:optional:path:<K-local-options.json>::-", "options:optional:path:<K-local-options.json>::input", "output:optional:path:<new-options.json>::-",
                "expected-sha256:optional:sha256:<64-hex-sha256>::-", "apply:optional:string:<presence-toggle>::-", "dry-run:optional:string:<presence-toggle>::-"],
            ["facegen build"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "manifest:required:path:<K-local-manifest.json>::-", "output:required:path:<new-semantic-corrections.json>::-", "corrections:optional:enum:auto:auto:-",
                "npc:optional:artifactReference:<hexadecimal-form-id>::-", "allow-poison:optional:string:<presence-toggle>::-"],
            ["facegen bake-all"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "manifests:optional:path:<K-local-batch.json>::-", "batch:optional:path:<K-local-batch.json>::manifests", "output:required:path:<new-semantic-batch.json>::-",
                "allow-poison:optional:string:<presence-toggle>::-"],
            ["facegen bake-all-native"] = [
                "edition:optional:enum:skyrimse:skyrimse:-", "game:optional:enum:skyrimse:skyrimse:edition", "data-root:required:path:<copied-Data-root>::-",
                "plugins:required:string:<plugin,...>::-", "output-root:required:path:<K-local-Data-root>::-", "winning-plugin:optional:identifier:<plugin-name>::-"],
            ["facegen build-plugin"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "manifest:optional:path:<K-local-targets.json>::-", "target:optional:path:<K-local-targets.json>::manifest", "plugin:optional:identifier:<plugin-name>::-",
                "target-plugin:optional:identifier:<plugin-name>::plugin", "output:required:path:<new-semantic-plugin-report.json>::-", "allow-poison:optional:string:<presence-toggle>::-"],
            ["facegen pack"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "data-root:required:path:<copied-Data-root>::-", "output-root:required:path:<new-K-local-package-root>::-", "npc:required:artifactReference:<hexadecimal-form-id>::-",
                "plugins:required:string:<plugin,...>::-", "anchor-plugin:required:identifier:<plugin-name>::-", "debug-sandbox:optional:string:<presence-toggle>::-", "shared-neutral-detail:optional:string:<presence-toggle>::-"],
            ["facegen deploy"] = [
                "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse:-", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse:edition",
                "package:required:path:<facegen-pack.json>::-", "data-root:required:path:<copied-Data-root>::-"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, Task5Profile> Task5Profiles =
        new Dictionary<string, Task5Profile>(StringComparer.Ordinal)
        {
            ["facegen build-geom"] = T5(["fallout4", "skyrimse"], ["facegen-shape-manifest"], ["semantic-facegeom-json"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "Written state, semantic artifact kind, edition, NPC FormID, output SHA-256, and diagnostics; the JSON is not a NIF and no grounded result schema identifier is published."),
            ["facegen build-geom-nif"] = T5(["fallout4", "skyrimse"],
                ["facegeom-source-nif", "facegeom-morph-selection@morphs:none:__absent__:present", "facegeom-carrier-nif@carrier:none:__absent__:present"], ["facegeom-nif"],
                ["readWorkspace:always", "invokeAdmittedProcess@all[optionValue,mode,noneOf,transport,true]", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "notApplicable", "established", "established", "notApplicable", "required", "required", "required"],
                "Written state and real NIF evidence including paths, hashes, byte and vertex counts, TRI/morph inputs, import mode, optional carrier hash, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published."),
            ["facegen build-geom-bound"] = T5(["fallout4", "skyrimse"], ["copied-data-root"], ["provider-bound-facegeom-nif"],
                ["readWorkspace:always", "invokeAdmittedProcess:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "established", "notApplicable", "required", "required", "required"],
                "Written state, provider-bound FaceGeom artifact, output SHA-256, and diagnostics; plugin loadability and runtime appearance remain separate and no grounded result schema identifier is published."),
            ["facegen build-tint"] = T5(["fallout4", "skyrimse"], ["facetint-layer-manifest"],
                ["semantic-facetint-json", "facetint-dds@dds-output:none:__absent__:present", "facetint-provider-evidence@provider-root:none:__absent__:present"],
                ["readWorkspace:always", "invokeAdmittedProcess@all[optionValue,provider-root,noneOf,__absent__,false]", "invokeAdmittedProcess@all[optionValue,dds-output,noneOf,__absent__,false;optionValue,format,anyOf,bc3|bc7,true]", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "Written state, artifact kind, edition, NPC FormID, dimensions, format, mip count, semantic output SHA-256, optional texture output path/SHA-256, and diagnostics; no grounded result schema identifier is published."),
            ["facegen build-tint-bound"] = T5(["fallout4", "skyrimse"], ["copied-data-root", "facetint-layer-manifest"], ["semantic-facetint-json", "provider-bound-facetint-dds"],
                ["readWorkspace:always", "invokeAdmittedProcess:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Written state, provider-bound FaceTint artifact, semantic and DDS hashes, and diagnostics; runtime appearance remains separate and no grounded result schema identifier is published."),
            ["facegen build-tint-native"] = T5(["skyrimse"], ["copied-data-root"], ["native-skyrim-facetint-dds"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "established", "notApplicable", "required", "required", "required"],
                "Written state, native Skyrim DDS artifact, plugin and mask authorities, independent readback evidence, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published."),
            ["facegen options"] = T5(["fallout4", "skyrimse"], ["chargen-facegen-options"], ["chargen-facegen-options@apply:none:__absent__:present"],
                ["readWorkspace:always", "writeNewArtifact@all[optionValue,apply,noneOf,__absent__,false]", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "SchemaVersion 1 validation/apply state, edition, input/output paths and hashes, typed options, and diagnostics; apply is presence-selected and no grounded result schema identifier is published."),
            ["facegen build"] = T5(["fallout4", "skyrimse"], ["facegen-shape-manifest"], ["semantic-facegen-correction-report"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "Written state, semantic correction artifact kind, edition, NPC FormID, correction count, output SHA-256, and diagnostics; no grounded result schema identifier is published."),
            ["facegen bake-all"] = T5(["fallout4", "skyrimse"], ["facegen-batch-manifest"], ["semantic-facegen-batch-report"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "Written/failure state, semantic batch artifact kind, attempted/passed/skipped/failed counts, output SHA-256, and diagnostics; no NIF/DDS output or grounded result schema identifier is published."),
            ["facegen bake-all-native"] = T5(["skyrimse"], ["copied-data-root"], ["native-skyrim-facegen-pairs"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "established", "notApplicable", "required", "required", "required"],
                "SchemaVersion 1 native batch status with discovered/baked/skipped/failed counts, ordered per-NPC outcomes, progress, diagnostics, and runtimeAuthority false. Partial outputs are retained; a safe retry skips complete pairs and reports half-pair conflicts per-NPC. No grounded result schema identifier is published."),
            ["facegen build-plugin"] = T5(["fallout4", "skyrimse"], ["facegen-plugin-target-manifest"], ["semantic-facegen-plugin-report"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                ["established", "notApplicable", "established", "required", "notApplicable", "required", "required", "required"],
                "Written/failure state, semantic plugin report kind, target plugin, attempted/selected/excluded/passed/skipped/failed counts, output SHA-256, and diagnostics; no grounded result schema identifier is published."),
            ["facegen pack"] = T5(["fallout4", "skyrimse"], ["copied-data-root"], ["facegen-package"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Written state, fresh package root, facegen-pack artifact and manifest SHA-256, and diagnostics. The deterministic Data package is source-preserving, non-overwriting, and runtimeProof false; no grounded result schema identifier is published."),
            ["facegen deploy"] = T5(["fallout4", "skyrimse"], ["facegen-package"], ["copied-data-facegen-files"],
                ["readWorkspace:always", "deployToCopiedData:always", "appendLocalOperationJournal:always"], AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
                ["established", "established", "established", "established", "notApplicable", "required", "required", "required"],
                "Deployed/alreadyPresent state, edition, package and copied Data paths, per-file hashes and outcomes, and diagnostics. Existing identical files are idempotent; any differing destination conflict refuses the whole preflight. No grounded result schema identifier is published.")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static async Task AssertTask5ContractsAsync()
    {
        AssertTask5ReviewCorrections();
        foreach ((string name, ImmutableArray<string> expected) in Task5OptionProfiles)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete,
                $"Task 5 contract stayed incomplete: {name}.");
            Assert(contract.Readiness == ProtocolReadiness.Legacy && contract.ResultSchemaIds.IsEmpty,
                $"Task 5 readiness or schema honesty drifted: {name}.");
            Assert(contract.Options.Select(Task5OptionProfile).SequenceEqual(expected, StringComparer.Ordinal),
                $"Task 5 option profile drifted: {name}: {string.Join(";", contract.Options.Select(Task5OptionProfile))}");
            Assert(contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Length == 8 && contract.Authority.All(item => item.Reason.Length > 0),
                $"Task 5 authority coverage drifted: {name}.");
            Assert(contract.InputArtifactKinds.SequenceEqual(contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal) &&
                   contract.InputArtifacts.AddRange(contract.OutputArtifacts).All(item => item.SchemaIds.IsEmpty),
                $"Task 5 artifact projection or schema honesty drifted: {name}.");
            Task5Profile profile = Task5Profiles[name];
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(profile.Games, StringComparer.Ordinal) &&
                   contract.InputArtifacts.Select(ArtifactProfile).SequenceEqual(profile.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(profile.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(Task5EffectProfile).SequenceEqual(profile.Effects, StringComparer.Ordinal) &&
                   contract.RetryPolicy == profile.Retry && contract.Determinism == profile.Determinism &&
                   contract.Authority.Select(item => item.State).SequenceEqual(profile.Authority) &&
                   contract.ResultShape == "object" && contract.ResultDescription == profile.Result,
                $"Task 5 full contract profile drifted: {name}.");
        }

        foreach (string command in new[] { "facegen build-geom", "facegen build", "facegen bake-all", "facegen build-plugin" })
        {
            AgentOptionContract toggle = AgentCommandRegistry.GetRequired(command).Options.Single(item => item.CliName == "allow-poison");
            Assert(toggle.ValueKind == AgentValueKind.String && toggle.ValueSyntax == "<presence-toggle>" &&
                   toggle.Description.Contains("false", StringComparison.Ordinal),
                $"Presence-vs-value allow-poison semantics drifted: {command}.");
        }
        AgentCommandContract options = AgentCommandRegistry.GetRequired("facegen options");
        Assert(options.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne && item.Options.SequenceEqual(["input", "options"], StringComparer.Ordinal)) &&
               options.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.ForbiddenWhen && item.Options.SequenceEqual(["apply", "dry-run"], StringComparer.Ordinal) && IsAllPresentTrigger(item.Trigger, "apply", "dry-run")) &&
               options.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.RequiredWhen && item.Options.SequenceEqual(["output"], StringComparer.Ordinal) && IsPresentTrigger(item.Trigger, "apply")) &&
               options.OutputArtifacts.Single().Trigger is { } optionsWrite && IsPresentTrigger(optionsWrite, "apply"),
            "FaceGen options aliases, presence conflict, or conditional write drifted.");

        foreach ((string command, string option) in new[]
                 {
                     ("facegen options", "apply"), ("facegen options", "dry-run"),
                     ("facegen pack", "debug-sandbox"), ("facegen pack", "shared-neutral-detail")
                 })
        {
            AgentOptionContract toggle = AgentCommandRegistry.GetRequired(command).Options.Single(item => item.CliName == option);
            Assert(toggle.ValueKind == AgentValueKind.String && toggle.ValueSyntax == "<presence-toggle>" &&
                   toggle.AllowedValues.IsEmpty && toggle.Description.Contains("false", StringComparison.Ordinal),
                $"Task 5 presence toggle was misrepresented as value-selected: {command} --{option}.");
        }

        AgentCommandContract binary = AgentCommandRegistry.GetRequired("facegen build-geom-nif");
        Assert(binary.OptionRelationships.Select(Task4RelationshipProfile).SequenceEqual([
                   "atLeastOne:edition,game>:trigger=none",
                   "requiredWhen:morphs>mode:trigger=all[optionValue,mode,noneOf,transport,true]",
                   "requiredWhen:source-sha256,transport-profile>mode:trigger=all[optionValue,mode,anyOf,transport,false]",
                   "requiredWhen:carrier,carrier-sha256,shape>transport-profile:trigger=all[optionValue,transport-profile,anyOf,geometry-into-carrier,false]",
                   "forbiddenWhen:carrier,carrier-sha256,shape>transport-profile:trigger=all[optionValue,transport-profile,anyOf,complete-carrier,false]",
                   "forbiddenWhen:source-sha256,transport-profile,carrier,carrier-sha256,shape>mode:trigger=all[optionValue,mode,noneOf,transport,true]"
               ], StringComparer.Ordinal) &&
               binary.Effects.Any(item => item.Kind == AgentEffectKind.InvokeAdmittedProcess),
            "FaceGeom NIF mode, carrier, or admitted-process contract drifted.");

        AgentCommandContract tint = AgentCommandRegistry.GetRequired("facegen build-tint");
        Assert(tint.OutputArtifacts.Select(ArtifactProfile).SequenceEqual([
                   "semantic-facetint-json", "facetint-dds@dds-output:none:__absent__:present",
                   "facetint-provider-evidence@provider-root:none:__absent__:present"], StringComparer.Ordinal) &&
               tint.Options.Single(item => item.CliName == "format").Description.Contains("uncompressed aliases bgra8", StringComparison.Ordinal),
            "FaceTint conditional DDS/provider or format-alias contract drifted.");

        AgentCommandContract nativeTint = AgentCommandRegistry.GetRequired("facegen build-tint-native");
        AgentCommandContract nativeBatch = AgentCommandRegistry.GetRequired("facegen bake-all-native");
        Assert(nativeTint.SupportedGames.SequenceEqual([GameEdition.SkyrimSpecialEdition]) &&
               nativeBatch.SupportedGames.SequenceEqual([GameEdition.SkyrimSpecialEdition]) &&
               nativeBatch.RetryPolicy == AgentRetryPolicy.SafeUnchanged &&
               nativeBatch.ResultDescription.Contains("partial", StringComparison.OrdinalIgnoreCase) &&
               nativeBatch.ResultDescription.Contains("per-NPC", StringComparison.Ordinal),
            "Native game restriction or partial-batch retry contract drifted.");

        AgentCommandContract pack = AgentCommandRegistry.GetRequired("facegen pack");
        AgentCommandContract deploy = AgentCommandRegistry.GetRequired("facegen deploy");
        Assert(pack.RetryPolicy == AgentRetryPolicy.RequiresFreshOutput &&
               pack.Determinism == AgentDeterminism.PinnedInputsAndTools &&
               deploy.RetryPolicy == AgentRetryPolicy.SafeUnchanged &&
               deploy.Effects.Any(item => item.Kind == AgentEffectKind.DeployToCopiedData) &&
               deploy.ResultDescription.Contains("identical", StringComparison.OrdinalIgnoreCase) &&
               deploy.ResultDescription.Contains("conflict", StringComparison.OrdinalIgnoreCase),
            "Pack freshness or deploy idempotence metadata drifted.");

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode jsonExit = await runner.RunAsync(CommandLine.Parse(
            ["facegen", "build-tint", "--help", "--json"]), CancellationToken.None);
        Assert(jsonExit == CommandExitCode.Success && error.ToString().Length == 0 &&
               JsonDocument.Parse(output.ToString()).RootElement.GetProperty("options").GetArrayLength() == 11,
            "Task 5 representative JSON help drifted.");
        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["facegen", "build-geom-nif", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("--transport-profile complete-carrier|geometry-into-carrier", StringComparison.Ordinal),
            "Task 5 representative human help drifted.");
        CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
            ["facegen", "deploy", "--protocol", "2", "--json"]);
        Assert(refused.ExitCode != 0 && refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
            "Task 5 legacy command became protocol-2 callable.");
    }

    private static void AssertTask5ReviewCorrections()
    {
        var failures = ImmutableArray.CreateBuilder<string>();
        foreach (string command in new[] { "facegen build-tint-bound", "facegen options", "facegen pack" })
        {
            AgentAuthorityState state = AgentCommandRegistry.GetRequired(command).Authority
                .Single(item => item.Kind == AgentAuthorityKind.IndependentStaticVerification).State;
            if (state != AgentAuthorityState.Required) failures.Add($"static verification: {command}={state}");
        }

        AgentCommandContract tint = AgentCommandRegistry.GetRequired("facegen build-tint");
        string[] processEffects = tint.Effects.Where(item => item.Kind == AgentEffectKind.InvokeAdmittedProcess)
            .Select(Task5EffectProfile).ToArray();
        string[] expectedEffects =
        [
            "invokeAdmittedProcess@all[optionValue,provider-root,noneOf,__absent__,false]",
            "invokeAdmittedProcess@all[optionValue,dds-output,noneOf,__absent__,false;optionValue,format,anyOf,bc3|bc7,true]"
        ];
        if (!processEffects.SequenceEqual(expectedEffects, StringComparer.Ordinal) ||
            tint.Determinism != AgentDeterminism.PinnedInputsAndTools)
            failures.Add($"conditional process/determinism: {string.Join(';', processEffects)} / {tint.Determinism}");

        const string result = "Written state, artifact kind, edition, NPC FormID, dimensions, format, mip count, semantic output SHA-256, optional texture output path/SHA-256, and diagnostics; no grounded result schema identifier is published.";
        if (tint.ResultDescription != result) failures.Add($"V1 result envelope: {tint.ResultDescription}");

        AgentCommandContract binary = AgentCommandRegistry.GetRequired("facegen build-geom-nif");
        bool exactSource = FaceGenMaterializationOptionCatalog.Contains(binary.Name) &&
            binary.Options.Single(item => item.CliName == "source").ValueSyntax == "<relative-or-K-local-NIF>" &&
            binary.Options.Single(item => item.CliName == "carrier").ValueSyntax == "<relative-or-K-local-NIF>";
        if (!exactSource) failures.Add("binary FaceGeom option source/path syntax");

        Assert(failures.Count == 0, "Task 5 review findings remained: " + string.Join(" | ", failures));
    }

    private static string Task5OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}:{option.AliasFor ?? "-"}";

    private static Task5Profile T5(
        ImmutableArray<string> games, ImmutableArray<string> inputs,
        ImmutableArray<string> outputs, ImmutableArray<string> effects,
        AgentRetryPolicy retry, AgentDeterminism determinism,
        ImmutableArray<string> authority, string result) =>
        new(games, inputs, outputs, effects, retry, determinism,
            authority.Select(ParseAuthorityState).ToImmutableArray(), result);

    private static readonly ImmutableArray<string> Task4Names =
    [
        "npc patch", "body patch", "npc face-patch", "npc edit-package",
        "facegen diagnose", "facegen analyze", "facegen verify",
        "facegen resolve-providers", "facegen plan-pack"
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task4Options =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc patch"] = PatchOptionNames(),
            ["body patch"] = PatchOptionNames(),
            ["npc face-patch"] = ["game", "edition", "plugin", "output", "data-root", "npc", "form-id", "headparts", "headpart-replace", "hair-color", "expected-sha256", "proposal", "dry-run", "apply"],
            ["npc edit-package"] = ["consolidate", "esl-flag", "output", "edition", "game", "input-plugin", "input-sha256", "expected-sha256", "npc", "output-root", "plugin", "editor-id", "name", "short-name", "race", "voice", "class", "combat-style", "level", "level-mult", "magicka-offset", "stamina-offset", "health-offset", "calc-min", "calc-max", "speed-multiplier", "disposition", "bleedout", "player-health", "player-magicka", "player-stamina", "skill-values", "skill-offsets", "far-model-distance", "geared-weapons", "set-flag", "clear-flag", "keywords", "add-keyword", "remove-keyword", "factions", "add-faction", "update-faction", "remove-faction", "inventory", "add-inventory", "update-inventory", "remove-inventory", "default-outfit", "sleep-outfit", "perks", "add-perk", "update-perk", "remove-perk", "actor-effects", "add-actor-effect", "remove-actor-effect", "output-kind"],
            ["facegen diagnose"] = ["edition", "game", "manifest", "npc"],
            ["facegen analyze"] = ["edition", "game", "manifest", "npc"],
            ["facegen verify"] = ["edition", "game", "manifest", "npc", "strict-shapes"],
            ["facegen resolve-providers"] = ["edition", "game", "data-root", "npc", "plugins", "shared-neutral-detail"],
            ["facegen plan-pack"] = ["edition", "game", "data-root", "npc", "plugins", "anchor-plugin", "debug-sandbox", "shared-neutral-detail"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableArray<string> PatchOptionNames() =>
    [
        "edition", "game", "input-plugin", "output", "form-id", "npc", "editor-id", "name", "sex", "race", "voice", "class", "combat-style", "level", "level-mult", "magicka-offset", "stamina-offset", "health-offset", "calc-min", "calc-max", "speed-multiplier", "disposition", "bleedout", "player-health", "player-magicka", "player-stamina", "skill-values", "skill-offsets", "height", "far-model-distance", "geared-weapons", "xp-offset", "set-flag", "clear-flag", "keywords", "add-keyword", "remove-keyword", "factions", "add-faction", "update-faction", "remove-faction", "inventory", "add-inventory", "update-inventory", "remove-inventory", "default-outfit", "sleep-outfit", "perks", "add-perk", "update-perk", "remove-perk", "actor-effects", "add-actor-effect", "remove-actor-effect", "properties", "add-property", "update-property", "remove-property", "appr", "add-appr", "remove-appr", "skin", "clear-skin", "preset-skin", "regions", "weight", "weight-triangle", "input-sha", "input-sha256", "expected-sha256", "proposal", "dry-run", "apply", "whole-skin", "whole-skin-sha256", "aidt"
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<AgentAuthorityState>> Task4Authority =
        new Dictionary<string, ImmutableArray<AgentAuthorityState>>(StringComparer.Ordinal)
        {
            ["npc patch"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["body patch"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["npc face-patch"] = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.Required, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["npc edit-package"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["facegen diagnose"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["facegen analyze"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["facegen verify"] = [AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["facegen resolve-providers"] = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required],
            ["facegen plan-pack"] = [AgentAuthorityState.Established, AgentAuthorityState.Established, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.NotApplicable, AgentAuthorityState.Required, AgentAuthorityState.Required, AgentAuthorityState.Required]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task4Inputs =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc patch"] = ["npc-patch-source-plugin", "npc-whole-skin"], ["body patch"] = ["npc-patch-source-plugin", "npc-whole-skin"],
            ["npc face-patch"] = ["npc-face-patch-source-plugin", "npc-face-patch-data-root"],
            ["npc edit-package"] = ["existing-npc-source-plugin"],
            ["facegen diagnose"] = ["facegen-shape-manifest"], ["facegen analyze"] = ["facegen-shape-manifest"],
            ["facegen verify"] = ["facegen-shape-manifest"],
            ["facegen resolve-providers"] = ["copied-data-root"], ["facegen plan-pack"] = ["copied-data-root"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, string> Task4Results =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npc patch"] = "npc patch proposal or apply outcome, exact hashes, changes, preserved fields, and diagnostics; no grounded result schema identifier is published.",
            ["body patch"] = "body patch proposal or apply outcome, exact hashes, changes, preserved fields, and diagnostics; no grounded result schema identifier is published.",
            ["npc face-patch"] = "FacePatchResponse applicability and applied state, edition, source and output plugin paths, target FormID, input and optional output SHA-256 hashes, changes, preserved fields, providers, and diagnostics; no grounded result schema identifier is published.",
            ["npc edit-package"] = "ExistingNpcEditResult completion and verdict, output-plugin and manifest paths and SHA-256 hashes, mutation changes, package verification, optional consolidationEvidencePath for fresh-plugin mode, and diagnostics; no grounded result schema identifier is published.",
            ["facegen diagnose"] = "Applicability, accepted shape evidence, and diagnostics; no grounded result schema identifier is published.",
            ["facegen analyze"] = "Applicability, accepted shape evidence, and diagnostics; no grounded result schema identifier is published.",
            ["facegen verify"] = "Applicability, accepted shape evidence, and diagnostics; no grounded result schema identifier is published.",
            ["facegen resolve-providers"] = "Provider-resolution verdict, artifact-kind/version evidence, provider paths and hashes, and diagnostics; artifact version is not promoted to a document schema identifier.",
            ["facegen plan-pack"] = "Pack-plan verdict, artifact-kind/version evidence, planned files and hashes, and diagnostics; artifact version is not promoted to a document schema identifier."
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task4Outputs =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc patch"] = ["npc-patch-proposal:npc.patch.proposal.v1,npc.patch.proposal.v2@proposal:none:__absent__:present", "npc-patch-output-plugin@apply:any:true|1:present"],
            ["body patch"] = ["npc-patch-proposal:npc.patch.proposal.v1,npc.patch.proposal.v2@proposal:none:__absent__:present", "npc-patch-output-plugin@apply:any:true|1:present"],
            ["npc face-patch"] = ["npc-face-patch-proposal@proposal:none:__absent__:present", "npc-face-patch-output-plugin@apply:any:true|1:present"],
            ["npc edit-package"] = ["existing-npc-edit-package@output-root:none:__absent__:present", "plugin-consolidation-output@output:none:__absent__:present", "plugin-consolidation-evidence@output:none:__absent__:present"],
            ["facegen diagnose"] = [], ["facegen analyze"] = [], ["facegen verify"] = [],
            ["facegen resolve-providers"] = [], ["facegen plan-pack"] = []
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task4Effects =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["npc patch"] = ["readWorkspace:always", "writeNewArtifact@proposal:none:__absent__:present", "writeNewArtifact@apply:any:true|1:present", "appendLocalOperationJournal:always"],
            ["body patch"] = ["readWorkspace:always", "writeNewArtifact@proposal:none:__absent__:present", "writeNewArtifact@apply:any:true|1:present", "appendLocalOperationJournal:always"],
            ["npc face-patch"] = ["readWorkspace:always", "writeNewArtifact@proposal:none:__absent__:present", "writeNewArtifact@apply:any:true|1:present", "appendLocalOperationJournal:always"],
            ["npc edit-package"] = ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
            ["facegen diagnose"] = ["readWorkspace:always", "appendLocalOperationJournal:always"],
            ["facegen analyze"] = ["readWorkspace:always", "appendLocalOperationJournal:always"],
            ["facegen verify"] = ["readWorkspace:always", "appendLocalOperationJournal:always"],
            ["facegen resolve-providers"] = ["readWorkspace:always", "appendLocalOperationJournal:always"],
            ["facegen plan-pack"] = ["readWorkspace:always", "appendLocalOperationJournal:always"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static async Task AssertTask4ContractsAsync()
    {
        foreach (string name in Task4Names)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.ContractStatus == AgentContractStatus.Complete,
                $"Task 4 contract stayed incomplete: {name}.");
            Assert(contract.Readiness == ProtocolReadiness.Legacy,
                $"Task 4 command became protocol-2 callable: {name}.");
            Assert(contract.Options.Select(item => item.CliName).SequenceEqual(
                       Task4Options[name], StringComparer.Ordinal) &&
                   contract.Options.All(item => item.ValueSyntax.Length > 0 && item.Description.Length > 0) &&
                   contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(
                       name == "npc edit-package" ? ["skyrimse"] : ["fallout4", "skyrimse"], StringComparer.Ordinal) &&
                   contract.ResultShape == "object" && contract.ResultDescription.Length > 0 &&
                   contract.ResultDescription == Task4Results[name] &&
                   contract.ResultSchemaIds.IsEmpty &&
                   contract.RetryPolicy == (name.StartsWith("facegen ", StringComparison.Ordinal)
                       ? AgentRetryPolicy.SafeUnchanged : AgentRetryPolicy.RequiresFreshOutput) &&
                   contract.Determinism == AgentDeterminism.Deterministic &&
                   contract.InputArtifacts.Select(item => item.Kind).SequenceEqual(Task4Inputs[name], StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(Task4Outputs[name], StringComparer.Ordinal) &&
                   contract.Effects.Select(EffectProfile).SequenceEqual(Task4Effects[name], StringComparer.Ordinal) &&
                   contract.SupportsDryRun == (name is "npc patch" or "body patch" or "npc face-patch") &&
                   contract.InputArtifactKinds.SequenceEqual(contract.InputArtifacts.Select(item => item.Kind), StringComparer.Ordinal) &&
                   contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Authority.Select(item => item.State).SequenceEqual(Task4Authority[name]),
                $"Task 4 full profile drifted: {name}.");
        }

        AgentCommandContract npcPatch = AgentCommandRegistry.GetRequired("npc patch");
        AgentCommandContract bodyPatch = AgentCommandRegistry.GetRequired("body patch");
        Assert(npcPatch.Options.Select(OptionProfile).SequenceEqual(
                   bodyPatch.Options.Select(OptionProfile), StringComparer.Ordinal) &&
               npcPatch.Options.Select(item => item.AliasFor ?? "").SequenceEqual(
                   bodyPatch.Options.Select(item => item.AliasFor ?? ""), StringComparer.Ordinal) &&
               npcPatch.Options.Select(item => string.Join('|', item.ConflictsWith)).SequenceEqual(
                   bodyPatch.Options.Select(item => string.Join('|', item.ConflictsWith)), StringComparer.Ordinal),
            "Patch routes do not publish identical unrestricted option rows.");
        Assert(npcPatch.Purpose.Contains("NPC mutation", StringComparison.Ordinal) &&
               bodyPatch.Purpose.Contains("same unrestricted", StringComparison.Ordinal),
            "Patch route purposes are not truthful.");
        Assert(npcPatch.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["npc-patch-proposal:npc.patch.proposal.v1,npc.patch.proposal.v2@proposal:none:__absent__:present", "npc-patch-output-plugin@apply:any:true|1:present"], StringComparer.Ordinal) &&
               bodyPatch.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["npc-patch-proposal:npc.patch.proposal.v1,npc.patch.proposal.v2@proposal:none:__absent__:present", "npc-patch-output-plugin@apply:any:true|1:present"], StringComparer.Ordinal) &&
               npcPatch.Effects.Select(EffectProfile).SequenceEqual(
                   ["readWorkspace:always", "writeNewArtifact@proposal:none:__absent__:present", "writeNewArtifact@apply:any:true|1:present", "appendLocalOperationJournal:always"], StringComparer.Ordinal),
            "Patch routes do not publish the same conditional artifacts/effects.");
        foreach (AgentCommandContract patch in new[] { npcPatch, bodyPatch })
        {
            Assert(patch.Options.Single(item => item.CliName == "npc").AliasFor == "form-id" &&
                   patch.Options.Single(item => item.CliName == "input-sha").AliasFor == "input-sha256" &&
                   patch.Options.Single(item => item.CliName == "expected-sha256").AliasFor == "input-sha256" &&
                   patch.Options.Single(item => item.CliName == "level").ConflictsWith.SequenceEqual(["level-mult"], StringComparer.Ordinal) &&
                   patch.Options.Single(item => item.CliName == "weight").ConflictsWith.SequenceEqual(["weight-triangle"], StringComparer.Ordinal) &&
                   patch.OptionRelationships.Where(item => item.Kind == AgentOptionRelationshipKind.RequiresTogether)
                       .Select(RelationshipProfile).SequenceEqual(["requiresTogether:whole-skin,whole-skin-sha256>"], StringComparer.Ordinal) &&
                   patch.InputArtifacts.Select(ArtifactProfile).SequenceEqual(
                       ["npc-patch-source-plugin", "npc-whole-skin:npc.whole-skin.request.v1@whole-skin:none:__absent__:present"], StringComparer.Ordinal) &&
                   patch.OptionRelationships.Count(item => item.Kind == AgentOptionRelationshipKind.ForbiddenWhen) == 4,
                $"Patch alias/conflict graph drifted: {patch.Name}.");
        }

        AgentCommandContract face = AgentCommandRegistry.GetRequired("npc face-patch");
        Assert(face.Options.Select(Task4OptionProfile).SequenceEqual(FacePatchOptionProfile(), StringComparer.Ordinal),
            "Face-patch option profiles drifted: " + string.Join(";", face.Options.Select(Task4OptionProfile)));
        Assert(face.Options.Single(item => item.CliName == "edition").AliasFor == "game" &&
               face.Options.Single(item => item.CliName == "npc").AliasFor == "form-id" &&
               face.OptionRelationships.Select(Task4RelationshipProfile).SequenceEqual(
                   [
                       "atLeastOne:game,edition>:trigger=none",
                       "atLeastOne:npc,form-id>:trigger=none",
                       "atLeastOne:headparts,headpart-replace,hair-color>:trigger=none",
                       "forbiddenWhen:headparts,headpart-replace>headparts,headpart-replace:trigger=all[optionValue,headparts,noneOf,__absent__,false;optionValue,headpart-replace,noneOf,__absent__,false]",
                       "forbiddenWhen:apply,dry-run>apply,dry-run:trigger=all[optionValue,apply,anyOf,true|1,false;optionValue,dry-run,anyOf,true|1,false]"
                   ], StringComparer.Ordinal),
            "Face-patch precedence or typed relationships drifted: " + string.Join(";", face.OptionRelationships.Select(Task4RelationshipProfile)));
        Assert(face.OutputArtifacts.Select(ArtifactProfile).SequenceEqual(
                   ["npc-face-patch-proposal@proposal:none:__absent__:present", "npc-face-patch-output-plugin@apply:any:true|1:present"], StringComparer.Ordinal) &&
               face.Effects.Select(EffectProfile).SequenceEqual(
                   ["readWorkspace:always", "writeNewArtifact@proposal:none:__absent__:present", "writeNewArtifact@apply:any:true|1:present", "appendLocalOperationJournal:always"], StringComparer.Ordinal),
            "Face-patch conditional artifacts/effects drifted.");

        AgentCommandContract edit = AgentCommandRegistry.GetRequired("npc edit-package");
        Assert(edit.Options.Select(Task4OptionProfile).SequenceEqual(EditPackageOptionProfile(), StringComparer.Ordinal) &&
               edit.Options.Single(item => item.CliName == "expected-sha256").AliasFor == "input-sha256" &&
               edit.Options.Single(item => item.CliName == "plugin").ValueKind == AgentValueKind.Identifier &&
               edit.Options.Single(item => item.CliName == "output-kind").AllowedValues.SequenceEqual(
                   ["source-mastered-override", "standalone-copy"], StringComparer.Ordinal) &&
               edit.ResultDescription == "ExistingNpcEditResult completion and verdict, output-plugin and manifest paths and SHA-256 hashes, mutation changes, package verification, optional consolidationEvidencePath for fresh-plugin mode, and diagnostics; no grounded result schema identifier is published." &&
               !edit.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                   item.Options.Any(option => option is "editor-id" or "name" or "level")),
            "Edit-package precedence, output dialect, or permissive mutation grammar drifted.");

        AgentCommandContract diagnose = AgentCommandRegistry.GetRequired("facegen diagnose");
        AgentCommandContract analyze = AgentCommandRegistry.GetRequired("facegen analyze");
        Assert(analyze.CanonicalCommand == "facegen diagnose" && analyze.Options.SequenceEqual(diagnose.Options),
            "facegen analyze is not a genuine diagnose alias.");
        Assert(diagnose.Options.Select(Task4OptionProfile).SequenceEqual(
                   ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "manifest:required:path:<K-local-manifest.json>:", "npc:optional:artifactReference:<hexadecimal-form-id>:"], StringComparer.Ordinal) &&
               analyze.Options.Select(Task4OptionProfile).SequenceEqual(
                   ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "manifest:required:path:<K-local-manifest.json>:", "npc:optional:artifactReference:<hexadecimal-form-id>:"], StringComparer.Ordinal) &&
               AgentCommandRegistry.GetRequired("facegen verify").Options.Select(Task4OptionProfile).SequenceEqual(
                   ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "manifest:required:path:<K-local-manifest.json>:", "npc:optional:artifactReference:<hexadecimal-form-id>:", "strict-shapes:optional:string:<presence-toggle>:"], StringComparer.Ordinal) &&
               AgentCommandRegistry.GetRequired("facegen resolve-providers").Options.Select(Task4OptionProfile).SequenceEqual(
                   ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "data-root:required:path:<copied-Data-root>:", "npc:required:artifactReference:<hexadecimal-form-id>:", "plugins:required:string:<plugin,...>:", "shared-neutral-detail:optional:string:<presence-toggle>:"], StringComparer.Ordinal) &&
               AgentCommandRegistry.GetRequired("facegen plan-pack").Options.Select(Task4OptionProfile).SequenceEqual(
                   ["edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse", "data-root:required:path:<copied-Data-root>:", "npc:required:artifactReference:<hexadecimal-form-id>:", "plugins:required:string:<plugin,...>:", "anchor-plugin:required:identifier:<plugin-name>:", "debug-sandbox:optional:string:<presence-toggle>:", "shared-neutral-detail:optional:string:<presence-toggle>:"], StringComparer.Ordinal),
            "Read-only FaceGen literal option profiles drifted.");
        foreach ((string command, string option) in new[]
                 {
                     ("facegen verify", "strict-shapes"),
                     ("facegen resolve-providers", "shared-neutral-detail"),
                     ("facegen plan-pack", "debug-sandbox"),
                     ("facegen plan-pack", "shared-neutral-detail")
                 })
        {
            AgentOptionContract toggle = AgentCommandRegistry.GetRequired(command).Options.Single(item => item.CliName == option);
            Assert(toggle.ValueKind == AgentValueKind.String && toggle.ValueSyntax == "<presence-toggle>" && toggle.AllowedValues.IsEmpty &&
                   toggle.Description.Contains("false", StringComparison.Ordinal),
                $"Presence toggle was misrepresented as a value-selected Boolean: {command} --{option}.");
        }
        Assert(Task4Names.Where(name => name.StartsWith("facegen ", StringComparison.Ordinal))
                   .Select(AgentCommandRegistry.GetRequired).All(contract =>
                       contract.InputArtifacts.AddRange(contract.OutputArtifacts).All(artifact => artifact.SchemaIds.IsEmpty) &&
                       contract.OutputArtifacts.IsEmpty &&
                       contract.Effects.All(effect => effect.Kind != AgentEffectKind.WriteNewArtifact) &&
                       contract.RetryPolicy == AgentRetryPolicy.SafeUnchanged),
            "Read-only FaceGen invented schema identifiers or write behavior.");

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode jsonExit = await runner.RunAsync(CommandLine.Parse(
            ["facegen", "verify", "--help", "--json"]), CancellationToken.None);
        Assert(jsonExit == CommandExitCode.Success && error.ToString().Length == 0,
            "Task 4 representative V1 JSON help failed.");
        using JsonDocument help = JsonDocument.Parse(output.ToString());
        Assert(help.RootElement.GetProperty("name").GetString() == "facegen verify" &&
               help.RootElement.GetProperty("options").GetArrayLength() == 5,
            "Task 4 representative V1 JSON help drifted.");
        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["npc", "face-patch", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("--headparts <JSON-array|type=Plugin|FormID,...>", StringComparison.Ordinal),
            "Task 4 representative human help drifted.");
        CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
            ["facegen", "verify", "--protocol", "2", "--json"]);
        Assert(refused.ExitCode != 0 && refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
            "Task 4 legacy command became protocol-2 callable.");
    }

    private static ImmutableArray<string> FacePatchOptionProfile() =>
    [
        "game:optional:enum:fallout4|skyrimse:fallout4|skyrimse",
        "edition:optional:enum:fallout4|skyrimse:fallout4|skyrimse",
        "plugin:required:path:<K-local-plugin>:", "output:required:path:<new-plugin>:",
        "data-root:required:path:<K-local-data-root>:", "npc:optional:artifactReference:<hexadecimal-form-id>:",
        "form-id:optional:artifactReference:<hexadecimal-form-id>:",
        "headparts:optional:string:<JSON-array|type=Plugin|FormID,...>:",
        "headpart-replace:optional:string:<old-Plugin|FormID=new-local-FormID>:",
        "hair-color:optional:artifactReference:<Plugin|FormID|none>:",
        "expected-sha256:optional:sha256:<64-hex-sha256>:", "proposal:optional:path:<new-proposal.json>:",
        "dry-run:optional:boolean:true|false|1:true|false|1", "apply:optional:boolean:true|false|1:true|false|1"
    ];

    private static ImmutableArray<string> EditPackageOptionProfile() =>
    [
        "consolidate:optional:string:<copied-provider.esp[,copied-provider.esp]>:",
        "esl-flag:optional:boolean:true|1:true|1", "output:optional:path:<fresh-K-local-plugin.esp>:",
        "edition:optional:enum:skyrimse:skyrimse", "game:optional:enum:skyrimse:skyrimse",
        "input-plugin:required:path:<K-local-plugin>:", "input-sha256:optional:sha256:<64-hex-sha256>:",
        "expected-sha256:optional:sha256:<64-hex-sha256>:", "npc:optional:artifactReference:<hexadecimal-form-id>:",
        "output-root:optional:path:<new-K-local-directory>:", "plugin:optional:identifier:<new-plugin.esp>:",
        "editor-id:optional:identifier:<ASCII-letter[A-Za-z0-9_]{0,63}>:",
        "name:optional:string:<0..255-non-control-characters>:", "short-name:optional:string:<0..255-non-control-characters>:",
        "race:optional:artifactReference:<Plugin.esp|0xFormID|none>:", "voice:optional:artifactReference:<Plugin.esp|0xFormID|none>:",
        "class:optional:artifactReference:<Plugin.esp|0xFormID|none>:", "combat-style:optional:artifactReference:<Plugin.esp|0xFormID|none>:",
        "level:optional:string:<invariant-decimal-integer-0..65535>:",
        "level-mult:optional:string:<invariant-decimal-0..65.535-max-3-fraction-digits>:",
        "magicka-offset:optional:integer:<signed-16-bit-integer>:", "stamina-offset:optional:integer:<signed-16-bit-integer>:",
        "health-offset:optional:integer:<signed-16-bit-integer>:", "calc-min:optional:integer:<unsigned-16-bit-integer>:",
        "calc-max:optional:integer:<unsigned-16-bit-integer>:", "speed-multiplier:optional:integer:<signed-16-bit-integer>:",
        "disposition:optional:integer:<signed-16-bit-integer>:", "bleedout:optional:integer:<signed-16-bit-integer>:",
        "player-health:optional:integer:<unsigned-16-bit-integer>:", "player-magicka:optional:integer:<unsigned-16-bit-integer>:",
        "player-stamina:optional:integer:<unsigned-16-bit-integer>:",
        "skill-values:optional:string:<skill=unsigned-byte,...>:", "skill-offsets:optional:string:<skill=unsigned-byte,...>:",
        "far-model-distance:optional:string:<invariant-single-precision-number>:", "geared-weapons:optional:integer:<unsigned-byte>:",
        "set-flag:optional:enumList:<flag,...>:female|essential|ischargenfacepreset|respawn|autocalcstats|unique|doesntaffectstealthmeter|skyrimusetemplate|protected|summonable|doesnotbleed|bleedoutoverride|oppositegenderanims|simpleactor|skyrimloopedscript|skyrimloopedaudio|isghost|invulnerable|pc-level-mult",
        "clear-flag:optional:enumList:<flag,...>:female|essential|ischargenfacepreset|respawn|autocalcstats|unique|doesntaffectstealthmeter|skyrimusetemplate|protected|summonable|doesnotbleed|bleedoutoverride|oppositegenderanims|simpleactor|skyrimloopedscript|skyrimloopedaudio|isghost|invulnerable|pc-level-mult",
        "keywords:optional:string:<Plugin.esp|0xFormID,...>:", "add-keyword:optional:string:<Plugin.esp|0xFormID,...>:",
        "remove-keyword:optional:string:<Plugin.esp|0xFormID,...>:",
        "factions:optional:string:<Plugin.esp|0xFormID=signed-byte-rank,...>:",
        "add-faction:optional:string:<Plugin.esp|0xFormID=signed-byte-rank,...>:",
        "update-faction:optional:string:<Plugin.esp|0xFormID=signed-byte-rank,...>:",
        "remove-faction:optional:string:<Plugin.esp|0xFormID,...>:",
        "inventory:optional:string:<Plugin.esp|0xFormID=signed-32-bit-count,...>:",
        "add-inventory:optional:string:<Plugin.esp|0xFormID=signed-32-bit-count,...>:",
        "update-inventory:optional:string:<Plugin.esp|0xFormID=signed-32-bit-count,...>:",
        "remove-inventory:optional:string:<Plugin.esp|0xFormID,...>:",
        "default-outfit:optional:artifactReference:<Plugin.esp|0xFormID|none>:",
        "sleep-outfit:optional:artifactReference:<Plugin.esp|0xFormID|none>:",
        "perks:optional:string:<Plugin.esp|0xFormID=unsigned-byte-rank,...>:",
        "add-perk:optional:string:<Plugin.esp|0xFormID=unsigned-byte-rank,...>:",
        "update-perk:optional:string:<Plugin.esp|0xFormID=unsigned-byte-rank,...>:",
        "remove-perk:optional:string:<Plugin.esp|0xFormID,...>:",
        "actor-effects:optional:string:<Plugin.esp|0xFormID,...>:", "add-actor-effect:optional:string:<Plugin.esp|0xFormID,...>:",
        "remove-actor-effect:optional:string:<Plugin.esp|0xFormID,...>:",
        "output-kind:optional:enum:source-mastered-override|standalone-copy:source-mastered-override|standalone-copy"
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task3Options =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["preset design-propose"] = ["intake", "intake-sha256", "output", "template-output"],
            ["preset create-from-reference"] = ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "jslot-output", "evidence-root", "apply", "accepted-proposal-sha256"],
            ["npc create-from-reference"] = ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "accepted-proposal-sha256", "request", "request-sha256", "data-root", "plugins", "transaction-root", "apply"],
            ["facegen hair-regions analyze"] = ["source", "expected-source-sha256", "analysis", "assignment-template"],
            ["facegen hair-regions propose"] = ["analysis", "analysis-sha256", "request", "request-sha256", "proposal"],
            ["facegen hair-regions preview"] = ["request", "request-sha256", "proposal", "proposal-sha256", "intake", "output-root"],
            ["facegen hair-regions apply"] = ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            ["facegen hair-regions verify"] = ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            ["npc finish analyze"] = ["request", "request-sha256", "proposal", "validate-all", "data-root", "plugins"],
            ["npc finish apply"] = ["request", "request-sha256", "proposal", "proposal-sha256", "validate-all", "data-root", "plugins"],
            ["npc finish verify"] = ["manifest", "manifest-sha256", "data-root", "plugins"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, Task3Profile> Task3Profiles =
        new Dictionary<string, Task3Profile>(StringComparer.Ordinal)
        {
            ["preset design-propose"] = Profile(
                ["reference-preset-intake:reference-authoring.intake.v1@template-output:any:__absent__:absent"],
                ["reference-intake-template:reference-authoring.intake.v1@template-output:none:__absent__:present",
                 "reference-design-proposal:reference-authoring.inference-proposal.v1@template-output:any:__absent__:absent"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Reference-authoring completion/status, exact hashes or handoff fields when available, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published."),
            ["preset create-from-reference"] = Profile(
                ["reference-inference-proposal:reference-authoring.inference-proposal.v1", "reference-preset-intake:reference-authoring.intake.v1", "reference-reviewed-design:reference-authoring.reviewed-design.v1", "reference-resource-snapshot:reference-authoring.resource-snapshot.v1"],
                ["reference-preset-evidence-root", "reference-authoring-proposal:reference-authoring.authoring-proposal.v1", "verified-reference-preset@apply:any:true:present"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Reference-authoring status and hashes. The transaction first promotes fresh evidence under --evidence-root; accepted apply derives the JSlot path from the reviewed target name. --jslot-output is only parsed and compared with that returned path after promotion, so a mismatch response can follow committed evidence artifacts. Runtime authority remains false; no grounded result schema identifier is published."),
            ["npc create-from-reference"] = Profile(
                ["reference-inference-proposal:reference-authoring.inference-proposal.v1", "reference-preset-intake:reference-authoring.intake.v1", "reference-reviewed-design:reference-authoring.reviewed-design.v1", "reference-resource-snapshot:reference-authoring.resource-snapshot.v1", "npc-create-from-jslot-request:npc.create-from-jslot.request.v1"],
                ["verified-reference-preset", "reference-authoring-proposal:reference-authoring.authoring-proposal.v1", "verified-reference-npc-handoff"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "established", "notApplicable", "required", "required", "required"],
                "Reference-authoring completion/status, exact hashes or handoff fields when available, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published."),
            ["facegen hair-regions analyze"] = Profile(
                ["facegeom-nif"],
                ["facegeom-hair-regions-analysis:npcmanager-facegeom-hair-regions-analysis/1", "facegeom-hair-regions-request:npcmanager-facegeom-hair-regions-request/1"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                HairWriteAuthority(staticVerified: false), HairResult),
            ["facegen hair-regions propose"] = Profile(
                ["facegeom-hair-regions-analysis:npcmanager-facegeom-hair-regions-analysis/1", "facegeom-hair-regions-request:npcmanager-facegeom-hair-regions-request/1"],
                ["facegeom-hair-regions-proposal:npcmanager-facegeom-hair-regions-proposal/1"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                HairWriteAuthority(staticVerified: false), HairResult),
            ["facegen hair-regions preview"] = Profile(
                ["facegeom-hair-regions-request:npcmanager-facegeom-hair-regions-request/1", "facegeom-hair-regions-proposal:npcmanager-facegeom-hair-regions-proposal/1", "reviewed-workspace-intake"],
                ["facegeom-hair-regions-preview-bundle", "facegeom-hair-regions-preview-evidence:npcmanager-facegeom-hair-regions-preview-evidence/1"],
                ["readWorkspace:always", "invokeAdmittedProcess:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "established", "established", "required", "required", "required"], HairResult),
            ["facegen hair-regions apply"] = Profile(
                ["facegeom-hair-regions-request:npcmanager-facegeom-hair-regions-request/1", "facegeom-hair-regions-proposal:npcmanager-facegeom-hair-regions-proposal/1", "facegeom-source-nif"],
                ["facegeom-nif", "facegeom-hair-regions-manifest:npcmanager-facegeom-hair-regions-manifest/1"],
                ["readWorkspace:always", "writeNewArtifact:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
                HairWriteAuthority(staticVerified: true), HairResult),
            ["facegen hair-regions verify"] = Profile(
                ["facegeom-hair-regions-request:npcmanager-facegeom-hair-regions-request/1", "facegeom-hair-regions-proposal:npcmanager-facegeom-hair-regions-proposal/1", "facegeom-source-nif", "facegeom-nif", "facegeom-hair-regions-manifest:npcmanager-facegeom-hair-regions-manifest/1"],
                [], ["readWorkspace:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
                ["established", "established", "notApplicable", "established", "notApplicable", "required", "required", "required"], HairResult),
            ["npc finish analyze"] = Profile(
                ["npc-finish-core-request:npc.finish-core.request.v1,npc.finish-core.request.v2,npc.finish-core.request.v3,npc.finish-core.request.v4"],
                ["npc-finish-core-proposal:npc.finish-core.proposal.v1,npc.finish-core.proposal.v2,npc.finish-core.proposal.v3,npc.finish-core.proposal.v4@validate-all:none:true|1:absent"],
                ["readWorkspace:always", "writeNewArtifact@validate-all:none:true|1:absent", "writeNewArtifact@validate-all:any:true|1:present", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Proposal or validation status and diagnostics; validation mode emits phase diagnostics without promotion. No grounded result schema identifier is published."),
            ["npc finish apply"] = Profile(
                ["npc-finish-core-request:npc.finish-core.request.v1,npc.finish-core.request.v2,npc.finish-core.request.v3,npc.finish-core.request.v4", "npc-finish-core-proposal:npc.finish-core.proposal.v1,npc.finish-core.proposal.v2,npc.finish-core.proposal.v3,npc.finish-core.proposal.v4"],
                ["npc-finish-core-manifest:npc.finish-core.manifest.v1,npc.finish-core.manifest.v2@validate-all:none:true|1:absent"],
                ["readWorkspace:always", "writeNewArtifact@validate-all:none:true|1:absent", "writeNewArtifact@validate-all:any:true|1:present", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "established", "required", "notApplicable", "required", "required", "required"],
                "Apply status, fresh output/archive paths, and diagnostics; no grounded result schema identifier is published."),
            ["npc finish verify"] = Profile(
                ["npc-finish-core-manifest:npc.finish-core.manifest.v1,npc.finish-core.manifest.v2"],
                [], ["readWorkspace:always", "appendLocalOperationJournal:always"],
                AgentRetryPolicy.SafeUnchanged, AgentDeterminism.PinnedInputsAndTools,
                ["established", "established", "notApplicable", "established", "notApplicable", "required", "required", "required"],
                "Static verification status, verification evidence, and diagnostics; no grounded result schema identifier is published.")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task3OptionProfiles =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["preset design-propose"] =
            [
                "intake:path:<intake.json>:", "intake-sha256:sha256:<64-hex-sha256>:",
                "output:path:<new-directory>:", "template-output:path:<fresh.json>:"
            ],
            ["preset create-from-reference"] =
            [
                "proposal:path:<proposal.json>:", "proposal-sha256:sha256:<64-hex-sha256>:",
                "review:path:<review.json>:", "review-sha256:sha256:<64-hex-sha256>:",
                "resource:path:<resource.json>:", "resource-sha256:sha256:<64-hex-sha256>:",
                "jslot-output:path:<expected-derived-preset.jslot>:", "evidence-root:path:<new-directory>:",
                "apply:boolean:true|false:true|false", "accepted-proposal-sha256:sha256:<64-hex-sha256>:"
            ],
            ["npc create-from-reference"] =
            [
                "proposal:path:<proposal.json>:", "proposal-sha256:sha256:<64-hex-sha256>:",
                "review:path:<review.json>:", "review-sha256:sha256:<64-hex-sha256>:",
                "resource:path:<resource.json>:", "resource-sha256:sha256:<64-hex-sha256>:",
                "accepted-proposal-sha256:sha256:<64-hex-sha256>:", "request:path:<request.json>:",
                "request-sha256:sha256:<64-hex-sha256>:", "data-root:path:<K-local-data-root>:",
                "plugins:string:<plugin,...>:", "transaction-root:path:<new-directory>:",
                "apply:boolean:true:true"
            ],
            ["facegen hair-regions analyze"] =
            [
                "source:path:<source.nif>:", "expected-source-sha256:sha256:<64-hex-sha256>:",
                "analysis:path:<new-analysis.json>:", "assignment-template:path:<new-request.json>:"
            ],
            ["facegen hair-regions propose"] =
            [
                "analysis:path:<analysis.json>:", "analysis-sha256:sha256:<64-hex-sha256>:",
                "request:path:<request.json>:", "request-sha256:sha256:<64-hex-sha256>:",
                "proposal:path:<new-proposal.json>:"
            ],
            ["facegen hair-regions preview"] =
            [
                "request:path:<request.json>:", "request-sha256:sha256:<64-hex-sha256>:",
                "proposal:path:<proposal.json>:", "proposal-sha256:sha256:<64-hex-sha256>:",
                "intake:path:<reviewed-intake.json>:", "output-root:path:<new-directory>:"
            ],
            ["facegen hair-regions apply"] = HairOptionProfile(),
            ["facegen hair-regions verify"] = HairOptionProfile(),
            ["npc finish analyze"] =
            [
                "request:path:<request.json>:", "request-sha256:sha256:<64-hex-sha256>:",
                "proposal:path:<new-proposal.json>:", "validate-all:boolean:true|false|1:true|false|1",
                "data-root:path:<absolute-K-local-path>:", "plugins:string:<plugin,...>:"
            ],
            ["npc finish apply"] =
            [
                "request:path:<request.json>:", "request-sha256:sha256:<64-hex-sha256>:",
                "proposal:path:<proposal.json>:", "proposal-sha256:sha256:<64-hex-sha256>:",
                "validate-all:boolean:true|false|1:true|false|1",
                "data-root:path:<absolute-K-local-path>:", "plugins:string:<plugin,...>:"
            ],
            ["npc finish verify"] =
            [
                "manifest:path:<manifest.json>:", "manifest-sha256:sha256:<64-hex-sha256>:",
                "data-root:path:<absolute-K-local-path>:", "plugins:string:<plugin,...>:"
            ]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private const string HairResult =
        "Phase verdict, observed or produced artifact evidence, diagnostics, visualAuthority false, and runtimeAuthority false; preview also returns off-engine preview evidence. No grounded result schema identifier is published.";

    private static readonly ImmutableDictionary<string, ImmutableArray<string>> Task2Options =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["capabilities"] = [], ["schema export"] = ["command", "output"],
            ["diagnose"] = [], ["gui"] = ["launch", "executable", "workflow-bundle", "workflow-bundle-sha256"],
            ["workspace preflight"] = ["edition", "game", "workspace-root", "data-root", "output-root", "plugin", "asset-index", "load-order", "loadorder", "selected", "intake-output", "npc-editor-id", "workflow-output"],
            ["workspace scan-generated"] = ["edition", "game", "data-root"],
            ["profile scan"] = ["edition", "game", "data-root", "load-order", "loadorder"],
            ["load-order validate"] = ["edition", "game", "plugin", "load-order", "loadorder"],
            ["plugins resolve-load-order"] = ["edition", "game", "plugins", "load-order", "loadorder"],
            ["plugins validate"] = ["edition", "game", "plugin", "load-order", "loadorder"],
            ["assets index"] = ["edition", "game", "plugin", "data-root", "form-id", "plugins", "filter", "category", "changed-only", "search", "output"],
            ["assets search"] = ["edition", "game", "kind", "plugin", "data-root", "plugins", "query", "search"],
            ["headpart choices"] = ["edition", "game", "data-root", "plugins", "race", "sex", "type", "search", "query"],
            ["paint choices"] = ["edition", "game", "data-root", "plugins", "category", "search", "query"],
            ["forms search"] = ["edition", "game", "plugin", "data-root", "type", "signature", "plugins", "form-id", "allow-null", "query", "search"],
            ["body sliders resolve"] = ["edition", "game", "tri", "preset"],
            ["body sliders inspect-preset"] = ["edition", "game", "preset-xml"],
            ["body sidecar inspect"] = ["edition", "game", "file"],
            ["body sidecar write"] = ["game", "edition", "plugin", "npc", "output", "sliders", "morphs", "editor-id"],
            ["bodygen write"] = ["edition", "game", "assignments", "output"],
            ["body weight resolve"] = ["edition", "game", "input"],
            ["body overlay patch"] = ["game", "edition", "npc", "form-id", "layers"],
            ["body overlay bake"] = ["game", "edition", "layers", "output"],
            ["body transforms apply"] = ["game", "edition", "npc", "form-id", "preset", "output", "transforms", "skin-overrides", "skins", "expected-sha256"],
            ["body reset"] = ["game", "edition", "npc", "form-id", "current", "baseline", "output", "section", "expected-sha256", "proposal", "dry-run", "apply"],
            ["body patch"] = ["edition", "game", "input-plugin", "output", "form-id", "npc", "editor-id", "name", "sex", "race", "voice", "class", "combat-style", "level", "level-mult", "magicka-offset", "stamina-offset", "health-offset", "calc-min", "calc-max", "speed-multiplier", "disposition", "bleedout", "player-health", "player-magicka", "player-stamina", "skill-values", "skill-offsets", "height", "far-model-distance", "geared-weapons", "xp-offset", "set-flag", "clear-flag", "keywords", "add-keyword", "remove-keyword", "factions", "add-faction", "update-faction", "remove-faction", "inventory", "add-inventory", "update-inventory", "remove-inventory", "default-outfit", "sleep-outfit", "perks", "add-perk", "update-perk", "remove-perk", "actor-effects", "add-actor-effect", "remove-actor-effect", "properties", "add-property", "update-property", "remove-property", "appr", "add-appr", "remove-appr", "skin", "clear-skin", "preset-skin", "regions", "weight", "weight-triangle", "input-sha", "input-sha256", "expected-sha256", "proposal", "dry-run", "apply", "whole-skin", "whole-skin-sha256", "aidt"],
            ["body weight normalize"] = ["game", "edition", "triangle", "current"],
            ["body weight redistribute"] = ["game", "edition", "triangle", "current", "axis", "value"],
            ["bodygen build"] = ["edition", "game", "plugin", "npc", "mod-name", "morphs", "output-root"],
            ["records list"] = ["edition", "game", "plugin", "data-root", "plugins", "signature", "search"],
            ["records propose"] = ["edition", "game", "type", "mode", "form-id", "editor-id", "output", "source", "masters", "name", "model", "tri-race", "tri-chargen", "tri-dialogue", "valid-races", "extra-parts", "flags", "part-type", "clone-from", "retarget-valid-races"],
            ["changes list"] = ["edition", "game", "session"],
            ["changes update"] = ["edition", "game", "session", "record", "action", "output", "signature"]
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static async Task AssertTask2ContractsAsync()
    {
        CliBoundaryResult response = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2", "--json"]);
        using JsonDocument envelope = JsonDocument.Parse(response.StandardOutput);
        JsonElement commands = envelope.RootElement.GetProperty("result").GetProperty("commands");
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task2Options)
        {
            JsonElement contract = Command(commands, name);
            Assert(contract.GetProperty("contractStatus").GetString() == "complete",
                $"Task 2 contract stayed incomplete: {name}.");
            Assert(contract.GetProperty("options").EnumerateArray()
                    .Select(item => item.GetProperty("cliName").GetString())
                    .SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 2 option grammar drifted: {name}.");
            AssertArtifactKindProjection(contract);
        }
        Assert(AgentCommandRegistry.Validate().IsEmpty,
            "Task 2 production-composed contracts failed validation.");

        var requiredByCommand = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
        {
            ["capabilities"] = [], ["schema export"] = [], ["diagnose"] = [], ["gui"] = [],
            ["workspace preflight"] = [], ["workspace scan-generated"] = ["data-root"],
            ["profile scan"] = ["data-root"], ["load-order validate"] = ["plugin"],
            ["plugins resolve-load-order"] = ["plugins"], ["plugins validate"] = ["plugin"],
            ["assets index"] = [], ["assets search"] = ["kind"],
            ["headpart choices"] = ["data-root", "plugins", "race", "sex", "type"],
            ["paint choices"] = ["data-root", "plugins", "category"], ["forms search"] = [],
            ["body sliders resolve"] = ["tri", "preset"], ["body sliders inspect-preset"] = ["preset-xml"],
            ["body sidecar inspect"] = ["file"], ["body sidecar write"] = ["plugin", "npc", "output"],
            ["bodygen write"] = ["assignments", "output"], ["body weight resolve"] = ["input"],
            ["body overlay patch"] = ["layers"], ["body overlay bake"] = ["layers", "output"],
            ["body transforms apply"] = ["preset", "output"],
            ["body reset"] = ["current", "baseline", "output", "section"],
            ["body patch"] = ["input-plugin", "output"], ["body weight normalize"] = [],
            ["body weight redistribute"] = ["axis", "value"],
            ["bodygen build"] = ["plugin", "npc", "mod-name", "morphs", "output-root"],
            ["records list"] = [],
            ["records propose"] = ["type", "mode", "form-id", "editor-id", "output"],
            ["changes list"] = ["session"],
            ["changes update"] = ["session", "record", "action", "output"]
        };
        foreach ((string name, ImmutableArray<string> required) in requiredByCommand)
            Assert(AgentCommandRegistry.GetRequired(name).Options.Where(item => item.Required)
                    .Select(item => item.CliName).SequenceEqual(required, StringComparer.Ordinal),
                $"Task 2 requiredness drifted: {name}.");

        var alternatives = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["load-order validate"] = ["edition|game", "load-order|loadorder"],
            ["plugins resolve-load-order"] = ["edition|game", "load-order|loadorder"],
            ["plugins validate"] = ["edition|game", "load-order|loadorder"],
            ["assets index"] = ["edition|game", "plugin|data-root"],
            ["assets search"] = ["edition|game", "plugin|data-root"],
            ["forms search"] = ["edition|game", "plugin|data-root", "type|signature"],
            ["body sidecar write"] = ["game|edition", "sliders|morphs"],
            ["body overlay patch"] = ["game|edition", "npc|form-id"],
            ["body transforms apply"] = ["game|edition", "npc|form-id"],
            ["body reset"] = ["game|edition", "npc|form-id"],
            ["body patch"] = ["edition|game", "form-id|npc"],
            ["body weight normalize"] = ["game|edition", "triangle|current"],
            ["body weight redistribute"] = ["game|edition", "triangle|current"],
            ["records list"] = ["edition|game", "plugin|data-root"]
        };
        foreach ((string name, string[] groups) in alternatives)
        {
            string[] actual = AgentCommandRegistry.GetRequired(name).OptionRelationships
                .Where(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne)
                .Select(item => string.Join('|', item.Options)).ToArray();
            Assert(actual.SequenceEqual(groups, StringComparer.Ordinal),
                $"Task 2 required alternatives drifted: {name}.");
        }
        foreach ((string name, _) in Task2Options)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            if (contract.Options.Any(item => item.CliName == "edition") && name != "workspace preflight")
                Assert(contract.OptionRelationships.Any(item => item.Kind == AgentOptionRelationshipKind.AtLeastOne &&
                    item.Options.Contains("edition", StringComparer.Ordinal) && item.Options.Contains("game", StringComparer.Ordinal)),
                    $"Task 2 edition/game alternative was not typed: {name}.");
        }

        AgentOptionContract schemaOutput = AgentCommandRegistry.GetRequired("schema export").Options
            .Single(item => item.CliName == "output");
        Assert(!schemaOutput.Required && schemaOutput.ValueKind == AgentValueKind.Path,
            "schema export lost its optional inline protocol-2 branch.");
        CliBoundaryResult inlineSchema = await ProtocolV2TestHost.RunAsync(
            ["schema", "export", "--protocol", "2", "--json"]);
        using JsonDocument inlineEnvelope = JsonDocument.Parse(inlineSchema.StandardOutput);
        Assert(inlineSchema.ExitCode == 0 &&
               inlineEnvelope.RootElement.GetProperty("artifacts").GetArrayLength() == 0,
            "schema export without --output did not stay inline.");

        AgentCommandContract bodyPatch = AgentCommandRegistry.GetRequired("body patch");
        AssertOptionFact(bodyPatch, "level", AgentValueKind.String, "<invariant-decimal>");
        AssertOptionFact(bodyPatch, "speed-multiplier", AgentValueKind.Integer, "<signed-16-bit-integer>");
        AssertOptionFact(bodyPatch, "input-plugin", AgentValueKind.Path, "<K-local-plugin>");
        AssertOptionFact(bodyPatch, "factions", AgentValueKind.String, "<Plugin.esp|0xFormID=signed-byte-rank,...>");
        AssertOptionFact(bodyPatch, "inventory", AgentValueKind.String, "<Plugin.esp|0xFormID=signed-32-bit-count,...>");
        AssertOptionFact(bodyPatch, "perks", AgentValueKind.String, "<Plugin.esp|0xFormID=unsigned-byte-rank,...>");
        AssertOptionFact(bodyPatch, "properties", AgentValueKind.String, "<Plugin.esp|0xFormID=invariant-float,...>");
        Assert(!bodyPatch.Options.Any(item => item.ValueSyntax.Contains("JSON|@K-local-file|value", StringComparison.Ordinal)),
            "body patch still collapsed distinct collection grammars.");
        AssertOptionFact(AgentCommandRegistry.GetRequired("gui"), "executable", AgentValueKind.Path, "<K-local-executable>");
        AssertOptionFact(AgentCommandRegistry.GetRequired("gui"), "workflow-bundle", AgentValueKind.Path, "<workflow-bundle.json>");
        AgentCommandContract gui = AgentCommandRegistry.GetRequired("gui");
        Assert(gui.Effects.Any(item => item.Kind == AgentEffectKind.LaunchDesktop && TriggerSubject(item.Trigger, "launch")) &&
               gui.Effects.Any(item => item.Kind == AgentEffectKind.InvokeAdmittedProcess && TriggerSubject(item.Trigger, "launch")),
            "gui omitted its conditional desktop/process effects.");

        foreach (string name in new[] { "profile scan", "load-order validate", "plugins resolve-load-order", "plugins validate" })
        foreach (string option in new[] { "load-order", "loadorder" })
            Assert(AgentCommandRegistry.GetRequired(name).Options.Single(item => item.CliName == option).ValueKind == AgentValueKind.Path,
                $"{name} --{option} was not typed as a validated path.");
        string[] categories = ["unique", "generic", "template", "unused"];
        foreach (string option in new[] { "filter", "category" })
        {
            AgentOptionContract row = AgentCommandRegistry.GetRequired("assets index").Options.Single(item => item.CliName == option);
            Assert(row.ValueKind == AgentValueKind.EnumList && row.ValueSyntax == "unique|generic|template|unused,..." &&
                   row.AllowedValues.SequenceEqual(categories, StringComparer.Ordinal),
                $"assets index --{option} did not publish its exact enum values.");
        }
        AgentCommandContract callableAssetIndex = AgentCommandRegistry.GetRequired("assets index") with { Readiness = ProtocolReadiness.V2 };
        ProtocolValidationResult validCategoryList = ProtocolV2CommandLine.Validate(
            CommandLine.Parse(["assets", "index", "--protocol", "2", "--json", "--filter", "unique,template"]),
            [callableAssetIndex]);
        ProtocolValidationResult invalidCategoryList = ProtocolV2CommandLine.Validate(
            CommandLine.Parse(["assets", "index", "--protocol", "2", "--json", "--filter", "unique,unknown"]),
            [callableAssetIndex]);
        Assert(validCategoryList.Diagnostics.IsEmpty &&
               invalidCategoryList.Diagnostics.Any(item => item.Code == ProtocolV2DiagnosticCodes.OptionEnumValue),
            "enum-list validation did not accept multiple known categories and reject an unknown member.");

        ImmutableArray<string> booleanDialect = ["true", "false", "1"];
        ImmutableArray<string> activeBooleanOptions = ["apply", "dry-run", "clear-skin"];
        Assert(bodyPatch.Options.Single(item => item.CliName == "level").ConflictsWith.SequenceEqual(["level-mult"], StringComparer.Ordinal) &&
               bodyPatch.Options.Single(item => item.CliName == "level-mult").ConflictsWith.SequenceEqual(["level"], StringComparer.Ordinal) &&
               bodyPatch.Options.Single(item => item.CliName == "apply").ConflictsWith.IsEmpty &&
               bodyPatch.Options.Single(item => item.CliName == "dry-run").ConflictsWith.IsEmpty &&
               bodyPatch.Options.Single(item => item.CliName == "clear-skin").ConflictsWith.IsEmpty &&
               bodyPatch.Options.Single(item => item.CliName == "skin").ConflictsWith.IsEmpty &&
               activeBooleanOptions.All(option => bodyPatch.Options.Single(item => item.CliName == option)
                   .AllowedValues.SequenceEqual(booleanDialect, StringComparer.Ordinal)) &&
               !bodyPatch.Options.Single(item => item.CliName == "preset-skin").ConflictsWith.Contains("clear-skin", StringComparer.Ordinal),
            "body patch conflicts did not match active handler predicates.");
        AssertActiveForbidden(bodyPatch, "apply", "dry-run");
        AssertActiveForbidden(bodyPatch, "clear-skin", "skin");
        AssertActiveForbidden(AgentCommandRegistry.GetRequired("body reset"), "apply", "dry-run");
        AssertOptionFact(AgentCommandRegistry.GetRequired("body sidecar write"), "output", AgentValueKind.Path, "<new-sidecar.bssliders>");
        Assert(gui.InputArtifacts.Select(item => item.Kind).SequenceEqual(["gui-executable", "gui-workflow-bundle"], StringComparer.Ordinal) &&
               IsPresentTrigger(gui.InputArtifacts.Single(item => item.Kind == "gui-executable").Trigger, "launch") &&
               IsAllPresentTrigger(gui.InputArtifacts.Single(item => item.Kind == "gui-workflow-bundle").Trigger, "launch", "workflow-bundle") &&
               gui.Effects.Any(item => item.Kind == AgentEffectKind.ReadWorkspace &&
                   item.AllowedResultScopes.SequenceEqual([ApplicationEffectScope.Workspace]) && IsPresentTrigger(item.Trigger, "launch")),
            "gui omitted admitted executable/workflow inputs or its workspace read effect.");

        foreach (string name in new[] { "body reset", "body patch" })
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.OutputArtifacts.Count(item => item.Trigger is not null) == 2 &&
                   contract.Effects.Count(item => item.Kind == AgentEffectKind.WriteNewArtifact && item.Trigger is not null) == 2,
                $"{name} did not type proposal and apply writes separately.");
        }

        AgentEffectContract guiProcess = gui.Effects.Single(item => item.Kind == AgentEffectKind.ReadWorkspace);
        ImmutableArray<AgentCommandContract> badEffectTrigger = AgentCommandRegistry.All.Select(item => item.Name == "gui"
            ? item with { Effects = item.Effects.Replace(guiProcess, guiProcess with { Trigger = PresentTestTrigger("missing-option") }) }
            : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(badEffectTrigger).Any(error => error.Contains("unknown effect trigger option subject: gui --missing-option", StringComparison.Ordinal)),
            "Registry validation accepted an unknown effect trigger option subject.");
        AgentArtifactContract guiExecutable = gui.InputArtifacts.Single(item => item.Kind == "gui-executable");
        ImmutableArray<AgentCommandContract> badArtifactTrigger = AgentCommandRegistry.All.Select(item => item.Name == "gui"
            ? item with { InputArtifacts = item.InputArtifacts.Replace(guiExecutable, guiExecutable with { Trigger = PresentTestTrigger("missing-option") }) }
            : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(badArtifactTrigger).Any(error => error.Contains("unknown artifact trigger option subject: gui --missing-option", StringComparison.Ordinal)),
            "Registry validation accepted an unknown artifact trigger option subject.");

        foreach (string name in new[] { "capabilities", "schema export" })
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            Assert(contract.Authority.Single(item => item.Kind == AgentAuthorityKind.GameRuntimeVerification).State == AgentAuthorityState.NotApplicable &&
                   contract.Authority.Single(item => item.Kind == AgentAuthorityKind.PromotionApproval).State == AgentAuthorityState.NotApplicable,
                $"{name} overstated runtime or promotion authority requirements.");
        }

        string[] skyrimOnly = ["headpart choices", "paint choices", "body sliders inspect-preset", "body weight resolve", "body overlay bake", "body transforms apply"];
        string[] falloutOnly = ["body weight normalize", "body weight redistribute"];
        foreach ((string name, _) in Task2Options)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(name);
            string[] expectedGames = skyrimOnly.Contains(name, StringComparer.Ordinal) ? ["skyrimse"] :
                falloutOnly.Contains(name, StringComparer.Ordinal) ? ["fallout4"] : ["fallout4", "skyrimse"];
            Assert(contract.SupportedGames.Select(item => item.ToWireName()).SequenceEqual(expectedGames, StringComparer.Ordinal) &&
                   contract.ResultShape == "object" && contract.ResultDescription.Length > 0 &&
                   contract.Authority.Select(item => item.Kind).SequenceEqual(Enum.GetValues<AgentAuthorityKind>()) &&
                   contract.Options.All(item => item.ValueSyntax.Length > 0 && item.Description.Length > 0),
                $"Task 2 complete profile drifted: {name}.");
            AgentRetryPolicy expectedRetry = name == "workspace preflight" || name is "schema export" or "assets index" or "body sidecar write" or "bodygen write" or "body overlay bake" or "body transforms apply" or "body reset" or "body patch" or "bodygen build" or "records propose" or "changes update"
                ? AgentRetryPolicy.RequiresFreshOutput : name == "gui" ? AgentRetryPolicy.NotRetryable : AgentRetryPolicy.SafeUnchanged;
            AgentDeterminism expectedDeterminism = name is "diagnose" or "gui" ? AgentDeterminism.EnvironmentDependent : AgentDeterminism.Deterministic;
            Assert(contract.RetryPolicy == expectedRetry && contract.Determinism == expectedDeterminism,
                $"Task 2 retry/determinism drifted: {name}.");
        }

        AgentCommandContract preflight = AgentCommandRegistry.GetRequired("workspace preflight");
        Assert(preflight.OptionRelationships.Length == 4 &&
               preflight.OptionRelationships.Count(item => item.Kind == AgentOptionRelationshipKind.RequiredWhen) == 3 &&
               preflight.OptionRelationships.Count(item => item.Kind == AgentOptionRelationshipKind.RequiresTogether) == 1 &&
               preflight.Options.Single(item => item.CliName == "loadorder").AliasFor == "load-order",
            "workspace preflight did not type its four presence-selected modes and alias precedence.");
        AgentCommandContract reset = AgentCommandRegistry.GetRequired("body reset");
        Assert(reset.Options.Single(item => item.CliName == "section").AllowedValues.SequenceEqual(
                   ["weight", "morphs", "sliders", "skin", "overlays", "transforms", "skin-overrides"], StringComparer.Ordinal) &&
               reset.Options.Single(item => item.CliName == "apply").ConflictsWith.IsEmpty,
            "body reset relationships were not exact.");
        AgentCommandContract update = AgentCommandRegistry.GetRequired("changes update");
        Assert(update.Options.Single(item => item.CliName == "action").AllowedValues.SequenceEqual(
                   ["reset", "delete"], StringComparer.Ordinal),
            "changes update action grammar was not exact.");
        AgentCommandContract assetIndex = AgentCommandRegistry.GetRequired("assets index");
        Assert(assetIndex.OutputArtifacts.Length == 1 &&
               assetIndex.Effects.Any(item => item.Kind == AgentEffectKind.WriteNewArtifact &&
                   item.Condition.Contains("--output", StringComparison.Ordinal)) &&
               assetIndex.RetryPolicy == AgentRetryPolicy.RequiresFreshOutput,
            "assets index did not publish its conditional --output write branch.");

        foreach (string representative in new[] { "workspace preflight", "body reset", "changes update" })
        {
            var (runner, output, error) = Program.CreateRunner();
            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
                [.. representative.Split(' '), "--help", "--json"]), CancellationToken.None);
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0,
                $"V1 JSON help failed for {representative}.");
            using JsonDocument help = JsonDocument.Parse(output.ToString());
            Assert(JsonNode.DeepEquals(JsonNode.Parse(Command(commands, representative).GetRawText()),
                       JsonNode.Parse(help.RootElement.GetRawText())),
                $"V1 and protocol-2 discovery diverged for {representative}.");
        }

        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["changes", "update", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("--action reset|delete", StringComparison.Ordinal) &&
               !humanOutput.ToString().Contains("actorwright — typed Actorwright CLI", StringComparison.Ordinal),
            "Representative Task 2 human help was not scoped.");

        CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
            ["changes", "update", "--protocol", "2", "--json"]);
        Assert(refused.ExitCode != 0 &&
               refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
            "Task 2 legacy invocation became protocol-2 callable.");
    }

    private static async Task AssertTask3ContractsAsync()
    {
        Assert(AgentCommandRegistry.Validate().IsEmpty,
            "Task 3 production-composed contracts failed validation.");
        foreach ((string name, ImmutableArray<string> expectedOptions) in Task3Options)
        {
            AgentCommandContract contract = AgentCommandRegistry.GetLegacyDiscoveryRequired(name);
            Task3Profile profile = Task3Profiles[name];
            Assert(contract.ContractStatus == AgentContractStatus.Complete,
                $"Task 3 contract stayed incomplete: {name}.");
            Assert(contract.Readiness == ProtocolReadiness.Legacy,
                $"Task 3 command became protocol-2 callable: {name}.");
            Assert(contract.SupportedGames.SequenceEqual(
                    [GameEdition.SkyrimSpecialEdition]),
                $"Task 3 supported-game scope drifted: {name}.");
            Assert(contract.Options.Select(item => item.CliName)
                    .SequenceEqual(expectedOptions, StringComparer.Ordinal),
                $"Task 3 option grammar drifted: {name}.");
            Assert(Task3OptionProfiles.TryGetValue(name, out ImmutableArray<string> expectedOptionProfile),
                $"Task 3 independent option profile is missing: {name}.");
            Assert(contract.Options.Select(OptionProfile)
                    .SequenceEqual(expectedOptionProfile, StringComparer.Ordinal),
                $"Task 3 option kind/syntax/value profile drifted: {name}.");
            Assert(contract.Options.All(option =>
                       option.ValueSyntax.Length != 0 && option.Description.Length != 0) &&
                   contract.ResultShape == "object" &&
                   contract.ResultDescription == profile.ResultDescription &&
                   contract.ResultSchemaIds.IsEmpty &&
                   contract.RetryPolicy == profile.Retry &&
                   contract.Determinism == profile.Determinism &&
                   !contract.SupportsDryRun &&
                   Enum.GetValues<AgentAuthorityKind>().Select(kind =>
                       contract.Authority.Single(item => item.Kind == kind).State)
                       .SequenceEqual(profile.Authority) &&
                   contract.Aliases.IsEmpty && contract.CanonicalCommand is null &&
                   contract.Options.All(option => option.AliasFor is null),
                $"Task 3 rich contract metadata is incomplete: {name}.");
            Assert(contract.InputArtifacts.Select(ArtifactProfile)
                       .SequenceEqual(profile.Inputs, StringComparer.Ordinal) &&
                   contract.OutputArtifacts.Select(ArtifactProfile)
                       .SequenceEqual(profile.Outputs, StringComparer.Ordinal) &&
                   contract.Effects.Select(EffectProfile)
                       .SequenceEqual(profile.Effects, StringComparer.Ordinal) &&
                   contract.InputArtifacts.AddRange(contract.OutputArtifacts)
                       .All(item => item.Description.Length != 0) &&
                   contract.Effects.All(item => item.Condition.Length != 0 &&
                       item.Scope == ExpectedEffectScope(item.Kind)),
                $"Task 3 artifact/schema/effect profile drifted: {name}.");
            Assert(contract.InputArtifactKinds.SequenceEqual(
                    contract.InputArtifacts.Select(item => item.Kind),
                    StringComparer.Ordinal),
                $"Task 3 input artifact projection drifted: {name}.");
        }
        AssertTask3RelationshipProfiles();

        var requiredByCommand = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["preset design-propose"] = [],
            ["preset create-from-reference"] = ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "jslot-output", "evidence-root"],
            ["npc create-from-reference"] = ["proposal", "proposal-sha256", "review", "review-sha256", "resource", "resource-sha256", "accepted-proposal-sha256", "request", "request-sha256", "data-root", "plugins", "transaction-root", "apply"],
            ["facegen hair-regions analyze"] = ["source", "expected-source-sha256", "analysis", "assignment-template"],
            ["facegen hair-regions propose"] = ["analysis", "analysis-sha256", "request", "request-sha256", "proposal"],
            ["facegen hair-regions preview"] = ["request", "request-sha256", "proposal", "proposal-sha256", "intake", "output-root"],
            ["facegen hair-regions apply"] = ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            ["facegen hair-regions verify"] = ["request", "request-sha256", "proposal", "proposal-sha256", "output", "manifest"],
            ["npc finish analyze"] = ["request", "request-sha256", "proposal"],
            ["npc finish apply"] = ["request", "request-sha256", "proposal", "proposal-sha256"],
            ["npc finish verify"] = ["manifest", "manifest-sha256"]
        };
        foreach ((string name, string[] required) in requiredByCommand)
            Assert(AgentCommandRegistry.GetLegacyDiscoveryRequired(name).Options.Where(item => item.Required)
                    .Select(item => item.CliName).SequenceEqual(required, StringComparer.Ordinal),
                $"Task 3 requiredness drifted: {name}.");

        AssertTask3RelationshipsAndAuthority();
        await AssertTask3HelpAndBoundaryAsync();
    }

    private static void AssertTask3RelationshipsAndAuthority()
    {
        AgentCommandContract preset = AgentCommandRegistry.GetRequired(
            "preset create-from-reference");
        AgentOptionRelationshipContract acceptedRequired = preset.OptionRelationships.Single(
            item => item.Kind == AgentOptionRelationshipKind.RequiredWhen);
        AgentOptionRelationshipContract acceptedForbidden = preset.OptionRelationships.Single(
            item => item.Kind == AgentOptionRelationshipKind.ForbiddenWhen);
        Assert(acceptedRequired.Options.SequenceEqual(["accepted-proposal-sha256"], StringComparer.Ordinal) &&
               IsTruthyTrigger(acceptedRequired.Trigger, "apply", "true") &&
               acceptedForbidden.Options.SequenceEqual(["accepted-proposal-sha256", "apply"], StringComparer.Ordinal) &&
               IsPresentAndNotTruthyTrigger(acceptedForbidden.Trigger,
                   "accepted-proposal-sha256", "apply", "true"),
            "Reference preset apply/accepted-hash iff relationship was not exact.");
        AssertOptionFact(preset, "apply", AgentValueKind.Boolean, "true|false");
        AssertOptionFact(preset, "accepted-proposal-sha256", AgentValueKind.Sha256,
            "<64-hex-sha256>");
        AgentOptionContract jslotOutput = preset.Options.Single(item =>
            item.CliName == "jslot-output");
        AgentArtifactContract presetIntake = preset.InputArtifacts.Single(item =>
            item.Kind == "reference-preset-intake");
        AgentArtifactContract evidenceRoot = preset.OutputArtifacts.Single(item =>
            item.Kind == "reference-preset-evidence-root");
        AgentArtifactContract verifiedPreset = preset.OutputArtifacts.Single(item =>
            item.Kind == "verified-reference-preset");
        Assert(jslotOutput.ValueSyntax == "<expected-derived-preset.jslot>" &&
               jslotOutput.Description.Contains("does not select the write destination", StringComparison.Ordinal) &&
               jslotOutput.Description.Contains("parsed and compared", StringComparison.Ordinal) &&
               jslotOutput.Description.Contains("after --evidence-root is promoted", StringComparison.Ordinal) &&
               presetIntake.SchemaIds.SequenceEqual(["reference-authoring.intake.v1"]) && presetIntake.Trigger is null &&
               presetIntake.Description.Contains("sibling authoring-intake.json", StringComparison.Ordinal) &&
               presetIntake.Description.Contains("proposal's canonical intake SHA-256", StringComparison.Ordinal) &&
               evidenceRoot.Description.Contains("committed before", StringComparison.Ordinal) &&
               IsTruthyTrigger(verifiedPreset.Trigger, "apply", "true") &&
               verifiedPreset.Description.Contains("derived target filename", StringComparison.Ordinal) &&
               preset.Effects.Single(item => item.Kind == AgentEffectKind.WriteNewArtifact)
                   .Condition.Contains("post-promotion", StringComparison.Ordinal),
            "Reference preset derived output and post-write check semantics drifted.");

        AgentCommandContract npcReference = AgentCommandRegistry.GetRequired(
            "npc create-from-reference");
        AgentOptionContract npcApply = npcReference.Options.Single(item => item.CliName == "apply");
        AgentOptionContract npcPlugins = npcReference.Options.Single(item => item.CliName == "plugins");
        AgentArtifactContract npcIntake = npcReference.InputArtifacts.Single(item =>
            item.Kind == "reference-preset-intake");
        Assert(npcApply.ValueKind == AgentValueKind.Boolean &&
               npcApply.AllowedValues.SequenceEqual(["true"], StringComparer.Ordinal) &&
               npcPlugins.ValueKind == AgentValueKind.String &&
               npcPlugins.Description.Contains("nonempty", StringComparison.OrdinalIgnoreCase) &&
               npcPlugins.Description.Contains("duplicate-free", StringComparison.OrdinalIgnoreCase) &&
               npcPlugins.Description.Contains("ordered", StringComparison.OrdinalIgnoreCase) &&
               npcIntake.SchemaIds.SequenceEqual(["reference-authoring.intake.v1"]) && npcIntake.Trigger is null &&
               npcIntake.Description.Contains("sibling authoring-intake.json", StringComparison.Ordinal) &&
               npcIntake.Description.Contains("proposal's canonical intake SHA-256", StringComparison.Ordinal),
            "NPC reference apply or ordered-plugin contract drifted.");

        AgentCommandContract hairApply = AgentCommandRegistry.GetRequired(
            "facegen hair-regions apply");
        AgentCommandContract hairVerify = AgentCommandRegistry.GetRequired(
            "facegen hair-regions verify");
        Assert(hairApply.Options.SequenceEqual(hairVerify.Options) &&
               hairApply.Effects.Any(item => item.Kind == AgentEffectKind.WriteNewArtifact) &&
               !hairVerify.Effects.Any(item => item.Kind == AgentEffectKind.WriteNewArtifact) &&
               hairApply.OutputArtifacts.Length == 2 && hairVerify.OutputArtifacts.IsEmpty,
            "Hair-region apply and verify were aliased or their distinct effects drifted.");
        AgentCommandContract hairPreview = AgentCommandRegistry.GetRequired(
            "facegen hair-regions preview");
        Assert(hairPreview.Authority.Single(item =>
                   item.Kind == AgentAuthorityKind.OffEnginePreview).State ==
               AgentAuthorityState.Established &&
               hairPreview.Authority.Single(item =>
                   item.Kind == AgentAuthorityKind.HumanVisualAcceptance).State ==
               AgentAuthorityState.Required &&
               hairPreview.Authority.Single(item =>
                   item.Kind == AgentAuthorityKind.GameRuntimeVerification).State ==
               AgentAuthorityState.Required,
            "Hair preview authority conflated off-engine evidence with human or runtime proof.");

        AgentCommandContract finishAnalyze = AgentCommandRegistry.GetLegacyDiscoveryRequired(
            "npc finish analyze");
        AssertOptionFact(finishAnalyze, "validate-all", AgentValueKind.Boolean,
            "true|false|1");
        Assert(finishAnalyze.OptionRelationships.Any(item =>
                   item.Kind == AgentOptionRelationshipKind.RequiresTogether &&
                   item.Options.SequenceEqual(["data-root", "plugins"], StringComparer.Ordinal)) &&
               IsNotTruthyTrigger(finishAnalyze.OutputArtifacts.Single().Trigger,
                   "validate-all", "true", "1") &&
               finishAnalyze.Effects.Count(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact) == 2 &&
               finishAnalyze.Effects.Any(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact &&
                   IsNotTruthyTrigger(item.Trigger, "validate-all", "true", "1")) &&
               finishAnalyze.Effects.Any(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact &&
                   IsTruthyTrigger(item.Trigger, "validate-all", "true", "1")),
            "Finish analyze pair or exact validation-mode trigger drifted.");

        AgentCommandContract finishApply = AgentCommandRegistry.GetLegacyDiscoveryRequired(
            "npc finish apply");
        Assert(finishApply.OptionRelationships.Any(item =>
                   item.Kind == AgentOptionRelationshipKind.RequiresTogether &&
                   item.Options.SequenceEqual(["data-root", "plugins"], StringComparer.Ordinal)) &&
               finishApply.OptionRelationships.Any(item =>
                   item.Kind == AgentOptionRelationshipKind.RequiredWhen &&
                   item.Trigger?.Predicates.Any(predicate =>
                       predicate.Source == AgentPredicateSource.InputArtifactSchema &&
                       predicate.Values.SequenceEqual(
                           [SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier],
                           StringComparer.Ordinal)) == true) &&
               IsNotTruthyTrigger(finishApply.OutputArtifacts.Single().Trigger,
                   "validate-all", "true", "1") &&
               finishApply.Effects.Count(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact) == 2 &&
               finishApply.Effects.Any(item =>
                   item.Kind == AgentEffectKind.WriteNewArtifact &&
                   IsTruthyTrigger(item.Trigger, "validate-all", "true", "1")),
            "Complete Finish apply regression contract drifted.");

        AgentCommandContract finishVerify = AgentCommandRegistry.GetLegacyDiscoveryRequired(
            "npc finish verify");
        Assert(finishVerify.OptionRelationships.Single().Kind ==
                   AgentOptionRelationshipKind.RequiresTogether &&
               finishVerify.OptionRelationships.Single().Options.SequenceEqual(
                   ["data-root", "plugins"], StringComparer.Ordinal) &&
               !finishVerify.Options.Any(item => item.CliName is
                   "verification-output" or "workflow-bundle" or
                   "workflow-bundle-sha256" or "workflow-output") &&
               finishVerify.Authority.Single(item =>
                   item.Kind == AgentAuthorityKind.IndependentStaticVerification).State ==
               AgentAuthorityState.Established &&
               finishVerify.Authority.Single(item =>
                   item.Kind == AgentAuthorityKind.GameRuntimeVerification).State ==
               AgentAuthorityState.Required,
            "Finish verify V1 grammar or static/runtime authority boundary drifted.");
    }

    private static async Task AssertTask3HelpAndBoundaryAsync()
    {
        CliBoundaryResult capabilities = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2", "--json"]);
        using JsonDocument envelope = JsonDocument.Parse(capabilities.StandardOutput);
        JsonElement commands = envelope.RootElement.GetProperty("result").GetProperty("commands");
        foreach (string representative in new[]
                 {
                     "preset create-from-reference",
                     "facegen hair-regions apply",
                     "npc finish analyze", "npc finish apply", "npc finish verify"
                 })
        {
            JsonElement expected = Command(commands, representative);
            CliBoundaryResult scoped = await ProtocolV2TestHost.RunAsync(
                [.. representative.Split(' '), "--help", "--protocol", "2", "--json"]);
            using JsonDocument scopedEnvelope = JsonDocument.Parse(scoped.StandardOutput);
            JsonElement scopedContract = scopedEnvelope.RootElement.GetProperty("result")
                .GetProperty("contract");
            Assert(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()),
                       JsonNode.Parse(scopedContract.GetRawText())),
                $"Task 3 scoped protocol-2 help drifted: {representative}.");

            var (runner, output, error) = Program.CreateRunner();
            CommandExitCode v1Exit = await runner.RunAsync(CommandLine.Parse(
                [.. representative.Split(' '), "--help", "--json"]),
                CancellationToken.None);
            using JsonDocument v1Help = JsonDocument.Parse(output.ToString());
            JsonNode? expectedV1 = JsonSerializer.SerializeToNode(
                AgentCommandRegistry.GetLegacyDiscoveryRequired(representative), CamelCaseJson);
            Assert(v1Exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   JsonNode.DeepEquals(expectedV1, JsonNode.Parse(v1Help.RootElement.GetRawText())),
                $"Task 3 V1 JSON help drifted: {representative}.");

            var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
            CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
                [.. representative.Split(' '), "--help"]), CancellationToken.None);
            Assert(humanExit == CommandExitCode.Success && humanError.ToString().Length == 0 &&
                   humanOutput.ToString().Contains(
                       $"{representative} — legacy command options", StringComparison.Ordinal) &&
                   !humanOutput.ToString().Contains(
                       "actorwright — typed Actorwright CLI", StringComparison.Ordinal),
                $"Task 3 human help was not scoped: {representative}.");

            CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
                [.. representative.Split(' '), "--protocol", "2", "--json"]);
            Assert(refused.ExitCode != 0 &&
                   (representative is "npc finish analyze" or "npc finish apply" or "npc finish verify"
                       ? refused.StandardOutput.Contains("option-required", StringComparison.Ordinal) &&
                         !refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal)
                       : refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal)),
                $"Task 3 normal protocol-2 invocation was not refused: {representative}.");
        }
    }

    private static void AssertTask3RelationshipProfiles()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["preset design-propose"] =
            [
                "requiredWhen:intake,intake-sha256,output>template-output",
                "forbiddenWhen:template-output>intake,intake-sha256,output"
            ],
            ["preset create-from-reference"] =
            [
                "requiredWhen:accepted-proposal-sha256>apply",
                "forbiddenWhen:accepted-proposal-sha256,apply>accepted-proposal-sha256,apply"
            ],
            ["npc create-from-reference"] = [],
            ["facegen hair-regions analyze"] = [],
            ["facegen hair-regions propose"] = [],
            ["facegen hair-regions preview"] = [],
            ["facegen hair-regions apply"] = [],
            ["facegen hair-regions verify"] = [],
            ["npc finish analyze"] = ["requiresTogether:data-root,plugins>"],
            ["npc finish apply"] =
            [
                "requiresTogether:data-root,plugins>",
                "requiredWhen:data-root,plugins>validate-all"
            ],
            ["npc finish verify"] = ["requiresTogether:data-root,plugins>"]
        };
        foreach ((string name, string[] relationships) in expected)
            Assert(AgentCommandRegistry.GetLegacyDiscoveryRequired(name).OptionRelationships
                    .Select(RelationshipProfile)
                    .SequenceEqual(relationships, StringComparer.Ordinal),
                $"Task 3 relationship set drifted: {name}.");
    }

    private static Task3Profile Profile(
        ImmutableArray<string> inputs,
        ImmutableArray<string> outputs,
        ImmutableArray<string> effects,
        AgentRetryPolicy retry,
        AgentDeterminism determinism,
        ImmutableArray<string> authority,
        string resultDescription) =>
        new(inputs, outputs, effects, retry, determinism,
            authority.Select(ParseAuthorityState).ToImmutableArray(),
            resultDescription);

    private static ImmutableArray<string> HairWriteAuthority(bool staticVerified) =>
    [
        "established", "established", "established",
        staticVerified ? "established" : "required",
        "notApplicable", "required", "required", "required"
    ];

    private static AgentAuthorityState ParseAuthorityState(string value) => value switch
    {
        "established" => AgentAuthorityState.Established,
        "required" => AgentAuthorityState.Required,
        "blocked" => AgentAuthorityState.Blocked,
        "notApplicable" => AgentAuthorityState.NotApplicable,
        _ => throw new InvalidOperationException($"Unknown authority-state fixture '{value}'.")
    };

    private static string ArtifactProfile(AgentArtifactContract artifact) =>
        artifact.Kind +
        (artifact.SchemaIds.IsEmpty ? "" : ":" + string.Join(',', artifact.SchemaIds)) +
        TriggerProfile(artifact.Trigger);

    private static string EffectProfile(AgentEffectContract effect) =>
        char.ToLowerInvariant(effect.Kind.ToString()[0]) + effect.Kind.ToString()[1..] +
        (effect.Trigger is null ? ":always" : TriggerProfile(effect.Trigger));

    private static string Task5EffectProfile(AgentEffectContract effect)
    {
        string kind = char.ToLowerInvariant(effect.Kind.ToString()[0]) + effect.Kind.ToString()[1..];
        if (effect.Trigger is null) return kind + ":always";
        string combination = ValueKindWireName(effect.Trigger.Operator.ToString());
        string predicates = string.Join(';', effect.Trigger.Predicates.Select(predicate =>
            $"{ValueKindWireName(predicate.Source.ToString())},{predicate.Subject},{ValueKindWireName(predicate.Match.ToString())},{string.Join('|', predicate.Values)},{predicate.MatchesWhenAbsent.ToString().ToLowerInvariant()}"));
        return $"{kind}@{combination}[{predicates}]";
    }

    private static string ExpectedEffectScope(AgentEffectKind kind) => kind switch
    {
        AgentEffectKind.ReadWorkspace or AgentEffectKind.InvokeAdmittedProcess =>
            "workspace",
        AgentEffectKind.WriteNewArtifact => "k-local-output",
        AgentEffectKind.AppendLocalOperationJournal =>
            "<labRoot>/.actorwright/operations",
        _ => throw new InvalidOperationException(
            $"Unexpected Task 3 effect kind '{kind}'.")
    };

    private static string TriggerProfile(AgentOptionRelationshipTrigger? trigger)
    {
        if (trigger is null) return "";
        Assert((trigger.Operator == AgentPredicateCombination.All || trigger.Operator == AgentPredicateCombination.Any) &&
               trigger.Predicates is [var predicate] &&
               predicate.Source == AgentPredicateSource.OptionValue,
            "Compact Task 3 projection trigger was not one exact option predicate.");
        AgentRelationshipPredicate row = trigger.Predicates[0];
        string match = row.Match == AgentPredicateMatch.AnyOf ? "any" : "none";
        string missing = row.MatchesWhenAbsent ? "absent" : "present";
        return $"@{row.Subject}:{match}:{string.Join('|', row.Values)}:{missing}";
    }

    private static string RelationshipProfile(AgentOptionRelationshipContract relationship)
    {
        string kind = char.ToLowerInvariant(relationship.Kind.ToString()[0]) +
                      relationship.Kind.ToString()[1..];
        return $"{kind}:{string.Join(',', relationship.Options)}>" +
               string.Join(',', relationship.ReferencedOptions);
    }

    private static string Task4OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{(option.Required ? "required" : "optional")}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}";

    private static string Task4RelationshipProfile(AgentOptionRelationshipContract relationship)
    {
        string profile = RelationshipProfile(relationship);
        if (relationship.Trigger is null) return profile + ":trigger=none";
        string combination = ValueKindWireName(relationship.Trigger.Operator.ToString());
        string predicates = string.Join(';', relationship.Trigger.Predicates.Select(predicate =>
            $"{ValueKindWireName(predicate.Source.ToString())},{predicate.Subject},{ValueKindWireName(predicate.Match.ToString())},{string.Join('|', predicate.Values)},{predicate.MatchesWhenAbsent.ToString().ToLowerInvariant()}"));
        return $"{profile}:trigger={combination}[{predicates}]";
    }

    private static string ValueKindWireName(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private static async Task AssertRepresentativeContractsAsync()
    {
        CliBoundaryResult response = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2", "--json"]);
        using JsonDocument envelope = JsonDocument.Parse(response.StandardOutput);
        JsonElement commands = envelope.RootElement.GetProperty("result")
            .GetProperty("commands");

        JsonElement version = Command(commands, "version");
        Assert(version.GetProperty("readiness").GetString() == "v2" &&
               version.GetProperty("contractStatus").GetString() == "complete" &&
               version.GetProperty("canonicalCommand").ValueKind == JsonValueKind.Null &&
               version.GetProperty("options").GetArrayLength() == 0 &&
               version.GetProperty("resultShape").GetString() == "object" &&
               version.GetProperty("resultDescription").GetString()!.Length > 0 &&
               version.GetProperty("resultSchemaIds").GetArrayLength() == 1 &&
               version.GetProperty("determinism").GetString() == "deterministic" &&
               !version.GetProperty("supportsDryRun").GetBoolean() &&
               version.GetProperty("authority").GetArrayLength() == 8,
            "version did not publish a complete explicit zero-option discovery contract.");

        JsonElement render = Command(commands, "render npc");
        Assert(render.GetProperty("readiness").GetString() == "legacy" &&
               render.GetProperty("contractStatus").GetString() == "complete" &&
               render.GetProperty("canonicalCommand").GetString() == "preview render",
            "render npc did not publish its complete structural alias contract.");
        AssertArtifactKindProjection(render);
        JsonElement[] renderOptions = render.GetProperty("options")
            .EnumerateArray().ToArray();
        ImmutableArray<LegacyCommandOption> previewOptions =
            LegacyCommandOptionCatalog.For("preview render");
        Assert(renderOptions.Length == previewOptions.Length,
            "render npc did not project every PreviewRenderOptions row.");
        for (int index = 0; index < renderOptions.Length; index++)
        {
            Assert(renderOptions[index].GetProperty("cliName").GetString() ==
                       previewOptions[index].Name &&
                   renderOptions[index].GetProperty("jsonName").GetString() ==
                       previewOptions[index].Name &&
                   renderOptions[index].GetProperty("valueSyntax").GetString() ==
                       previewOptions[index].ValueSyntax &&
                   renderOptions[index].GetProperty("description").GetString() ==
                       previewOptions[index].Description &&
                   renderOptions[index].GetProperty("required").GetBoolean() ==
                       previewOptions[index].Required,
                $"render npc contract drifted at --{previewOptions[index].Name}.");
        }

        JsonElement apply = Command(commands, "npc finish apply");
        Assert(apply.GetProperty("readiness").GetString() == "v2" &&
               apply.GetProperty("contractStatus").GetString() == "complete" &&
               apply.GetProperty("canonicalCommand").ValueKind == JsonValueKind.Null &&
               apply.GetProperty("options").GetArrayLength() == 12,
            "npc finish apply did not publish its complete V2 contract.");
        AssertArtifactKindProjection(apply);
        apply = JsonSerializer.SerializeToElement(AgentCommandRegistry.GetLegacyDiscoveryRequired("npc finish apply"), CamelCaseJson);
        Assert(apply.GetProperty("readiness").GetString() == "legacy" && apply.GetProperty("options").GetArrayLength() == 7,
            "npc finish apply did not preserve its exact legacy option projection.");
        JsonElement validateAll = apply.GetProperty("options").EnumerateArray()
            .Single(item => item.GetProperty("cliName").GetString() == "validate-all");
        Assert(validateAll.GetProperty("valueKind").GetString() == "boolean" &&
               validateAll.GetProperty("allowedValues").EnumerateArray()
                   .Select(item => item.GetString())
                   .SequenceEqual(["true", "false", "1"], StringComparer.Ordinal),
            "npc finish apply --validate-all was not typed as the accepted Boolean dialect.");
        JsonElement[] relationships = apply.GetProperty("optionRelationships")
            .EnumerateArray().ToArray();
        JsonElement requiredWhen = relationships.Single(item =>
            item.GetProperty("kind").GetString() == "requiredWhen");
        JsonElement trigger = requiredWhen.GetProperty("trigger");
        JsonElement[] predicates = trigger.GetProperty("predicates")
            .EnumerateArray().ToArray();
        Assert(relationships.Any(item =>
                   item.GetProperty("kind").GetString() == "requiresTogether" &&
                   item.GetProperty("options").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["data-root", "plugins"], StringComparer.Ordinal)) &&
               requiredWhen.GetProperty("options").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["data-root", "plugins"], StringComparer.Ordinal) &&
               requiredWhen.GetProperty("referencedOptions").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["validate-all"], StringComparer.Ordinal) &&
               trigger.GetProperty("operator").GetString() == "all" &&
               predicates.Any(predicate =>
                   predicate.GetProperty("source").GetString() == "optionValue" &&
                   predicate.GetProperty("subject").GetString() == "validate-all" &&
                   predicate.GetProperty("match").GetString() == "noneOf" &&
                   predicate.GetProperty("values").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["true", "1"], StringComparer.Ordinal) &&
                   predicate.GetProperty("matchesWhenAbsent").GetBoolean()) &&
               predicates.Any(predicate =>
                   predicate.GetProperty("source").GetString() == "inputArtifactSchema" &&
                   predicate.GetProperty("subject").GetString() ==
                       WorkflowArtifactKinds.NpcFinishCoreRequest &&
                   predicate.GetProperty("match").GetString() == "anyOf" &&
                   predicate.GetProperty("values").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(
                           [SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier],
                           StringComparer.Ordinal) &&
                   !predicate.GetProperty("matchesWhenAbsent").GetBoolean()),
            "npc finish apply omitted the typed validation/pair rules.");

        JsonElement completedGui = Command(commands, "gui");
        Assert(completedGui.GetProperty("contractStatus").GetString() == "complete",
            "Task 2 GUI discovery contract stayed incomplete.");
        Assert(AgentCommandRegistry.Validate().IsEmpty,
            "The production-composed contract registry did not validate.");

        AgentCommandContract renderContract =
            AgentCommandRegistry.GetRequired("render npc");
        ImmutableArray<AgentOptionContract> cyclicOptions = renderContract.Options
            .Select(item => item.CliName == "edition"
                ? item with { AliasFor = "game" }
                : item)
            .ToImmutableArray();
        ImmutableArray<AgentCommandContract> cyclicContracts =
            AgentCommandRegistry.All.Select(item => item.Name == "render npc"
                ? item with { Options = cyclicOptions }
                : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(cyclicContracts).Any(error =>
                error.Contains("cyclic option relationship: render npc",
                    StringComparison.Ordinal)),
            "Option-alias cycles were not rejected.");

        ImmutableArray<AgentCommandContract> unknownCanonical =
            AgentCommandRegistry.All.Select(item => item.Name == "render npc"
                ? item with { CanonicalCommand = "missing command" }
                : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(unknownCanonical).Any(error =>
                error.Contains("unknown canonical command: render npc",
                    StringComparison.Ordinal)),
            "Unknown canonical commands were not rejected.");

        AgentCommandContract applyContract =
            AgentCommandRegistry.GetLegacyDiscoveryRequired("npc finish apply");
        AgentOptionRelationshipContract applyRequiredWhen =
            applyContract.OptionRelationships.Single(item =>
                item.Kind == AgentOptionRelationshipKind.RequiredWhen);
        AgentOptionRelationshipTrigger badOptionTrigger =
            applyRequiredWhen.Trigger! with
            {
                Predicates =
                [
                    new AgentRelationshipPredicate(
                        AgentPredicateSource.OptionValue,
                        "missing-option",
                        AgentPredicateMatch.NoneOf,
                        ["true"],
                        true)
                ]
            };
        ImmutableArray<AgentCommandContract> badOptionPredicate =
            ReplaceRelationship(
                applyContract,
                applyRequiredWhen with { Trigger = badOptionTrigger });
        Assert(AgentCommandRegistry.Validate(badOptionPredicate).Any(error =>
                error.Contains("unknown option predicate subject: npc finish apply --missing-option",
                    StringComparison.Ordinal)),
            "Unknown option predicate references were not rejected.");

        AgentOptionRelationshipTrigger badSchemaTrigger =
            applyRequiredWhen.Trigger! with
            {
                Predicates =
                [
                    new AgentRelationshipPredicate(
                        AgentPredicateSource.InputArtifactSchema,
                        WorkflowArtifactKinds.NpcFinishCoreRequest,
                        AgentPredicateMatch.AnyOf,
                        ["npc.finish-core.request.unknown"],
                        false)
                ]
            };
        ImmutableArray<AgentCommandContract> badSchemaPredicate =
            ReplaceRelationship(
                applyContract,
                applyRequiredWhen with { Trigger = badSchemaTrigger });
        Assert(AgentCommandRegistry.Validate(badSchemaPredicate).Any(error =>
                error.Contains("unknown input artifact schema predicate: npc finish apply",
                    StringComparison.Ordinal)),
            "Unknown input-artifact schema predicate values were not rejected.");

        ImmutableArray<AgentCommandContract> contradictoryArtifacts =
            AgentCommandRegistry.All.Select(item => item.Name == "render npc"
                ? item with { InputArtifactKinds = ["contradictory-kind"] }
                : item).ToImmutableArray();
        Assert(AgentCommandRegistry.Validate(contradictoryArtifacts).Any(error =>
                error.Contains("input artifact kind projection mismatch: render npc",
                    StringComparison.Ordinal)),
            "Contradictory input artifact projections were not rejected.");

        CliBoundaryResult scoped = await ProtocolV2TestHost.RunAsync(
            ["render", "npc", "--help", "--protocol", "2", "--json"]);
        using JsonDocument scopedEnvelope = JsonDocument.Parse(scoped.StandardOutput);
        JsonElement scopedContract = scopedEnvelope.RootElement.GetProperty("result")
            .GetProperty("contract");
        Assert(JsonNode.DeepEquals(JsonNode.Parse(render.GetRawText()),
                   JsonNode.Parse(scopedContract.GetRawText())),
            "Protocol-2 scoped help did not serialize the discovery contract.");

        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode jsonExit = await runner.RunAsync(CommandLine.Parse(
            ["render", "npc", "--help", "--json"]), CancellationToken.None);
        Assert(jsonExit == CommandExitCode.Success && error.ToString().Length == 0,
            "V1 JSON scoped help failed for render npc.");
        using JsonDocument jsonHelp = JsonDocument.Parse(output.ToString());
        Assert(JsonNode.DeepEquals(JsonNode.Parse(render.GetRawText()),
                   JsonNode.Parse(jsonHelp.RootElement.GetRawText())),
            "V1 JSON scoped help did not semantically match discovery.");

        var (humanRunner, humanOutput, humanError) = Program.CreateRunner();
        CommandExitCode humanExit = await humanRunner.RunAsync(CommandLine.Parse(
            ["render", "npc", "--help"]), CancellationToken.None);
        Assert(humanExit == CommandExitCode.Success &&
               humanError.ToString().Length == 0 &&
               humanOutput.ToString().Contains("render npc", StringComparison.Ordinal) &&
               humanOutput.ToString().Contains("--manifest", StringComparison.Ordinal) &&
               !humanOutput.ToString().Contains("actorwright — typed Actorwright CLI", StringComparison.Ordinal),
            "Human render npc help was not command-scoped.");
    }

    private static async Task AssertLegacyDiscoveryAsync(
        DiscoveryCase testCase,
        string root)
    {
        var (runner, output, error) = Program.CreateRunner();
        CommandExitCode helpExit = await runner.RunAsync(
            CommandLine.Parse([.. testCase.Name.Split(' '), "--help"]),
            CancellationToken.None);
        Assert(helpExit == CommandExitCode.Success && error.ToString().Length == 0,
            $"Scoped help failed for {testCase.Name}: {output}{error}");
        string help = output.ToString();
        string expectedHeading = Task2Options.ContainsKey(testCase.Name) ||
            testCase.Name == "workspace preflight"
            ? $"{testCase.Name} — command options"
            : $"{testCase.Name} — legacy command options";
        Assert(help.Contains(expectedHeading, StringComparison.Ordinal) &&
               !help.Contains("actorwright — typed Actorwright CLI", StringComparison.Ordinal),
            $"Scoped help did not select the legacy discovery surface for {testCase.Name}.");

        ImmutableArray<LegacyCommandOption> catalog =
            LegacyCommandOptionCatalog.For(testCase.Name);
        Assert(catalog.Select(item => item.Name)
                .SequenceEqual(testCase.Options, StringComparer.Ordinal),
            $"Option names drifted for {testCase.Name}.");
        foreach (LegacyCommandOption option in catalog)
        {
            Assert(option.Required == testCase.Required.Contains(option.Name) &&
                   option.ValueSyntax.Length != 0 && option.Description.Length != 0,
                $"Requiredness or metadata drifted for {testCase.Name} --{option.Name}.");
            Assert(help.Contains($"--{option.Name} {option.ValueSyntax}", StringComparison.Ordinal) &&
                   help.Contains(option.Description, StringComparison.Ordinal),
                $"Scoped help omitted metadata for {testCase.Name} --{option.Name}.");
            if (ExpectedFacts.TryGetValue((testCase.Name, option.Name), out ExpectedFact? fact))
            {
                Assert(option.ValueSyntax == fact.ValueSyntax &&
                       option.AcceptedValues.SequenceEqual(
                           fact.AcceptedValues, StringComparer.Ordinal) &&
                       fact.DescriptionFragments.All(fragment =>
                           option.Description.Contains(fragment, StringComparison.Ordinal)),
                    $"Independent handler fact drifted for {testCase.Name} --{option.Name}.");
            }
        }

        string commandSlug = testCase.Name.Replace(' ', '-');
        string outputDirectory = Path.Combine(root, commandSlug);
        Directory.CreateDirectory(outputDirectory);
        var workspace = new WorkspacePath(root);
        var schemaPath = new WorkspacePath(Path.Combine(outputDirectory, "schema.json"));
        var policy = new KOnlyWorkspacePolicy(workspace, new WorkspacePath(@"F:\ExampleGame"));
        var (schemaRunner, schemaOutput, schemaError) = Program.CreateRunner(
            schemaExportService: new SchemaExportService(policy, workspace));
        CommandExitCode schemaExit = await schemaRunner.RunAsync(CommandLine.Parse([
            "schema", "export", "--command", testCase.Name,
            "--output", schemaPath.Value, "--json"
        ]), CancellationToken.None);
        Assert(schemaExit == CommandExitCode.Success && schemaError.ToString().Length == 0 &&
               File.Exists(schemaPath.Value),
            $"Legacy schema export failed for {testCase.Name}: {schemaOutput}{schemaError}");
        using JsonDocument schema = JsonDocument.Parse(
            await File.ReadAllTextAsync(schemaPath.Value));
        JsonElement[] rows = schema.RootElement.GetProperty("commands")[0]
            .GetProperty("options").EnumerateArray().ToArray();
        Assert(rows.Length == catalog.Length,
            $"Legacy schema option count drifted for {testCase.Name}.");
        for (int index = 0; index < rows.Length; index++)
        {
            JsonElement row = rows[index];
            LegacyCommandOption option = catalog[index];
            Assert(row.GetProperty("name").GetString() == option.Name &&
                   row.GetProperty("required").GetBoolean() == option.Required &&
                   row.GetProperty("valueSyntax").GetString() == option.ValueSyntax &&
                   row.GetProperty("description").GetString() == option.Description &&
                   row.GetProperty("acceptedValues").EnumerateArray()
                       .Select(item => item.GetString())
                       .SequenceEqual(option.AcceptedValues, StringComparer.Ordinal),
                $"Legacy schema metadata drifted for {testCase.Name} --{option.Name}.");
            if (ExpectedFacts.TryGetValue((testCase.Name, option.Name), out ExpectedFact? fact))
            {
                Assert(row.GetProperty("valueSyntax").GetString() == fact.ValueSyntax &&
                       row.GetProperty("acceptedValues").EnumerateArray()
                           .Select(item => item.GetString())
                           .SequenceEqual(fact.AcceptedValues, StringComparer.Ordinal) &&
                       fact.DescriptionFragments.All(fragment =>
                           row.GetProperty("description").GetString()!
                               .Contains(fragment, StringComparison.Ordinal)),
                    $"Schema omitted independent handler facts for {testCase.Name} --{option.Name}.");
            }
        }
    }

    private static async Task AssertProtocolV2BoundaryAsync()
    {
        CliBoundaryResult response = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2", "--json"]);
        Assert(response.ExitCode == 0 && response.StandardError == string.Empty,
            "Protocol-2 capabilities failed.");
        using JsonDocument envelope = JsonDocument.Parse(response.StandardOutput);
        JsonElement commands = envelope.RootElement.GetProperty("result")
            .GetProperty("commands");
        Assert(commands.GetArrayLength() == 142,
            "Protocol-2 capabilities no longer reports 142 commands.");
        foreach (DiscoveryCase testCase in Cases)
        {
            JsonElement command = commands.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == testCase.Name);
            string expectedReadiness = testCase.Name is "workspace preflight" or "npc create-from-jslot" or "preview npc" or "npc finish analyze" or "npc finish apply" or "npc finish verify"
                ? "v2"
                : "legacy";
            Assert(command.GetProperty("readiness").GetString() == expectedReadiness,
                $"Protocol readiness drifted for {testCase.Name}.");
            if (expectedReadiness == "legacy")
            {
                int expectedOptions = Task2Options.TryGetValue(testCase.Name, out var task2Options)
                    ? task2Options.Length
                    : Task3Options.TryGetValue(testCase.Name, out var task3Options)
                        ? task3Options.Length
                    : Task4Options.TryGetValue(testCase.Name, out var task4Options)
                        ? task4Options.Length
                    : testCase.Name is "npc create" or "npc create-from-preset"
                        ? testCase.Options.Length
                    : testCase.Name is "render npc" or "npc finish apply"
                        ? testCase.Options.Length
                    : Task7Profiles.ContainsKey(testCase.Name)
                        ? testCase.Options.Length
                    : Task8Profiles.ContainsKey(testCase.Name)
                        ? testCase.Options.Length
                    : Task9Profiles.ContainsKey(testCase.Name)
                        ? testCase.Options.Length
                        : 0;
                Assert(command.GetProperty("options").GetArrayLength() == expectedOptions,
                    $"Legacy protocol-2 option contract drifted for {testCase.Name}.");
                CliBoundaryResult refused = await ProtocolV2TestHost.RunAsync(
                    [.. testCase.Name.Split(' '), "--protocol", "2", "--json"]);
                Assert(refused.ExitCode != 0 &&
                       refused.StandardOutput.Contains("protocol-command-legacy", StringComparison.Ordinal),
                    $"Strict protocol-2 invocation did not refuse {testCase.Name} as legacy.");
            }
        }

        CliBoundaryResult routed = await ProtocolV2TestHost.RunAsync(
            ["workspace", "preflight", "--protocol", "2", "--json"]);
        using JsonDocument routedEnvelope = JsonDocument.Parse(routed.StandardOutput);
        JsonElement routedRoot = routedEnvelope.RootElement;
        string[] diagnosticCodes = routedRoot.GetProperty("diagnostics")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()!)
            .ToArray();
        Assert(routed.ExitCode == 2 && routed.StandardError == string.Empty &&
               routedRoot.GetProperty("protocolVersion").GetString() == "2" &&
               routedRoot.GetProperty("command").GetString() == "workspace preflight" &&
               diagnosticCodes.Contains("option-required", StringComparer.Ordinal) &&
               !diagnosticCodes.Contains("protocol-command-legacy", StringComparer.Ordinal),
            "workspace preflight was not actually routed through strict protocol 2.");
    }

    private static ImmutableArray<string> PreviewOptions() =>
    [
        "edition", "game", "manifest", "output", "visible", "morphs",
        "outfit", "variant", "camera", "lighting", "animation", "frame",
        "time", "fps", "play", "asset-root", "image-output", "width",
        "height", "hair-slots", "render-headwear"
    ];

    private static JsonElement Command(JsonElement commands, string name) =>
        commands.EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == name);

    private static ImmutableArray<string> HairOptionProfile() =>
    [
        "request:path:<request.json>:", "request-sha256:sha256:<64-hex-sha256>:",
        "proposal:path:<proposal.json>:", "proposal-sha256:sha256:<64-hex-sha256>:",
        "output:path:<output.nif>:", "manifest:path:<manifest.json>:"
    ];

    private static string OptionProfile(AgentOptionContract option) =>
        $"{option.CliName}:{ValueKindWire(option.ValueKind)}:{option.ValueSyntax}:{string.Join('|', option.AllowedValues)}";

    private static string ValueKindWire(AgentValueKind kind)
    {
        string name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void AssertOptionFact(
        AgentCommandContract contract, string option, AgentValueKind kind, string syntax)
    {
        AgentOptionContract row = contract.Options.Single(item => item.CliName == option);
        Assert(row.ValueKind == kind && row.ValueSyntax == syntax,
            $"{contract.Name} --{option} kind or syntax drifted.");
    }

    private static bool TriggerSubject(AgentOptionRelationshipTrigger? trigger, string subject) =>
        trigger is not null && trigger.Predicates.Any(item => item.Source == AgentPredicateSource.OptionValue && item.Subject == subject);

    private static bool IsPresentTrigger(AgentOptionRelationshipTrigger? trigger, string subject) =>
        trigger is { Operator: AgentPredicateCombination.All } && trigger.Predicates is [var predicate] &&
        predicate.Source == AgentPredicateSource.OptionValue && predicate.Subject == subject &&
        predicate.Match == AgentPredicateMatch.NoneOf && predicate.Values.SequenceEqual(["__absent__"], StringComparer.Ordinal) &&
        !predicate.MatchesWhenAbsent;

    private static bool IsAllPresentTrigger(AgentOptionRelationshipTrigger? trigger, params string[] subjects) =>
        trigger is { Operator: AgentPredicateCombination.All } &&
        trigger.Predicates.Select(item => item.Subject).SequenceEqual(subjects, StringComparer.Ordinal) &&
        trigger.Predicates.All(item => item.Source == AgentPredicateSource.OptionValue &&
            item.Match == AgentPredicateMatch.NoneOf && item.Values.SequenceEqual(["__absent__"], StringComparer.Ordinal) &&
            !item.MatchesWhenAbsent);

    private static bool IsTruthyTrigger(
        AgentOptionRelationshipTrigger? trigger, string subject, params string[] values) =>
        trigger is { Operator: AgentPredicateCombination.All } &&
        trigger.Predicates is [var predicate] &&
        predicate.Source == AgentPredicateSource.OptionValue &&
        predicate.Subject == subject && predicate.Match == AgentPredicateMatch.AnyOf &&
        predicate.Values.SequenceEqual(values, StringComparer.Ordinal) &&
        !predicate.MatchesWhenAbsent;

    private static bool IsNotTruthyTrigger(
        AgentOptionRelationshipTrigger? trigger, string subject, params string[] values) =>
        trigger is { Operator: AgentPredicateCombination.All } &&
        trigger.Predicates is [var predicate] &&
        predicate.Source == AgentPredicateSource.OptionValue &&
        predicate.Subject == subject && predicate.Match == AgentPredicateMatch.NoneOf &&
        predicate.Values.SequenceEqual(values, StringComparer.Ordinal) &&
        predicate.MatchesWhenAbsent;

    private static bool IsPresentAndNotTruthyTrigger(
        AgentOptionRelationshipTrigger? trigger, string presentSubject,
        string booleanSubject, params string[] trueValues) =>
        trigger is { Operator: AgentPredicateCombination.All } &&
        trigger.Predicates is [var present, var boolean] &&
        present.Source == AgentPredicateSource.OptionValue && present.Subject == presentSubject &&
        present.Match == AgentPredicateMatch.NoneOf &&
        present.Values.SequenceEqual(["__absent__"], StringComparer.Ordinal) &&
        !present.MatchesWhenAbsent &&
        boolean.Source == AgentPredicateSource.OptionValue && boolean.Subject == booleanSubject &&
        boolean.Match == AgentPredicateMatch.NoneOf &&
        boolean.Values.SequenceEqual(trueValues, StringComparer.Ordinal) &&
        boolean.MatchesWhenAbsent;

    private static AgentOptionRelationshipTrigger PresentTestTrigger(string subject) => new(AgentPredicateCombination.All,
        [new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, subject, AgentPredicateMatch.NoneOf, ["__absent__"], false)]);

    private static void AssertActiveForbidden(AgentCommandContract contract, string first, string second)
    {
        AgentOptionRelationshipContract row = contract.OptionRelationships.Single(item =>
            item.Kind == AgentOptionRelationshipKind.ForbiddenWhen &&
            item.Options.SequenceEqual([first, second], StringComparer.Ordinal));
        AgentRelationshipPredicate[] predicates = row.Trigger?.Predicates.ToArray() ?? [];
        bool secondIsBoolean = contract.Options.Single(item => item.CliName == second).ValueKind == AgentValueKind.Boolean;
        Assert(row.Trigger is { Operator: AgentPredicateCombination.All } &&
               predicates.Select(item => item.Subject).SequenceEqual([first, second], StringComparer.Ordinal) &&
               predicates[0].Match == AgentPredicateMatch.AnyOf && predicates[0].Values.SequenceEqual(["true", "1"], StringComparer.Ordinal) &&
               (secondIsBoolean
                   ? predicates[1].Match == AgentPredicateMatch.AnyOf && predicates[1].Values.SequenceEqual(["true", "1"], StringComparer.Ordinal)
                   : predicates[1].Match == AgentPredicateMatch.NoneOf && predicates[1].Values.SequenceEqual(["__absent__"], StringComparer.Ordinal)) &&
               predicates.All(item => !item.MatchesWhenAbsent),
            $"{contract.Name} {first}/{second} did not use active-value forbidden predicates.");
    }

    private static void AssertArtifactKindProjection(JsonElement contract)
    {
        string?[] legacyKinds = contract.GetProperty("inputArtifactKinds")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        string?[] richKinds = contract.GetProperty("inputArtifacts")
            .EnumerateArray().Select(item => item.GetProperty("kind").GetString())
            .ToArray();
        Assert(legacyKinds.SequenceEqual(richKinds, StringComparer.Ordinal),
            $"Input artifact projections contradict for {contract.GetProperty("name").GetString()}.");
    }

    private static ImmutableArray<AgentCommandContract> ReplaceRelationship(
        AgentCommandContract contract,
        AgentOptionRelationshipContract replacement) =>
        AgentCommandRegistry.All.Select(item => item.Name == contract.Name
            ? contract with
            {
                OptionRelationships = contract.OptionRelationships.Select(relationship =>
                        relationship.Kind == replacement.Kind
                            ? replacement
                            : relationship)
                    .ToImmutableArray()
            }
            : item).ToImmutableArray();

    private static DiscoveryCase Case(
        string name,
        ImmutableArray<string> options,
        params string[] required) =>
        new(name, options, required.ToImmutableHashSet(StringComparer.Ordinal));

    private static ExpectedFact Fact(
        string valueSyntax,
        ImmutableArray<string> acceptedValues,
        params string[] descriptionFragments) =>
        new(valueSyntax, acceptedValues, descriptionFragments.ToImmutableArray());

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
