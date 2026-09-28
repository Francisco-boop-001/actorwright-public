using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public partial class RaceMenuNpcStandaloneAuthorityReader
{
    private const int MaximumDecisionManifestBytes = 1 * 1024 * 1024;
    private const long MaximumUserDecisionEvidenceBytes = 4 * 1024 * 1024;

    private async ValueTask<RaceMenuNpcOverlayDecisionSet?> ReadOverlayDecisionsAsync(
        WorkspacePath manifestPath,
        Sha256Hash expectedManifestSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadHashBoundOrdinaryFileAsync(manifestPath,
            expectedManifestSha256, MaximumDecisionManifestBytes,
            "racemenu-overlay-decisions", "overlay-decision manifest", diagnostics,
            cancellationToken);
        if (bytes is null) return null;

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 12
        });
        RejectDuplicateKeys(document.RootElement);
        var root = document.RootElement;
        RequireShape(root, "overlay-decision manifest", "schemaVersion", "edition",
            "presetSha256", "decisions");
        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(RequiredString(root, "edition"), "skyrimse",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Overlay decisions require schemaVersion 1 and edition 'skyrimse'.");

        var presetSha256 = new Sha256Hash(RequiredString(root, "presetSha256"));
        var rows = root.GetProperty("decisions");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw new InvalidDataException(
                "An overlay-decision manifest must contain at least one decision.");
        var decisions = ImmutableArray.CreateBuilder<RaceMenuNpcOverlayDecision>();
        var indices = new HashSet<int>();
        var evidenceHashes = new Dictionary<string, Sha256Hash>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.EnumerateArray())
        {
            RequireShape(row, "overlay decision", "sourceIndex", "node",
                "expectedTexture", "action", "reason", "userDecisionEvidence");
            var sourceIndex = RequiredInt(row, "sourceIndex");
            if (sourceIndex < 0 || !indices.Add(sourceIndex))
                throw new InvalidDataException(
                    "Overlay-decision sourceIndex values must be unique non-negative integers.");
            var node = RequiredString(row, "node");
            if (node.Length > 256 || string.IsNullOrWhiteSpace(node))
                throw new InvalidDataException(
                    "Overlay-decision node must contain at most 256 non-whitespace characters.");
            var expectedTexture = CanonicalDataTexturePath(
                RequiredString(row, "expectedTexture"), "overlay decision");
            if (!string.Equals(RequiredString(row, "action"), "omit",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The only supported overlay-decision action is 'omit'.");
            var reason = RequiredString(row, "reason");
            if (reason.Length > 2048 || string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException(
                    "Overlay-decision reason must contain at most 2048 non-whitespace characters.");

            var evidence = row.GetProperty("userDecisionEvidence");
            RequireShape(evidence, "user-decision evidence", "path", "sha256");
            var evidencePath = ResolveRelative(RequiredString(evidence, "path"));
            var evidenceSha256 = new Sha256Hash(RequiredString(evidence, "sha256"));
            if (evidenceHashes.TryGetValue(evidencePath.Value, out var priorHash) &&
                priorHash != evidenceSha256)
                throw new InvalidDataException(
                    "One user-decision evidence path was declared with conflicting hashes.");
            evidenceHashes[evidencePath.Value] = evidenceSha256;
            decisions.Add(new RaceMenuNpcOverlayDecision(sourceIndex, node,
                expectedTexture, RaceMenuNpcOverlayDecisionAction.Omit, reason,
                new RaceMenuNpcUserDecisionEvidence(evidencePath, evidenceSha256)));
        }

        foreach (var (path, hash) in evidenceHashes)
        {
            if (await ReadHashBoundOrdinaryFileAsync(new WorkspacePath(path), hash,
                    MaximumUserDecisionEvidenceBytes, "racemenu-overlay-decision-evidence",
                    "user-decision evidence", diagnostics, cancellationToken) is null)
                return null;
        }
        return new RaceMenuNpcOverlayDecisionSet(manifestPath,
            expectedManifestSha256, presetSha256, decisions.ToImmutable());
    }

    private async ValueTask<bool> VerifyExternalTextureAuthoritiesAsync(
        ImmutableArray<RaceMenuNpcExternalTextureAuthority> authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, AssetIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var authority in authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveProviderIndexRoot(authority, diagnostics, out var dataRoot,
                    out var providerKind))
                return false;
            if (!cache.TryGetValue(dataRoot.Value, out var index))
            {
                diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
                    LaboratoryRoot,
                    dataRoot));
                if (HasErrors(diagnostics)) return false;
                index = await assetIndexer.IndexAsync(
                    new AssetIndexRequest(GameEdition.SkyrimSpecialEdition, dataRoot),
                    cancellationToken);
                diagnostics.AddRange(index.Diagnostics);
                if (index.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                    return false;
                cache.Add(dataRoot.Value, index);
            }

            var matches = index.Providers.Where(item =>
                    item.Kind == providerKind &&
                    string.Equals(item.Path.Value, authority.DataRelativePath.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Source, authority.Provider,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                diagnostics.Add(Error("racemenu-assets-external-provider",
                    $"Expected exactly one '{authority.Provider}' provider for '{authority.DataRelativePath.Value}', found {matches.Length}."));
                return false;
            }
            var member = matches[0];
            if (member.Size <= 0 ||
                new Sha256Hash(member.Sha256) != authority.ExpectedMemberSha256)
            {
                diagnostics.Add(Error("racemenu-assets-external-member-hash",
                    $"External member '{authority.DataRelativePath.Value}' from '{authority.Provider}' does not match SHA256 {authority.ExpectedMemberSha256}."));
                return false;
            }
        }
        return true;
    }

    private static bool TryResolveProviderIndexRoot(
        RaceMenuNpcExternalTextureAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out WorkspacePath dataRoot,
        out AssetProviderKind providerKind)
    {
        var extension = Path.GetExtension(authority.Source.Value);
        if (extension.Equals(".bsa", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(authority.Source.Value);
            if (parent is null ||
                !string.Equals(authority.Provider, Path.GetFileName(authority.Source.Value),
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("racemenu-assets-external-provider",
                    "An archive authority provider must equal the exact source archive file name."));
                dataRoot = default;
                providerKind = default;
                return false;
            }
            dataRoot = new WorkspacePath(parent);
            providerKind = AssetProviderKind.Archive;
            return true;
        }

        if (!extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(authority.Provider, "loose", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("racemenu-assets-external-provider",
                "An external texture source must be a .bsa archive or an exact loose .dds with provider 'loose'."));
            dataRoot = default;
            providerKind = default;
            return false;
        }

        var relative = authority.DataRelativePath.Value.Replace('/', Path.DirectorySeparatorChar);
        var suffix = Path.DirectorySeparatorChar + relative;
        var source = Path.GetFullPath(authority.Source.Value);
        if (!source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("racemenu-assets-external-loose-path",
                $"Loose source '{authority.Source.Value}' does not end in its declared Data-relative path '{authority.DataRelativePath.Value}'."));
            dataRoot = default;
            providerKind = default;
            return false;
        }
        dataRoot = new WorkspacePath(source[..^suffix.Length]);
        providerKind = AssetProviderKind.Loose;
        return true;
    }

    protected async ValueTask<byte[]?> ReadHashBoundOrdinaryFileAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        long maximumBytes,
        string diagnosticPrefix,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
            LaboratoryRoot,
            path));
        if (HasErrors(diagnostics)) return null;
        try
        {
            await using var stream = new FileStream(path.Value, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (!TryVerifyRaceMenuInputFinalPath(stream.SafeFileHandle, path.Value,
                    out var finalPathError))
            {
                diagnostics.Add(Error($"{diagnosticPrefix}-file",
                    $"The {role} could not be identity-pinned to its declared path: " +
                    finalPathError));
                return null;
            }
            if (stream.Length is <= 0 || stream.Length > maximumBytes ||
                stream.Length > int.MaxValue)
            {
                diagnostics.Add(Error($"{diagnosticPrefix}-file",
                    $"The {role} must be an ordinary non-empty K-local file no larger than {maximumBytes} bytes."));
                return null;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException(
                        $"The {role} ended before its admitted length was read.");
                offset += read;
            }
            var actual = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (actual != expectedSha256)
            {
                diagnostics.Add(Error($"{diagnosticPrefix}-hash",
                    $"The {role} hash {actual} does not match {expectedSha256}."));
                return null;
            }
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error($"{diagnosticPrefix}-file",
                $"The {role} could not be reopened as a stable bounded file: " +
                exception.Message));
            return null;
        }
    }

    protected static bool TryVerifyRaceMenuInputFinalPath(
        SafeFileHandle handle,
        string expectedPath,
        out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Final-path identity verification is supported only on Windows.";
            return false;
        }
        try
        {
            var actual = CanonicalRaceMenuInputPath(
                GetRaceMenuInputFinalDosPath(handle));
            var expected = CanonicalRaceMenuInputPath(expectedPath);
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }
            error = $"The opened file resolved as '{actual}' instead of '{expected}'.";
            return false;
        }
        catch (Win32Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string GetRaceMenuInputFinalDosPath(SafeFileHandle handle)
    {
        const uint fileNameNormalized = 0;
        const uint volumeNameDos = 0;
        var required = GetRaceMenuInputFinalPathNameByHandle(
            handle, null, 0, fileNameNormalized | volumeNameDos);
        if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[checked((int)required + 1)];
        var written = GetRaceMenuInputFinalPathNameByHandle(
            handle, buffer, (uint)buffer.Length, fileNameNormalized | volumeNameDos);
        if (written == 0 || written >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new string(buffer, 0, checked((int)written));
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
    }

    private static string CanonicalRaceMenuInputPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetRaceMenuInputFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    private static AssetPath CanonicalDataTexturePath(string value, string role)
    {
        var path = new AssetPath(value);
        var canonical = path.Value.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
            ? path
            : new AssetPath($"Textures/{path.Value}");
        if (!canonical.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{role} texture paths must end in .dds.");
        return canonical;
    }
}
