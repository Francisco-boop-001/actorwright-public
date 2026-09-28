using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using System.Text.Json;

namespace NpcManager.Presets;

public sealed class PresetService(IWorkspacePolicy policy, WorkspacePath labRoot) :
    IPresetService,
    IPresetCopyService,
    IPresetExactInspectionService
{
    public async ValueTask<PresetParseResult> InspectAsync(PresetParseRequest request, CancellationToken cancellationToken)
    {
        var pathDiagnostics = ValidateSource(request.SourcePath);
        if ((request.Format == PresetFormat.LooksMenu && request.Edition != GameEdition.Fallout4) ||
            (request.Format == PresetFormat.RaceMenuJslot && request.Edition != GameEdition.SkyrimSpecialEdition))
            pathDiagnostics = pathDiagnostics.Add(new Diagnostic("preset-format-edition-mismatch", DiagnosticSeverity.Error,
                "LooksMenu is currently bound to fallout4 and RaceMenu .jslot to skyrimse."));
        if (pathDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetParseResult(null, pathDiagnostics);

        try
        {
            var fileInfo = new FileInfo(request.SourcePath.Value);
            if (fileInfo.Length > PresetJsonSupport.MaxBytes)
                return new PresetParseResult(null, [new Diagnostic("preset-size-limit", DiagnosticSeverity.Error, $"Preset exceeds the {PresetJsonSupport.MaxBytes} byte safety limit.")]);
            var bytes = await File.ReadAllBytesAsync(request.SourcePath.Value, cancellationToken);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            return ParseExact(request, bytes, hash, pathDiagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            return new PresetParseResult(null, pathDiagnostics.Add(new Diagnostic("preset-read-failed", DiagnosticSeverity.Error, exception.Message)));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new PresetParseResult(null, pathDiagnostics.Add(new Diagnostic("preset-read-denied", DiagnosticSeverity.Error, exception.Message)));
        }
    }

    public ValueTask<PresetParseResult> InspectExactAsync(
        PresetParseRequest request,
        ReadOnlyMemory<byte> exactUtf8Json,
        Sha256Hash admittedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray<Diagnostic>.Empty;
        string observedSha256 = Convert.ToHexString(
            SHA256.HashData(exactUtf8Json.Span));
        if (!string.Equals(
                new Sha256Hash(observedSha256).Value,
                admittedSha256.Value,
                StringComparison.Ordinal))
            return ValueTask.FromResult(new PresetParseResult(
                null,
                [new Diagnostic(
                    "preset-exact-bytes-hash-mismatch",
                    DiagnosticSeverity.Error,
                    "The exact preset bytes do not match their admitted SHA-256.")]));
        if ((request.Format == PresetFormat.LooksMenu &&
             request.Edition != GameEdition.Fallout4) ||
            (request.Format == PresetFormat.RaceMenuJslot &&
             request.Edition != GameEdition.SkyrimSpecialEdition))
            diagnostics = diagnostics.Add(new Diagnostic(
                "preset-format-edition-mismatch",
                DiagnosticSeverity.Error,
                "LooksMenu is currently bound to fallout4 and RaceMenu .jslot to skyrimse."));
        return ValueTask.FromResult(
            ParseExact(request, exactUtf8Json, admittedSha256, diagnostics));
    }

    private static PresetParseResult ParseExact(
        PresetParseRequest request,
        ReadOnlyMemory<byte> bytes,
        Sha256Hash hash,
        ImmutableArray<Diagnostic> initialDiagnostics)
    {
        if (!PresetJsonSupport.TryParse(
                bytes,
                out JsonDocument? document,
                out ImmutableArray<Diagnostic> jsonDiagnostics) ||
            document is null)
            return new PresetParseResult(
                null,
                initialDiagnostics.AddRange(jsonDiagnostics));
        using (document)
        {
            PresetDocument parsed = request.Format switch
            {
                PresetFormat.LooksMenu => LooksMenuPresetCodec.Parse(
                    document,
                    request.Edition,
                    hash,
                    initialDiagnostics.AddRange(jsonDiagnostics)),
                PresetFormat.RaceMenuJslot => RaceMenuJslotCodec.Parse(
                    document,
                    request.Edition,
                    hash,
                    initialDiagnostics.AddRange(jsonDiagnostics)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Format,
                    "Unsupported preset format.")
            };
            return new PresetParseResult(parsed, parsed.Diagnostics);
        }
    }

    public async ValueTask<PresetExportResult> ExportAsync(PresetExportRequest request, CancellationToken cancellationToken)
    {
        var source = await InspectAsync(new PresetParseRequest(request.Format, request.Edition, request.SourcePath), cancellationToken);
        if (source.Document is null)
            return new PresetExportResult(false, EmptyDocument(request.Format, request.Edition), null, source.Diagnostics);

        var diagnostics = source.Diagnostics.ToBuilder();
        if (request.Format == PresetFormat.LooksMenu)
            diagnostics.AddRange(LooksMenuPresetCodec.ValidateForWrite(source.Document.Appearance));
        else if (request.Format == PresetFormat.RaceMenuJslot)
            diagnostics.AddRange(RaceMenuJslotCodec.ValidateForWrite(source.Document.Appearance));
        diagnostics.AddRange(ValidateDestination(request.SourcePath, request.DestinationPath));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetExportResult(false, source.Document, null, diagnostics.ToImmutable());

        var bytes = request.Format switch
        {
            PresetFormat.LooksMenu => LooksMenuPresetCodec.Write(source.Document.Appearance),
            PresetFormat.RaceMenuJslot => RaceMenuJslotCodec.Write(source.Document.Appearance),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Format, "Unsupported preset format.")
        };
        var temporary = request.DestinationPath.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            await using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                await handle.FlushAsync(cancellationToken);
            File.Move(temporary, request.DestinationPath.Value, overwrite: false);
            var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            return new PresetExportResult(true, source.Document, outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("preset-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PresetExportResult(false, source.Document, null, diagnostics.ToImmutable());
        }
    }

    public ValueTask<PresetCopyResult> CopyAsync(PresetCopyRequest request, CancellationToken cancellationToken) =>
        new PresetCopyService(this, policy, labRoot).CopyAsync(request, cancellationToken);

    public async ValueTask<PresetDiffResult> DiffAsync(PresetDiffRequest request, CancellationToken cancellationToken)
    {
        var left = await InspectAsync(request.Left, cancellationToken);
        var right = await InspectAsync(request.Right, cancellationToken);
        var diagnostics = left.Diagnostics.AddRange(right.Diagnostics)
            .AddRange(PresetDiffBuilder.BuildDiagnostics(left.Document?.Appearance ?? EmptyDocument(request.Left.Format, request.Left.Edition).Appearance, "left"))
            .AddRange(PresetDiffBuilder.BuildDiagnostics(right.Document?.Appearance ?? EmptyDocument(request.Right.Format, request.Right.Edition).Appearance, "right"));
        var leftDocument = left.Document ?? EmptyDocument(request.Left.Format, request.Left.Edition);
        var rightDocument = right.Document ?? EmptyDocument(request.Right.Format, request.Right.Edition);
        var differences = PresetDiffBuilder.Build(leftDocument.Appearance, rightDocument.Appearance);
        return new PresetDiffResult(leftDocument, rightDocument, differences, diagnostics);
    }

    private ImmutableArray<Diagnostic> ValidateSource(WorkspacePath source)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!source.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preset-input-outside-lab", DiagnosticSeverity.Error, "Preset inputs must remain under the K-only lab root."));
        if (!File.Exists(source.Value)) diagnostics.Add(new Diagnostic("preset-input-missing", DiagnosticSeverity.Error, "The explicit preset input does not exist."));
        var parent = Path.GetDirectoryName(source.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("preset-input-parent-invalid", DiagnosticSeverity.Error, "Preset input has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, source));
        return diagnostics.ToImmutable();
    }

    internal ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath source, WorkspacePath destination)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!destination.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preset-output-outside-lab", DiagnosticSeverity.Error, "Preset outputs must remain under the K-only lab root."));
        if (File.Exists(destination.Value)) diagnostics.Add(new Diagnostic("preset-output-exists", DiagnosticSeverity.Error, "Preset export never overwrites an existing artifact."));
        if (string.Equals(source.Value, destination.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("preset-input-output-same", DiagnosticSeverity.Error, "Preset input and output must be different files."));
        var parent = Path.GetDirectoryName(destination.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("preset-output-parent-missing", DiagnosticSeverity.Error, "Preset output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    internal static PresetDocument EmptyDocument(PresetFormat format, GameEdition edition) =>
        new(format, edition, new PresetAppearance(null, ImmutableArray<PresetHeadPart>.Empty, null, null,
            ImmutableDictionary<string, float>.Empty, ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty, ImmutableArray<float>.Empty, ImmutableArray<PresetTint>.Empty,
            ImmutableArray<PresetOverlay>.Empty, null, new PresetFieldPresence(false, false, false, false, false, false, false, false, false), ImmutableArray<PresetUnknownField>.Empty),
            new Sha256Hash(new string('0', 64)), ImmutableArray<Diagnostic>.Empty);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
