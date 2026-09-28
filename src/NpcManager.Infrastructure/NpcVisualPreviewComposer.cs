using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class NpcVisualPreviewComposer(
    INpcVisualSourceComposer sourceComposer,
    INpcVisualPreviewRenderer renderer,
    INpcVisualPreviewVisualValidator visualValidator,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : INpcVisualPreviewComposer
{
    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    public async ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
        NpcVisualPreviewComposeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        Directory.CreateDirectory(request.OutputRoot.Value);
        try
        {
            NpcVisualSourceComposeResult sourceResult =
                await sourceComposer.ComposeSourceAsync(
                    request, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(sourceResult.Diagnostics);
            NpcVisualSourceGraph? source = sourceResult.Source;
            if (!sourceResult.Composed || source is null)
            {
                if (!HasErrors(diagnostics))
                    diagnostics.Add(Error(
                        "npc-preview-source-unavailable",
                        "The selected NPC could not be composed from the reviewed providers."));
                return Refused(diagnostics);
            }
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            if (source.Identity != request.Identity)
            {
                diagnostics.Add(Error(
                    "npc-preview-source-identity-mismatch",
                    "The composed source graph does not belong to the selected NPC."));
                return Refused(diagnostics);
            }
            if (source.Route == NpcVisualPreviewRoute.Ube)
            {
                diagnostics.Add(Error(
                    "npc-preview-runtime-composed-route-unsupported",
                    "UBE preview is refused because the active MDNR/OBody runtime composition cannot yet be reproduced faithfully off-engine."));
                return Refused(diagnostics);
            }
            if (source.Assets.Count(item =>
                    item.Role == NpcVisualAssetRole.FaceGeom) != 1 ||
                source.Assets.Any(item =>
                    item.BakedIntoFaceGeom &&
                    item.Role is NpcVisualAssetRole.Hair or
                        NpcVisualAssetRole.Eyes))
            {
                diagnostics.Add(Error(
                    "npc-preview-facegeom-duplicate",
                    "The final FaceGeom must be the single authoritative head graph; baked headparts may not be imported again."));
                return Refused(diagnostics);
            }

            NpcVisualPreviewRenderResult render =
                await renderer.RenderAsync(
                    new NpcVisualPreviewRenderRequest(
                        NpcVisualPreviewPersistenceContract.SceneSchema,
                        source,
                        request.OutputRoot,
                        request.Options),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(render.Diagnostics);
            if (!render.Rendered ||
                render.ContactSheetPath is null ||
                render.ContactSheetSha256 is null ||
                render.Evidence is null ||
                HasErrors(diagnostics))
                return Refused(diagnostics);
            if (!render.Views.Select(item => item.Id)
                    .SequenceEqual(
                        NpcVisualPreviewPersistenceContract.RequiredViewIds,
                        StringComparer.Ordinal))
            {
                diagnostics.Add(Error(
                    "npc-preview-view-set-invalid",
                    "The renderer did not return the six required views in deterministic order."));
                return Refused(diagnostics);
            }
            foreach (NpcVisualPreviewView view in render.Views)
            {
                await ValidateOutputAsync(
                    view.ImagePath, view.ImageSha256,
                    request.OutputRoot, diagnostics, cancellationToken);
                await ValidateOutputAsync(
                    view.RoleMaskPath, view.RoleMaskSha256,
                    request.OutputRoot, diagnostics, cancellationToken);
                if (view.Width != request.Options.Width ||
                    view.Height != request.Options.Height)
                    diagnostics.Add(Error(
                        "npc-preview-view-dimensions",
                        $"View '{view.Id}' does not match the requested dimensions."));
            }
            await ValidateOutputAsync(
                render.ContactSheetPath.Value,
                render.ContactSheetSha256.Value,
                request.OutputRoot,
                diagnostics,
                cancellationToken);
            await ValidateOutputAsync(
                render.Evidence.StatusPath,
                render.Evidence.StatusSha256,
                request.OutputRoot,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            NpcVisualPreviewVisualEvidence visualEvidence =
                await visualValidator.ValidateAsync(
                    render.Views.Single(view =>
                        view.Id == "face-front"),
                    cancellationToken).ConfigureAwait(false);
            ImmutableArray<Diagnostic> visualDiagnostics =
                visualEvidence.Diagnostics.IsDefaultOrEmpty
                    ? []
                    : visualEvidence.Diagnostics.Select(AsAdvisory)
                        .ToImmutableArray();
            visualEvidence = visualEvidence with
            {
                Diagnostics = visualDiagnostics
            };
            diagnostics.AddRange(visualDiagnostics);
            if (visualEvidence.DetectedFaceCount != 1 ||
                visualEvidence.LandmarkCount != 478 ||
                visualEvidence.SemanticAnchorCount != 31 ||
                !visualEvidence.EyesNoseAndMouthBounded)
            {
                diagnostics.Add(new Diagnostic(
                    "npc-preview-face-visual-check-failed",
                    DiagnosticSeverity.Warning,
                    "The front view did not establish one bounded face with the required eye, nose, mouth, and 31-anchor evidence; the rendered images remain available for inspection."));
            }

            WorkspacePath bundlePath = new(Path.Combine(
                request.OutputRoot.Value, "npc-preview-bundle.json"));
            WorkspacePath hashesPath = new(Path.Combine(
                request.OutputRoot.Value, "npc-preview.hashes.sha256"));
            if (File.Exists(bundlePath.Value) || File.Exists(hashesPath.Value))
            {
                diagnostics.Add(Error(
                    "npc-preview-output-exists",
                    "Preview bundle outputs never overwrite existing files."));
                return Refused(diagnostics);
            }

            var payload = new NpcVisualPreviewPersistenceDocument(
                NpcVisualPreviewPersistenceContract.BundleSchema,
                NpcVisualPreviewPersistenceContract.SceneSchema,
                NpcVisualPreviewPersistenceContract.OffEngineLabel,
                false,
                source,
                render.Views,
                render.ContactSheetPath.Value,
                render.ContactSheetSha256.Value,
                render.Evidence,
                visualEvidence,
                diagnostics.ToImmutable());
            await WriteNewAsync(
                bundlePath,
                JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
                cancellationToken);
            Sha256Hash bundleHash =
                await HashFileAsync(bundlePath, cancellationToken);
            byte[] hashBytes =
                await BuildHashManifestAsync(
                    request.OutputRoot,
                    hashesPath,
                    cancellationToken);
            await WriteNewAsync(hashesPath, hashBytes, cancellationToken);
            Sha256Hash hashesHash =
                await HashFileAsync(hashesPath, cancellationToken);

            return new NpcVisualPreviewComposeResult(
                true,
                new NpcVisualPreviewBundle(
                    NpcVisualPreviewPersistenceContract.BundleSchema,
                    NpcVisualPreviewPersistenceContract.SceneSchema,
                    NpcVisualPreviewPersistenceContract.OffEngineLabel,
                    false,
                    source,
                    render.Views,
                    render.ContactSheetPath.Value,
                    render.ContactSheetSha256.Value,
                    bundlePath,
                    bundleHash,
                    hashesPath,
                    hashesHash,
                    render.Evidence,
                    visualEvidence,
                    diagnostics.ToImmutable()),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            diagnostics.Add(Error(
                "npc-preview-compose-failed", exception.Message));
            return Refused(diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(
        NpcVisualPreviewComposeRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error(
                "npc-preview-edition",
                "NPC visual composition supports a reviewed Skyrim SE/AE intake."));
        if (request.Intake.RuntimeAuthority)
            diagnostics.Add(Error(
                "npc-preview-intake-runtime-authority",
                "A reviewed copied intake cannot claim runtime authority."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(Error(
                "npc-preview-output-outside-workspace",
                "Preview output must remain under the K-local lab root."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        if (Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputRoot.Value))
            diagnostics.Add(Error(
                "npc-preview-output-exists",
                "Preview output roots must be new and never overwrite existing data."));
        string? parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error(
                "npc-preview-output-parent-missing",
                "The preview output parent must already exist."));
        if (request.Options.Width != 900 ||
            request.Options.Height != 900)
            diagnostics.Add(Error(
                "npc-preview-dimensions",
                "High-fidelity NPC review views are fixed at 900×900."));
        return diagnostics.ToImmutable();
    }

    private static async ValueTask ValidateOutputAsync(
        WorkspacePath path,
        Sha256Hash expected,
        WorkspacePath root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!path.IsUnder(root) || !File.Exists(path.Value) ||
            Directory.Exists(path.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-render-output-invalid",
                $"Renderer output '{path}' is missing or escapes the output root."));
            return;
        }
        Sha256Hash observed = await HashFileAsync(path, cancellationToken);
        if (observed != expected)
            diagnostics.Add(Error(
                "npc-preview-render-output-hash",
                $"Renderer output '{path}' changed after rendering."));
    }

    private static async ValueTask<byte[]> BuildHashManifestAsync(
        WorkspacePath root,
        WorkspacePath manifest,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Path, string Hash)>();
        foreach (string file in Directory.EnumerateFiles(
                     root.Value, "*",
                     SearchOption.AllDirectories)
                     .OrderBy(
                         path => path,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = new WorkspacePath(file);
            if (path == manifest)
                continue;
            rows.Add((
                Relative(root, path),
                (await HashFileAsync(
                    path, cancellationToken)).Value));
        }
        string text = string.Join(
            "\n",
            rows.OrderBy(item => item.Path, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Hash.ToLowerInvariant()}  {item.Path}")) + "\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    private static string Relative(WorkspacePath root, WorkspacePath path) =>
        Path.GetRelativePath(root.Value, path.Value).Replace('\\', '/');

    private static async ValueTask WriteNewAsync(
        WorkspacePath destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic AsAdvisory(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error
            ? diagnostic with { Severity = DiagnosticSeverity.Warning }
            : diagnostic;

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        NpcVisualPreviewJson.AddCanonicalDictionaryConverters(options);
        return options;
    }

    private static NpcVisualPreviewComposeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
