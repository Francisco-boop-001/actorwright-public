using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;

namespace NpcManager.Rendering;

/// <summary>
/// Materializes immutable comparison evidence from an accepted solution. The
/// service rebuilds solved face geometry through the same production
/// plan/evaluator used by FaceGen before invoking the CPU renderer.
/// </summary>
public sealed class ReferencePresetComparisonService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    ISseFaceMorphPlanBuilder planBuilder,
    ISseFaceMorphEvaluator evaluator,
    ReferencePresetCpuRenderer renderer)
    : IReferencePresetComparisonService
{
    private const int ViewSize = 256;
    private const double CustomMorphEpsilon = 0.0001;

    public async ValueTask<ReferencePresetComparisonResult> RenderAsync(
        ReferencePresetComparisonRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        ImmutableArray<ReferenceRenderShape> solvedShapes =
            BuildSolvedShapes(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        string destination = request.OutputRoot.Value;
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null)
        {
            diagnostics.Add(Error("reference-comparison-parent",
                "The comparison root has no parent directory."));
            return Refused(request, diagnostics);
        }
        string temporary = destination + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        var pending = new List<PendingArtifact>();
        try
        {
            Directory.CreateDirectory(temporary);
            Dictionary<ReferenceImageViewRole,
                    ReferencePresetCpuRenderResult> rendered = [];
            foreach (ReferenceOrthographicCamera camera in
                     request.RenderInput.Cameras.OrderBy(item =>
                         (int)item.ViewRole))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReferencePresetCpuRenderResult result = renderer.Render(
                    new ReferencePresetCpuRenderRequest(
                        solvedShapes,
                        request.RenderInput.Textures,
                        camera,
                        ViewSize,
                        ViewSize));
                diagnostics.AddRange(result.Diagnostics);
                if (!result.Accepted ||
                    result.PngSha256 is null ||
                    result.RendererSha256 is null ||
                    result.GeometrySha256 is null ||
                    result.TextureSetSha256 is null ||
                    result.TintSetSha256 is null)
                {
                    diagnostics.Add(Error(
                        "reference-comparison-render",
                        $"The {camera.ViewRole.ToWireName()} comparison view was refused."));
                    break;
                }

                string fileName =
                    camera.ViewRole.ToWireName() + ".png";
                await WriteDurableAsync(
                    Path.Combine(temporary, fileName),
                    result.PngBytes,
                    cancellationToken).ConfigureAwait(false);
                rendered.Add(camera.ViewRole, result);
                Sha256Hash? source = request.Intake!.Images
                    .SingleOrDefault(item =>
                        item.ViewRole == camera.ViewRole)
                    ?.SourceSha256;
                pending.Add(new PendingArtifact(
                    camera.ViewRole,
                    fileName,
                    result.PngSha256.Value,
                    result.RendererSha256.Value,
                    camera.CameraSha256,
                    result.GeometrySha256.Value,
                    result.TextureSetSha256.Value,
                    result.TintSetSha256.Value,
                    source));
            }
            if (HasErrors(diagnostics))
                return RefusedAfterCleanup(
                    request, temporary, diagnostics);

            foreach ((string Side,
                         Func<ReferenceImageViewRole, bool> IsObserved)
                     side in new (string,
                         Func<ReferenceImageViewRole, bool>)[]
                     {
                         ("left", role => role is
                             ReferenceImageViewRole.LeftThreeQuarter or
                             ReferenceImageViewRole.LeftProfile),
                         ("right", role => role is
                             ReferenceImageViewRole.RightThreeQuarter or
                             ReferenceImageViewRole.RightProfile)
                     })
            {
                if (request.ReviewedDesign.Views.Any(item =>
                        side.IsObserved(item.ViewRole)))
                    continue;
                string fileName =
                    side.Side + "-not-observed.json";
                byte[] marker = AbsentSideMarker(side.Side);
                await WriteDurableAsync(
                    Path.Combine(temporary, fileName),
                    ImmutableArray.CreateRange(marker),
                    cancellationToken).ConfigureAwait(false);
                Sha256Hash hash = Hash(marker);
                pending.Add(new PendingArtifact(
                    null, fileName, hash, hash, hash, hash,
                    hash, hash, null));
            }

            byte[] sheet = await BuildComparisonSheetAsync(
                request,
                rendered,
                cancellationToken).ConfigureAwait(false);
            string sheetName = "comparison-sheet.png";
            await WriteDurableAsync(
                Path.Combine(temporary, sheetName),
                ImmutableArray.CreateRange(sheet),
                cancellationToken).ConfigureAwait(false);
            Sha256Hash sheetHash = Hash(sheet);
            Sha256Hash aggregateRenderer =
                Hash(rendered.Values
                    .Select(item => item.RendererSha256!.Value.Value)
                    .OrderBy(item => item, StringComparer.Ordinal));
            Sha256Hash aggregateCamera =
                Hash(request.RenderInput.Cameras
                    .OrderBy(item => (int)item.ViewRole)
                    .Select(item => item.CameraSha256.Value));
            Sha256Hash aggregateGeometry =
                Hash(rendered.Values
                    .Select(item => item.GeometrySha256!.Value.Value)
                    .OrderBy(item => item, StringComparer.Ordinal));
            Sha256Hash aggregateTextures =
                Hash(rendered.Values
                    .Select(item => item.TextureSetSha256!.Value.Value)
                    .OrderBy(item => item, StringComparer.Ordinal));
            Sha256Hash aggregateTints =
                Hash(rendered.Values
                    .Select(item => item.TintSetSha256!.Value.Value)
                    .OrderBy(item => item, StringComparer.Ordinal));
            pending.Add(new PendingArtifact(
                null,
                sheetName,
                sheetHash,
                aggregateRenderer,
                aggregateCamera,
                aggregateGeometry,
                aggregateTextures,
                aggregateTints,
                null));

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, destination);
            ImmutableArray<ReferencePresetComparisonArtifact> artifacts =
                pending.Select(item =>
                        new ReferencePresetComparisonArtifact(
                            item.ViewRole,
                            new AssetPath(
                                "renders/" +
                                item.FileName.Replace(
                                    '\\', '/')),
                            item.ContentSha256,
                            item.RendererSha256,
                            item.CameraSha256,
                            item.GeometrySha256,
                            item.TextureSetSha256,
                            item.TintSetSha256,
                            item.SourceReferenceSha256))
                    .OrderBy(item => Path.GetFileName(item.Path.Value),
                        StringComparer.Ordinal)
                    .ToImmutableArray();
            diagnostics.Add(new Diagnostic(
                "reference-comparison-written",
                DiagnosticSeverity.Info,
                $"Wrote {artifacts.Length} immutable comparison artifacts from solved geometry."));
            return new ReferencePresetComparisonResult(
                artifacts,
                request.SolverResult.Residuals,
                request.SolverResult.Losses,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteOwnDirectory(temporary);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            TryDeleteOwnDirectory(temporary);
            diagnostics.Add(Error(
                "reference-comparison-write",
                exception.Message));
            return Refused(request, diagnostics);
        }
    }

    private void Validate(
        ReferencePresetComparisonRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Snapshot is null ||
            request.Intake is null ||
            !request.SolverResult.Accepted ||
            request.SolverResult.ResultSha256 is null ||
            !request.ReviewedDesign.ReviewAccepted ||
            request.RenderInput.Shapes.IsDefaultOrEmpty ||
            request.RenderInput.Cameras.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("reference-comparison-input",
                "Comparison requires the reviewed design, exact snapshot/intake, accepted solution, shapes, and cameras."));
        }
        if (Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputRoot.Value))
        {
            diagnostics.Add(Error("reference-comparison-output-exists",
                "Comparison output must be an absent path."));
        }
        diagnostics.AddRange(policy.Evaluate(
            labRoot, request.OutputRoot));
        string? parent =
            Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("reference-comparison-parent",
                "Comparison output parent must already exist."));
        }
        if (request.Intake is { } intake)
        {
            foreach (ReviewedReferenceView view in
                     request.ReviewedDesign.Views)
            {
                ReferenceImageAuthority[] images =
                    intake.Images.Where(item =>
                            item.ImageId == view.ImageId &&
                            item.ViewRole == view.ViewRole)
                        .ToArray();
                if (images.Length != 1)
                {
                    diagnostics.Add(Error(
                        "reference-comparison-source-authority",
                        $"Reviewed view '{view.ImageId}' has no unique intake image."));
                }
            }
        }
    }

    private ImmutableArray<ReferenceRenderShape> BuildSolvedShapes(
        ReferencePresetComparisonRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, ReferenceFaceMorphShapeBasis> bases =
            request.Snapshot!.MorphBases.ToDictionary(
                item => ShapeKey(
                    item.NifIdentity, item.ShapeIdentity),
                StringComparer.Ordinal);
        var result =
            ImmutableArray.CreateBuilder<ReferenceRenderShape>(
                request.RenderInput.Shapes.Length);
        var resolvedCustomMorphs = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ReferenceRenderShape shape in request.RenderInput.Shapes)
        {
            if (!bases.TryGetValue(
                    ShapeKey(shape.NifIdentity, shape.ShapeIdentity),
                    out ReferenceFaceMorphShapeBasis? basis))
            {
                result.Add(shape);
                continue;
            }

            SkyrimFaceMorphPlanBuildRequest solved =
                ApplySolution(
                    basis.PlanTemplate,
                    request.SolverResult,
                    shape.NifIdentity,
                    shape.ShapeIdentity);
            SkyrimFaceMorphPlanBuildResult plan =
                planBuilder.Build(solved);
            diagnostics.AddRange(plan.Diagnostics);
            if (!plan.Accepted || plan.Plan is null)
            {
                diagnostics.Add(Error(
                    "reference-comparison-face-plan",
                    $"Solved face plan refused '{shape.NifIdentity}/{shape.ShapeIdentity}'."));
                continue;
            }
            resolvedCustomMorphs.UnionWith(
                plan.Plan.ResolvedCustomMorphNames);
            ImmutableArray<Vector3> sourcePositions =
                shape.SourceRestPositions.IsDefaultOrEmpty
                    ? shape.Positions
                    : shape.SourceRestPositions;
            SkyrimFaceMorphEvaluationResult evaluated =
                evaluator.Evaluate(
                    new SkyrimFaceMorphEvaluationRequest(
                        sourcePositions, plan.Plan));
            diagnostics.AddRange(evaluated.Diagnostics);
            if (!evaluated.Accepted)
            {
                diagnostics.Add(Error(
                    "reference-comparison-face-evaluate",
                    $"Solved face geometry refused '{shape.NifIdentity}/{shape.ShapeIdentity}'."));
                continue;
            }
            ImmutableArray<Vector3> renderPositions =
                shape.SourceRestPositions.IsDefaultOrEmpty
                    ? evaluated.Positions
                    : evaluated.Positions
                        .Select(shape.RenderPlacement.TransformPoint)
                        .ToImmutableArray();
            result.Add(shape with
            {
                Positions = renderPositions,
                GeometrySha256 = HashPositions(renderPositions)
            });
        }
        foreach ((string name, double value) in
                 request.SolverResult.CustomMorphs)
        {
            if (Math.Abs(value) >= CustomMorphEpsilon &&
                !resolvedCustomMorphs.Contains(name))
            {
                diagnostics.Add(Error(
                    "reference-comparison-custom-unresolved",
                    $"Solved RaceMenu morph '{name}' has no TRI geometry on any selected headpart shape."));
            }
        }
        return result.ToImmutable();
    }

    private static SkyrimFaceMorphPlanBuildRequest ApplySolution(
        SkyrimFaceMorphPlanBuildRequest template,
        ReferenceRaceMenuPresetSolverResult solution,
        string nifIdentity,
        string shapeIdentity)
    {
        float[] nam9 = new float[18];
        uint[] nama = Enumerable.Repeat(
                uint.MaxValue, 4)
            .ToArray();
        foreach ((string name, double value) in
                 solution.NativeMorphs)
        {
            if (TryParseIndexed(name, "NAM9", 18,
                    out int nam9Index))
                nam9[nam9Index] = checked((float)value);
            else if (TryParseIndexed(name, "NAMA", 4,
                         out int namaIndex))
                nama[namaIndex] = checked((uint)Math.Round(
                    value, MidpointRounding.AwayFromZero));
        }
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> custom =
            solution.CustomMorphs
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item =>
                    new SkyrimRaceMenuCustomMorphValue(
                        item.Key, checked((float)item.Value)))
                .ToImmutableArray();
        ImmutableArray<RaceMenuSculptPart> sculpt = [];
        if (!solution.Sculpt.IsEmpty &&
            (solution.SculptNifIdentity.Length == 0 ||
             solution.SculptNifIdentity == nifIdentity) &&
            (solution.SculptShapeIdentity.Length == 0 ||
             solution.SculptShapeIdentity == shapeIdentity))
        {
            string host = solution.SculptHost.Length > 0
                ? solution.SculptHost
                : ResolveSculptHost(template);
            if (host.Length > 0)
            {
                sculpt =
                [
                    new RaceMenuSculptPart(
                        host,
                        template.VertexCount,
                        solution.Sculpt.Select(item =>
                                new RaceMenuSculptVertex(
                                    item.VertexIndex,
                                    checked((float)item.X),
                                    checked((float)item.Y),
                                    checked((float)item.Z)))
                            .ToImmutableArray(),
                        true,
                        true)
                ];
            }
        }
        return template with
        {
            NativeMorphs = new SkyrimFaceMorphSnapshot(
                ImmutableArray.CreateRange(nam9),
                float.MaxValue,
                ImmutableArray.CreateRange(nama),
                true,
                true),
            CustomMorphs = custom,
            SculptParts = sculpt,
            RequireAllCustomMorphs = false
        };
    }

    private static string ResolveSculptHost(
        SkyrimFaceMorphPlanBuildRequest template) =>
        template.ChargenMorphTri?.Document.SourcePath.Value ??
        template.RaceMorphTri?.Document.SourcePath.Value ??
        template.MeshMorphTri?.Document.SourcePath.Value ??
        string.Empty;

    private static async Task<byte[]> BuildComparisonSheetAsync(
        ReferencePresetComparisonRequest request,
        Dictionary<ReferenceImageViewRole,
            ReferencePresetCpuRenderResult> rendered,
        CancellationToken cancellationToken)
    {
        ReviewedReferenceView[] views =
            request.ReviewedDesign.Views
                .OrderBy(item => (int)item.ViewRole)
                .ToArray();
        int width = ViewSize * 3;
        int height = Math.Max(ViewSize, ViewSize * views.Length);
        byte[] sheet = new byte[checked(width * height * 4)];
        Fill(sheet, 20, 20, 20, 255);
        for (var row = 0; row < views.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReviewedReferenceView view = views[row];
            ReferenceImageAuthority image = request.Intake!.Images
                .Single(item =>
                    item.ImageId == view.ImageId &&
                    item.ViewRole == view.ViewRole);
            byte[] sourceBytes = await File.ReadAllBytesAsync(
                image.SourcePath.Value,
                cancellationToken).ConfigureAwait(false);
            if (Hash(sourceBytes) != image.SourceSha256)
                throw new InvalidDataException(
                    $"Source image '{view.ImageId}' changed after intake.");
            byte[] source = DecodeSquare(sourceBytes);
            Blit(source, ViewSize, ViewSize,
                sheet, width, height, 0, row * ViewSize);
            if (rendered.TryGetValue(
                    view.ViewRole,
                    out ReferencePresetCpuRenderResult? render))
            {
                Blit(render.CanonicalRgba.ToArray(),
                    ViewSize, ViewSize,
                    sheet, width, height,
                    ViewSize, row * ViewSize);
            }
            DrawResidualPanel(
                sheet, width, height,
                ViewSize * 2, row * ViewSize,
                request.SolverResult.Residuals.Where(item =>
                    item.ViewRole == view.ViewRole),
                request.SolverResult.Losses);
        }
        return ReferencePresetCpuRenderer.EncodeCanonicalPng(
            width, height, sheet);
    }

    private static byte[] DecodeSquare(byte[] sourceBytes)
    {
        using SKBitmap? bitmap = SKBitmap.Decode(sourceBytes);
        if (bitmap is null ||
            bitmap.Width <= 0 ||
            bitmap.Height <= 0)
            throw new InvalidDataException(
                "A source comparison image could not be decoded.");
        byte[] target = new byte[ViewSize * ViewSize * 4];
        Fill(target, 8, 8, 8, 255);
        double scale = Math.Min(
            (double)ViewSize / bitmap.Width,
            (double)ViewSize / bitmap.Height);
        int drawWidth = Math.Max(
            1, (int)Math.Round(bitmap.Width * scale));
        int drawHeight = Math.Max(
            1, (int)Math.Round(bitmap.Height * scale));
        int originX = (ViewSize - drawWidth) / 2;
        int originY = (ViewSize - drawHeight) / 2;
        for (var y = 0; y < drawHeight; y++)
        {
            int sourceY = Math.Clamp(
                (int)Math.Floor(
                    (y + 0.5) * bitmap.Height / drawHeight),
                0, bitmap.Height - 1);
            for (var x = 0; x < drawWidth; x++)
            {
                int sourceX = Math.Clamp(
                    (int)Math.Floor(
                        (x + 0.5) * bitmap.Width / drawWidth),
                    0, bitmap.Width - 1);
                SKColor color = bitmap.GetPixel(sourceX, sourceY);
                int targetOffset =
                    ((originY + y) * ViewSize +
                     originX + x) * 4;
                target[targetOffset] = color.Red;
                target[targetOffset + 1] = color.Green;
                target[targetOffset + 2] = color.Blue;
                target[targetOffset + 3] = color.Alpha;
            }
        }
        return target;
    }

    private static void DrawResidualPanel(
        byte[] target,
        int width,
        int height,
        int originX,
        int originY,
        IEnumerable<ReferencePresetResidual> residuals,
        ImmutableArray<ReferencePresetLoss> losses)
    {
        FillRectangle(target, width, height,
            originX, originY, ViewSize, ViewSize,
            12, 12, 12, 255);
        DrawLine(target, width, height,
            originX + ViewSize / 2, originY,
            originX + ViewSize / 2, originY + ViewSize - 1,
            56, 56, 56, 255);
        DrawLine(target, width, height,
            originX, originY + ViewSize / 2,
            originX + ViewSize - 1, originY + ViewSize / 2,
            56, 56, 56, 255);
        foreach (ReferencePresetResidual residual in residuals
                     .OrderBy(item => (int)item.Anchor))
        {
            int targetX = originX + Math.Clamp(
                (int)Math.Round(residual.TargetX *
                    (ViewSize - 1)), 0, ViewSize - 1);
            int targetY = originY + Math.Clamp(
                (int)Math.Round(residual.TargetY *
                    (ViewSize - 1)), 0, ViewSize - 1);
            int actualX = originX + Math.Clamp(
                (int)Math.Round(residual.ActualX *
                    (ViewSize - 1)), 0, ViewSize - 1);
            int actualY = originY + Math.Clamp(
                (int)Math.Round(residual.ActualY *
                    (ViewSize - 1)), 0, ViewSize - 1);
            DrawLine(target, width, height,
                targetX, targetY, actualX, actualY,
                255, 196, 0, 255);
            Plot(target, width, height,
                targetX, targetY, 0, 220, 120, 255);
            Plot(target, width, height,
                actualX, actualY, 255, 72, 72, 255);
        }
        int lossOrdinal = 0;
        foreach (ReferencePresetLoss loss in losses
                     .OrderBy(item => item.Code,
                         StringComparer.Ordinal))
        {
            int y = originY + ViewSize - 1 -
                    Math.Min(lossOrdinal, 15) * 3;
            byte red = loss.Acknowledged ? (byte)48 : (byte)220;
            byte green = loss.Acknowledged ? (byte)180 : (byte)52;
            DrawLine(target, width, height,
                originX, y,
                originX + ViewSize - 1, y,
                red, green, 64, 255);
            lossOrdinal++;
        }
    }

    private static void DrawLine(
        byte[] target,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            Plot(target, width, height,
                x0, y0, red, green, blue, alpha);
            if (x0 == x1 && y0 == y1)
                break;
            int twice = error * 2;
            if (twice >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (twice <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static void FillRectangle(
        byte[] target,
        int width,
        int height,
        int originX,
        int originY,
        int rectangleWidth,
        int rectangleHeight,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        for (var y = originY;
             y < Math.Min(height, originY + rectangleHeight);
             y++)
        {
            for (var x = originX;
                 x < Math.Min(width, originX + rectangleWidth);
                 x++)
                Plot(target, width, height, x, y,
                    red, green, blue, alpha);
        }
    }

    private static void Plot(
        byte[] target,
        int width,
        int height,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
            return;
        int offset = (y * width + x) * 4;
        target[offset] = red;
        target[offset + 1] = green;
        target[offset + 2] = blue;
        target[offset + 3] = alpha;
    }

    private static void Fill(
        byte[] target,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        for (var offset = 0;
             offset < target.Length;
             offset += 4)
        {
            target[offset] = red;
            target[offset + 1] = green;
            target[offset + 2] = blue;
            target[offset + 3] = alpha;
        }
    }

    private static void Blit(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        byte[] target,
        int targetWidth,
        int targetHeight,
        int originX,
        int originY)
    {
        for (var y = 0;
             y < sourceHeight && originY + y < targetHeight;
             y++)
        {
            source.AsSpan(y * sourceWidth * 4,
                    sourceWidth * 4)
                .CopyTo(target.AsSpan(
                    ((originY + y) * targetWidth + originX) * 4,
                    sourceWidth * 4));
        }
    }

    private static byte[] AbsentSideMarker(
        string side)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("side", side);
            writer.WriteString("status", "not-observed");
            writer.WriteString("meaning",
                "No reviewed source image exists for this side; the comparison does not infer symmetry.");
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool TryParseIndexed(
        string name,
        string prefix,
        int count,
        out int index)
    {
        index = -1;
        if (!name.StartsWith(prefix + "[",
                StringComparison.Ordinal) ||
            !name.EndsWith(']'))
            return false;
        ReadOnlySpan<char> value = name.AsSpan(
            prefix.Length + 1,
            name.Length - prefix.Length - 2);
        return int.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out index) &&
               index >= 0 &&
               index < count;
    }

    private static Sha256Hash HashPositions(
        ImmutableArray<Vector3> positions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (Vector3 position in positions)
        {
            writer.Write(position.X);
            writer.Write(position.Y);
            writer.Write(position.Z);
        }
        return Hash(stream.ToArray());
    }

    private static async Task WriteDurableAsync(
        string path,
        ImmutableArray<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65_536,
            FileOptions.Asynchronous |
            FileOptions.WriteThrough);
        await stream.WriteAsync(
            bytes.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash Hash(
        IEnumerable<string> values)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            hash.AppendData(
                System.Text.Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static string ShapeKey(
        string nifIdentity,
        string shapeIdentity) =>
        nifIdentity + "\0" + shapeIdentity;

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferencePresetComparisonResult Refused(
        ReferencePresetComparisonRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new([], request.SolverResult.Residuals,
            request.SolverResult.Losses,
            diagnostics.ToImmutable());

    private static ReferencePresetComparisonResult RefusedAfterCleanup(
        ReferencePresetComparisonRequest request,
        string temporary,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        TryDeleteOwnDirectory(temporary);
        return Refused(request, diagnostics);
    }

    private static void TryDeleteOwnDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            // The primary refusal remains authoritative; a uniquely named
            // unpromoted temporary directory is never treated as output.
        }
    }

    private sealed record PendingArtifact(
        ReferenceImageViewRole? ViewRole,
        string FileName,
        Sha256Hash ContentSha256,
        Sha256Hash RendererSha256,
        Sha256Hash CameraSha256,
        Sha256Hash GeometrySha256,
        Sha256Hash TextureSetSha256,
        Sha256Hash TintSetSha256,
        Sha256Hash? SourceReferenceSha256);
}
