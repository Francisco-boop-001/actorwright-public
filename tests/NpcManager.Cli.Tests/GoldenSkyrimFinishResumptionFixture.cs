using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static async Task<WorkspacePath> CreateCurrentFinishResumptionFixtureAsync(
        WorkspacePath root,
        TranscriptA transcript)
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        WorkspacePath package = Child(transcript.Root, "planned-npc-output");
        WorkspacePath manifest = Child(package, "npcmanager-package.json");
        WorkspacePath pluginPath = Child(
            package, "Data", "PackagedNpc.esp");
        var pluginKey = ModKey.FromNameAndExtension("PackagedNpc.esp");
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(pluginKey, new FilePath(pluginPath.Value)),
            SkyrimRelease.SkyrimSE);
        INpcGetter npc = plugin.Npcs.Single();

        WorkspacePath copiedMaster = Child(
            root, "finish-evidence", "Skyrim.esm");
        SkyrimFinishMasterFixture.AppendCanonicalPackGroupToMaster(
            Child(root, "Data", "Skyrim.esm").Value,
            copiedMaster.Value,
            root.Value);
        var request = new SkyrimNpcFinishCoreRequest
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            Source = new()
            {
                PackageRoot = package,
                PackageManifest = manifest,
                PackageManifestSha256 = new Sha256Hash(HashFile(manifest)),
                PackageTreeSha256 =
                    SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(package),
                PluginPath = pluginPath,
                Plugin = new PluginName(pluginKey.ToString()),
                PluginSha256 = new Sha256Hash(HashFile(pluginPath))
            },
            Actor = new()
            {
                EditorId = new EditorId(npc.EditorID!),
                FormId = new FormId(npc.FormKey.ID)
            },
            Authorities = new()
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers =
                [
                    new()
                    {
                        Plugin = new PluginName(pluginKey.ToString()),
                        Path = pluginPath,
                        Sha256 = new Sha256Hash(HashFile(pluginPath)),
                        ByteLength = new FileInfo(pluginPath.Value).Length
                    }
                ]
            },
            AiPolicy = new()
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral
            },
            CombatPolicy = new()
            {
                SeedLocalStyle = true,
                Profile = SkyrimNpcFinishCoreCombatProfile.RangedFirst
            },
            OutfitPolicy = new()
            {
                Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                ArmorItems =
                [
                    new FormReference(
                        new PluginName("Skyrim.esm"), new FormId(0x900))
                ]
            },
            SandboxAuthority = new()
            {
                CopiedMaster = copiedMaster,
                CopiedMasterSha256 = new Sha256Hash(HashFile(copiedMaster)),
                Template = new FormReference(
                    new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new()
            {
                Root = Child(root, "current-finish-output"),
                Archive = Child(root, "current-finish.zip"),
                PluginFileName = pluginKey.ToString()
            }
        };

        WorkspacePath requestPath = Child(
            root, "finish-evidence", "current-request.json");
        WorkspacePath proposalPath = Child(
            root, "finish-evidence", "current-proposal.json");
        File.WriteAllBytes(
            requestPath.Value,
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root));
        ProtocolInvocation analyzed = await RunCliAsync(
            root,
            "npc", "finish", "analyze", "--json",
            "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath),
            "--proposal", proposalPath.Value);
        Require(analyzed.ExitCode == 0 &&
                analyzed.Root.GetProperty("schema").GetString() ==
                    SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier &&
                analyzed.Root.GetProperty("status").GetString() ==
                    "ReadyForReviewedWrite",
            "Transcript A's actual package was not ready for Finish: " +
            analyzed.Root.GetRawText() + " stderr=" + analyzed.StdErr);

        ProtocolInvocation applied = await RunCliAsync(
            root,
            "npc", "finish", "apply", "--json",
            "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath),
            "--proposal", proposalPath.Value,
            "--proposal-sha256",
            analyzed.Root.GetProperty("proposalSha256").GetString() ?? "");
        Require(applied.ExitCode == 0 &&
                applied.Root.GetProperty("applied").GetBoolean(),
            "Transcript A's actual package was not finished: " +
            applied.Root.GetRawText() + " stderr=" + applied.StdErr);
        Require(SkyrimNpcFinishCoreSourcePackageReader
                    .ComputePackageTreeSha256(package) ==
                request.Source.PackageTreeSha256,
            "Finish resumption modified Transcript A's generated package.");

        return Child(
            root, "current-finish-output", "NPCManager", "Evidence",
            "finish-core-manifest.json");
    }
}
