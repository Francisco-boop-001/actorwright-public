using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFollowerFinishService
{
    private const long MaximumFollowerFinishManifestBytes =
        4L * 1024 * 1024;
    private const long MaximumFollowerFinishArtifactBytes =
        512L * 1024 * 1024;
    private const long MaximumFollowerFinishArchiveBytes =
        2L * 1024 * 1024 * 1024;
    private const int MaximumFollowerFinishEntryCount = 10_000;
    private static readonly UTF8Encoding FollowerFinishUtf8 = new(false);
    private static readonly ImmutableArray<string>
        FollowerFinishRootDocumentPaths =
        [
            "BUILD_INFO.txt",
            "README-NPCMANAGER-RUNTIME-TEST.txt",
            "RUNTIME-TEST-INSTRUCTIONS.md"
        ];
    private static readonly ImmutableArray<string>
        FollowerFinishRequiredObservedSignatures =
        [
            "PACK",
            "REFR",
            "ACHR"
        ];
    private static readonly ImmutableArray<string>
        FollowerFinishForbiddenObservedSignatures =
        [
            "LAND", "WATR", "LTEX", "NAVM", "NAVI",
            "LCTN", "REGN", "CLMT", "MUSC", "IMGS"
        ];
    private static readonly ImmutableArray<FollowerFinishEvidenceDefinition>
        FollowerFinishEvidenceDefinitions =
        [
            new(
                "follower-finish-request",
                "evidence/follower-finish-request.json"),
            new(
                "follower-finish-proposal",
                "evidence/follower-finish-proposal.json"),
            new(
                "follower-finish-verification",
                "evidence/follower-finish-verification.json"),
            new(
                "source-package-binding",
                "evidence/source-package-binding.json"),
            new(
                "world-conflict-audit",
                "evidence/world-conflict-audit.json"),
            new(
                "runtime-test-instructions",
                "evidence/runtime-test-instructions.json")
        ];
    private static readonly JsonDocumentOptions
        FollowerFinishJsonDocumentOptions =
        new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };

    private async ValueTask<FollowerFinishSourcePlan>
        ReadFollowerFinishSourcePlanAsync(
            SkyrimFollowerFinishRequest request,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<Diagnostic> manifestPolicy =
            policy.EvaluateReadRoot(
                workspaceRoot,
                request.Source.PackageManifest);
        ThrowOnFollowerFinishErrors(manifestPolicy);
        byte[] manifestBytes = await ReadBoundFollowerFinishFileAsync(
            request.Source.PackageManifest,
            expectedLength: null,
            request.Source.PackageManifestSha256,
            MaximumFollowerFinishManifestBytes,
            cancellationToken);

        using JsonDocument document = JsonDocument.Parse(
            manifestBytes,
            FollowerFinishJsonDocumentOptions);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "The source package manifest root must be an object.");
        ValidateFollowerFinishDuplicateProperties(root, "$");
        int schemaVersion = ReadFollowerFinishInt(
            root,
            "schemaVersion");
        if (schemaVersion != 1)
            throw new InvalidDataException(
                "Only source package manifest schemaVersion 1 is admitted.");

        string edition = ReadFollowerFinishString(root, "edition");
        string presetFormat =
            ReadFollowerFinishString(root, "presetFormat");
        string sourcePreset =
            ReadFollowerFinishString(root, "sourcePreset");
        var sourcePresetSha256 = new Sha256Hash(
            ReadFollowerFinishString(
                root,
                "sourcePresetSha256"));
        string sourcePlugin =
            ReadFollowerFinishString(root, "sourcePlugin");
        var sourcePluginSha256 = new Sha256Hash(
            ReadFollowerFinishString(
                root,
                "sourcePluginSha256"));
        string outputPlugin =
            ReadFollowerFinishString(root, "outputPlugin");
        if (!string.Equals(
                outputPlugin,
                request.Source.Plugin.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source manifest output plugin does not match the request.");
        }

        string targetText =
            ReadFollowerFinishString(root, "targetFormId");
        if (!FormId.TryParse(targetText, out FormId targetFormId) ||
            targetFormId != request.NpcFormId)
        {
            throw new InvalidDataException(
                "The source manifest target FormID does not match the request.");
        }

        if (!root.TryGetProperty("artifacts", out JsonElement artifacts) ||
            artifacts.ValueKind != JsonValueKind.Array ||
            artifacts.GetArrayLength() == 0 ||
            artifacts.GetArrayLength() > MaximumFollowerFinishEntryCount)
        {
            throw new InvalidDataException(
                "The source manifest artifacts array is empty or out of bounds.");
        }

        var rows =
            ImmutableArray.CreateBuilder<FollowerFinishSourceArtifact>();
        var paths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "Every source manifest artifact must be an object.");
            string kind =
                ReadFollowerFinishString(artifact, "kind");
            var manifestPath = new AssetPath(
                ReadFollowerFinishString(
                    artifact,
                    "relativePath"));
            bool inRuntimeArchive =
                manifestPath.Value.StartsWith(
                    "Data/",
                    StringComparison.Ordinal);
            if (!inRuntimeArchive &&
                !manifestPath.Value.StartsWith(
                    "evidence/",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Source artifact '{manifestPath.Value}' is neither runtime Data nor manifest-only evidence.");
            }
            if (!paths.Add(manifestPath.Value))
                throw new InvalidDataException(
                    $"Source artifact '{manifestPath.Value}' is duplicated or case-aliased.");
            long byteLength = ReadFollowerFinishLong(
                artifact,
                "byteLength");
            if (byteLength <= 0 ||
                byteLength > MaximumFollowerFinishArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Source artifact '{manifestPath.Value}' has an inadmissible size.");
            }
            var sha256 = new Sha256Hash(
                ReadFollowerFinishString(
                    artifact,
                    "sha256"));
            rows.Add(
                new FollowerFinishSourceArtifact(
                    kind,
                    manifestPath,
                    inRuntimeArchive
                        ? manifestPath.Value["Data/".Length..]
                        : manifestPath.Value,
                    byteLength,
                    sha256,
                    inRuntimeArchive));
        }

        ImmutableArray<FollowerFinishSourceArtifact> sourceArtifacts =
            rows.ToImmutable();
        FollowerFinishSourceArtifact[] pluginArtifacts =
            sourceArtifacts.Where(artifact =>
                    string.Equals(
                        artifact.ZipPath,
                        request.Source.Plugin.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        artifact.Kind,
                        "plugin",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (pluginArtifacts.Length != 1 ||
            pluginArtifacts[0].Sha256 !=
                request.Source.PluginSha256)
        {
            throw new InvalidDataException(
                "The source plugin manifest binding is missing or ambiguous.");
        }

        RequireFollowerFinishKindHash(
            sourceArtifacts,
            "facegeom",
            request.Source.FaceGeomSha256);
        RequireFollowerFinishKindHash(
            sourceArtifacts,
            "facetint",
            request.Source.FaceTintSha256);

        var expectedAllowed = new HashSet<string>(
            sourceArtifacts.Select(artifact =>
                artifact.ManifestPath.Value),
            StringComparer.OrdinalIgnoreCase)
        {
            "npcmanager-package.json"
        };
        if (!expectedAllowed.SetEquals(
                request.AllowedPackageFiles.Select(path =>
                    path.Value)))
        {
            throw new InvalidDataException(
                "The request allowed-package closure differs from the source manifest.");
        }

        return new FollowerFinishSourcePlan(
            manifestBytes,
            edition,
            presetFormat,
            sourcePreset,
            sourcePresetSha256,
            sourcePlugin,
            sourcePluginSha256,
            outputPlugin,
            targetFormId,
            sourceArtifacts);
    }

    private async ValueTask<ImmutableArray<FollowerFinishMappedArtifact>>
        ExtractFollowerFinishSourceAsync(
            SkyrimFollowerFinishRequest request,
            FollowerFinishSourcePlan sourcePlan,
            WorkspacePath stageRoot,
            WorkspacePath extractedPlugin,
            CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> sourcePolicy =
            policy.EvaluateReadRoot(
                workspaceRoot,
                request.Source.Zip);
        ThrowOnFollowerFinishErrors(sourcePolicy);
        await using var stream = new FileStream(
            request.Source.Zip.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        if (stream.Length != request.Source.ZipByteLength ||
            stream.Length <= 0 ||
            stream.Length > MaximumFollowerFinishArchiveBytes)
        {
            throw new InvalidDataException(
                "The source ZIP length does not match its hash-bound request.");
        }

        Sha256Hash sourceZipHash =
            await HashFollowerFinishRetainedStreamAsync(
                stream,
                cancellationToken);
        if (sourceZipHash != request.Source.ZipSha256)
            throw new InvalidDataException(
                "The source ZIP hash changed after proposal approval.");
        stream.Position = 0;

        var expected = new Dictionary<
            string,
            FollowerFinishSourceArtifact?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FollowerFinishSourceArtifact artifact in
                 sourcePlan.Artifacts.Where(artifact =>
                     artifact.InRuntimeArchive))
        {
            if (!expected.TryAdd(artifact.ZipPath, artifact))
                throw new InvalidDataException(
                    $"Source ZIP path '{artifact.ZipPath}' is ambiguous.");
        }
        foreach (string document in FollowerFinishRootDocumentPaths)
        {
            if (!expected.TryAdd(document, null))
                throw new InvalidDataException(
                    $"Root document '{document}' collides with source payload.");
        }

        var mapped =
            ImmutableArray.CreateBuilder<FollowerFinishMappedArtifact>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            FollowerFinishUtf8);
        if (archive.Entries.Count != expected.Count ||
            archive.Entries.Count > MaximumFollowerFinishEntryCount)
        {
            throw new InvalidDataException(
                "The source ZIP entry closure differs from the admitted manifest plus root documents.");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(extractedPlugin.Value)!);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFollowerFinishZipEntry(entry);
            if (!seen.Add(entry.FullName) ||
                !expected.TryGetValue(
                    entry.FullName,
                    out FollowerFinishSourceArtifact? artifact) ||
                !string.Equals(
                    entry.FullName,
                    artifact?.ZipPath ?? entry.FullName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Source ZIP entry '{entry.FullName}' is undeclared, duplicated, or case-aliased.");
            }

            string outputRelative = artifact is null
                ? entry.FullName
                : artifact.ManifestPath.Value;
            var outputPath = new WorkspacePath(Path.Combine(
                stageRoot.Value,
                outputRelative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
            string writePath;
            if (artifact is not null &&
                string.Equals(
                    artifact.ZipPath,
                    request.Source.Plugin.Value,
                    StringComparison.Ordinal))
            {
                writePath = extractedPlugin.Value;
            }
            else if (IsFollowerFinishDiagnosticSource(
                         request,
                         entry.FullName))
            {
                writePath = Path.Combine(
                    stageRoot.Value,
                    ".transaction",
                    "source-diagnostic.txt");
            }
            else
            {
                writePath = outputPath.Value;
            }
            Directory.CreateDirectory(
                Path.GetDirectoryName(writePath)!);
            (long length, Sha256Hash sha256) =
                await ExtractFollowerFinishEntryAsync(
                    entry,
                    writePath,
                    cancellationToken);
            if (artifact is not null &&
                (length != artifact.ByteLength ||
                 sha256 != artifact.Sha256))
            {
                throw new InvalidDataException(
                    $"Source ZIP entry '{entry.FullName}' failed its manifest size/hash binding.");
            }
            mapped.Add(
                new FollowerFinishMappedArtifact(
                    artifact?.Kind ??
                        "source-root-document",
                    entry.FullName,
                    new AssetPath(outputRelative),
                    length,
                    sha256));
        }

        if (seen.Count != expected.Count)
            throw new InvalidDataException(
                "The source ZIP omitted an admitted entry.");
        return mapped.ToImmutable();
    }

    private static async ValueTask<ImmutableArray<FollowerFinishMappedArtifact>>
        ReadFollowerFinishSourceMapAsync(
            SkyrimFollowerFinishRequest request,
            FollowerFinishSourcePlan sourcePlan,
            CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            request.Source.Zip.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        if (stream.Length != request.Source.ZipByteLength ||
            await HashFollowerFinishRetainedStreamAsync(
                stream,
                cancellationToken) != request.Source.ZipSha256)
        {
            throw new InvalidDataException(
                "The source ZIP changed after proposal approval.");
        }
        stream.Position = 0;

        var expected = new Dictionary<
            string,
            FollowerFinishSourceArtifact?>(
            StringComparer.OrdinalIgnoreCase);
        foreach (FollowerFinishSourceArtifact artifact in
                 sourcePlan.Artifacts.Where(artifact =>
                     artifact.InRuntimeArchive))
            expected.Add(artifact.ZipPath, artifact);
        foreach (string document in FollowerFinishRootDocumentPaths)
            expected.Add(document, null);

        var mapped =
            ImmutableArray.CreateBuilder<FollowerFinishMappedArtifact>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            FollowerFinishUtf8);
        if (archive.Entries.Count != expected.Count)
            throw new InvalidDataException(
                "The source ZIP entry closure changed.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateFollowerFinishZipEntry(entry);
            if (!seen.Add(entry.FullName) ||
                !expected.TryGetValue(
                    entry.FullName,
                    out FollowerFinishSourceArtifact? artifact))
            {
                throw new InvalidDataException(
                    $"Source ZIP entry '{entry.FullName}' is undeclared or case-aliased.");
            }
            await using Stream content = entry.Open();
            var sha256 = new Sha256Hash(
                Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        content,
                        cancellationToken)));
            if (artifact is not null &&
                (entry.Length != artifact.ByteLength ||
                 sha256 != artifact.Sha256))
            {
                throw new InvalidDataException(
                    $"Source ZIP entry '{entry.FullName}' changed.");
            }
            mapped.Add(
                new FollowerFinishMappedArtifact(
                    artifact?.Kind ??
                        "source-root-document",
                    entry.FullName,
                    new AssetPath(
                        artifact?.ManifestPath.Value ??
                        entry.FullName),
                    entry.Length,
                    sha256));
        }
        return mapped.ToImmutable();
    }

    private static async ValueTask<Sha256Hash>
        HashFollowerFinishRetainedStreamAsync(
            FileStream stream,
            CancellationToken cancellationToken)
    {
        if (stream.Position != 0)
            throw new InvalidOperationException(
                "A retained source ZIP hash must start at byte zero.");
        return new Sha256Hash(
            Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)));
    }

    private static async ValueTask<WorkspacePath>
        WriteFollowerFinishEvidenceAndManifestAsync(
            SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash proposalSha256,
            FollowerFinishSourcePlan sourcePlan,
            ImmutableArray<FollowerFinishMappedArtifact>
                sourceArtifacts,
            SkyrimFollowerFinishPluginVerification pluginVerification,
            WorkspacePath stageRoot,
            CancellationToken cancellationToken)
    {
        byte[] requestBytes = SerializeRequest(request);
        byte[] proposalBytes = SerializeProposal(proposal);
        byte[] verificationBytes = Serialize(
            FollowerFinishPluginVerificationJson(
                pluginVerification));
        byte[] sourceBindingBytes = Serialize(
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["artifactKind"] =
                    "skyrim-follower-finish-source-package-binding",
                ["sourceZip"] = request.Source.Zip.Value,
                ["sourceZipByteLength"] =
                    request.Source.ZipByteLength,
                ["sourceZipSha256"] =
                    request.Source.ZipSha256.Value,
                ["sourceManifest"] =
                    request.Source.PackageManifest.Value,
                ["sourceManifestSha256"] =
                    request.Source.PackageManifestSha256.Value,
                ["sourcePlugin"] =
                    request.Source.Plugin.Value,
                ["sourcePluginSha256"] =
                    request.Source.PluginSha256.Value,
                ["proposalSha256"] = proposalSha256.Value,
                ["allowedChangedPaths"] = new JsonArray(
                    "Data/" + request.Source.Plugin.Value,
                    FollowerFinishDiagnosticPath(request)),
                ["sourcePayloadMutation"] = false
            });
        byte[] conflictAuditBytes = Serialize(
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["artifactKind"] =
                    "skyrim-follower-finish-world-conflict-audit",
                ["worldspace"] =
                    request.Placement.Worldspace.ToString(),
                ["cell"] = request.Placement.Cell.ToString(),
                ["allowedRawSurface"] =
                    StringArray(proposal.RawGroupTreeSurface),
                ["forbiddenSignatures"] = new JsonArray(
                    "LAND",
                    "WATR",
                    "LTEX",
                    "NAVM",
                    "NAVI",
                    "LCTN",
                    "REGN",
                    "CLMT",
                    "MUSC",
                    "IMGS"),
                ["runtimeAuthority"] = false
            });
        byte[] runtimeBytes = Serialize(
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["artifactKind"] =
                    "skyrim-follower-finish-runtime-test-instructions",
                ["status"] =
                    "STATIC_PASS_RUNTIME_REQUIRED",
                ["outputPlugin"] =
                    request.Source.Plugin.Value,
                ["targetFormId"] =
                    request.NpcFormId.ToString(),
                ["runtimeAuthority"] = false,
                ["requirements"] = new JsonArray(
                    "verify the clicked actor and active plugin provider",
                    "verify active FaceGeom and FaceTint provider hashes",
                    "capture face, neck, body, hands, eyes, hair, and outfit",
                    "retain a known-good control NPC in the same frame")
            });
        byte[][] evidenceBytes =
        [
            requestBytes,
            proposalBytes,
            verificationBytes,
            sourceBindingBytes,
            conflictAuditBytes,
            runtimeBytes
        ];

        var artifacts =
            ImmutableArray.CreateBuilder<FollowerFinishOutputArtifact>();
        foreach (FollowerFinishMappedArtifact source in
                 sourceArtifacts)
        {
            if (IsFollowerFinishDiagnosticSource(
                    request,
                    source.ZipPath))
                continue;
            string path = Path.Combine(
                stageRoot.Value,
                source.OutputPath.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            FileInfo info = new(path);
            if (!info.Exists)
                throw new InvalidDataException(
                    $"Mapped source artifact '{source.OutputPath.Value}' is missing.");
            artifacts.Add(
                new FollowerFinishOutputArtifact(
                    source.Kind,
                    source.OutputPath,
                    info.Length,
                    await HashFollowerFinishFileAsync(
                        path,
                        cancellationToken)));
        }

        for (int index = 0;
             index < FollowerFinishEvidenceDefinitions.Length;
             index++)
        {
            FollowerFinishEvidenceDefinition definition =
                FollowerFinishEvidenceDefinitions[index];
            var relativePath = new AssetPath(definition.Path);
            string path = Path.Combine(
                stageRoot.Value,
                relativePath.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            await WriteNewFollowerFinishFileAsync(
                path,
                evidenceBytes[index],
                cancellationToken);
            artifacts.Add(
                new FollowerFinishOutputArtifact(
                    definition.Kind,
                    relativePath,
                    evidenceBytes[index].LongLength,
                    HashFollowerFinishBytes(
                        evidenceBytes[index])));
        }

        byte[] diagnosticBytes =
            FollowerFinishDiagnosticBytes(request);
        var diagnosticPath = new AssetPath(
            FollowerFinishDiagnosticPath(request));
        string diagnosticFile = Path.Combine(
            stageRoot.Value,
            diagnosticPath.Value.Replace(
                '/',
                Path.DirectorySeparatorChar));
        Directory.CreateDirectory(
            Path.GetDirectoryName(diagnosticFile)!);
        await WriteNewFollowerFinishFileAsync(
            diagnosticFile,
            diagnosticBytes,
            cancellationToken);
        artifacts.Add(
            new FollowerFinishOutputArtifact(
                "runtime-diagnostic",
                diagnosticPath,
                diagnosticBytes.LongLength,
                HashFollowerFinishBytes(
                    diagnosticBytes)));

        byte[] manifestBytes = Serialize(
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["edition"] = sourcePlan.Edition,
                ["presetFormat"] =
                    "skyrim-simple-follower-finish",
                ["sourcePreset"] =
                    FollowerFinishEvidenceDefinitions[0].Path,
                ["sourcePresetSha256"] =
                    HashFollowerFinishBytes(requestBytes).Value,
                ["sourcePlugin"] =
                    request.Source.Plugin.Value,
                ["sourcePluginSha256"] =
                    request.Source.PluginSha256.Value,
                ["outputPlugin"] =
                    request.Source.Plugin.Value,
                ["targetFormId"] =
                    request.NpcFormId.ToString(),
                ["artifacts"] =
                    FollowerFinishArtifactArray(
                        artifacts.ToImmutable())
            });
        var manifestPath = new WorkspacePath(Path.Combine(
            stageRoot.Value,
            "npcmanager-package.json"));
        await WriteNewFollowerFinishFileAsync(
            manifestPath.Value,
            manifestBytes,
            cancellationToken);
        return manifestPath;
    }

    private static async ValueTask<ImmutableArray<Diagnostic>>
        VerifyFollowerFinishPackageClosureAsync(
            SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash expectedProposalSha256,
            FollowerFinishSourcePlan sourcePlan,
            ImmutableArray<FollowerFinishMappedArtifact>
                sourceArtifacts,
            WorkspacePath packageRoot,
            PackageVerifyResult packageVerification,
            SkyrimFollowerFinishPluginVerification
                pluginVerification,
            CancellationToken cancellationToken)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!packageVerification.Verified ||
            packageVerification.Artifact is null ||
            !packageVerification.Artifact.NoUndeclaredFiles ||
            packageVerification.Artifact.RuntimeProof ||
            !string.Equals(
                packageVerification.Artifact.OutputPlugin,
                request.Source.Plugin.Value,
                StringComparison.Ordinal) ||
            packageVerification.Artifact.TargetFormId !=
                request.NpcFormId)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-package-verification",
                    DiagnosticSeverity.Error,
                    "The package verifier did not close the exact static follower-finish package."));
        }

        if (!pluginVerification.Verified ||
            pluginVerification.SourceSha256 !=
                request.Source.PluginSha256 ||
            pluginVerification.OutputSha256 is null ||
            !pluginVerification.ExistingRecordChanges.SequenceEqual(
                proposal.ExistingRecordChanges) ||
            !pluginVerification.NewRecords.SequenceEqual(
                proposal.NewRecords) ||
            pluginVerification.RawGroupTreeSurface.IsDefaultOrEmpty ||
            !FollowerFinishRequiredObservedSignatures.All(
                signature =>
                    pluginVerification.RawGroupTreeSurface.Any(
                        row => row.Contains(
                            signature,
                            StringComparison.Ordinal))) ||
            FollowerFinishForbiddenObservedSignatures.Any(signature =>
                    pluginVerification.RawGroupTreeSurface.Any(
                        row => row.Contains(
                            signature,
                            StringComparison.Ordinal))) ||
            pluginVerification.RuntimeAuthority)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-plugin-verification",
                    DiagnosticSeverity.Error,
                    "The plugin verification evidence does not match the approved proposal."));
        }

        string diagnosticPath =
            FollowerFinishDiagnosticPath(request);
        var expectedFiles = new HashSet<string>(
            sourceArtifacts.Select(artifact =>
                artifact.OutputPath.Value),
            StringComparer.OrdinalIgnoreCase)
        {
            "npcmanager-package.json"
        };
        expectedFiles.UnionWith(
            FollowerFinishEvidenceDefinitions.Select(
                definition => definition.Path));
        expectedFiles.Add(diagnosticPath);
        var actualFiles = new HashSet<string>(
            Directory.EnumerateFiles(
                    packageRoot.Value,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path =>
                    Path.GetRelativePath(
                            packageRoot.Value,
                            path)
                        .Replace(
                            Path.DirectorySeparatorChar,
                            '/')),
            StringComparer.OrdinalIgnoreCase);
        if (!expectedFiles.SetEquals(actualFiles))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-package-closure",
                    DiagnosticSeverity.Error,
                    "The package tree widened or dropped an admitted source/evidence path."));
        }

        foreach (FollowerFinishMappedArtifact source in
                 sourceArtifacts)
        {
            if (string.Equals(
                    source.ZipPath,
                    request.Source.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                IsFollowerFinishDiagnosticSource(
                    request,
                    source.ZipPath))
                continue;
            string path = Path.Combine(
                packageRoot.Value,
                source.OutputPath.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                diagnostics.Add(
                    new Diagnostic(
                        "follower-finish-source-preservation",
                        DiagnosticSeverity.Error,
                        $"Source-preserved path '{source.ZipPath}' is missing."));
                continue;
            }
            FileInfo info = new(path);
            Sha256Hash hash = await HashFollowerFinishFileAsync(
                path,
                cancellationToken);
            if (info.Length != source.ByteLength ||
                hash != source.Sha256)
            {
                diagnostics.Add(
                    new Diagnostic(
                        "follower-finish-source-preservation",
                        DiagnosticSeverity.Error,
                        $"Source-preserved path '{source.ZipPath}' changed."));
            }
        }

        string diagnosticFile = Path.Combine(
            packageRoot.Value,
            diagnosticPath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        byte[] expectedDiagnostic =
            FollowerFinishDiagnosticBytes(request);
        byte[] actualDiagnostic =
            File.Exists(diagnosticFile)
                ? await File.ReadAllBytesAsync(
                    diagnosticFile,
                    cancellationToken)
                : [];
        if (!actualDiagnostic.AsSpan().SequenceEqual(
                expectedDiagnostic))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-diagnostic-binding",
                    DiagnosticSeverity.Error,
                    "The runtime diagnostic is not the exact request-derived generic batch."));
        }

        string outputPlugin = Path.Combine(
            packageRoot.Value,
            "Data",
            request.Source.Plugin.Value);
        if (!File.Exists(outputPlugin) ||
            await HashFollowerFinishFileAsync(
                outputPlugin,
                cancellationToken) !=
                pluginVerification.OutputSha256)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-output-plugin-binding",
                    DiagnosticSeverity.Error,
                    "The output plugin hash differs from its typed verification evidence."));
        }

        string requestEvidence = Path.Combine(
            packageRoot.Value,
            FollowerFinishEvidenceDefinitions[0].Path.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string proposalEvidence = Path.Combine(
            packageRoot.Value,
            FollowerFinishEvidenceDefinitions[1].Path.Replace(
                '/',
                Path.DirectorySeparatorChar));
        byte[] requestEvidenceBytes = File.Exists(requestEvidence)
            ? await File.ReadAllBytesAsync(
                requestEvidence,
                cancellationToken)
            : [];
        byte[] proposalEvidenceBytes = File.Exists(proposalEvidence)
            ? await File.ReadAllBytesAsync(
                proposalEvidence,
                cancellationToken)
            : [];
        if (!File.Exists(requestEvidence) ||
            !File.Exists(proposalEvidence) ||
            !SerializeRequest(request).AsSpan().SequenceEqual(
                requestEvidenceBytes) ||
            !SerializeProposal(proposal).AsSpan().SequenceEqual(
                proposalEvidenceBytes) ||
            HashFollowerFinishBytes(
                proposalEvidenceBytes) != expectedProposalSha256)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-evidence-binding",
                    DiagnosticSeverity.Error,
                    "Request/proposal evidence is not canonically hash-bound."));
        }

        return diagnostics.ToImmutable();
    }

    private static string FollowerFinishDiagnosticPath(
        SkyrimFollowerFinishRequest request) =>
        "Data/diag-" +
        Path.GetFileNameWithoutExtension(
                request.Source.Plugin.Value)
            .ToLowerInvariant() +
        ".txt";

    private static bool IsFollowerFinishDiagnosticSource(
        SkyrimFollowerFinishRequest request,
        string zipPath) =>
        string.Equals(
            zipPath,
            FollowerFinishDiagnosticPath(request)["Data/".Length..],
            StringComparison.OrdinalIgnoreCase);

    private static byte[] FollowerFinishDiagnosticBytes(
        SkyrimFollowerFinishRequest request) =>
        FollowerFinishUtf8.GetBytes(
            string.Join(
                '\n',
                "; NPC Manager follower-finish diagnostic",
                "; status=STATIC_PASS_RUNTIME_REQUIRED",
                "; plugin=" + request.Source.Plugin.Value,
                "; editorId=" + request.NpcEditorId.Value,
                "; formId=" + request.NpcFormId,
                $"help \"{request.NpcEditorId.Value}\" 4",
                string.Empty));

    private static JsonObject FollowerFinishPluginVerificationJson(
        SkyrimFollowerFinishPluginVerification verification) =>
        new()
        {
            ["schemaVersion"] = 1,
            ["artifactKind"] =
                "skyrim-follower-finish-plugin-verification",
            ["verified"] = verification.Verified,
            ["sourceSha256"] =
                verification.SourceSha256?.Value,
            ["outputSha256"] =
                verification.OutputSha256?.Value,
            ["existingRecordChanges"] =
                StringArray(
                    verification.ExistingRecordChanges),
            ["newRecords"] =
                StringArray(verification.NewRecords),
            ["rawGroupTreeSurface"] =
                StringArray(
                    verification.RawGroupTreeSurface),
            ["runtimeAuthority"] =
                verification.RuntimeAuthority,
            ["diagnostics"] =
                DiagnosticArray(verification.Diagnostics)
        };

    private static SkyrimFollowerFinishPluginVerification
        ReadFollowerFinishPluginVerification(
            string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(
            bytes,
            FollowerFinishJsonDocumentOptions);
        JsonElement root = document.RootElement;
        ValidateFollowerFinishDuplicateProperties(root, "$");
        if (ReadFollowerFinishInt(root, "schemaVersion") != 1 ||
            !string.Equals(
                ReadFollowerFinishString(
                    root,
                    "artifactKind"),
                "skyrim-follower-finish-plugin-verification",
                StringComparison.Ordinal) ||
            !ReadFollowerFinishBool(root, "verified"))
        {
            throw new InvalidDataException(
                "Follower-finish verification evidence is not admitted.");
        }

        var sourceSha256 = new Sha256Hash(
            ReadFollowerFinishString(root, "sourceSha256"));
        var outputSha256 = new Sha256Hash(
            ReadFollowerFinishString(root, "outputSha256"));
        ImmutableArray<string> existing =
            ReadFollowerFinishStringArray(
                root,
                "existingRecordChanges");
        ImmutableArray<string> added =
            ReadFollowerFinishStringArray(
                root,
                "newRecords");
        ImmutableArray<string> raw =
            ReadFollowerFinishStringArray(
                root,
                "rawGroupTreeSurface");
        bool runtime =
            ReadFollowerFinishBool(root, "runtimeAuthority");
        return new SkyrimFollowerFinishPluginVerification(
            true,
            sourceSha256,
            outputSha256,
            existing,
            added,
            raw,
            runtime,
            []);
    }

    private static JsonArray FollowerFinishArtifactArray(
        ImmutableArray<FollowerFinishOutputArtifact> artifacts)
    {
        var array = new JsonArray();
        foreach (FollowerFinishOutputArtifact artifact in
                 artifacts.OrderBy(
                     artifact => artifact.Path.Value,
                     StringComparer.Ordinal))
        {
            array.Add(
                new JsonObject
                {
                    ["kind"] = artifact.Kind,
                    ["relativePath"] =
                        artifact.Path.Value,
                    ["byteLength"] =
                        artifact.ByteLength,
                    ["sha256"] =
                        artifact.Sha256.Value.ToLowerInvariant()
                });
        }
        return array;
    }

    private static async ValueTask<byte[]>
        ReadBoundFollowerFinishFileAsync(
            WorkspacePath path,
            long? expectedLength,
            Sha256Hash expectedSha256,
            long maximumLength,
            CancellationToken cancellationToken)
    {
        FileInfo info = new(path.Value);
        if (!info.Exists ||
            info.Length <= 0 ||
            info.Length > maximumLength ||
            (expectedLength is not null &&
             info.Length != expectedLength.Value))
        {
            throw new InvalidDataException(
                $"Hash-bound file '{path.Value}' has an inadmissible length.");
        }
        byte[] bytes = await File.ReadAllBytesAsync(
            path.Value,
            cancellationToken);
        if (HashFollowerFinishBytes(bytes) != expectedSha256)
            throw new InvalidDataException(
                $"Hash-bound file '{path.Value}' changed.");
        return bytes;
    }

    private static async ValueTask<(long Length, Sha256Hash Sha256)>
        ExtractFollowerFinishEntryAsync(
            ZipArchiveEntry entry,
            string outputPath,
            CancellationToken cancellationToken)
    {
        await using Stream source = entry.Open();
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(
                    buffer,
                    cancellationToken)) > 0)
        {
            length += read;
            if (length > MaximumFollowerFinishArtifactBytes)
                throw new InvalidDataException(
                    $"ZIP entry '{entry.FullName}' exceeds the extraction bound.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        if (length != entry.Length)
            throw new InvalidDataException(
                $"ZIP entry '{entry.FullName}' length changed during extraction.");
        return (
            length,
            new Sha256Hash(
                Convert.ToHexString(
                    hash.GetHashAndReset())));
    }

    private static void ValidateFollowerFinishZipEntry(
        ZipArchiveEntry entry)
    {
        string path = entry.FullName;
        if (string.IsNullOrWhiteSpace(path) ||
            path.EndsWith('/') ||
            path.Contains('\\') ||
            path.StartsWith('/') ||
            path.Contains(':') ||
            entry.Length <= 0 ||
            entry.Length > MaximumFollowerFinishArtifactBytes)
        {
            throw new InvalidDataException(
                $"ZIP entry '{path}' is not a bounded no-wrapper file path.");
        }
        _ = new AssetPath(path);
        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000 ||
            (entry.ExternalAttributes &
             (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"ZIP entry '{path}' is a link/reparse entry.");
        }
    }

    private static void ValidateFollowerFinishDuplicateProperties(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (JsonProperty property in
                     element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{path}.{property.Name}' is not admitted.");
                ValidateFollowerFinishDuplicateProperties(
                    property.Value,
                    $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement child in
                     element.EnumerateArray())
            {
                ValidateFollowerFinishDuplicateProperties(
                    child,
                    $"{path}[{index++}]");
            }
        }
    }

    private static string ReadFollowerFinishString(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"JSON property '{property}' must be a nonblank string.");
        }
        return value.GetString()!;
    }

    private static int ReadFollowerFinishInt(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                $"JSON property '{property}' must be a 32-bit integer.");
        }
        return result;
    }

    private static long ReadFollowerFinishLong(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
        {
            throw new InvalidDataException(
                $"JSON property '{property}' must be a 64-bit integer.");
        }
        return result;
    }

    private static bool ReadFollowerFinishBool(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out JsonElement value) ||
            value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"JSON property '{property}' must be a boolean.");
        }
        return value.GetBoolean();
    }

    private static ImmutableArray<string>
        ReadFollowerFinishStringArray(
            JsonElement root,
            string property)
    {
        if (!root.TryGetProperty(
                property,
                out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"JSON property '{property}' must be an array.");
        }
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"JSON property '{property}' contains an invalid string.");
            }
            result.Add(item.GetString()!);
        }
        return result.ToImmutable();
    }

    private static void RequireFollowerFinishKindHash(
        ImmutableArray<FollowerFinishSourceArtifact> artifacts,
        string kind,
        Sha256Hash expected)
    {
        FollowerFinishSourceArtifact[] matches =
            artifacts.Where(artifact =>
                    string.Equals(
                        artifact.Kind,
                        kind,
                        StringComparison.OrdinalIgnoreCase) &&
                    artifact.Sha256 == expected)
                .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"The source manifest {kind} hash binding is missing or ambiguous.");
    }

    private static async ValueTask
        WriteNewFollowerFinishFileAsync(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<Sha256Hash>
        HashFollowerFinishFileAsync(
            string path,
            CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(
                await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)));
    }

    private static Sha256Hash HashFollowerFinishBytes(
        byte[] bytes) =>
        new(
            Convert.ToHexString(
                SHA256.HashData(bytes)));

    private static void ThrowOnFollowerFinishErrors(
        IEnumerable<Diagnostic> diagnostics)
    {
        Diagnostic? error = diagnostics.FirstOrDefault(
            diagnostic =>
                diagnostic.Severity ==
                DiagnosticSeverity.Error);
        if (error is not null)
            throw new InvalidDataException(error.Message);
    }

    private sealed record FollowerFinishSourcePlan(
        byte[] ManifestBytes,
        string Edition,
        string PresetFormat,
        string SourcePreset,
        Sha256Hash SourcePresetSha256,
        string SourcePlugin,
        Sha256Hash SourcePluginSha256,
        string OutputPlugin,
        FormId TargetFormId,
        ImmutableArray<FollowerFinishSourceArtifact> Artifacts);

    private sealed record FollowerFinishSourceArtifact(
        string Kind,
        AssetPath ManifestPath,
        string ZipPath,
        long ByteLength,
        Sha256Hash Sha256,
        bool InRuntimeArchive);

    private sealed record FollowerFinishMappedArtifact(
        string Kind,
        string ZipPath,
        AssetPath OutputPath,
        long ByteLength,
        Sha256Hash Sha256);

    private sealed record FollowerFinishOutputArtifact(
        string Kind,
        AssetPath Path,
        long ByteLength,
        Sha256Hash Sha256);

    private sealed record FollowerFinishEvidenceDefinition(
        string Kind,
        string Path);
}
