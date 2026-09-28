using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNpcFinishCoreContracts()
    {
        Assert(
            SkyrimNpcFinishCoreRequest.SchemaIdentifier ==
                "npc.finish-core.request.v2" &&
            SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier ==
                "npc.finish-core.request.v1",
            "Finish Core request schema identifier drifted.");
        Assert(
            SkyrimNpcFinishCoreProposal.SchemaIdentifier ==
                "npc.finish-core.proposal.v2" &&
            SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier ==
                "npc.finish-core.proposal.v1" &&
            SkyrimNpcFinishCoreManifest.SchemaIdentifier ==
                "npc.finish-core.manifest.v1" &&
            SkyrimNpcFinishCoreVerification.SchemaIdentifier ==
                "npc.finish-core.verification.v1",
            "Finish Core document schema identifiers drifted.");
        Assert(
            Enum.GetNames<SkyrimNpcFinishCoreBodyRoute>()
                .SequenceEqual(["Cbbe3Ba", "Cotr", "Ube"]) &&
            Enum.GetNames<SkyrimNpcFinishCoreOutfitPolicy>()
                .SequenceEqual(["ExistingOutfit", "PrivateOutfit"]) &&
            Enum.GetNames<SkyrimNpcFinishCoreInventoryPolicy>()
                .SequenceEqual(["PreserveInventory", "ReplaceExactInventory"]) &&
            Enum.GetNames<SkyrimNpcFinishCoreStatus>()
                .SequenceEqual([
                    "ReadyForReviewedWrite",
                    "NoChanges",
                    "Refused",
                    "StaticPassRuntimeRequired",
                    "StaticPassInstallDependencyRequired"
                ]),
            "Finish Core closed enum members drifted.");

        JsonObject valid = new()
        {
            ["schema"] = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
            ["source"] = new JsonObject
            {
                ["packageRoot"] = "projects/NpcManagerReimplementation",
                ["packageManifest"] = "projects/NpcManagerReimplementation/PROJECT_MANIFEST.json",
                ["packageManifestSha256"] = new string('a', 64),
                ["packageTreeSha256"] = new string('b', 64),
                ["pluginPath"] = "projects/NpcManagerReimplementation/PROJECT_MANIFEST.json",
                ["plugin"] = "Test.esp",
                ["pluginSha256"] = new string('c', 64)
            },
            ["actor"] = new JsonObject
            {
                ["editorId"] = "TestActor",
                ["formId"] = "0x00000800"
            },
            ["authorities"] = new JsonObject
            {
                ["bodyRoute"] = "Cbbe3Ba",
                ["providers"] = new JsonArray(),
                ["additionalMasters"] = new JsonArray()
            },
            ["followerPolicy"] = new JsonObject
            {
                ["recruitable"] = true,
                ["defensiveOnly"] = true,
                ["potentialFollowerFaction"] = "Skyrim.esm|0x0005C84D",
                ["currentFollowerFaction"] = "Skyrim.esm|0x0005C84E",
                ["relationshipRank"] = "Ally"
            },
            ["aiPolicy"] = new JsonObject
            {
                ["aggression"] = "Unaggressive",
                ["confidence"] = "Brave",
                ["energy"] = 50,
                ["morality"] = "NoCrime",
                ["assistance"] = "HelpsFriendsAndAllies",
                ["mood"] = "Neutral"
            },
            ["outfitPolicy"] = new JsonObject
            {
                ["policy"] = "ExistingOutfit",
                ["existingOutfit"] = null,
                ["armorItems"] = new JsonArray()
            },
            ["inventoryPolicy"] = new JsonObject
            {
                ["policy"] = "PreserveInventory",
                ["expectedSourceItems"] = new JsonArray(),
                ["desiredItems"] = new JsonArray()
            },
            ["sandboxAuthority"] = new JsonObject
            {
                ["copiedMaster"] = "projects/NpcManagerReimplementation/PROJECT_MANIFEST.json",
                ["copiedMasterSha256"] = new string('d', 64),
                ["template"] = "Skyrim.esm|0x0001B217",
                ["templateEditorId"] = "DefaultSandboxEditorLocation512",
                ["rawRecordDigest"] = new string('e', 64)
            },
            ["output"] = new JsonObject
            {
                ["root"] = "projects/NpcManagerReimplementation/03-builds/work/contract-output",
                ["archive"] = "projects/NpcManagerReimplementation/03-builds/work/contract-output.zip",
                ["pluginFileName"] = "Test.esp"
            }
        };
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(valid);
        SkyrimNpcFinishCoreRequest request =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                canonical,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(request.Schema == SkyrimNpcFinishCoreRequest.SchemaIdentifier,
            "A closed Finish Core request did not parse.");

        JsonObject withAdditionalMaster = valid.DeepClone().AsObject();
        withAdditionalMaster["authorities"]!.AsObject()["additionalMasters"] =
            new JsonArray
            {
                new JsonObject
                {
                    ["plugin"] = "Update.esm",
                    ["path"] = "projects/NpcManagerReimplementation/Update.esm",
                    ["sha256"] = new string('A', 64),
                    ["byteLength"] = 1,
                    ["loadOrderIndex"] = 1
                }
            };
        SkyrimNpcFinishCoreRequest additionalMasterRequest =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                JsonSerializer.SerializeToUtf8Bytes(withAdditionalMaster),
                new WorkspacePath(@"K:\ExampleWorkspace"));
        SkyrimNpcFinishCoreAdditionalMasterBinding additionalMaster =
            additionalMasterRequest.Authorities.AdditionalMasters.Single();
        byte[] additionalMasterCanonical =
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                additionalMasterRequest,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        SkyrimNpcFinishCoreRequest additionalMasterReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                additionalMasterCanonical,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        var embeddedAdditionalMasterProposal = new SkyrimNpcFinishCoreProposal
        {
            RequestSha256 = new Sha256Hash(new string('a', 64)),
            Request = additionalMasterRequest,
            Status = SkyrimNpcFinishCoreStatus.NoChanges
        };
        byte[] embeddedAdditionalMasterProposalBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                embeddedAdditionalMasterProposal,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        SkyrimNpcFinishCoreProposal embeddedAdditionalMasterReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                embeddedAdditionalMasterProposalBytes,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(
            additionalMaster.Plugin.Value == "Update.esm" &&
            additionalMaster.Path.Value == Path.Combine(
                @"K:\ExampleWorkspace",
                "projects",
                "NpcManagerReimplementation",
                "Update.esm") &&
            additionalMaster.Sha256 == new Sha256Hash(new string('A', 64)) &&
            additionalMaster.ByteLength == 1 &&
            additionalMaster.LoadOrderIndex == 1,
            "The parsed non-empty v2 additional-master binding fields did not match the fixture.");
        Assert(
            JsonNode.Parse(additionalMasterCanonical)!["authorities"]!
                ["additionalMasters"]![0]!["sha256"]!.GetValue<string>() ==
                new string('A', 64),
            "The serialized non-empty v2 additional-master SHA-256 was not uppercase on the wire.");
        Assert(
            additionalMasterReplay.Authorities.AdditionalMasters.Single() ==
                additionalMaster,
            "The non-empty v2 additional-master binding did not round-trip through request parsing.");
        Assert(
            embeddedAdditionalMasterReplay.Request!.Authorities.AdditionalMasters.Single() ==
                additionalMaster,
            "The non-empty v2 additional-master binding did not survive embedded proposal parsing.");
        Assert(
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    additionalMasterReplay,
                    new WorkspacePath(@"K:\ExampleWorkspace"))
                .AsSpan().SequenceEqual(additionalMasterCanonical),
            "The non-empty v2 additional-master request did not replay to identical canonical bytes.");
        Assert(
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    embeddedAdditionalMasterReplay,
                    new WorkspacePath(@"K:\ExampleWorkspace"))
                .AsSpan().SequenceEqual(embeddedAdditionalMasterProposalBytes),
            "The non-empty v2 additional-master proposal did not replay to identical canonical bytes.");

        JsonObject legacy = valid.DeepClone().AsObject();
        legacy["schema"] = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        legacy["authorities"]!.AsObject().Remove("additionalMasters");
        legacy["aiPolicy"]!.AsObject().Remove("mood");
        SkyrimNpcFinishCoreRequest legacyRequest =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                JsonSerializer.SerializeToUtf8Bytes(legacy),
                new WorkspacePath(@"K:\ExampleWorkspace"));
        byte[] legacyCanonical = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            legacyRequest,
            new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(
            legacyRequest.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            !JsonNode.Parse(legacyCanonical)!.AsObject()["authorities"]!
                .AsObject().ContainsKey("additionalMasters") &&
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                    legacyCanonical,
                    new WorkspacePath(@"K:\ExampleWorkspace")),
                new WorkspacePath(@"K:\ExampleWorkspace"))
                .AsSpan().SequenceEqual(legacyCanonical),
            "The explicit v1 fixture did not replay without injecting v2 authority members.");

        JsonObject withAiPolicy = valid.DeepClone().AsObject();
        withAiPolicy["aiPolicy"] = new JsonObject
        {
            ["aggression"] = "Unaggressive",
            ["confidence"] = "Brave",
            ["energy"] = 50,
            ["morality"] = "NoCrime",
            ["assistance"] = "HelpsFriendsAndAllies",
            ["mood"] = "Neutral"
        };
        SkyrimNpcFinishCoreRequest aiRequest =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(withAiPolicy.ToJsonString()),
                new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(
            aiRequest.AiPolicy is
            {
                Aggression: SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence: SkyrimNpcFinishCoreConfidence.Brave,
                Energy: 50,
                Morality: SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance: SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                Mood: SkyrimNpcFinishCoreMood.Neutral
            },
            "Finish Core did not parse the complete v2 AIDT authoring policy.");
        Assert(
            JsonNode.Parse(SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    request,
                    new WorkspacePath(@"K:\ExampleWorkspace")))!
                .AsObject()["aiPolicy"]!.AsObject()["mood"]!.GetValue<string>() ==
                "Neutral",
            "Finish Core did not retain the required v2 aiPolicy mood.");

        TestFinishCoreProjectRelativePathDiagnostics(valid);

        JsonObject unknown = valid.DeepClone().AsObject();
        unknown["cell"] = "Skyrim.esm|0x0000A16A";
        try
        {
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(unknown.ToJsonString()),
                new WorkspacePath(@"K:\ExampleWorkspace"));
            throw new InvalidOperationException(
                "Finish Core accepted an unknown top-level member.");
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message ==
                    "Finish Core request members are closed; unknown=[cell], missing=[].",
                "Finish Core closed-member refusal lost its actionable detail: " +
                exception.Message);
        }

        JsonObject unknownAndMissing = valid.DeepClone().AsObject();
        unknownAndMissing["cell"] = "Skyrim.esm|0x0000A16A";
        unknownAndMissing.Remove("output");
        try
        {
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(unknownAndMissing.ToJsonString()),
                new WorkspacePath(@"K:\ExampleWorkspace"));
            throw new InvalidOperationException(
                "Finish Core accepted unknown and missing top-level members.");
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message ==
                    "Finish Core request members are closed; unknown=[cell], missing=[output].",
                "Finish Core unknown-and-missing refusal lost its actionable detail: " +
                exception.Message);
        }

        JsonObject invalidInventoryPolicy = valid.DeepClone().AsObject();
        invalidInventoryPolicy["inventoryPolicy"]!["policy"] = "Unknown";
        try
        {
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(invalidInventoryPolicy.ToJsonString()),
                new WorkspacePath(@"K:\ExampleWorkspace"));
            throw new InvalidOperationException(
                "Finish Core accepted an unknown inventory policy.");
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message ==
                    "Finish Core enum 'policy' is invalid; expected one of [PreserveInventory, ReplaceExactInventory].",
                "Finish Core enum refusal omitted the exact admitted values: " +
                exception.Message);
        }

        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(
                    "{\"schema\":\"npc.finish-core.request.v2\",\"schema\":\"npc.finish-core.request.v2\"}"),
                new WorkspacePath(@"K:\ExampleWorkspace")));
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetBytes(
                    "{\"schema\":\"npc.finish-core.request.v2\",}"),
                new WorkspacePath(@"K:\ExampleWorkspace")));
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                Encoding.UTF8.GetPreamble()
                    .Concat(canonical)
                    .ToArray(),
                new WorkspacePath(@"K:\ExampleWorkspace")));

        JsonObject reversed = new();
        foreach (KeyValuePair<string, JsonNode?> property in valid.Reverse())
            reversed[property.Key] = property.Value?.DeepClone();
        byte[] first = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
            Encoding.UTF8.GetBytes(valid.ToJsonString()),
            new WorkspacePath(@"K:\ExampleWorkspace"));
        byte[] second = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
            Encoding.UTF8.GetBytes(reversed.ToJsonString()),
            new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(first.SequenceEqual(second),
            "Finish Core canonical bytes depend on input property order.");

        var proposal = new SkyrimNpcFinishCoreProposal
        {
            RequestSha256 = new Sha256Hash(new string('a', 64)),
            ProposalSha256 = new Sha256Hash(new string('b', 64)),
            Request = request,
            Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
        };
        byte[] proposalBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal,
                new WorkspacePath(@"K:\ExampleWorkspace"));
        Assert(
            new Sha256Hash(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    SkyrimNpcFinishCoreDocumentCodec.RemoveProposalHash(
                        proposalBytes)))) ==
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    proposalBytes),
            "Proposal self-hash omission is not deterministic.");

        await TestFinishCoreApplyAcceptsExactRawRequestBindingAsync(valid);
        await TestFinishCoreNullActorAssemblyEvidenceIsAdmittedAsync(valid);
        await TestFinishCoreApplyRejectsEmbeddedRequestDriftAsync(valid);
        await TestFinishCoreRejectsDefensiveOnlyFalseAsync(valid);
        await TestFinishCoreRejectsRecruitableHelpsNobodyAsync(valid);
        await TestFinishCoreMissingBindingDiagnosticAsync();
        await TestFinishCorePackageTreeMismatchDiagnosticAsync();
        await TestFinishCorePackageRelativePluginBindingAndProviderDiagnosticsAsync();
    }

    private static void TestFinishCoreProjectRelativePathDiagnostics(
        JsonObject valid)
    {
        var cases = new[]
        {
            (Value: @"C:/outside/Test.esp", Violation: "rooted"),
            (Value: @"projects\\NpcManagerReimplementation\\Test.esp", Violation: "backslash"),
            (Value: @"projects/NpcManagerReimplementation/./Test.esp", Violation: "dot-or-empty"),
            (Value: @"../outside/Test.esp", Violation: "escape")
        };
        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string value, string violation) in cases)
        {
            JsonObject candidate = valid.DeepClone().AsObject();
            candidate["source"]!.AsObject()["pluginPath"] = value;
            try
            {
                SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                    JsonSerializer.SerializeToUtf8Bytes(candidate),
                    new WorkspacePath(@"K:\ExampleWorkspace"));
                throw new InvalidOperationException(
                    $"Finish Core accepted {violation} project path '{value}'.");
            }
            catch (InvalidDataException exception)
            {
                Assert(
                    exception.Message.Contains("pluginPath", StringComparison.Ordinal) &&
                    exception.Message.Contains(
                        "project-relative forward-slash path", StringComparison.Ordinal) &&
                    exception.Message.Contains(violation, StringComparison.Ordinal),
                    $"Finish Core path refusal for {violation} lost its field, path contract, or distinct violation: {exception.Message}");
                messages.Add(exception.Message);
            }
        }
        Assert(messages.Count == cases.Length,
            "Finish Core path violations collapsed into one indistinguishable diagnostic.");
    }

    private static async Task TestFinishCoreApplyAcceptsExactRawRequestBindingAsync(
        JsonObject valid)
    {
        WorkspacePath workspace = new(@"K:\ExampleWorkspace");
        byte[] rawRequestBytes = Encoding.UTF8.GetBytes(valid.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));
        Sha256Hash rawRequestSha = new(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(rawRequestBytes)));
        SkyrimNpcFinishCoreRequest request =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(rawRequestBytes, workspace);
        var proposalWithoutHash = new SkyrimNpcFinishCoreProposal
        {
            RequestSha256 = rawRequestSha,
            Request = request,
            Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
        };
        Sha256Hash proposalSha =
            SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    proposalWithoutHash, workspace));
        SkyrimNpcFinishCoreProposal proposal = proposalWithoutHash with
        {
            ProposalSha256 = proposalSha
        };
        var sourceResult = new SkyrimNpcFinishCoreSourceReadResult(
            false,
            null,
            new Sha256Hash(new string('1', 64)),
            new Sha256Hash(new string('2', 64)),
            new FormReference(new PluginName("Test.esp"), new FormId(0x800)),
            new EditorId("TestActor"),
            false,
            null,
            ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, int>.Empty,
            [new Diagnostic(
                "finish-core-test-source-stop",
                DiagnosticSeverity.Error,
                "The request binding reached source reinspection.")]);
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(sourceResult),
            workspace);

        SkyrimNpcFinishCoreApplyResult result = await service.ApplyAsync(
            request,
            rawRequestSha,
            proposal,
            proposalSha,
            CancellationToken.None);

        Assert(
            result.Diagnostics.Any(item =>
                item.Code == "finish-core-test-source-stop") &&
            result.Diagnostics.All(item =>
                item.Code != "finish-core-apply-request-hash"),
            "Finish Core apply rejected an exact raw request hash after parsing the same valid request bytes: " +
            string.Join(" | ", result.Diagnostics.Select(item =>
                item.Code + ":" + item.Message)));
    }

    private static async Task TestFinishCoreNullActorAssemblyEvidenceIsAdmittedAsync(
        JsonObject valid)
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SkyrimNpcFinishCoreRequest request = LocalAdmittedRequest(
                valid,
                workspace,
                root);
            Assert(request.Authorities.ActorAssemblySha256 is null,
                "The Actor Assembly null-evidence regression fixture drifted.");
            SkyrimNpcFinishCoreSourceReadResult source = AdmittedSource(request);
            var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source),
                workspace);
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request,
                workspace);

            SkyrimNpcFinishCoreProposalResult result = await service.AnalyzeAsync(
                request,
                requestSha,
                new WorkspacePath(Path.Combine(root, "proposal.json")),
                CancellationToken.None);

            Assert(
                result.Proposed && result.Proposal is not null &&
                result.Diagnostics.All(item =>
                    !item.Code.Contains("actor-assembly", StringComparison.Ordinal)),
                "Finish Core rejected a valid source inspection merely because optional Actor Assembly evidence was null: " +
                string.Join(" | ", result.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestFinishCoreApplyRejectsEmbeddedRequestDriftAsync(
        JsonObject valid)
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SkyrimNpcFinishCoreRequest request = LocalAdmittedRequest(
                valid,
                workspace,
                root);
            SkyrimNpcFinishCoreSourceReadResult source = AdmittedSource(request);
            var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source),
                workspace);
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request,
                workspace);
            SkyrimNpcFinishCoreProposalResult analyzed = await service.AnalyzeAsync(
                request,
                requestSha,
                new WorkspacePath(Path.Combine(root, "proposal.json")),
                CancellationToken.None);
            Assert(analyzed.Proposal is not null,
                "The embedded-request drift fixture did not produce a proposal.");
            SkyrimNpcFinishCoreRequest driftedRequest = analyzed.Proposal!.Request! with
            {
                Output = analyzed.Proposal.Request!.Output with
                {
                    PluginFileName = "Different.esp"
                }
            };
            SkyrimNpcFinishCoreProposal driftedWithoutHash = analyzed.Proposal with
            {
                Request = driftedRequest,
                ProposalSha256 = null
            };
            Sha256Hash driftedProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        driftedWithoutHash,
                        workspace));
            SkyrimNpcFinishCoreProposal driftedProposal = driftedWithoutHash with
            {
                ProposalSha256 = driftedProposalSha
            };

            SkyrimNpcFinishCoreApplyResult result = await service.ApplyAsync(
                request,
                requestSha,
                driftedProposal,
                driftedProposalSha,
                CancellationToken.None);

            Assert(
                !result.Applied && result.Diagnostics.Any(item =>
                    item.Code == "finish-core-apply-stale-proposal"),
                "Finish Core apply accepted proposal semantics whose embedded request drifted from the analyzed request.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static SkyrimNpcFinishCoreRequest LocalAdmittedRequest(
        JsonObject valid,
        WorkspacePath workspace,
        string root)
    {
        JsonObject local = valid.DeepClone().AsObject();
        string relativeRoot = Path.GetRelativePath(workspace.Value, root)
            .Replace('\\', '/');
        local["source"]!["packageRoot"] = relativeRoot + "/source";
        local["source"]!["packageManifest"] = relativeRoot + "/source/manifest.json";
        local["source"]!["pluginPath"] = relativeRoot + "/source/Test.esp";
        local["sandboxAuthority"]!["copiedMaster"] =
            relativeRoot + "/source/Skyrim.esm";
        local["output"]!["root"] = relativeRoot + "/output";
        local["output"]!["archive"] = relativeRoot + "/output.zip";
        return SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            Encoding.UTF8.GetBytes(local.ToJsonString()),
            workspace);
    }

    private static async Task TestFinishCoreRejectsDefensiveOnlyFalseAsync(
        JsonObject valid)
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SkyrimNpcFinishCoreRequest admitted = LocalAdmittedRequest(
                valid,
                workspace,
                root);
            SkyrimNpcFinishCoreRequest request = admitted with
            {
                FollowerPolicy = admitted.FollowerPolicy with
                {
                    DefensiveOnly = false
                }
            };
            SkyrimNpcFinishCoreSourceReadResult source = AdmittedSource(request);
            var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source),
                workspace);
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request,
                workspace);

            SkyrimNpcFinishCoreProposalResult result = await service.AnalyzeAsync(
                request,
                requestSha,
                new WorkspacePath(Path.Combine(root, "proposal.json")),
                CancellationToken.None);

            Assert(
                !result.Proposed && result.Diagnostics.Any(item =>
                    item.Code == "finish-core-policy-defensive-only"),
                "Finish Core accepted followerPolicy.defensiveOnly=false even though the writer always authors the closed defensive combat style.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task TestFinishCoreRejectsRecruitableHelpsNobodyAsync(
        JsonObject valid)
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SkyrimNpcFinishCoreRequest admitted = LocalAdmittedRequest(valid, workspace, root);
            SkyrimNpcFinishCoreRequest request = admitted with
            {
                AiPolicy = admitted.AiPolicy! with
                {
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsNobody
                }
            };
            SkyrimNpcFinishCoreSourceReadResult source = AdmittedSource(request) with
            {
                AiData = new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Cowardly,
                    Energy = 10,
                    Morality = SkyrimNpcFinishCoreMorality.AnyCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsNobody
                }
            };
            var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source), workspace);

            SkyrimNpcFinishCoreProposalResult result = await service.AnalyzeAsync(
                request,
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, workspace),
                new WorkspacePath(Path.Combine(root, "proposal.json")),
                CancellationToken.None);

            Assert(
                !result.Proposed && result.Diagnostics.Any(item =>
                    item.Code == "finish-core-policy-assistance"),
                "Finish Core admitted a recruitable actor whose effective AIDT assistance remained HelpsNobody.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static SkyrimNpcFinishCoreSourceReadResult AdmittedSource(
        SkyrimNpcFinishCoreRequest request) =>
        new(
            true,
            null,
            request.Source.PackageTreeSha256!.Value,
            request.Source.PluginSha256!.Value,
            new FormReference(
                request.Source.Plugin!.Value,
                request.Actor.FormId!.Value),
            request.Actor.EditorId!.Value,
            true,
            "None",
            ImmutableDictionary<string, int>.Empty,
            ImmutableDictionary<string, int>.Empty,
            ImmutableArray<Diagnostic>.Empty)
        {
            NextFormId = new FormId(0x801),
            MasterOrder = ["Skyrim.esm"],
            AiData = new SkyrimNpcFinishCoreAiPolicy
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Average,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsAllies
            }
        };

    private static async Task TestFinishCoreMissingBindingDiagnosticAsync()
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        WorkspacePath packageRoot = new(Path.Combine(workspace.Value, "artifacts", "missing-package"));
        WorkspacePath manifestPath = new(Path.Combine(packageRoot.Value, "npcmanager-package.json"));
        WorkspacePath pluginPath = new(Path.Combine(packageRoot.Value, "Test.esp"));
        SkyrimNpcFinishCoreRequest request = BoundSourceRequest(
            packageRoot,
            manifestPath,
            new Sha256Hash(new string('a', 64)),
            null,
            pluginPath,
            new Sha256Hash(new string('c', 64)));
        SkyrimNpcFinishCoreSourcePackageReader reader = CreateSourceReader(
            workspace,
            new UnexpectedPackageVerifyService());

        SkyrimNpcFinishCoreSourceReadResult result = await reader.InspectAsync(
            request,
            CancellationToken.None);
        Diagnostic diagnostic = result.Diagnostics.Single(item =>
            item.Code == "finish-core-source-fields");

        Assert(
            !result.Admitted &&
            diagnostic.Message ==
                "Finish Core source and actor bindings are incomplete; missing=[source.packageTreeSha256].",
            "Finish Core did not identify the exact missing source binding while optional authority hashes remained null: " +
            diagnostic.Message);
    }

    private static async Task TestFinishCorePackageTreeMismatchDiagnosticAsync()
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-tree-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath packageRoot = new(root);
            WorkspacePath manifestPath = new(Path.Combine(root, "npcmanager-package.json"));
            WorkspacePath pluginPath = new(Path.Combine(root, "Test.esp"));
            await File.WriteAllBytesAsync(manifestPath.Value, Encoding.UTF8.GetBytes("{}"));
            await File.WriteAllBytesAsync(pluginPath.Value, [1, 2, 3]);
            Sha256Hash manifestSha256 = new(
                "44136FA355B3678A1146AD16F7E8649E94FB4FC21FE77E8310C060F61CAAFF8A");
            Sha256Hash pluginSha256 = new(
                "039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81");
            Sha256Hash received = new(new string('a', 64));
            Sha256Hash expected = new(
                "8D17F686F9ED4756F7B1A2A1C62EE7B96534EC010504132D1BFF8BAAC36383F9");
            var artifact = new PackageVerificationArtifact(
                "1",
                "package-verification",
                "skyrimSpecialEdition",
                "jslot",
                "Test.esp",
                new FormId(0x800),
                manifestPath,
                manifestSha256,
                ImmutableArray<PackageFileVerification>.Empty,
                true,
                true,
                false);
            SkyrimNpcFinishCoreSourcePackageReader reader = CreateSourceReader(
                workspace,
                new FixedPackageVerifyService(artifact));
            SkyrimNpcFinishCoreRequest request = BoundSourceRequest(
                packageRoot,
                manifestPath,
                manifestSha256,
                received,
                pluginPath,
                pluginSha256);

            SkyrimNpcFinishCoreSourceReadResult result = await reader.InspectAsync(
                request,
                CancellationToken.None);
            Diagnostic diagnostic = result.Diagnostics.Single(item =>
                item.Code == "finish-core-source-package-tree-hash");

            Assert(
                !result.Admitted &&
                result.PackageTreeSha256 == expected &&
                diagnostic.Message ==
                    $"The package tree differs from the request binding; expected={expected.Value}, received={received.Value}.",
                "Finish Core package-tree mismatch did not expose expected and received bindings: " +
                diagnostic.Message);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestFinishCorePackageRelativePluginBindingAndProviderDiagnosticsAsync()
    {
        WorkspacePath workspace = ActorwrightWorkspace.ResolveRoot();
        string root = Path.Combine(
            workspace.Value,
            "artifacts",
            "finish-core-plugin-binding-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath packageRoot = new(root);
            WorkspacePath manifestPath = new(Path.Combine(root, "npcmanager-package.json"));
            WorkspacePath pluginPath = new(Path.Combine(root, "Data", "Test.esp"));
            WorkspacePath providerPath = new(Path.Combine(root, "Data", "Skyrim.esm"));
            string faceGeomRelative =
                "Data/meshes/actors/character/FaceGenData/FaceGeom/Test.esp/00000800.nif";
            string faceTintRelative =
                "Data/textures/actors/character/FaceGenData/FaceTint/Test.esp/00000800.dds";
            string faceGeomPath = Path.Combine(root,
                faceGeomRelative.Replace('/', Path.DirectorySeparatorChar));
            string faceTintPath = Path.Combine(root,
                faceTintRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(faceTintPath)!);

            byte[] manifestBytes = Encoding.UTF8.GetBytes("{}");
            byte[] pluginBytes = [1, 2, 3];
            byte[] providerBytes = [4, 5];
            byte[] faceGeomBytes = [6];
            byte[] faceTintBytes = [7];
            await File.WriteAllBytesAsync(manifestPath.Value, manifestBytes);
            await File.WriteAllBytesAsync(pluginPath.Value, pluginBytes);
            await File.WriteAllBytesAsync(providerPath.Value, providerBytes);
            await File.WriteAllBytesAsync(faceGeomPath, faceGeomBytes);
            await File.WriteAllBytesAsync(faceTintPath, faceTintBytes);

            static Sha256Hash HashBytes(byte[] bytes) => new(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)));
            static PackageFileVerification VerifiedFile(
                string kind,
                string relativePath,
                byte[] bytes)
            {
                Sha256Hash hash = HashBytes(bytes);
                return new PackageFileVerification(
                    kind,
                    new AssetPath(relativePath),
                    bytes.LongLength,
                    bytes.LongLength,
                    hash,
                    hash,
                    true);
            }

            Sha256Hash manifestSha256 = HashBytes(manifestBytes);
            Sha256Hash pluginSha256 = HashBytes(pluginBytes);
            Sha256Hash providerSha256 = HashBytes(providerBytes);
            Sha256Hash packageTreeSha256 =
                SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(packageRoot);
            var artifact = new PackageVerificationArtifact(
                "1",
                "package-verification",
                "skyrimSpecialEdition",
                "jslot",
                "Test.esp",
                new FormId(0x800),
                manifestPath,
                manifestSha256,
                [
                    VerifiedFile("plugin", "Data/Test.esp", pluginBytes),
                    VerifiedFile("facegeom", faceGeomRelative, faceGeomBytes),
                    VerifiedFile("facetint", faceTintRelative, faceTintBytes),
                    VerifiedFile("provider", "Data/Skyrim.esm", providerBytes)
                ],
                true,
                true,
                false);
            SkyrimNpcFinishCoreSourcePackageReader reader = CreateSourceReader(
                workspace,
                new FixedPackageVerifyService(artifact));
            SkyrimNpcFinishCoreRequest request = BoundSourceRequest(
                packageRoot,
                manifestPath,
                manifestSha256,
                packageTreeSha256,
                pluginPath,
                pluginSha256) with
            {
                Authorities = new SkyrimNpcFinishCoreAuthorities
                {
                    BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                    Providers =
                    [
                        new SkyrimNpcFinishCoreProviderAuthority
                        {
                            Plugin = new PluginName("Skyrim.esm"),
                            Path = providerPath,
                            Sha256 = providerSha256,
                            ByteLength = providerBytes.LongLength
                        }
                    ]
                },
                OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                    ExistingOutfit = new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x12E46))
                }
            };

            SkyrimNpcFinishCoreSourceReadResult pluginResult = await reader.InspectAsync(
                request,
                CancellationToken.None);
            SkyrimNpcFinishCoreSourceReadResult emptyProvidersResult = await reader.InspectAsync(
                request with
                {
                    Authorities = request.Authorities with
                    {
                        Providers = ImmutableArray<SkyrimNpcFinishCoreProviderAuthority>.Empty
                    }
                },
                CancellationToken.None);
            Diagnostic? emptyProvidersDiagnostic = emptyProvidersResult.Diagnostics
                .FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);

            Assert(
                !pluginResult.Admitted &&
                pluginResult.Diagnostics.Any(item =>
                    item.Code == "finish-core-source-plugin-raw-invalid" &&
                    item.Message == "The retained plugin bytes must begin with TES4.") &&
                pluginResult.Diagnostics.All(item =>
                    item.Code != "finish-core-source-plugin-manifest-entry") &&
                !emptyProvidersResult.Admitted &&
                emptyProvidersDiagnostic?.Code == "finish-core-authority-provider-empty" &&
                emptyProvidersDiagnostic?.Message ==
                    "At least one fully bound provider authority is required.",
                "Finish Core did not bind the exact package-relative plugin or report an empty provider set truthfully: " +
                $"plugin=[{string.Join(',', pluginResult.Diagnostics.Select(item => item.Code))}] " +
                $"providers=[{string.Join(',', emptyProvidersResult.Diagnostics.Select(item => item.Code))}].");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SkyrimNpcFinishCoreSourcePackageReader CreateSourceReader(
        WorkspacePath workspace,
        IPackageVerifyService packageVerifier)
    {
        var policy = new KOnlyWorkspacePolicy(
            workspace,
            ActorwrightWorkspace.ResolveProtectedRoot(workspace));
        return new SkyrimNpcFinishCoreSourcePackageReader(
            workspace,
            policy,
            new PackageManifestReader(policy, workspace),
            packageVerifier,
            new BethesdaSkyrimNpcFinishCoreSourceReader());
    }

    private static SkyrimNpcFinishCoreRequest BoundSourceRequest(
        WorkspacePath packageRoot,
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        Sha256Hash? packageTreeSha256,
        WorkspacePath pluginPath,
        Sha256Hash pluginSha256) =>
        new()
        {
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = packageRoot,
                PackageManifest = manifestPath,
                PackageManifestSha256 = manifestSha256,
                PackageTreeSha256 = packageTreeSha256,
                PluginPath = pluginPath,
                Plugin = new PluginName("Test.esp"),
                PluginSha256 = pluginSha256
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("TestActor"),
                FormId = new FormId(0x800)
            }
        };

    private sealed class UnexpectedPackageVerifyService : IPackageVerifyService
    {
        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Package verification must not run before required bindings are complete.");
    }

    private sealed class FixedPackageVerifyService(
        PackageVerificationArtifact artifact) : IPackageVerifyService
    {
        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PackageVerifyResult(
                true,
                artifact,
                ImmutableArray<Diagnostic>.Empty));
    }
}
