using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimNpcFinishCoreSandbox()
    {
        byte[] malformedGroup = new byte[24];
        "GRUP"u8.CopyTo(malformedGroup);
        foreach (uint malformedSize in new uint[] { 0, 10, 25 })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                malformedGroup.AsSpan(4, 4), malformedSize);
            bool malformedRefused = false;
            try
            {
                _ = FindFollowerFinishRawRecordInfo(
                    malformedGroup,
                    0,
                    malformedGroup.Length,
                    string.Empty,
                    "PACK",
                    0x1B217);
            }
            catch (InvalidDataException exception) when (
                exception.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase))
            {
                malformedRefused = true;
            }
            Assert(malformedRefused,
                $"The raw Skyrim master reader accepted malformed GRUP size {malformedSize}.");
        }

        BethesdaSkyrimNpcFinishCoreSandboxCandidate valid = ValidCandidate();
        SkyrimNpcFinishCoreSandboxSnapshot snapshot =
            BethesdaSkyrimNpcFinishCoreSandboxGate.InspectCandidateForTests(valid);

        Assert(snapshot.Template ==
                   new FormReference(
                       new PluginName("Skyrim.esm"),
                       new FormId(0x1B217)),
            "Finish Core sandbox template FormReference drifted.");
        Assert(snapshot.TemplateEditorId == "DefaultSandboxEditorLocation512" &&
               snapshot.LocationKind == "NearEditorLocation" &&
               snapshot.LocationData == 0 &&
               snapshot.Radius == 512 &&
               snapshot.PlacedTarget is null &&
               snapshot.OwnerQuest is null &&
               !snapshot.HasVirtualMachineAdapter &&
               snapshot.ConditionCount == 0 &&
               snapshot.LocationCount == 1 &&
               snapshot.Procedure == "Sandbox" &&
               snapshot.ContinuousSchedule,
            "The admitted sandbox snapshot did not preserve its self-contained typed shape.");

        AssertRefusal(valid with { LocationKind = "NearReference" },
            "npc-finish-core-sandbox-not-self-contained", "NearReference");
        AssertRefusal(valid with { LocationData = 1 },
            "npc-finish-core-sandbox-not-self-contained", "nonzero fallback payload");
        AssertRefusal(valid with
        {
            PlacedTarget = new FormReference(
                new PluginName("SandboxFixture.esp"),
                new FormId(0x800))
        }, "npc-finish-core-sandbox-not-self-contained", "placed link");
        AssertRefusal(valid with { Radius = 256 },
            "npc-finish-core-sandbox-not-self-contained", "radius");
        AssertRefusal(valid with { LocationCount = 2 },
            "npc-finish-core-sandbox-not-self-contained", "second location");
        AssertRefusal(valid with { OwnerQuest = "Skyrim.esm|0x00012345" },
            "npc-finish-core-sandbox-not-self-contained", "owner quest");
        AssertRefusal(valid with { HasVirtualMachineAdapter = true },
            "npc-finish-core-sandbox-not-self-contained", "VMAD");
        AssertRefusal(valid with { ConditionCount = 1 },
            "npc-finish-core-sandbox-not-self-contained", "condition");
        AssertRefusal(valid with { ContinuousSchedule = false },
            "npc-finish-core-sandbox-not-self-contained", "schedule");
        AssertRefusal(valid with { RawRecordDigest = BadDigest },
            "npc-finish-core-sandbox-record-digest", "raw record digest");

        return Task.CompletedTask;
    }

    private static Task TestSkyrimNpcFinishCoreSandboxAuthentic()
    {
        byte[] canonicalRecord =
            SkyrimFinishMasterFixture.ReadCanonicalPackRecord();
        string workspaceRoot = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "test-work",
            "finish-sandbox-authentic",
            Guid.NewGuid().ToString("N"));
        string portableInputRoot = Path.Combine(
            Path.GetTempPath(),
            "actorwright-finish-master-" + Guid.NewGuid().ToString("N"));
        string? previousInput = Environment.GetEnvironmentVariable(
            SkyrimFinishMasterFixture.InputEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(portableInputRoot);
            string portableInput = Path.Combine(
                portableInputRoot, "Skyrim.esm");
            SkyrimFollowerFinishCoreFixture
                .WriteCanonicalFollowerFinishTemplateMaster(
                    portableInput,
                    canonicalRecord);
            Environment.SetEnvironmentVariable(
                SkyrimFinishMasterFixture.InputEnvironmentVariable,
                portableInput);
            Assert(SkyrimFinishMasterFixture.ReadCanonicalPackRecord()
                       .AsSpan().SequenceEqual(canonicalRecord),
                "A valid ordinary local Skyrim.esm at a different path did not preserve the approved PACK bytes.");

            Environment.SetEnvironmentVariable(
                SkyrimFinishMasterFixture.InputEnvironmentVariable,
                previousInput);
            string admittedMaster = Path.Combine(
                workspaceRoot, "Skyrim.esm");
            SkyrimFollowerFinishCoreFixture
                .WriteCanonicalFollowerFinishTemplateMaster(
                    admittedMaster,
                    canonicalRecord);

            var workspace = new WorkspacePath(workspaceRoot);
            var labRoot = new WorkspacePath(
                Path.GetPathRoot(workspaceRoot)!);
            var protectedRoot = new WorkspacePath(Path.Combine(
                Path.GetDirectoryName(workspaceRoot)!,
                "protected-" + Guid.NewGuid().ToString("N")));
            var admission =
                new BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission(
                    new KOnlyWorkspacePolicy(labRoot, protectedRoot),
                    workspace);
            BethesdaSkyrimFollowerFinishSandboxAuthority authority =
                admission.Admit(
                    new WorkspacePath(admittedMaster),
                    SkyrimFollowerFinishCoreFixture
                        .HashFollowerFinishCoreFile(admittedMaster));

            SkyrimNpcFinishCoreSandboxSnapshot snapshot =
                BethesdaSkyrimNpcFinishCoreSandboxGate.Inspect(authority);
            Assert(snapshot.TemplateEditorId == "DefaultSandboxEditorLocation512" &&
                   snapshot.LocationKind == "NearEditorLocation" &&
                   snapshot.LocationData == 0 &&
                   snapshot.Radius == 512 &&
                   snapshot.PlacedTarget is null,
                "The copied canonical PACK sandbox does not satisfy the exact self-contained predicate.");
            return Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SkyrimFinishMasterFixture.InputEnvironmentVariable,
                previousInput);
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
            if (Directory.Exists(portableInputRoot))
                Directory.Delete(portableInputRoot, recursive: true);
        }
    }
    private static BethesdaSkyrimNpcFinishCoreSandboxCandidate ValidCandidate() =>
        new(
            new FormReference(
                new PluginName("Skyrim.esm"),
                new FormId(0x1B217)),
            "DefaultSandboxEditorLocation512",
            "NearEditorLocation",
            0,
            512,
            null,
            null,
            false,
            0,
            1,
            "Sandbox",
            true,
            new Sha256Hash(
                "fba3cca0eff98528da3985962ff9058ee" +
                "7662ea944c250f3ffb90a0490d52685"));

    private static readonly Sha256Hash BadDigest =
        new(new string('a', 64));

    private static void AssertRefusal(
        BethesdaSkyrimNpcFinishCoreSandboxCandidate candidate,
        string code,
        string caseName)
    {
        try
        {
            BethesdaSkyrimNpcFinishCoreSandboxGate.InspectCandidateForTests(candidate);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(code, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Hostile Finish Core sandbox case was accepted: {caseName}.");
    }
}
