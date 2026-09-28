using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Creates and independently reopens a no-wrapper install archive from the
/// Data payload of an already verified NPC Manager package.
/// </summary>
public sealed partial class PackageArchiveService(
    IPackageVerifyService verifier,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IPackageArchiveService
{
    private const int MaximumEntryCount = 10_000;
    private const long MaximumCandidateProfileBytes = 4L * 1024 * 1024;
    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly HashSet<string> StandardRootDocuments =
        new(
        [
            "BUILD_INFO.txt",
            "README-NPCMANAGER-RUNTIME-TEST.txt",
            "RUNTIME-TEST-INSTRUCTIONS.md"
        ],
        StringComparer.OrdinalIgnoreCase);

    public async ValueTask<PackageArchiveResult> ArchiveAsync(
        PackageArchiveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        var manifestPath = new WorkspacePath(
            Path.Combine(request.SourceRoot.Value, "npcmanager-package.json"));
        var verification = await verifier.VerifyAsync(
            new PackageVerifyRequest(manifestPath), cancellationToken);
        diagnostics.AddRange(verification.Diagnostics);
        if (!verification.Verified || verification.Artifact is null)
            return Refused(diagnostics);

        var verified = verification.Artifact;
        if (!string.Equals(
                Path.GetFullPath(verified.ManifestPath.Value),
                Path.GetFullPath(manifestPath.Value),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-manifest-root",
                DiagnosticSeverity.Error,
                "The source manifest must be the root npcmanager-package.json file."));
            return Refused(diagnostics);
        }

        var planned = PlanEntries(verified, request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        var temporaryArchive = request.OutputArchive.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var writtenEntries = await WriteArchiveAsync(
                request.SourceRoot, temporaryArchive, planned, diagnostics, cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            var temporaryReadback = await ReopenAndVerifyAsync(
                temporaryArchive, writtenEntries, verified.OutputPlugin, diagnostics, cancellationToken, request.Payload);
            if (!temporaryReadback || HasErrors(diagnostics))
                return Refused(diagnostics);

            var preMoveHash = await HashFileAsync(temporaryArchive, cancellationToken);
            File.Move(temporaryArchive, request.OutputArchive.Value);
            temporaryArchive = string.Empty;

            var finalReadback = await ReopenAndVerifyAsync(
                request.OutputArchive.Value,
                writtenEntries,
                verified.OutputPlugin,
                diagnostics,
                CancellationToken.None, request.Payload);
            var finalHash = await HashFileAsync(
                request.OutputArchive.Value,
                CancellationToken.None);
            if (!string.Equals(preMoveHash.Value, finalHash.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-move-mismatch",
                    DiagnosticSeverity.Error,
                    "The archive hash changed during final placement."));
            }

            if (!finalReadback || HasErrors(diagnostics))
                return RefusedAfterFinalWrite(
                    request.OutputArchive.Value,
                    preMoveHash,
                    diagnostics);

            var artifact = new PackageArchiveArtifact(
                "1",
                "npcmanager-install-archive",
                request.SourceRoot,
                verified.ManifestPath,
                verified.ManifestSha256,
                request.OutputArchive,
                finalHash,
                writtenEntries,
                ForwardSlashEntries: true,
                NoWrapperDirectory: true,
                IndependentlyReopened: true,
                NoWriteToSource: true,
                RuntimeProof: false)
            { Payload = request.Payload == PackageArchivePayload.RuntimeOnly ? "runtime-only" : null };
            return new PackageArchiveResult(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-invalid-data",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-write-failed",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-write-denied",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
        }

        return Refused(diagnostics);
    }

    private void ValidateRequest(
        PackageArchiveRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(request.Payload) ||
            request.IncludeRuntimePreset is { } preset &&
            (request.Payload != PackageArchivePayload.RuntimeOnly || !IsRuntimePreset(preset.Value)))
            diagnostics.Add(new Diagnostic("package-archive-payload", DiagnosticSeverity.Error,
                "Payload must be full or runtime-only; an explicit runtime preset requires its canonical SKSE/F4SE preset path and runtime-only payload."));
        if (!request.SourceRoot.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-source-outside-lab",
                DiagnosticSeverity.Error,
                "The archive source root must remain under the K-only lab root."));
        }

        if (!Directory.Exists(request.SourceRoot.Value))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-source-missing",
                DiagnosticSeverity.Error,
                "The archive source root does not exist."));
        }
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourceRoot));
        }

        if (!request.OutputArchive.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-output-outside-lab",
                DiagnosticSeverity.Error,
                "The archive output must remain under the K-only lab root."));
        }

        if (!string.Equals(
                Path.GetExtension(request.OutputArchive.Value),
                ".zip",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-output-extension",
                DiagnosticSeverity.Error,
                "The archive output must use the .zip extension."));
        }

        if (request.OutputArchive.IsUnder(request.SourceRoot))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-root-overlap",
                DiagnosticSeverity.Error,
                "The archive output may not be written below its source package."));
        }

        if (File.Exists(request.OutputArchive.Value) ||
            Directory.Exists(request.OutputArchive.Value))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-output-exists",
                DiagnosticSeverity.Error,
                "Package archives never overwrite an existing output."));
        }

        var parent = Path.GetDirectoryName(request.OutputArchive.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-parent-missing",
                DiagnosticSeverity.Error,
                "The archive output parent must already exist."));
        }
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        }
    }

    private static ImmutableArray<PlannedEntry> PlanEntries(
        PackageVerificationArtifact verified,
        PackageArchiveRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var planned = ImmutableArray.CreateBuilder<PlannedEntry>();
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long runtimeBytes = 0;
        var declaredRootDocuments =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool runtimeOnly = request.Payload == PackageArchivePayload.RuntimeOnly;
        var intentionallyNaked = !runtimeOnly && ReadIntentionallyNakedProfile(verified, diagnostics);
        foreach (var file in verified.Files
                     .OrderBy(item => item.RelativePath.Value, StringComparer.Ordinal))
        {
            if (!file.Matches)
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-source-mismatch",
                    DiagnosticSeverity.Error,
                    $"Verified source file '{file.RelativePath.Value}' does not match its manifest."));
                continue;
            }

            if (!runtimeOnly && StandardRootDocuments.Contains(file.RelativePath.Value))
            {
                if (!archivePaths.Add(file.RelativePath.Value))
                {
                    diagnostics.Add(new Diagnostic(
                        "package-archive-entry-collision",
                        DiagnosticSeverity.Error,
                        $"Archive path '{file.RelativePath.Value}' is duplicated by a declared root document."));
                    continue;
                }

                declaredRootDocuments.Add(file.RelativePath.Value);
                planned.Add(new PlannedEntry(
                    file.Kind,
                    file.RelativePath,
                    file.RelativePath,
                    null,
                    file.ExpectedByteLength,
                    file.ExpectedSha256));
                continue;
            }

            if (!file.RelativePath.Value.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
                continue;

            var archivePath = new AssetPath(file.RelativePath.Value["Data/".Length..]);
            string kind = file.Kind;
            if (runtimeOnly)
            {
                if (!TryRuntimeKind(archivePath.Value, request.IncludeRuntimePreset, out kind)) continue;
                if (file.ExpectedByteLength > MaximumRuntimeEntryBytes || runtimeBytes > MaximumRuntimePayloadBytes - file.ExpectedByteLength)
                {
                    diagnostics.Add(new Diagnostic("package-runtime-entry-size", DiagnosticSeverity.Error,
                        $"Runtime file '{archivePath}' has {file.ExpectedByteLength} bytes; per-file limit is {MaximumRuntimeEntryBytes}, total limit {MaximumRuntimePayloadBytes} (prior total {runtimeBytes})."));
                    continue;
                }
                runtimeBytes += file.ExpectedByteLength;
            }
            if (!archivePaths.Add(archivePath.Value))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-entry-collision",
                    DiagnosticSeverity.Error,
                    $"Archive path '{archivePath.Value}' is duplicated after Data-root mapping."));
                continue;
            }

            planned.Add(new PlannedEntry(
                kind,
                archivePath,
                file.RelativePath,
                null,
                file.ExpectedByteLength,
                file.ExpectedSha256));
        }

        if (planned.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-data-payload-missing",
                DiagnosticSeverity.Error,
                "The verified package contains no declared files below Data/."));
        }

        var outputPlugin = planned.Where(item =>
                string.Equals(item.ArchivePath.Value, verified.OutputPlugin, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!string.Equals(Path.GetFileName(verified.OutputPlugin), verified.OutputPlugin, StringComparison.Ordinal) ||
            outputPlugin.Length != 1 ||
            !string.Equals(outputPlugin[0].Kind, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-plugin-root",
                DiagnosticSeverity.Error,
                "The declared output plugin must map to exactly one plugin entry at archive root."));
        }

        if (request.IncludeRuntimePreset is { } selectedPreset &&
            !archivePaths.Contains(selectedPreset.Value))
            diagnostics.Add(new Diagnostic("package-archive-runtime-preset-missing", DiagnosticSeverity.Error,
                $"Explicit runtime preset '{selectedPreset}' is not a declared verified Data payload file."));

        foreach (var generated in runtimeOnly ? [] : CreateGeneratedEntries(verified, intentionallyNaked))
        {
            if (declaredRootDocuments.Contains(generated.ArchivePath.Value))
                continue;
            if (!archivePaths.Add(generated.ArchivePath.Value))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-entry-collision",
                    DiagnosticSeverity.Error,
                    $"Generated archive path '{generated.ArchivePath.Value}' collides with package payload."));
                continue;
            }

            planned.Add(generated);
        }

        if (planned.Count > MaximumEntryCount)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-entry-count",
                DiagnosticSeverity.Error,
                $"An install archive may contain at most {MaximumEntryCount} files."));
        }

        return planned
            .OrderBy(item => item.ArchivePath.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool ReadIntentionallyNakedProfile(
        PackageVerificationArtifact verified,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var proposals = verified.Files
            .Where(item => string.Equals(
                item.Kind,
                "npc-creation-proposal",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (proposals.Length == 0)
            return false;
        if (proposals.Length != 1)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-ambiguous",
                DiagnosticSeverity.Error,
                "Candidate-aware runtime instructions require exactly one declared NPC creation proposal."));
            return false;
        }

        var proposal = proposals[0];
        var sourceRoot = Path.GetDirectoryName(verified.ManifestPath.Value)!;
        var path = Path.GetFullPath(Path.Combine(
            sourceRoot,
            proposal.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(path, sourceRoot))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-outside-root",
                DiagnosticSeverity.Error,
                "The declared NPC creation proposal escaped the verified package root."));
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumCandidateProfileBytes)
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-proposal-size",
                    DiagnosticSeverity.Error,
                    $"Candidate-aware proposal evidence must be between 1 byte and {MaximumCandidateProfileBytes} bytes."));
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.LongLength != proposal.ExpectedByteLength ||
                !string.Equals(hash, proposal.ExpectedSha256.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-proposal-changed",
                    DiagnosticSeverity.Error,
                    "The NPC creation proposal changed after package verification."));
                return false;
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("artifactKind", out var artifactKind) ||
                artifactKind.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    artifactKind.GetString(),
                    "skyrim-npc-creation-proposal",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("references", out var references) ||
                references.ValueKind != JsonValueKind.Object ||
                !references.TryGetProperty("defaultOutfit", out var defaultOutfit))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-proposal-contract",
                    DiagnosticSeverity.Error,
                    "The declared NPC creation proposal does not expose the required Skyrim default-outfit contract."));
                return false;
            }

            if (defaultOutfit.ValueKind == JsonValueKind.Null)
                return true;
            if (defaultOutfit.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(defaultOutfit.GetString()))
            {
                return false;
            }

            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-contract",
                DiagnosticSeverity.Error,
                "The declared default outfit must be a nonblank FormReference string or null."));
            return false;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-json",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-read",
                DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-proposal-read-denied",
                DiagnosticSeverity.Error,
                exception.Message));
        }

        return false;
    }

    private static ImmutableArray<PlannedEntry> CreateGeneratedEntries(
        PackageVerificationArtifact verified,
        bool intentionallyNaked)
    {
        var diagnosticBatch = verified.Files
            .Where(item => item.RelativePath.Value.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RelativePath.Value["Data/".Length..])
            .FirstOrDefault(path =>
                Path.GetFileName(path).StartsWith("diag-", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase));
        var batchCommand = diagnosticBatch is null
            ? "use the diagnostic batch included by the package, if present."
            : $"run bat {Path.GetFileNameWithoutExtension(diagnosticBatch)} in the Skyrim console.";

        var buildInfo = string.Join('\n',
        [
            "NPC Manager install archive",
            "schemaVersion=1",
            "status=STATIC_PASS_RUNTIME_REQUIRED",
            $"edition={verified.Edition}",
            $"outputPlugin={verified.OutputPlugin}",
            $"targetFormId={verified.TargetFormId}",
            $"sourceManifestSha256={verified.ManifestSha256.Value}",
            $"defaultOutfit={(intentionallyNaked ? "none-intentional" : "candidate-defined")}",
            "archiveAuthor=NPC Manager",
            "sourcePayloadMutation=false",
            "runtimeProof=false",
            ""
        ]);
        var readme = intentionallyNaked
            ? string.Join('\n',
            [
                "NPC Manager runtime-test candidate",
                "",
                $"Plugin: {verified.OutputPlugin}",
                $"Target FormID: {verified.TargetFormId}",
                "Status: STATIC_PASS_RUNTIME_REQUIRED",
                "",
                "Install this ZIP as a new mod in MO2. Replace the previous test candidate; do not stack versions.",
                "This candidate intentionally has no default outfit. The untouched naked actor is the visual acceptance target.",
                "This archive has passed static source verification and archive readback only.",
                "It does not prove Skyrim rendering, provider precedence, follower behavior, or visual likeness.",
                "",
                "Take the initial naked face-and-neck screenshot before changing weight, equipping clothing, or running a diagnostic batch.",
                "Do not run the included diagnostic batch unless a later investigation specifically calls for it.",
                ""
            ])
            : string.Join('\n',
            [
                "NPC Manager runtime-test candidate",
                "",
                $"Plugin: {verified.OutputPlugin}",
                $"Target FormID: {verified.TargetFormId}",
                "Status: STATIC_PASS_RUNTIME_REQUIRED",
                "",
                "Install this ZIP as a new mod in MO2. Replace the previous test candidate; do not stack versions.",
                "This archive has passed static source verification and archive readback only.",
                "It does not prove Skyrim rendering, provider precedence, follower behavior, or visual likeness.",
                "",
                "Capture the untouched actor before running the included diagnostic batch.",
                $"Afterward, on a throwaway save only, {batchCommand}",
                "The diagnostic batch is separate evidence because setnpcweight mutates the actor.",
                ""
            ]);
        var runtime = intentionallyNaked
            ? string.Join('\n',
            [
                "# NPC Manager runtime test — intentionally naked candidate",
                "",
                "Primary question: does this exact Manager package render the untouched naked NPC without a visible neck seam?",
                "",
                "1. Snapshot the live profile environment fingerprint before installing.",
                "2. In MO2, install this ZIP as a new mod and replace—not stack—any earlier candidate for the same NPC.",
                $"3. Enable {verified.OutputPlugin}; prefer a new game or a clean test save.",
                "4. Leave Face Discoloration Fix in its normal profile state.",
                "5. Resolve and place the NPC, select it in the console, and confirm the clicked actor/FormID and active plugin, FaceGeom, and FaceTint providers match this package.",
                "6. Capture the untouched naked actor: face, neck, body, hands, eyes, and hair. One clear normal-light screenshot is sufficient to reject a visible neck seam.",
                "7. Keep a known-good control NPC in the same frame if the screenshot will be used for color or seam acceptance; without it, the shot can still reject gross geometry.",
                "8. Do not equip clothing, change weight, or run the included diagnostic batch before the initial screenshot.",
                "9. Stop after that initial screenshot and report the result. Diagnostics and follower-behavior checks are follow-up work only after the naked visual baseline passes.",
                "",
                "Do not promote this candidate from STATIC_PASS_RUNTIME_REQUIRED without retained runtime evidence.",
                ""
            ])
            : string.Join('\n',
            [
                "# NPC Manager runtime test",
                "",
                "Primary question: does this exact Manager package load, render the intended NPC coherently, and support the claimed follower behavior?",
                "",
                "1. Snapshot the live profile environment fingerprint before installing.",
                "2. In MO2, install this ZIP as a new mod and replace—not stack—any earlier candidate for the same NPC.",
                $"3. Enable {verified.OutputPlugin}; prefer a new game or a clean test save.",
                "4. Leave Face Discoloration Fix in its normal profile state unless a later diagnostic explicitly tests that layer.",
                "5. Resolve and place the NPC, select it in the console, and confirm the clicked actor/FormID and active plugin, FaceGeom, and FaceTint providers match this package.",
                "6. Capture the untouched actor before running any diagnostic batch: face, neck, body, hands, eyes, hair, and outfit in normal and alternate lighting.",
                "7. Keep a known-good control NPC in the same frame for every visual-judgment screenshot.",
                $"8. After the untouched captures, on a throwaway save only, {batchCommand} The included setnpcweight diagnostic can change neck geometry; judge it separately from the initial render.",
                "9. Capture the post-diagnostic state separately, including the expression and disable/enable probes.",
                "10. Exercise recruit, follow, wait, dismiss, combat, and cell-transition behavior.",
                "",
                "Do not promote this candidate from STATIC_PASS_RUNTIME_REQUIRED without retained runtime evidence.",
                ""
            ]);

        return
        [
            Generated("runtime-build-info", "BUILD_INFO.txt", buildInfo),
            Generated("runtime-readme", "README-NPCMANAGER-RUNTIME-TEST.txt", readme),
            Generated("runtime-instructions", "RUNTIME-TEST-INSTRUCTIONS.md", runtime)
        ];
    }

    private static PlannedEntry Generated(string kind, string archivePath, string content)
    {
        var bytes = Utf8NoBom.GetBytes(content);
        return new PlannedEntry(
            kind,
            new AssetPath(archivePath),
            null,
            bytes,
            bytes.LongLength,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static async ValueTask<ImmutableArray<PackageArchiveEntry>> WriteArchiveAsync(
        WorkspacePath sourceRoot,
        string temporaryArchive,
        ImmutableArray<PlannedEntry> planned,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var written = ImmutableArray.CreateBuilder<PackageArchiveEntry>(planned.Length);
        await using var output = new FileStream(
            temporaryArchive,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Utf8NoBom))
        {
            foreach (var item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(item.ArchivePath.Value, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicTimestamp;
                Sha256Hash actualHash;
                long actualLength;
                await using (var destination = entry.Open())
                {
                    if (item.GeneratedBytes is not null)
                    {
                        await destination.WriteAsync(item.GeneratedBytes, cancellationToken);
                        actualLength = item.GeneratedBytes.LongLength;
                        actualHash = new Sha256Hash(
                            Convert.ToHexString(SHA256.HashData(item.GeneratedBytes)));
                    }
                    else
                    {
                        var sourcePath = Path.GetFullPath(Path.Combine(
                            sourceRoot.Value,
                            item.SourceRelativePath!.Value.Value.Replace(
                                '/', Path.DirectorySeparatorChar)));
                        if (!IsUnder(sourcePath, sourceRoot.Value))
                        {
                            diagnostics.Add(new Diagnostic(
                                "package-archive-source-path-escape",
                                DiagnosticSeverity.Error,
                                $"Source payload '{item.SourceRelativePath.Value.Value}' escaped its package root."));
                            return [];
                        }

                        (actualLength, actualHash) = await CopyAndHashAsync(
                            sourcePath, destination, cancellationToken);
                    }
                }

                if (actualLength != item.ByteLength ||
                    !string.Equals(actualHash.Value, item.Sha256.Value, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new Diagnostic(
                        "package-archive-source-changed",
                        DiagnosticSeverity.Error,
                        $"Source payload for '{item.ArchivePath.Value}' changed during archive creation."));
                    return [];
                }

                written.Add(new PackageArchiveEntry(
                    item.Kind,
                    item.ArchivePath,
                    item.SourceRelativePath,
                    actualLength,
                    actualHash));
            }
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return written.ToImmutable();
    }

    private static async ValueTask<bool> ReopenAndVerifyAsync(
        string archivePath,
        ImmutableArray<PackageArchiveEntry> expected,
        string outputPlugin,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken,
        PackageArchivePayload payload = PackageArchivePayload.Full)
    {
        await using var input = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false, Utf8NoBom);
        if (archive.Entries.Count != expected.Length)
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-readback-count",
                DiagnosticSeverity.Error,
                "The reopened archive entry count differs from the written plan."));
            return false;
        }

        var expectedByPath = expected.ToDictionary(
            item => item.ArchivePath.Value,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') ||
                entry.FullName.Contains('\\') ||
                entry.FullName.StartsWith('/') ||
                entry.FullName.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-readback-layout",
                    DiagnosticSeverity.Error,
                    $"Archive entry '{entry.FullName}' is not a no-wrapper install file path."));
                continue;
            }

            try
            {
                _ = new AssetPath(entry.FullName);
            }
            catch (ArgumentException)
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-readback-path",
                    DiagnosticSeverity.Error,
                    $"Archive entry '{entry.FullName}' is not traversal-safe."));
                continue;
            }

            if (!seen.Add(entry.FullName) || !expectedByPath.TryGetValue(entry.FullName, out var planned))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-readback-entry",
                    DiagnosticSeverity.Error,
                    $"Archive entry '{entry.FullName}' is duplicate or undeclared."));
                continue;
            }

            await using var content = entry.Open();
            var hash = new Sha256Hash(
                Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken)));
            if (entry.Length != planned.ByteLength ||
                !string.Equals(hash.Value, planned.Sha256.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "package-archive-readback-mismatch",
                    DiagnosticSeverity.Error,
                    $"Archive entry '{entry.FullName}' failed size/hash readback."));
            }
        }

        if (seen.Count != expected.Length ||
            !seen.Contains(outputPlugin) ||
            payload == PackageArchivePayload.Full &&
            (!seen.Contains("BUILD_INFO.txt") ||
             !seen.Contains("README-NPCMANAGER-RUNTIME-TEST.txt") ||
             !seen.Contains("RUNTIME-TEST-INSTRUCTIONS.md")))
        {
            diagnostics.Add(new Diagnostic(
                "package-archive-readback-required-entry",
                DiagnosticSeverity.Error,
                "The reopened archive does not contain every required root entry."));
        }

        return !HasErrors(diagnostics);
    }

    private static async ValueTask<(long Length, Sha256Hash Hash)> CopyAndHashAsync(
        string sourcePath,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return (length, new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset())));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        return new Sha256Hash(
            Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PackageArchiveResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static PackageArchiveResult RefusedAfterFinalWrite(
        string archivePath,
        Sha256Hash ownedSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        TryDeleteOwnedFile(
            archivePath,
            ownedSha256,
            diagnostics);
        return Refused(diagnostics);
    }

    private static void TryDeleteOwnedFile(
        string path,
        Sha256Hash ownedSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!File.Exists(path))
                return;
            Sha256Hash actual;
            using (var stream = File.OpenRead(path))
                actual = new Sha256Hash(
                    Convert.ToHexString(SHA256.HashData(stream)));
            if (actual != ownedSha256)
            {
                diagnostics.Add(
                    new Diagnostic(
                        "package-archive-cleanup-ownership",
                        DiagnosticSeverity.Error,
                        "The final archive no longer has the transaction-owned fingerprint and was preserved."));
                return;
            }
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "package-archive-cleanup-failed",
                    DiagnosticSeverity.Error,
                    exception.Message));
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PlannedEntry(
        string Kind,
        AssetPath ArchivePath,
        AssetPath? SourceRelativePath,
        byte[]? GeneratedBytes,
        long ByteLength,
        Sha256Hash Sha256);
}
