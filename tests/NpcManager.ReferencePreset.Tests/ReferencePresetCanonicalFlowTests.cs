using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.ReferencePreset.Tests;

internal static partial class ReferencePresetTransactionTests
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public static async Task TestCanonicalReferenceFlowAsync()
    {
        string root = NewReferenceTestRoot();
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath(root), new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(policy, new WorkspacePath(root));
        var transaction = CreateReferenceTransaction(root, sessions, policy);
        var output = new StringWriter();
        var handler = new ReferencePresetCommandHandler(transaction, sessions, null!, output, new StringWriter());
        AuthoritySet authorities = await WriteAuthoritiesAsync(sessions, root, Hash('8'));
        string templatePath = Path.Combine(root, "flow-template.json");
        Require(await handler.RunAsync(CommandLine.Parse(["preset", "design-propose", "--template-output", templatePath, "--json"]),
            CancellationToken.None) == CommandExitCode.Success, "Canonical flow could not emit its actual intake template.");
        JsonObject filled = JsonNode.Parse(File.ReadAllBytes(templatePath))!.AsObject();
        JsonObject values = JsonNode.Parse(File.ReadAllBytes(authorities.IntakePath.Value))!["intake"]!.AsObject();
        foreach (var value in values) filled["intake"]![value.Key] = value.Value?.DeepClone();
        File.WriteAllText(authorities.IntakePath.Value, filled.ToJsonString(PrettyOptions));
        Sha256Hash prettyIntake = MakePretty(authorities.IntakePath);
        Require(prettyIntake != authorities.IntakeSha256, "Pretty intake must have a distinct physical hash.");
        CommandExitCode result = await handler.RunAsync(CommandLine.Parse(
            ["preset", "design-propose", "--intake", authorities.IntakePath.Value, "--intake-sha256", prettyIntake.Value,
                "--output", Path.Combine(root, "design"), "--json"]), CancellationToken.None);
        Require(result == CommandExitCode.Success, "Pretty/raw-hash intake failed the real design transaction: " + output);
        string persisted = Path.Combine(root, "design", "authoring-intake.json");
        Require(Digest(File.ReadAllBytes(persisted)) == authorities.IntakeSha256,
            "Design must retain canonical intake identity after raw-hash transport.");

        var request = authorities.Request(Path.Combine(root, "proposal"), false, null) with
        {
            IntakeSha256 = prettyIntake,
            InferenceProposalSha256 = MakePretty(authorities.InferencePath),
            ReviewedDesignSha256 = MakePretty(authorities.ReviewPath),
            ResourceSnapshotSha256 = MakePretty(authorities.SnapshotPath)
        };
        ReferencePresetWriteResult proposed = await transaction.WritePresetAsync(request, null, CancellationToken.None);
        Require(proposed.Completed && proposed.Proposal is not null && proposed.ProposalSha256 is not null,
            "Pretty/raw-hash linked authoring sessions failed the real transaction: " + Codes(proposed.Diagnostics));
        Require(proposed.Proposal!.ReviewedDesignSha256 == authorities.ReviewSha256 &&
                proposed.Proposal.ResourceSnapshotSha256 == authorities.SnapshotSha256,
            "Proposal embedded transport hashes instead of canonical authority identities.");
        ReferencePresetWriteResult applied = await transaction.WritePresetAsync(request with
        {
            OutputRoot = new WorkspacePath(Path.Combine(root, "apply")), Apply = true,
            AcceptedAuthoringProposalSha256 = proposed.ProposalSha256
        }, null, CancellationToken.None);
        Require(applied.Completed && applied.VerifiedPreset is not null,
            "Canonical proposal identity was not replayable through reviewed apply: " + Codes(applied.Diagnostics));
        ReferencePresetWriteResult tampered = await transaction.WritePresetAsync(request with
        {
            OutputRoot = new WorkspacePath(Path.Combine(root, "wrong-binding")),
            InferenceProposalSha256 = Hash('1')
        }, null, CancellationToken.None);
        Require(!tampered.Completed, "Canonical identity forwarding must preserve supplied-hash refusal.");
    }

    public static async Task TestIntakeTemplateAsync()
    {
        string root = NewReferenceTestRoot();
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath(root), new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(policy, new WorkspacePath(root));
        var output = new StringWriter();
        // A template must return before touching any inference or request-loading dependency.
        var handler = new ReferencePresetCommandHandler(null!, sessions, null!, output, new StringWriter());
        string path = Path.Combine(root, "intake-template.json");
        var command = CommandLine.Parse(["preset", "design-propose", "--template-output", path, "--json"]);
        Require(await handler.RunAsync(command, CancellationToken.None) == CommandExitCode.Success && File.Exists(path),
            "The exclusive intake template route is unavailable: " + output);
        byte[] bytes = File.ReadAllBytes(path);
        string firstResponse = output.ToString();
        string secondPath = Path.Combine(root, "second-template.json");
        Require(await handler.RunAsync(CommandLine.Parse(["preset", "design-propose", "--template-output", secondPath, "--json"]),
            CancellationToken.None) == CommandExitCode.Success && File.ReadAllBytes(secondPath).AsSpan().SequenceEqual(bytes),
            "Template bytes depend on output path or run identity.");
        string outside = Path.Combine(Directory.GetParent(root)!.FullName, "outside-" + Guid.NewGuid().ToString("N") + ".json");
        Require(await handler.RunAsync(CommandLine.Parse(["preset", "design-propose", "--template-output", outside, "--json"]),
            CancellationToken.None) != CommandExitCode.Success && !File.Exists(outside), "Template escaped the admitted workspace root.");
        JsonObject template = JsonNode.Parse(bytes)!.AsObject();
        Require(template["kind"]!.GetValue<int>() == 0 && template["intake"]!["schemaVersion"]!.GetValue<int>() == 1 &&
                template.Count == 8 && !template.ContainsKey("schema"), "Template is not the existing intake session envelope.");
        using JsonDocument response = JsonDocument.Parse(firstResponse);
        Require(response.RootElement.GetProperty("templateSha256").GetString() == Digest(bytes).Value,
            "Template response does not bind its exact canonical bytes.");
        output.GetStringBuilder().Clear();
        Require(await handler.RunAsync(command, CancellationToken.None) != CommandExitCode.Success &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "Template overwrite did not preserve the existing file.");
        string mixed = Path.Combine(root, "mixed.json");
        Require(await handler.RunAsync(CommandLine.Parse(["preset", "design-propose", "--template-output", mixed,
            "--intake", path, "--json"]), CancellationToken.None) == CommandExitCode.UsageError && !File.Exists(mixed),
            "Template and normal design arguments must be mutually exclusive.");

        ReferencePresetIntake intake = ReferencePresetRulesTests.ValidIntake();
        // Fill placeholders as JSON; invalid placeholders must never enter domain constructors.
        JsonObject payload = template["intake"]!.AsObject();
        payload["projectId"] = intake.ProjectId; payload["targetName"] = intake.TargetName;
        payload["race"] = "Skyrim.esm|00013746"; payload["sex"] = (int)intake.Sex;
        payload["weight"] = intake.Weight; payload["headSystemId"] = intake.HeadSystemId;
        payload["baselineJslot"] = Path.Combine(root, "baseline.jslot"); payload["baselineJslotSha256"] = Hash('a').Value;
        payload["description"] = intake.Description;
        JsonObject image = payload["images"]![0]!.AsObject();
        image["imageId"] = "front"; image["sourcePath"] = Path.Combine(root, "front.png");
        image["sourceSha256"] = Hash('b').Value; image["encodedLength"] = 1024;
        JsonObject target = payload["target"]!.AsObject();
        target["authorityId"] = "reviewed-target"; target["race"] = "Skyrim.esm|00013746";
        target["sex"] = (int)intake.Sex; target["dataRoot"] = Path.Combine(root, "Data");
        string filled = Path.Combine(root, "filled.json"); File.WriteAllBytes(filled, JsonSerializer.SerializeToUtf8Bytes(template, PrettyOptions));
        var read = await sessions.ReadAsync(new(new WorkspacePath(filled), Digest(File.ReadAllBytes(filled)),
            ReferencePresetSessionDocumentKind.Intake), CancellationToken.None);
        Require(read.Document?.Intake is { } completed && !ReferencePresetAuthoringRules.ValidateIntake(completed)
            .Any(item => item.Severity == DiagnosticSeverity.Error), "Filled template is not admitted by actual typed intake rules.");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "artifacts", "task11", "filled-template-path.txt"), filled);
    }

    private static Sha256Hash MakePretty(WorkspacePath path)
    {
        byte[] pretty = JsonSerializer.SerializeToUtf8Bytes(JsonNode.Parse(File.ReadAllBytes(path.Value)), PrettyOptions);
        File.WriteAllBytes(path.Value, pretty); return Digest(pretty);
    }

    private static string NewReferenceTestRoot()
    {
        string root = Path.Combine(Path.GetFullPath(Environment.CurrentDirectory), "artifacts", "task11", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static ReferencePresetAuthoringTransaction CreateReferenceTransaction(string root,
        ReferencePresetSessionService sessions, IWorkspacePolicy policy) => new(policy, new WorkspacePath(root), Hash('8'), sessions,
            new ControlledImageDecoder(), new ControlledInferenceService(), new ControlledDescriptionInterpreter(), new ControlledProjector(),
            new ControlledRenderInputBuilder(), new ControlledResponseMatrixBuilder(), new ControlledSolver(), new ControlledComparisonService(),
            new ControlledPresetWriter(), new ControlledNpcBuildService());
}
