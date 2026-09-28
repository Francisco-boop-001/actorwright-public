using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private const long MaximumJsonBytes = 4 * 1_048_576;
    private const long MaximumFaceArtifactBytes = 256 * 1_048_576;
    private const long MaximumPluginBytes = 1_073_741_824;

    private async ValueTask VerifyStaticBundleFilesAsync(
        RaceMenuNpcPresetBundle bundle,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        await VerifyBoundFileAsync(bundle.PresetPath, bundle.ExpectedPresetSha256,
            MaximumJsonBytes, BundleFileKind.Preset, diagnostics, cancellationToken);
        await VerifyBoundFileAsync(bundle.CharGenFaceGeom,
            bundle.ExpectedCharGenFaceGeomSha256, MaximumFaceArtifactBytes,
            BundleFileKind.FaceGeom, diagnostics, cancellationToken);
        await VerifyBoundFileAsync(bundle.CharGenFaceTint,
            bundle.ExpectedCharGenFaceTintSha256, MaximumFaceArtifactBytes,
            BundleFileKind.FaceTint, diagnostics, cancellationToken);
    }

    private async ValueTask<bool> VerifyBoundFileAsync(
        WorkspacePath path,
        Sha256Hash expectedHash,
        long maximumBytes,
        BundleFileKind kind,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error($"racemenu-plan-{kind.ToWireName()}-missing",
                $"The hash-bound {kind.ToDisplayName()} is missing."));
            return false;
        }

        try
        {
            await using var stream = OpenReadLease(path.Value);
            if (stream.Length <= 0 || stream.Length > maximumBytes)
            {
                diagnostics.Add(Error($"racemenu-plan-{kind.ToWireName()}-size",
                    $"The {kind.ToDisplayName()} is empty or exceeds the {maximumBytes} byte safety limit."));
                return false;
            }

            var header = new byte[Math.Min(160, checked((int)stream.Length))];
            _ = await stream.ReadAsync(header, cancellationToken);
            if (!HasExpectedHeader(kind, path.Value, header))
            {
                diagnostics.Add(Error($"racemenu-plan-{kind.ToWireName()}-format",
                    $"The {kind.ToDisplayName()} does not have the expected file extension and header."));
                return false;
            }

            stream.Position = 0;
            var actualHash = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
            if (actualHash != expectedHash)
            {
                diagnostics.Add(Error($"racemenu-plan-{kind.ToWireName()}-hash-mismatch",
                    $"The {kind.ToDisplayName()} hash {actualHash} does not match {expectedHash}."));
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error($"racemenu-plan-{kind.ToWireName()}-read-failed",
                $"The {kind.ToDisplayName()} could not be read: {exception.Message}"));
            return false;
        }
    }

    private static bool HasExpectedHeader(BundleFileKind kind, string path, byte[] header) =>
        kind switch
        {
            BundleFileKind.Preset =>
                string.Equals(Path.GetExtension(path), ".jslot", StringComparison.OrdinalIgnoreCase) &&
                header.Length > 0,
            BundleFileKind.FaceGeom =>
                string.Equals(Path.GetExtension(path), ".nif", StringComparison.OrdinalIgnoreCase) &&
                (Encoding.ASCII.GetString(header).StartsWith("Gamebryo File Format", StringComparison.Ordinal) ||
                 Encoding.ASCII.GetString(header).StartsWith("NetImmerse File Format", StringComparison.Ordinal)),
            BundleFileKind.FaceTint =>
                string.Equals(Path.GetExtension(path), ".dds", StringComparison.OrdinalIgnoreCase) &&
                header.Length >= 128 && header.AsSpan(0, 4).SequenceEqual("DDS "u8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) == 124,
            BundleFileKind.RuntimeRouteArtifact => header.Length > 0,
            BundleFileKind.DependencyManifest => header.Length > 0,
            BundleFileKind.FormProviderPlugin =>
                Path.GetExtension(path) is var extension &&
                (string.Equals(extension, ".esp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(extension, ".esm", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(extension, ".esl", StringComparison.OrdinalIgnoreCase)) &&
                header.Length >= 4 && header.AsSpan(0, 4).SequenceEqual("TES4"u8),
            _ => false
        };

    private static FileStream OpenReadLease(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async ValueTask<(JsonDocument? Document, Sha256Hash? Hash)> ReadJsonAsync(
        WorkspacePath path,
        Sha256Hash expectedHash,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-missing",
                $"The hash-bound {role.Replace('-', ' ')} is missing."));
            return (null, null);
        }

        try
        {
            await using var stream = OpenReadLease(path.Value);
            if (stream.Length <= 0 || stream.Length > MaximumJsonBytes)
                throw new InvalidDataException($"The {role.Replace('-', ' ')} is empty or exceeds 4 MiB.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash != expectedHash)
                throw new InvalidDataException($"The {role.Replace('-', ' ')} hash {hash} does not match {expectedHash}.");
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            return (document, hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or OverflowException)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-invalid", exception.Message));
            return (null, null);
        }
    }

    private WorkspacePath ResolveRelative(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':'))
            throw new InvalidDataException($"The {role} must be a relative K-local path.");
        return new WorkspacePath(Path.Combine(labRoot.Value,
            value.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void RequireShape(JsonElement element, string role, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {role} must be an object.");
        var allowed = properties.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"The {role} contains duplicate property '{property.Name}'.");
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"The {role} contains unsupported property '{property.Name}'.");
        }
        var missing = properties.Where(property => !seen.Contains(property)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The {role} is missing {string.Join(", ", missing)}.");
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"'{property}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"'{property}' must be an integer.");
        return result;
    }

    private static void BindPath(string role, WorkspacePath expected, WorkspacePath actual)
    {
        if (!string.Equals(expected.Value, actual.Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The bundle {role} does not match the typed request.");
    }

    private static void BindHash(string role, Sha256Hash expected, Sha256Hash actual)
    {
        if (expected != actual)
            throw new InvalidDataException($"The bundle {role} hash does not match the typed request.");
    }

    internal enum BundleFileKind
    {
        Preset,
        FaceGeom,
        FaceTint,
        RuntimeRouteArtifact,
        DependencyManifest,
        FormProviderPlugin
    }
}

internal static class BundleFileKindExtensions
{
    internal static string ToWireName(this RaceMenuNpcAppearancePlanService.BundleFileKind kind) => kind switch
    {
        RaceMenuNpcAppearancePlanService.BundleFileKind.Preset => "preset",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FaceGeom => "facegeom",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FaceTint => "facetint",
        RaceMenuNpcAppearancePlanService.BundleFileKind.RuntimeRouteArtifact => "runtime-route-artifact",
        RaceMenuNpcAppearancePlanService.BundleFileKind.DependencyManifest => "dependency-manifest",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FormProviderPlugin => "form-provider-plugin",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string ToDisplayName(this RaceMenuNpcAppearancePlanService.BundleFileKind kind) => kind switch
    {
        RaceMenuNpcAppearancePlanService.BundleFileKind.Preset => "RaceMenu .jslot",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FaceGeom => "CharGen FaceGeom NIF",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FaceTint => "CharGen FaceTint DDS",
        RaceMenuNpcAppearancePlanService.BundleFileKind.RuntimeRouteArtifact => "runtime route artifact",
        RaceMenuNpcAppearancePlanService.BundleFileKind.DependencyManifest => "dependency manifest",
        RaceMenuNpcAppearancePlanService.BundleFileKind.FormProviderPlugin => "copied form-provider plugin",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
