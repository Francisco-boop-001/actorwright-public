using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferencePresetSessionTests
{
    private static readonly JsonSerializerOptions PrettyDocumentOptions = new() { WriteIndented = true };
    private static readonly string LabRoot = Path.Combine(Environment.CurrentDirectory,
        "artifacts", "reference-session-tests", Guid.NewGuid().ToString("N"));

    public static async Task TestCanonicalSessionWriteRead()
    {
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var service = new ReferencePresetSessionService(
            policy, new WorkspacePath(LabRoot));
        ReferencePresetIntake intake = Intake();
        var document = new ReferencePresetSessionDocument(
            ReferencePresetSessionDocumentKind.Intake,
            Intake: intake);
        string parent = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests");
        string first = Path.Combine(
            parent,
            $"scratch-session-a-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        string second = Path.Combine(
            parent,
            $"scratch-session-b-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        string resourcePath = Path.Combine(
            parent,
            $"scratch-session-resource-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        string proposalPath = Path.Combine(
            parent,
            $"scratch-session-proposal-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        string oversizedPath = Path.Combine(
            parent,
            $"scratch-session-oversized-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        string protectedDirectory = Path.Combine(parent, "protected");
        string protectedPath = Path.Combine(
            protectedDirectory,
            $"scratch-session-protected-{Guid.NewGuid():N}.json");
        string outsidePath = Path.Combine(
            Path.GetDirectoryName(LabRoot)!,
            $"scratch-session-outside-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(parent);
        try
        {
            var boundedService = new ReferencePresetSessionService(
                policy,
                new WorkspacePath(LabRoot),
                1_024);
            ReferencePresetSessionWriteResult oversized =
                await boundedService.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(oversizedPath),
                        document with
                        {
                            Intake = intake with
                            {
                                Description =
                                    new string('x', 4_096)
                            }
                        }),
                    CancellationToken.None);
            Require(!oversized.Written &&
                    oversized.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-size-limit") &&
                    !File.Exists(oversizedPath) &&
                    Directory.GetFiles(
                        parent,
                        Path.GetFileName(oversizedPath) +
                        ".tmp-*").Length == 0,
                $"bounded session serialization did not refuse before retaining an oversized document: {Details(oversized.Diagnostics)}");

            ReferencePresetSessionWriteResult a =
                await service.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(first), document),
                    CancellationToken.None);
            ReferencePresetSessionWriteResult b =
                await service.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(second), document),
                    CancellationToken.None);
            Require(a.Written && b.Written,
                $"session writes refused: a={Details(a.Diagnostics)}; b={Details(b.Diagnostics)}");
            byte[] aBytes = await File.ReadAllBytesAsync(first);
            byte[] bBytes = await File.ReadAllBytesAsync(second);
            Require(a.Written &&
                    b.Written &&
                    a.ContentSha256 is not null &&
                    a.ContentSha256 == b.ContentSha256 &&
                    aBytes.SequenceEqual(bBytes) &&
                    aBytes.Length > 0 &&
                    aBytes[0] == (byte)'{',
                "canonical session destinations differed or used a BOM");
            Sha256Hash contentSha256 =
                a.ContentSha256 ??
                throw new InvalidOperationException(
                    "written session hash was absent");

            ReferencePresetSessionReadResult reopened =
                await service.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(first),
                        contentSha256,
                        ReferencePresetSessionDocumentKind.Intake),
                    CancellationToken.None);
            Require(reopened.Document?.Intake is
                        { } reopenedIntake &&
                    reopenedIntake.ProjectId == intake.ProjectId &&
                    reopenedIntake.TargetName ==
                    intake.TargetName &&
                    reopenedIntake.Images.Length ==
                    intake.Images.Length &&
                    reopenedIntake.Images[0] ==
                    intake.Images[0] &&
                    reopened.ContentSha256 == a.ContentSha256 &&
                    !HasErrors(reopened.Diagnostics),
                $"canonical session did not reopen: {Codes(reopened.Diagnostics)}");

            var nestedProtectedPolicy = new KOnlyWorkspacePolicy(
                new WorkspacePath(LabRoot),
                new WorkspacePath(protectedDirectory));
            var nestedProtectedService = new ReferencePresetSessionService(
                nestedProtectedPolicy,
                new WorkspacePath(LabRoot));
            ReferencePresetSessionReadResult adjacentRead =
                await nestedProtectedService.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(first),
                        contentSha256,
                        ReferencePresetSessionDocumentKind.Intake),
                    CancellationToken.None);
            Require(adjacentRead.Document?.Intake?.ProjectId ==
                        intake.ProjectId &&
                    !HasErrors(adjacentRead.Diagnostics),
                $"a session adjacent to a protected subtree was refused: {Codes(adjacentRead.Diagnostics)}");

            Directory.CreateDirectory(protectedDirectory);
            File.Copy(first, protectedPath);
            ReferencePresetSessionReadResult protectedRead =
                await nestedProtectedService.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(protectedPath),
                        contentSha256,
                        ReferencePresetSessionDocumentKind.Intake),
                    CancellationToken.None);
            Require(protectedRead.Document is null &&
                    protectedRead.Diagnostics.Any(item =>
                        item.Code ==
                        ProtocolV2DiagnosticCodes.ProtectedRootRefused),
                "the session reader admitted a file beneath the protected subtree");

            File.Copy(first, outsidePath);
            ReferencePresetSessionReadResult outsideRead =
                await nestedProtectedService.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(outsidePath),
                        contentSha256,
                        ReferencePresetSessionDocumentKind.Intake),
                    CancellationToken.None);
            Require(outsideRead.Document is null &&
                    outsideRead.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-input-outside-lab"),
                "the session reader admitted a file outside the lab root");

            JsonObject prettyNode = JsonNode.Parse(aBytes)!.AsObject();
            prettyNode["intake"]!["images"]![0]!["sourceSha256"] = intake.Images[0].SourceSha256.Value.ToUpperInvariant();
            byte[] prettyBytes = JsonSerializer.SerializeToUtf8Bytes(prettyNode,
                PrettyDocumentOptions);
            File.WriteAllBytes(second, prettyBytes);
            Sha256Hash rawPrettyHash = new(Convert.ToHexString(SHA256.HashData(prettyBytes)));
            foreach (Sha256Hash suppliedHash in new[] { rawPrettyHash, contentSha256 })
            {
                var prettyRead = await service.ReadAsync(new ReferencePresetSessionReadRequest(
                    new WorkspacePath(second), suppliedHash, ReferencePresetSessionDocumentKind.Intake), CancellationToken.None);
                Require(prettyRead.Document?.Intake?.TargetName == intake.TargetName &&
                        prettyRead.ContentSha256 == contentSha256 && !HasErrors(prettyRead.Diagnostics),
                    "Pretty session must accept its raw or canonical hash and return canonical identity: " + Details(prettyRead.Diagnostics));
            }
            prettyNode["intake"]!["targetName"] = "Semantically changed";
            File.WriteAllBytes(second, JsonSerializer.SerializeToUtf8Bytes(prettyNode));
            var changedRead = await service.ReadAsync(new ReferencePresetSessionReadRequest(
                new WorkspacePath(second), contentSha256, ReferencePresetSessionDocumentKind.Intake), CancellationToken.None);
            Require(changedRead.Document is null && changedRead.Diagnostics.Any(item =>
                    item.Code == "reference-session-hash-mismatch" && item.Message.Contains(contentSha256.Value, StringComparison.Ordinal)),
                "Semantic edits must retain hash refusal and expected hash evidence.");

            JsonObject unknownNode = JsonNode.Parse(aBytes)!.AsObject();
            unknownNode["unexpectedAuthority"] = true;
            File.WriteAllBytes(second, JsonSerializer.SerializeToUtf8Bytes(unknownNode));
            var unknownRead = await service.ReadAsync(new ReferencePresetSessionReadRequest(
                new WorkspacePath(second), contentSha256, ReferencePresetSessionDocumentKind.Intake), CancellationToken.None);
            Require(unknownRead.Document is null && HasErrors(unknownRead.Diagnostics),
                "Canonical binding must not discard unknown document members.");

            File.WriteAllText(second, System.Text.Encoding.UTF8.GetString(aBytes)
                .Replace("\"targetName\":", "\"targetName\":\"Shadowed authority\",\"targetName\":", StringComparison.Ordinal));
            var duplicateRead = await service.ReadAsync(new ReferencePresetSessionReadRequest(
                new WorkspacePath(second), contentSha256, ReferencePresetSessionDocumentKind.Intake), CancellationToken.None);
            Require(duplicateRead.Document is null && HasErrors(duplicateRead.Diagnostics),
                "Canonical binding must refuse duplicate members instead of discarding the shadowed value.");

            ReferencePresetSessionReadResult stale =
                await service.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(first),
                        Hash('9'),
                        ReferencePresetSessionDocumentKind.Intake),
                    CancellationToken.None);
            Require(stale.Document is null &&
                    stale.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-hash-mismatch"),
                "session reader accepted a stale expected hash");
            ReferencePresetSessionReadResult wrongKind =
                await service.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(first),
                        contentSha256,
                        ReferencePresetSessionDocumentKind.ReviewedDesign),
                    CancellationToken.None);
            Require(wrongKind.Document is null &&
                    wrongKind.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-kind-mismatch"),
                "session reader accepted the wrong authority kind");

            ReferencePresetSessionWriteResult collision =
                await service.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(first), document),
                    CancellationToken.None);
            Require(!collision.Written &&
                    collision.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-output-exists"),
                "session writer overwrote an existing document");

            (_, ReferencePresetResourceSnapshot snapshot,
                ReferencePresetAuthoringProposal proposal, _) =
                ReferencePresetWriterTests.Fixture();
            ImmutableArray<byte> canonicalRgba =
            [
                10, 20, 30, 255,
                40, 50, 60, 255,
                70, 80, 90, 255,
                100, 110, 120, 255
            ];
            var textureAuthority = new SkyrimAssetAuthority(
                "session-texture",
                AssetProviderKind.Loose,
                new WorkspacePath(Path.Combine(
                    parent, "session-texture.dds")),
                Hash('6'),
                new AssetPath(
                    "textures/session/session-texture.dds"),
                16,
                Hash('7'));
            var texture = new ReferenceRenderTexture(
                textureAuthority,
                4,
                1,
                canonicalRgba,
                new Sha256Hash(Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        canonicalRgba.AsSpan()))));
            snapshot = snapshot with
            {
                RenderTextures = [texture]
            };
            ReferencePresetSessionWriteResult resourceWrite =
                await service.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(resourcePath),
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind
                                .ResourceSnapshot,
                            ResourceSnapshot: snapshot)),
                    CancellationToken.None);
            ReferencePresetSessionWriteResult proposalWrite =
                await service.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        new WorkspacePath(proposalPath),
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind
                                .AuthoringProposal,
                            AuthoringProposal: proposal)),
                    CancellationToken.None);
            Require(resourceWrite.ContentSha256 is not null &&
                    proposalWrite.ContentSha256 is not null,
                $"complex session writes refused: resource={Details(resourceWrite.Diagnostics)}; proposal={Details(proposalWrite.Diagnostics)}");
            Sha256Hash resourceHash =
                resourceWrite.ContentSha256 ??
                throw new InvalidOperationException(
                    "resource hash was absent");
            Sha256Hash proposalHash =
                proposalWrite.ContentSha256 ??
                throw new InvalidOperationException(
                    "proposal hash was absent");
            ReferencePresetSessionReadResult resourceRead =
                await service.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(resourcePath),
                        resourceHash,
                        ReferencePresetSessionDocumentKind
                            .ResourceSnapshot),
                    CancellationToken.None);
            ReferencePresetSessionReadResult proposalRead =
                await service.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        new WorkspacePath(proposalPath),
                        proposalHash,
                        ReferencePresetSessionDocumentKind
                            .AuthoringProposal),
                    CancellationToken.None);
            ReferenceRenderTexture? reopenedTexture =
                resourceRead.Document?.ResourceSnapshot?
                    .RenderTextures.SingleOrDefault();
            ImmutableArray<byte> reopenedRgba = [];
            bool textureHydrated =
                reopenedTexture is not null &&
                reopenedTexture.TryGetCanonicalRgba(
                    out reopenedRgba,
                    out _);
            Require(resourceRead.Document?.ResourceSnapshot is
                        { } reopenedSnapshot &&
                    reopenedSnapshot.ProjectId ==
                    snapshot.ProjectId &&
                    reopenedSnapshot.MorphChannels.Length ==
                    snapshot.MorphChannels.Length &&
                    reopenedSnapshot.RenderTextures.Length == 1 &&
                    reopenedSnapshot.RenderTextures[0]
                        .CanonicalRgba.IsDefaultOrEmpty &&
                    textureHydrated &&
                    reopenedRgba.SequenceEqual(canonicalRgba) &&
                    proposalRead.Document?.AuthoringProposal is
                        { } reopenedProposal &&
                    reopenedProposal.SolverResult.NativeMorphs[
                        "NAM9[0]"] == 0.25,
                $"complex authority documents did not round-trip: resource={Details(resourceRead.Diagnostics)}; proposal={Details(proposalRead.Diagnostics)}");
        }
        finally
        {
            Delete(first);
            Delete(second);
            Delete(resourcePath);
            Delete(proposalPath);
            Delete(oversizedPath);
            Delete(protectedPath);
            Delete(outsidePath);
            if (Directory.Exists(protectedDirectory))
                Directory.Delete(protectedDirectory);
        }
    }

    private static ReferencePresetIntake Intake()
    {
        var race = new FormReference(
            new PluginName("Skyrim.esm"),
            new FormId(0x013746));
        var source = new WorkspacePath(Path.Combine(
            LabRoot, "Resources", "Screenshot",
            "Sofia Fergar.png"));
        var baseline = new WorkspacePath(Path.Combine(
            LabRoot, "projects", "Emi2FreshBuild",
            "01-source-copies", "fresh-export-drop",
            "emi2-neutral.jslot"));
        var target = new RaceMenuPresetTarget(
            "session-test",
            race,
            NpcSex.Female,
            new WorkspacePath(Path.Combine(
                LabRoot, "projects",
                "NpcManagerReimplementation")),
            []);
        return new ReferencePresetIntake(
            1,
            "session-test",
            "Session Test",
            race,
            NpcSex.Female,
            50,
            "high-poly-head",
            baseline,
            Hash('1'),
            "wide cheeks",
            [
                new ReferenceImageAuthority(
                    "front",
                    source,
                    Hash('b'),
                    123,
                    ReferenceImageViewRole.Front)
            ],
            target);
    }

    private static Sha256Hash Hash(char value) =>
        ReferenceMeshAnchorBindingTests.Hash(value);

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static string Codes(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));

    private static string Details(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            $"{item.Code}:{item.Message}"));

    private static void Require(
        bool condition,
        string message) =>
        ReferenceMeshAnchorBindingTests.Require(
            condition, message);

    private static void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
