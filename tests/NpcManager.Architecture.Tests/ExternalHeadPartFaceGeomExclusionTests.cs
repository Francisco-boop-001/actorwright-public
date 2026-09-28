using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class ExternalHeadPartFaceGeomExclusionTests
{
    private static readonly string[] OrdinaryNames =
        ["FemaleHead", "MouthHumanF", "EyeFemaleLashes", "EyesFemale", "FemaleBrows"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "task10",
            "structured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = new WorkspacePath(Path.Combine(root, "carrier.nif"));
        var carrier = CreateCarrier(OrdinaryNames);
        byte[] cleanBytes = carrier.Bytes.ToArray();
        Require(Encoding.Latin1.GetString(cleanBytes).Contains("Hl", StringComparison.Ordinal),
            "The clean fixture must contain an incidental binary Hl pair.");
        Require(!SseFaceGeomCarrierCodec.Parse(cleanBytes).Strings.Contains("Hl", StringComparer.OrdinalIgnoreCase),
            "Hl must be absent from the structured string table.");
        await File.WriteAllBytesAsync(output.Value, cleanBytes, cancellationToken);
        var failures = new List<string>();
        ExternalHeadPartFaceGeomExclusionAttestation? clean = null;
        foreach (string metadata in new[] { "Hl", "Eye", "FemaleHe" })
        {
            var result = await VerifyAsync(output, carrier, WithEditorId(metadata), cancellationToken);
            if (!result.Verified || result.Attestation is null)
                failures.Add($"Clean carrier rejected incidental metadata '{metadata}': {Diagnostics(result)}");
            else clean = result.Attestation;
        }
        Require(File.ReadAllBytes(output.Value).AsSpan().SequenceEqual(cleanBytes),
            "Exclusion must not mutate the retained carrier.");
        Require(!(await VerifyAsync(output, carrier, WithEditorId("Female"), cancellationToken)).Verified,
            "The actual female texture-path segment must remain a structured match.");

        // Each negative's ordinary shape evidence matches its actual carrier, so
        // failure must come from the descriptor metadata rather than a stale shape set.
        var descriptor = WithEditorId("UnrelatedProvider");
        foreach (string token in new[] { "Hl", "HDT Skinned Mesh Physics Object",
                     descriptor.Physics.Shapes[0].XmlPath.Value,
                     descriptor.Physics.Shapes[0].XmlPath.Value.Replace('/', '\\'),
                     descriptor.Physics.Shapes[0].ShapeName })
        {
            var contaminated = CreateCarrier([.. OrdinaryNames.Take(4), token]);
            await File.WriteAllBytesAsync(output.Value, contaminated.Bytes.ToArray(), cancellationToken);
            var result = await VerifyAsync(output, contaminated,
                token == "Hl" ? WithEditorId("hL") : descriptor, cancellationToken);
            Require(!result.Verified && result.Attestation is null && result.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.FaceGeomContaminated),
                $"Genuine structured provider token '{token}' was accepted: {Diagnostics(result)}");
        }
        var pathCarrier = CreateCarrier([.. OrdinaryNames.Take(4), "SKSE\\Configs\\22a.xml"]);
        await File.WriteAllBytesAsync(output.Value, pathCarrier.Bytes.ToArray(), cancellationToken);
        Require(!(await VerifyAsync(output, pathCarrier, WithEditorId("22a.xml"), cancellationToken)).Verified,
            "A complete long path segment must remain contaminated.");
        var shortPathCarrier = CreateCarrier([.. OrdinaryNames.Take(4), "textures/Hl/ordinary.dds"]);
        await File.WriteAllBytesAsync(output.Value, shortPathCarrier.Bytes.ToArray(), cancellationToken);
        var shortPath = await VerifyAsync(output, shortPathCarrier, WithEditorId("Hl"), cancellationToken);
        if (!shortPath.Verified) failures.Add("Short metadata must not match an embedded path segment: " + Diagnostics(shortPath));
        await File.WriteAllBytesAsync(output.Value, cleanBytes, cancellationToken);
        var stale = await new ExternalHeadPartFaceGeomExclusionVerifier().VerifyAsync(new(output,
            Hash("stale"), cleanBytes.LongLength, descriptor, Included(carrier)), cancellationToken);
        Require(!stale.Verified && stale.Diagnostics.Any(item => item.Code == "external-facegeom-exclusion-hash-mismatch"),
            "Structured matching must retain exact carrier hash refusal.");

        // A clean descriptor establishes the independently hash-bound legacy attestation.
        var baseline = await VerifyAsync(output, carrier, descriptor, cancellationToken);
        Require(baseline.Verified && baseline.Attestation is not null, "Baseline verifier failed: " + Diagnostics(baseline));
        CheckTriEvidence(baseline.Attestation!, failures);
        if (clean is not null)
            await File.WriteAllBytesAsync(Path.Combine(root, "exclusion.json"),
                ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(clean), cancellationToken);
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Console.WriteLine("PASS structured external exclusion, exact metadata negatives, TRI identities and retained carrier. Evidence: " + root);
    }

    private static void CheckTriEvidence(ExternalHeadPartFaceGeomExclusionAttestation baseline, List<string> failures)
    {
        var first = new SkyrimRaceMenuFaceBakeTriEvidence(SkyrimFaceMorphTriRole.Extended,
            new AssetPath("meshes/ordinary/first.tri"), Hash("first-tri"), 16, 1,
            SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource);
        var second = first with { SourcePath = new AssetPath("meshes/ordinary/second.tri"), SourceSha256 = Hash("second-tri") };
        var draft = baseline with { IncludedOrdinaryShapes = baseline.IncludedOrdinaryShapes.SetItem(0,
            baseline.IncludedOrdinaryShapes[0] with { TriEvidence = [first] }) };
        draft = draft with { AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(draft) };
        byte[] legacy = ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(draft);
        Require(ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
            ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(legacy)).AsSpan().SequenceEqual(legacy),
            "Legacy omitted evidence IDs must retain exact canonical bytes.");
        Require(!Encoding.UTF8.GetString(legacy).Contains("evidenceId", StringComparison.Ordinal),
            "Legacy evidence IDs must remain absent.");
        try
        {
            var two = draft with { IncludedOrdinaryShapes = draft.IncludedOrdinaryShapes.SetItem(0,
                draft.IncludedOrdinaryShapes[0] with { TriEvidence = [first, second] }) };
            two = two with { AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(two) };
            var parsed = ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(two));
            Require(parsed.IncludedOrdinaryShapes[0].TriEvidence.SequenceEqual([first, second]),
                "Distinct sources sharing a TRI role lost ordered evidence.");
        }
        catch (InvalidDataException exception) { failures.Add("Same-role distinct TRI sources rejected: " + exception.Message); }
        try
        {
            _ = ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(draft with
            { IncludedOrdinaryShapes = draft.IncludedOrdinaryShapes.SetItem(0,
                draft.IncludedOrdinaryShapes[0] with { TriEvidence = [first, first] }) });
            throw new InvalidOperationException("An exact duplicate TRI role/path was accepted.");
        }
        catch (InvalidDataException) { }

        JsonObject node = JsonNode.Parse(legacy)!.AsObject();
        JsonObject row = node["includedOrdinaryShapes"]![0]!["triEvidence"]![0]!.AsObject();
        string expectedLegacyRow = "{\"role\":\"extended\",\"sourcePath\":\"meshes/ordinary/first.tri\",\"sourceSha256\":\"" +
            first.SourceSha256.Value + "\",\"declaredVertexCount\":16,\"morphCount\":1,\"disposition\":\"eligible-morph-source\"}";
        Require(row.ToJsonString(JsonOptions) == expectedLegacyRow,
            "Legacy TRI row bytes changed when evidenceId was omitted.");
        row["evidenceId"] = DomainHash("Actorwright.ExternalHeadPartTriEvidence.v1\0", row.ToJsonString(JsonOptions)).Value;
        JsonObject selfless = node.DeepClone().AsObject();
        selfless.Remove("attestationSha256");
        node["attestationSha256"] = DomainHash("Actorwright.ExternalHeadPartFaceGeomExclusion.v1\0", selfless.ToJsonString(JsonOptions)).Value;
        byte[] identified = Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));
        try
        {
            var parsed = ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(identified);
            Require(ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(parsed).AsSpan().SequenceEqual(identified),
                "Explicit stable TRI evidence identity was not preserved.");
            var identifiedRow = parsed.IncludedOrdinaryShapes[0].TriEvidence[0];
            Require(identifiedRow.EvidenceId == ExternalHeadPartDependencyDescriptorCodec.ComputeTriEvidenceId(first),
                "Producer identity must match the canonical six-field binding.");
            foreach (var changed in new[] { first with { Role = SkyrimFaceMorphTriRole.Mesh }, second,
                         first with { SourceSha256 = Hash("changed-tri") }, first with { DeclaredVertexCount = 17 },
                         first with { MorphCount = 2 }, first with { Disposition = SkyrimRaceMenuFaceBakeTriDisposition.EmptyMorphNoOp } })
            {
                Require(ExternalHeadPartDependencyDescriptorCodec.ComputeTriEvidenceId(changed) != identifiedRow.EvidenceId,
                    "A TRI binding mutation retained the same evidence identity.");
                try
                {
                    _ = ExternalHeadPartDependencyDescriptorCodec.ComputeAttestationHash(parsed with
                    { IncludedOrdinaryShapes = parsed.IncludedOrdinaryShapes.SetItem(0,
                        parsed.IncludedOrdinaryShapes[0] with { TriEvidence = [changed with { EvidenceId = identifiedRow.EvidenceId }] }) });
                    throw new InvalidOperationException("A stale TRI evidence identity survived a binding mutation.");
                }
                catch (InvalidDataException) { }
            }
        }
        catch (InvalidDataException exception) { failures.Add("Stable TRI evidence identity rejected: " + exception.Message); }
    }

    private static SseFaceGeomCarrierAssemblyArtifact CreateCarrier(string[] names)
    {
        byte[] bytes = File.ReadAllBytes("tests/fixtures/sse-packed-normals/dynamic-16.nif");
        var sourcePath = new AssetPath("meshes/ordinary/source.nif");
        Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(bytes)));
        var source = new SseSelectedHeadpartNifGeometryReader().Read(new(sourcePath, hash, [.. bytes]));
        Require(source.Accepted && source.Document is not null, "Synthetic selected model must be admitted.");
        var positions = source.Document!.Shapes[0].RestPositions;
        positions = positions.SetItem(0, new Vector3(BitConverter.Int32BitsToSingle(0x3f006c48), positions[0].Y, positions[0].Z));
        var parts = names.Select((name, index) => new SseFaceGeomCarrierAssemblyPart(
            new FormReference(new PluginName("Skyrim.esm"), new FormId((uint)(0x800 + index))),
            new AssetPath($"meshes/ordinary/source-{index}.nif"), hash, [.. bytes], name, index == 0, positions)).ToImmutableArray();
        var result = new SseFaceGeomCarrierAssembler().Assemble(new(parts,
            new AssetPath("textures/actors/character/facegendata/facetint/Ordinary.esp/00000800.dds")));
        Require(result.Assembled && result.Verified && result.Artifact is not null,
            "Five-shape synthetic assembly failed: " + string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        return result.Artifact!;
    }

    private static ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> Included(SseFaceGeomCarrierAssemblyArtifact carrier) =>
        carrier.Shapes.Select((shape, index) => new SkyrimNativeFaceGeomShapeEvidence(shape.HeadPart,
            index == 0 ? NpcHeadPartType.Face : NpcHeadPartType.Misc, shape.SourcePath, shape.SourceSha256,
            shape.SourceShapeName, shape.OutputShapeName, shape.VertexCount, shape.TopologySha256,
            shape.SourcePositionSha256, shape.OutputPositionSha256, [], shape.UsesFaceTint)).ToImmutableArray();

    private static ExternalHeadPartDependencyDescriptor WithEditorId(string value)
    {
        var descriptor = ExternalHeadPartDependencyContractTests.CreateDescriptor();
        descriptor = descriptor with { Members = descriptor.Members.SetItem(0, descriptor.Members[0] with { EditorId = value }) };
        return descriptor with { DescriptorId = ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(descriptor) };
    }
    private static ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult> VerifyAsync(WorkspacePath output,
        SseFaceGeomCarrierAssemblyArtifact carrier, ExternalHeadPartDependencyDescriptor descriptor, CancellationToken token) =>
        new ExternalHeadPartFaceGeomExclusionVerifier().VerifyAsync(new(output, carrier.Sha256, carrier.ByteLength,
            descriptor, Included(carrier)), token);
    private static Sha256Hash Hash(string value) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
    private static Sha256Hash DomainHash(string domain, string value) => Hash(domain + value);
    private static string Diagnostics(ExternalHeadPartFaceGeomExclusionVerificationResult result) =>
        string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Message));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
