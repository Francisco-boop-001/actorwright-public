using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNpcFinishCoreProposal()
    {
        string root = Path.Combine(
            Environment.CurrentDirectory, "artifacts", "finish-core-proposal-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath projectRoot = new(Environment.CurrentDirectory);
            SkyrimNpcFinishCoreRequest request = CreateProposalRequest(root);
            SkyrimNpcFinishCoreSourceReadResult source = CreateSource(request);
            var service = new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source),
                projectRoot);

            WorkspacePath firstPath = new(Path.Combine(root, "first-proposal.json"));
            SkyrimNpcFinishCoreProposalResult first = await service.AnalyzeAsync(
                request,
                ProposalHash("request"),
                firstPath,
                CancellationToken.None);
            Assert(first.Proposed && first.Proposal is not null && first.ProposalSha256 is not null,
                "A valid source authority did not produce a Finish Core proposal: " +
                string.Join(" | ", first.Diagnostics.Select(item => item.Code + ":" + item.Message)));
            Assert(first.Proposal!.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
                   first.Proposal.NextFormId == new FormId(0x809) &&
                   first.Proposal.NewRecords.SequenceEqual([
                       "RELA 0x00000805", "CSTY 0x00000806",
                       "OTFT 0x00000807", "PACK 0x00000808"]) &&
                   first.Proposal.ExistingRecordChanges.Any(value => value.Contains("0x00000820", StringComparison.Ordinal)) &&
                   first.Proposal.ExistingRecordChanges.Any(value => value.Contains("PotentialFollowerFaction", StringComparison.Ordinal)) &&
                   first.Proposal.ExistingRecordChanges.Any(value => value.Contains("CurrentFollowerFaction", StringComparison.Ordinal)) &&
                   first.Proposal.ForbiddenRecordCounts.All(value => value.EndsWith("=0", StringComparison.Ordinal)) &&
                   first.Proposal.MasterOrder.SequenceEqual(["Skyrim.esm", "Update.esm"]),
                "The deterministic Finish Core semantic proposal widened or lost its bounded changes.");

            SkyrimNpcFinishCoreSourceReadResult acbsSource = source with
            {
                TargetConfigurationFlags = 0x00000001u
            };
            SkyrimNpcFinishCoreProposalResult acbs = await new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(acbsSource),
                projectRoot).AnalyzeAsync(
                request,
                ProposalHash("acbs-request"),
                new WorkspacePath(Path.Combine(root, "acbs-proposal.json")),
                CancellationToken.None);
            string[] acbsChanges = acbs.Proposal?.ExistingRecordChanges
                .Where(value => value.StartsWith(
                    "NPC_ 0x00000800: ACBS", StringComparison.Ordinal))
                .ToArray() ?? [];
            Assert(
                acbs.Proposed &&
                acbsChanges.Length == 1 &&
                acbsChanges[0].Contains("before=0x00000001", StringComparison.Ordinal) &&
                acbsChanges[0].Contains("added=0x00000820", StringComparison.Ordinal) &&
                acbsChanges[0].Contains("after=0x00000821", StringComparison.Ordinal),
                "The ACBS proposal evidence did not state exact before/mask/after values: " +
                string.Join(" | ", acbs.Diagnostics.Select(item => item.Code + ":" + item.Message)));

            SkyrimNpcFinishCoreRequest legacyMoodRequest = request with
            {
                Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                Authorities = request.Authorities with
                {
                    AdditionalMasters = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
                },
                AiPolicy = request.AiPolicy! with { Mood = null }
            };
            SkyrimNpcFinishCoreSourceReadResult sourceWithMood = source with
            {
                AiData = request.AiPolicy! with { Mood = SkyrimNpcFinishCoreMood.Angry }
            };
            SkyrimNpcFinishCoreProposalResult legacyMood = await new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(sourceWithMood),
                projectRoot).AnalyzeAsync(
                legacyMoodRequest,
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                    legacyMoodRequest, projectRoot),
                new WorkspacePath(Path.Combine(root, "legacy-mood-proposal.json")),
                CancellationToken.None);
            Assert(
                legacyMood.Proposed && legacyMood.Proposal is not null &&
                !legacyMood.Proposal.ExistingRecordChanges.Any(value =>
                    value.Contains("AIDT", StringComparison.Ordinal)),
                "v1 analysis compared nullable mood instead of ignoring it: " +
                string.Join(" | ", legacyMood.Diagnostics.Select(item => item.Code + ":" + item.Message)));

            byte[] firstBytes = await File.ReadAllBytesAsync(firstPath.Value);
            Assert(SHA256.HashData(firstBytes).Length == 32 &&
                   SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(firstBytes) == first.ProposalSha256,
                "The proposal self-hash was not reproducible from its canonical bytes.");
            SkyrimNpcFinishCoreProposal reopened =
                SkyrimNpcFinishCoreDocumentCodec.ParseProposal(firstBytes, projectRoot);
            SkyrimNpcFinishCoreAdditionalMasterBinding additionalMaster =
                reopened.Request!.Authorities.AdditionalMasters.Single();
            Assert(
                additionalMaster.Plugin.Value == "Update.esm" &&
                additionalMaster.Path.Value == Path.Combine(root, "Update.esm") &&
                additionalMaster.Sha256 == ProposalHash("Update.esm") &&
                additionalMaster.ByteLength == 1 &&
                additionalMaster.LoadOrderIndex == 1 &&
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(reopened, projectRoot)
                    .AsSpan().SequenceEqual(firstBytes),
                "A non-empty v2 additional-master binding did not survive proposal embedding and canonical replay.");
            AssertThrows<InvalidDataException>(() =>
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    reopened with
                    {
                        Status = SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired
                    },
                    projectRoot));

            WorkspacePath secondPath = new(Path.Combine(root, "second-proposal.json"));
            SkyrimNpcFinishCoreProposalResult second = await service.AnalyzeAsync(
                request,
                ProposalHash("request"),
                secondPath,
                CancellationToken.None);
            byte[] secondBytes = await File.ReadAllBytesAsync(secondPath.Value);
            Assert(second.Proposed &&
                   first.ProposalSha256 == second.ProposalSha256 &&
                   firstBytes.AsSpan().SequenceEqual(secondBytes),
                "Repeated analysis did not produce byte-identical proposal output.");

            SkyrimNpcFinishCoreProposalResult existing = await new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source with { AlreadySatisfied = true }),
                projectRoot).AnalyzeAsync(
                request,
                ProposalHash("request"),
                new WorkspacePath(Path.Combine(root, "no-changes.json")),
                CancellationToken.None);
            Assert(existing.Proposed && existing.Proposal?.Status == SkyrimNpcFinishCoreStatus.NoChanges &&
                   existing.Proposal.NewRecords.IsEmpty && existing.Proposal.ExistingRecordChanges.IsEmpty,
                "Already-satisfied Finish Core input did not return NoChanges.");

            foreach (string marker in new[]
                     {
                         "FOLLOWER-RANK-CONFLICT", "COMBAT-STYLE-CONFLICT",
                         "RELATIONSHIP-CONFLICT", "OUTFIT-CONFLICT", "PACKAGE-CONFLICT"
                     })
            {
                SkyrimNpcFinishCoreProposalResult conflict = await new SkyrimNpcFinishCoreService(
                    (_, _) => ValueTask.FromResult(source with { SemanticSurfaceValues = [marker] }),
                    projectRoot).AnalyzeAsync(
                    request,
                    ProposalHash("request"),
                    new WorkspacePath(Path.Combine(root, marker + ".json")),
                    CancellationToken.None);
                Assert(!conflict.Proposed && conflict.Diagnostics.Any(item =>
                           item.Code == "finish-core-source-conflict"),
                    $"Conflict marker {marker} was not refused before proposal creation.");
            }

            SkyrimNpcFinishCoreProposalResult duplicateMaster = await new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source with { MasterOrder = ["Skyrim.esm", "skyrim.esm"] }),
                projectRoot).AnalyzeAsync(
                request,
                ProposalHash("request"),
                new WorkspacePath(Path.Combine(root, "duplicate-master.json")),
                CancellationToken.None);
            Assert(!duplicateMaster.Proposed && duplicateMaster.Diagnostics.Any(item =>
                       item.Code == "finish-core-master-duplicate"),
                "Case-insensitive duplicate masters were not refused.");

            SkyrimNpcFinishCoreRequest lightRequest = request with
            {
                Source = request.Source with
                {
                    Plugin = new PluginName("LightSource.esl"),
                    PluginPath = new WorkspacePath(Path.Combine(root, "LightSource.esl"))
                }
            };
            SkyrimNpcFinishCoreProposalResult overflow = await new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(source with
                {
                    NextFormId = new FormId(0xFFF),
                    BaseNpc = new FormReference(new PluginName("LightSource.esl"), new FormId(0x800))
                }),
                projectRoot).AnalyzeAsync(
                lightRequest,
                ProposalHash("request"),
                new WorkspacePath(Path.Combine(root, "overflow.json")),
                CancellationToken.None);
            Assert(!overflow.Proposed && overflow.Diagnostics.Any(item =>
                       item.Code == "finish-core-id-overflow"),
                "Light-plugin FormID overflow was not refused.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static SkyrimNpcFinishCoreSourceReadResult CreateSource(
        SkyrimNpcFinishCoreRequest request) =>
        new(
            true,
            null,
            request.Source.PackageTreeSha256!.Value,
            request.Source.PluginSha256!.Value,
            new FormReference(request.Source.Plugin!.Value, request.Actor.FormId!.Value),
            request.Actor.EditorId!.Value,
            true,
            "None",
            BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
                .ToImmutableDictionary(signature => signature, _ => 0, StringComparer.Ordinal),
            BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
                .ToImmutableDictionary(signature => signature, _ => 0, StringComparer.Ordinal),
            ImmutableArray<Diagnostic>.Empty)
        {
            NextFormId = new FormId(0x805),
            Tes4Flags = 0,
            MasterOrder = ["Skyrim.esm"],
            VerifiedAdditionalMasters = [
                new SkyrimNpcFinishCoreVerifiedAdditionalMaster(
                    new PluginName("Update.esm"),
                    new WorkspacePath(Path.Combine(
                        request.Source.PackageRoot!.Value.Value,
                        "Update.esm")),
                    ProposalHash("Update.esm"),
                    1,
                    1,
                    [])
            ],
            OccupiedIds = ["NPC_ 0x00000800", "CLFM 0x00000801"],
            SemanticSurfaceValues = ImmutableArray<string>.Empty
        };

    private static SkyrimNpcFinishCoreRequest CreateProposalRequest(string root)
    {
        PluginName plugin = new("ProposalSource.esp");
        string pluginPath = Path.Combine(root, plugin.Value);
        return new SkyrimNpcFinishCoreRequest
        {
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = new WorkspacePath(root),
                PackageManifest = new WorkspacePath(Path.Combine(root, "manifest.json")),
                PackageManifestSha256 = ProposalHash("manifest"),
                PackageTreeSha256 = ProposalHash("tree"),
                PluginPath = new WorkspacePath(pluginPath),
                Plugin = plugin,
                PluginSha256 = ProposalHash("plugin")
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("ProposalActor"),
                FormId = new FormId(0x800)
            },
            Authorities = new SkyrimNpcFinishCoreAuthorities
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = [
                    new SkyrimNpcFinishCoreProviderAuthority
                    {
                        Plugin = new PluginName("Skyrim.esm"),
                        Path = new WorkspacePath(Path.Combine(root, "Skyrim.esm")),
                        Sha256 = ProposalHash("Skyrim.esm"),
                        ByteLength = 1
                    },
                    new SkyrimNpcFinishCoreProviderAuthority
                    {
                        Plugin = new PluginName("Update.esm"),
                        Path = new WorkspacePath(Path.Combine(root, "Update.esm")),
                        Sha256 = ProposalHash("Update.esm"),
                        ByteLength = 1
                    }
                ],
                AdditionalMasters = [
                    new SkyrimNpcFinishCoreAdditionalMasterBinding(
                        new PluginName("Update.esm"),
                        new WorkspacePath(Path.Combine(root, "Update.esm")),
                        ProposalHash("Update.esm"),
                        1,
                        1)
                ]
            },
            AiPolicy = new SkyrimNpcFinishCoreAiPolicy
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral
            },
            OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                ArmorItems = [
                    new FormReference(new PluginName("Skyrim.esm"), new FormId(0x100)),
                    new FormReference(new PluginName("Update.esm"), new FormId(0x200))
                ]
            },
            InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory,
                ExpectedSourceItems = ["Skyrim.esm|0x00000100"],
                DesiredItems = ["Update.esm|0x00000200"]
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                Root = new WorkspacePath(Path.Combine(root, "out")),
                Archive = new WorkspacePath(Path.Combine(root, "out.zip")),
                PluginFileName = plugin.Value
            }
        };
    }

    private static Sha256Hash ProposalHash(string value) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
}
