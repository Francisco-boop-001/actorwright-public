using System.Buffers;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

/// <summary>
/// Projects the canonical external install evidence into a Protocol result.
/// The nested verification document is emitted by the existing portable
/// codec; only the create-time wrapper is assembled here.
/// </summary>
internal static class ExternalInstallPrepublicationProtocolProjection
{
    public static JsonElement Serialize(
        RaceMenuNpcExternalInstallPrepublicationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        byte[] verificationBytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact.Verification);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "packageManifestSha256",
                artifact.PackageManifestSha256.Value);
            writer.WriteString(
                "selectedManifestSha256",
                artifact.SelectedManifestSha256.Value);
            writer.WritePropertyName("bindings");
            writer.WriteStartArray();
            foreach (RaceMenuNpcExternalInstallPrepublicationBinding binding in
                artifact.Bindings
                    .OrderBy(item => item.DescriptorId.Value,
                        StringComparer.Ordinal)
                    .ThenBy(item =>
                        item.FaceGeomExclusionAttestationSha256.Value,
                        StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("descriptorId", binding.DescriptorId.Value);
                writer.WriteString(
                    "faceGeomExclusionAttestationSha256",
                    binding.FaceGeomExclusionAttestationSha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("verification");
            using (JsonDocument verification =
                JsonDocument.Parse(verificationBytes))
                verification.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument projected = JsonDocument.Parse(output.WrittenMemory);
        return projected.RootElement.Clone();
    }
}
