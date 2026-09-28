using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

internal sealed record PackageManifestWriteResult(
    bool Written,
    Sha256Hash? Hash,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed class PackageManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async ValueTask<PackageManifestWriteResult> WriteAsync(
        PresetToNpcPackageManifest manifest,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (File.Exists(destination.Value))
        {
            diagnostics.Add(new Diagnostic("pipeline-manifest-exists", DiagnosticSeverity.Error,
                "The package manifest already exists; pipeline output never overwrites an artifact."));
            return new PackageManifestWriteResult(false, null, diagnostics.ToImmutable());
        }

        var bytes = Serialize(manifest);
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination.Value, overwrite: false);
            return new PackageManifestWriteResult(true,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (IOException exception)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("pipeline-manifest-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PackageManifestWriteResult(false, null, diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("pipeline-manifest-write-denied", DiagnosticSeverity.Error, exception.Message));
            return new PackageManifestWriteResult(false, null, diagnostics.ToImmutable());
        }
    }

    internal static byte[] Serialize(PresetToNpcPackageManifest manifest)
    {
        var serializable = new SerializablePackageManifest(manifest.SchemaVersion, manifest.Edition,
            manifest.PresetFormat, manifest.SourcePreset, manifest.SourcePresetSha256.Value, manifest.SourcePlugin,
            manifest.SourcePluginSha256.Value, manifest.OutputPlugin, manifest.TargetFormId.ToString(),
            manifest.Artifacts.Select(artifact => new SerializableArtifact(artifact.Kind, artifact.RelativePath.Value,
                artifact.ByteLength, artifact.Sha256.Value)).ToImmutableArray());
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(serializable, JsonOptions));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record SerializablePackageManifest(int SchemaVersion, string Edition, string PresetFormat,
        string SourcePreset, string SourcePresetSha256, string SourcePlugin, string SourcePluginSha256,
        string OutputPlugin, string TargetFormId, ImmutableArray<SerializableArtifact> Artifacts);

    private sealed record SerializableArtifact(string Kind, string RelativePath, int ByteLength, string Sha256);
}
