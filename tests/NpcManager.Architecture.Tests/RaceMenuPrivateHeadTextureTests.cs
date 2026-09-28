using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class RaceMenuStandaloneSchema8Tests
{
    public static async ValueTask RunPrivateTexturePolicyAsync(CancellationToken cancellationToken)
    {
        string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "task36", "writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var descriptor = CreateDescriptor();
        var attestation = CreateAttestation(descriptor);
        string legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
        // Reuse the existing no-override writer/strict-reader and byte-repeatability controls.
        await AssertWriterAsync(legacy, descriptor, attestation, "unused", cancellationToken);
        WriterFixture fixture = CreateWriterFixture(root, descriptor, attestation);
        var source = new WorkspacePath(Path.Combine(root, "Textures", "Private", "Head.dds"));
        Directory.CreateDirectory(Path.GetDirectoryName(source.Value)!);
        await File.WriteAllBytesAsync(source.Value, "private DDS authority"u8.ToArray(), cancellationToken);
        Sha256Hash hash = HashBytes(File.ReadAllBytes(source.Value));
        var path = new AssetPath("Textures/Private/Head.dds");
        var paths = new SkyrimPrivateHeadTexturePaths(path, path, path, path, path);
        var copy = new BlankNpcTransitivePackageAsset(source, hash, path);
        var admittedCopy = copy with { Destination = new AssetPath("textures/private/head.dds") };
        var external = new RaceMenuNpcExternalTextureAuthority(path, "loose", source, hash, hash);
        var requested = fixture.Request with
        {
            PrivateHeadTextures = paths,
            RetainedExternalTextures = [external],
            Destination = new WorkspacePath(Path.Combine(root, "private-external8.json"))
        };
        var written = await fixture.Writer.WriteAsync(requested, cancellationToken);
        Require(written.Written, "Schema8 refused private paths with truthful external authority: " + FormatDiagnostics(written.Diagnostics));
        byte[] bytes = File.ReadAllBytes(requested.Destination.Value);
        var read = await ReadManifestAsync(requested.Destination, bytes, cancellationToken, new BethesdaAssetIndexer());
        Require(read.Accepted && read.Assets is { } externalAssets && externalAssets.PrivateHeadTextures == paths && externalAssets.PackageAssets.IsEmpty &&
                externalAssets.ExternalTextureAuthorities.Single() == external,
            "Schema8 private external paths did not survive real writer/strict-reader admission: " + FormatDiagnostics(read.Diagnostics));
        Require(fixture.Request.RecordDraft.HeadTextureAuthority.Paths.Diffuse.Value == "Textures/head.dds",
            "Private output paths changed truthful source TXST authority.");

        var copied = requested with
        {
            ExternalHeadPartDependencies = [], ExternalHeadPartExclusionAttestations = [],
            RetainedExternalTextures = [],
            RetainedPackageAssets = [copy, copy with { Destination = new AssetPath("Textures/Unused.dds") }],
            Destination = new WorkspacePath(Path.Combine(root, "private-copy6.json"))
        };
        written = await fixture.Writer.WriteAsync(copied, cancellationToken);
        Require(written.Written, "Schema6 refused private copied authority: " + FormatDiagnostics(written.Diagnostics));
        bytes = File.ReadAllBytes(copied.Destination.Value);
        read = await ReadManifestAsync(copied.Destination, bytes, cancellationToken, new BethesdaAssetIndexer());
        Require(read.Accepted && read.Assets is { } copiedAssets && copiedAssets.PrivateHeadTextures == paths && copiedAssets.PackageAssets.SequenceEqual([admittedCopy]) &&
                copiedAssets.ExternalTextureAuthorities.IsEmpty,
            "Private copy serialization lost required rows or retained unrelated rows: " + FormatDiagnostics(read.Diagnostics));

        BodySlideFixture body = CreateBodySlideFixture(root);
        JsonNode bodyInput = JsonNode.Parse(bytes)!;
        bodyInput["schemaVersion"] = 7;
        bodyInput["bodySlidePresetAuthority"] = new JsonObject { ["manifestPath"] = Relative(body.PresetManifest), ["manifestSha256"] = body.PresetManifestHash.Value };
        bodyInput["bodyMeshAuthority"] = new JsonObject { ["manifestPath"] = Relative(body.MeshManifest), ["manifestSha256"] = body.MeshManifestHash.Value };
        bodyInput["externalCharGenExportAuthority"] = null;
        var bodyPath = new WorkspacePath(Path.Combine(root, "admitted-body7.json"));
        bytes = JsonSerializer.SerializeToUtf8Bytes(bodyInput);
        await File.WriteAllBytesAsync(bodyPath.Value, bytes, cancellationToken);
        var bodyRead = await ReadManifestAsync(bodyPath, bytes, cancellationToken);
        Require(bodyRead.Accepted && bodyRead.Assets is not null, "Synthetic schema7 body inputs were not admitted: " + FormatDiagnostics(bodyRead.Diagnostics));
        var copied7 = copied with { Destination = new WorkspacePath(Path.Combine(root, "private-copy7.json")),
            BodySlidePresetAuthority = bodyRead.Assets!.BodySlidePresetAuthority, BodyMeshAuthority = bodyRead.Assets.BodyMeshAuthority };
        written = await fixture.Writer.WriteAsync(copied7, cancellationToken);
        Require(written.Written, "Schema7 refused private copied authority: " + FormatDiagnostics(written.Diagnostics));
        bytes = File.ReadAllBytes(copied7.Destination.Value);
        read = await ReadManifestAsync(copied7.Destination, bytes, cancellationToken);
        Require(read.Accepted && read.Assets is { SchemaVersion: 7 } copied7Assets && copied7Assets.PrivateHeadTextures == paths && copied7Assets.PackageAssets.SequenceEqual([admittedCopy]),
            "Schema7 private copies did not survive writer/strict-reader admission: " + FormatDiagnostics(read.Diagnostics));

        async ValueTask Refuses(string name, RaceMenuPresetStandaloneAuthorityWriteRequest request)
        {
            request = request with { Destination = new WorkspacePath(Path.Combine(root, name + ".json")) };
            var result = await fixture.Writer.WriteAsync(request, cancellationToken);
            Require(!result.Written && !File.Exists(request.Destination.Value) && result.Diagnostics.Any(item =>
                    item.Code == "racemenu-private-head-textures-refused" && item.Message.Contains("standaloneAssets.privateHeadTextures", StringComparison.Ordinal)),
                name + " must give field-naming refusal without publishing output: " + FormatDiagnostics(result.Diagnostics));
        }
        await Refuses("schema8-copy-policy", requested with { RetainedPackageAssets = [copy] });
        await Refuses("missing-private-authority", copied with { RetainedPackageAssets = [] });
        await Refuses("duplicate-copy-destination", copied with { RetainedPackageAssets = [copy, copy] });
        await Refuses("conflicting-private-authority", copied with
        {
            RetainedExternalTextures = [external with { ExpectedMemberSha256 = Hash("different private texture") }]
        });
        Require(HashBytes(File.ReadAllBytes(source.Value)) == hash, "Writer changed private source bytes.");
        Console.WriteLine("Private head texture writer policy, strict readback, legacy bytes, and authority refusals verified: " + root);
    }
}
