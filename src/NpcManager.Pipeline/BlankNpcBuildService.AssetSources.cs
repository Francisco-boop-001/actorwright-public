using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private const long MaximumPackageAssetBytes = int.MaxValue;
    private const int MaximumTransitivePackageAssets = 9_991;

    private void ValidateBoundAssetContracts(
        BlankNpcBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (request.FaceGeomSource)
        {
            case ProviderBlankNpcFaceGeomSource:
                break;
            case ExactNifBlankNpcFaceGeomSource exact:
                diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, exact.SourceNif));
                if (!Enum.IsDefined(exact.QualificationProfile))
                {
                    diagnostics.Add(new Diagnostic(
                        "blank-npc-exact-facegeom-profile",
                        DiagnosticSeverity.Error,
                        "The exact FaceGeom qualification profile is unsupported."));
                }
                if (!exact.SourceNif.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new Diagnostic(
                        "blank-npc-exact-facegeom-extension",
                        DiagnosticSeverity.Error,
                        "An exact FaceGeom source must use the .nif extension."));
                }
                break;
            default:
                diagnostics.Add(new Diagnostic(
                    "blank-npc-facegeom-source",
                    DiagnosticSeverity.Error,
                    "A supported provider-carrier or exact-NIF FaceGeom source is required."));
                break;
        }

        switch (request.FaceTintSource)
        {
            case GeneratedBlankNpcFaceTintSource:
                break;
            case ExactDdsBlankNpcFaceTintSource exact:
                diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, exact.SourceDds));
                if (exact.Width <= 0 || exact.Height <= 0)
                {
                    diagnostics.Add(new Diagnostic(
                        "blank-npc-exact-facetint-dimensions",
                        DiagnosticSeverity.Error,
                        "Exact FaceTint width and height must both be positive."));
                }
                break;
            default:
                diagnostics.Add(new Diagnostic(
                    "blank-npc-facetint-source",
                    DiagnosticSeverity.Error,
                    "A supported generated or exact-DDS FaceTint source is required."));
                break;
        }

        if (request.TransitivePackageAssets.IsDefault)
        {
            diagnostics.Add(new Diagnostic(
                "blank-npc-transitive-assets-default",
                DiagnosticSeverity.Error,
                "The transitive package asset list must be initialized, even when empty."));
            return;
        }
        if (request.TransitivePackageAssets.Length > MaximumTransitivePackageAssets)
        {
            diagnostics.Add(new Diagnostic(
                "blank-npc-transitive-asset-count",
                DiagnosticSeverity.Error,
                $"At most {MaximumTransitivePackageAssets} transitive assets are accepted so the exact " +
                "package inventory remains within its 10,000-artifact schema limit."));
            return;
        }

        const string formId = "00000800";
        var destinations = new List<string>
        {
            request.OutputPlugin.Value,
            $"meshes/actors/character/FaceGenData/FaceGeom/{request.OutputPlugin.Value}/{formId}.nif",
            $"textures/actors/character/FaceGenData/FaceTint/{request.OutputPlugin.Value}/{formId}.dds"
        };
        foreach (var asset in request.TransitivePackageAssets)
        {
            if (asset is null)
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-transitive-asset-null",
                    DiagnosticSeverity.Error,
                    "The transitive package asset list cannot contain null entries."));
                continue;
            }

            if (asset.Kind is not null &&
                !string.Equals(
                    asset.Kind,
                    BlankNpcTransitivePackageAssetKinds.ExternalHeadPartDependencies,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-transitive-kind",
                    DiagnosticSeverity.Error,
                    $"Transitive package asset kind '{asset.Kind}' is not admitted."));
            }

            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, asset.Source));
            if (!IsSafeWindowsGameDestination(asset.Destination))
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-transitive-destination",
                    DiagnosticSeverity.Error,
                    $"Transitive destination '{asset.Destination.Value}' is not a normalized Windows game path."));
            }
            if (destinations.Any(existing => PackageDestinationsCollide(
                    existing, asset.Destination.Value)))
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-transitive-destination-collision",
                    DiagnosticSeverity.Error,
                    $"Transitive destination '{asset.Destination.Value}' duplicates or collides with another package artifact."));
            }
            else
            {
                destinations.Add(asset.Destination.Value);
            }
        }
    }

    private static bool PackageDestinationsCollide(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeWindowsGameDestination(AssetPath destination)
    {
        foreach (var segment in destination.Value.Split('/'))
        {
            if (segment.Any(char.IsControl) ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                return false;

            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 &&
                 (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 stem[3] is >= '1' and <= '9'))
                return false;
        }

        return true;
    }

    private async ValueTask<bool> PreflightBoundAssetsAsync(
        BlankNpcBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (request.FaceGeomSource is ExactNifBlankNpcFaceGeomSource exactFaceGeom &&
            await VerifyBoundSourceAsync(
                exactFaceGeom.SourceNif,
                exactFaceGeom.ExpectedSha256,
                "exact FaceGeom NIF",
                diagnostics,
                cancellationToken) is null)
            return false;

        if (request.FaceTintSource is ExactDdsBlankNpcFaceTintSource exact)
        {
            var hash = await VerifyBoundSourceAsync(
                exact.SourceDds,
                exact.ExpectedSha256,
                "exact FaceTint DDS",
                diagnostics,
                cancellationToken);
            if (hash is null) return false;

            var decode = await faceTintDecoder.DecodeAsync(exact.SourceDds, cancellationToken);
            diagnostics.AddRange(decode.Diagnostics);
            if (!decode.Decoded || decode.SourceSha256 != exact.ExpectedSha256 ||
                decode.Width != exact.Width || decode.Height != exact.Height)
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-exact-facetint-preflight",
                    DiagnosticSeverity.Error,
                    $"The exact FaceTint source did not independently decode as the declared " +
                    $"{exact.Width}x{exact.Height} DDS with hash {exact.ExpectedSha256}."));
                return false;
            }
        }

        foreach (var asset in NormalizedTransitiveAssets(request))
        {
            if (await VerifyBoundSourceAsync(
                    asset.Source,
                    asset.ExpectedSha256,
                    $"transitive package asset '{asset.Destination.Value}'",
                    diagnostics,
                    cancellationToken) is null)
                return false;
        }

        return !HasErrors(diagnostics);
    }

    private async ValueTask<Sha256Hash?> VerifyBoundSourceAsync(
        WorkspacePath source,
        Sha256Hash expectedHash,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var pathDiagnostics = policy.EvaluateReadRoot(labRoot, source);
        diagnostics.AddRange(pathDiagnostics);
        if (HasErrors(pathDiagnostics)) return null;

        try
        {
            var info = new FileInfo(source.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumPackageAssetBytes)
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-bound-asset-size",
                    DiagnosticSeverity.Error,
                    $"The {role} must be an existing non-empty ordinary file no larger than " +
                    $"{MaximumPackageAssetBytes} bytes."));
                return null;
            }

            var attributes = File.GetAttributes(source.Value);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-bound-asset-reparse",
                    DiagnosticSeverity.Error,
                    $"The {role} must be an ordinary K-local file, not a directory or reparse point."));
                return null;
            }

            var actualHash = await HashFileAsync(source, cancellationToken);
            if (actualHash != expectedHash)
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-bound-asset-hash",
                    DiagnosticSeverity.Error,
                    $"The {role} hash {actualHash} does not match the admitted hash {expectedHash}."));
                return null;
            }

            return actualHash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                "blank-npc-bound-asset-read",
                DiagnosticSeverity.Error,
                $"The {role} could not be qualified: {exception.Message}"));
            return null;
        }
    }

    private static async ValueTask<bool> MaterializeTransitiveAssetsAsync(
        ImmutableArray<TransitiveAssetPath> assets,
        OwnedFileLedger ownedFiles,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
        {
            await CopyHashBoundAtomicallyAsync(
                asset.Source.Source,
                asset.Destination,
                asset.Source.ExpectedSha256,
                cancellationToken);
            if (!TryRegisterOwnedFile(
                    ownedFiles,
                    asset.Destination,
                    asset.Source.ExpectedSha256,
                    "blank-npc-transitive-asset-ownership",
                    $"transitive package asset '{asset.Source.Destination.Value}'",
                    diagnostics))
                return false;

            var outputHash = await HashFileAsync(asset.Destination, cancellationToken);
            if (outputHash != asset.Source.ExpectedSha256)
            {
                diagnostics.Add(new Diagnostic(
                    "blank-npc-transitive-asset-readback",
                    DiagnosticSeverity.Error,
                    $"Transitive asset '{asset.Source.Destination.Value}' changed during materialization."));
                return false;
            }
        }

        return true;
    }

    private static async ValueTask CopyHashBoundAtomicallyAsync(
        WorkspacePath source,
        WorkspacePath destination,
        Sha256Hash expectedHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source.Value, temporary, overwrite: false);
            var temporaryHash = await HashFileAsync(
                new WorkspacePath(temporary), cancellationToken);
            if (temporaryHash != expectedHash)
                throw new IOException(
                    $"Copied source hash {temporaryHash} does not match admitted hash {expectedHash}.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static ImmutableArray<BlankNpcTransitivePackageAsset> NormalizedTransitiveAssets(
        BlankNpcBuildRequest request) =>
        request.TransitivePackageAssets.IsDefault
            ? []
            : request.TransitivePackageAssets;

    private static (
        WorkspacePath Source,
        Sha256Hash Sha256,
        bool ProviderBound,
        QualifiedFaceGeomCarrierProfile QualificationProfile)
        ResolveFaceGeomSource(BlankNpcBuildRequest request) => request.FaceGeomSource switch
        {
            ProviderBlankNpcFaceGeomSource =>
                (
                    request.FaceGeomCarrier,
                    request.ExpectedFaceGeomCarrierSha256,
                    true,
                    request.ProviderResources is null
                        ? QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape
                        : QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete),
            ExactNifBlankNpcFaceGeomSource exact =>
                (
                    exact.SourceNif,
                    exact.ExpectedSha256,
                    false,
                    exact.QualificationProfile),
            _ => throw new InvalidDataException("Unsupported FaceGeom source.")
        };
}
