using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Resolves the pinned Skyrim _0/_1 body-weight interpolation over an explicit,
/// K-local vertex-pair manifest. NIF/ARMA discovery remains a separate boundary.
/// </summary>
public sealed class SseBodyWeightResolutionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISseBodyWeightResolutionService
{
    private const int MaxBytes = PresetJsonSupport.MaxBytes;
    private const int MaxVertices = 1_000_000;
    private const float MaxCoordinate = 1_000_000F;
    private const float SparseSquaredThreshold = 0.0000001F;

    public async ValueTask<SseBodyWeightResolutionResult> ResolveAsync(
        SseBodyWeightResolutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePath(request.InputPath).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Empty(request, diagnostics.ToImmutable());

        try
        {
            var length = new FileInfo(request.InputPath.Value).Length;
            if (length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("body-weight-size-limit", DiagnosticSeverity.Error,
                    $"Body-weight input exceeds the {MaxBytes} byte safety limit."));
                return Empty(request, diagnostics.ToImmutable());
            }

            var bytes = await File.ReadAllBytesAsync(request.InputPath.Value, cancellationToken);
            if (bytes.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("body-weight-size-limit", DiagnosticSeverity.Error,
                    $"Body-weight input exceeds the {MaxBytes} byte safety limit."));
                return Empty(request, diagnostics.ToImmutable());
            }

            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (!BodyWeightManifestCodec.TryParse(bytes, request.Edition, out var input,
                    diagnostics) || input is null)
                return Empty(request, diagnostics.ToImmutable(), hash);

            if (input.Edition != GameEdition.SkyrimSpecialEdition || request.Edition != GameEdition.SkyrimSpecialEdition)
            {
                diagnostics.Add(new Diagnostic("body-weight-game-unsupported", DiagnosticSeverity.Error,
                    "The Skyrim body-weight resolver accepts skyrimse only."));
                return Empty(request, diagnostics.ToImmutable(), hash, input);
            }

            var sliderEnabled = (input.WeightSliderFlags & 0x02) != 0;
            var clampedWeight = Math.Clamp(input.WeightPercent / 100F, 0F, 1F);
            var channelWeight = input.BaseDigit == 0 ? clampedWeight : 1F - clampedWeight;
            if (!sliderEnabled)
            {
                diagnostics.Add(new Diagnostic("body-weight-slider-disabled", DiagnosticSeverity.Warning,
                    "The selected ARMA gender has no _0/_1 weight-slider flag; no channel was emitted."));
                return Result(request, hash, input, sliderEnabled, false, clampedWeight, channelWeight,
                    ImmutableArray<SseBodyWeightDelta>.Empty, diagnostics.ToImmutable());
            }

            if (input.BaseVertices.Length != input.TwinVertices.Length)
            {
                diagnostics.Add(new Diagnostic("body-weight-vertex-count-mismatch", DiagnosticSeverity.Error,
                    "The base and twin vertex arrays must have the same length."));
                return Result(request, hash, input, sliderEnabled, false, clampedWeight, channelWeight,
                    ImmutableArray<SseBodyWeightDelta>.Empty, diagnostics.ToImmutable());
            }

            var deltas = ImmutableArray.CreateBuilder<SseBodyWeightDelta>();
            for (var index = 0; index < input.BaseVertices.Length; index++)
            {
                var baseVertex = input.BaseVertices[index];
                var twinVertex = input.TwinVertices[index];
                var dx = twinVertex.X - baseVertex.X;
                var dy = twinVertex.Y - baseVertex.Y;
                var dz = twinVertex.Z - baseVertex.Z;
                if (dx * dx + dy * dy + dz * dz < SparseSquaredThreshold) continue;
                deltas.Add(new SseBodyWeightDelta(index, new SseBodyWeightVector(dx, dy, dz)));
            }

            if (deltas.Count == 0)
                diagnostics.Add(new Diagnostic("body-weight-no-deltas", DiagnosticSeverity.Warning,
                    "The base and twin meshes contain no non-zero vertex deltas."));

            return Result(request, hash, input, sliderEnabled, deltas.Count > 0, clampedWeight,
                channelWeight, deltas.ToImmutable(), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("body-weight-read-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("body-weight-read-denied", DiagnosticSeverity.Error, exception.Message));
        }

        return Empty(request, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidatePath(WorkspacePath path)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!path.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("body-weight-outside-lab", DiagnosticSeverity.Error,
                "Body-weight input must remain under the K-only lab root."));
        if (!File.Exists(path.Value))
            diagnostics.Add(new Diagnostic("body-weight-missing", DiagnosticSeverity.Error,
                "Body-weight input does not exist."));
        if (!string.Equals(Path.GetExtension(path.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-weight-extension-invalid", DiagnosticSeverity.Error,
                "Body-weight input must use the .json extension."));
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is not null) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, path.Value);
        return diagnostics.ToImmutable();
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("body-weight-reparse-refused", DiagnosticSeverity.Error,
                        "Body-weight input traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("body-weight-path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("body-weight-path-inspection-denied", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static SseBodyWeightResolutionResult Result(
        SseBodyWeightResolutionRequest request,
        Sha256Hash hash,
        SseBodyWeightInput input,
        bool sliderEnabled,
        bool applied,
        float clampedWeight,
        float channelWeight,
        ImmutableArray<SseBodyWeightDelta> deltas,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(request.Edition, request.InputPath, hash, sliderEnabled, applied, input.Gender,
            input.WeightPercent, clampedWeight, input.BaseDigit, channelWeight,
            applied ? "SseBodyWeight" : null,
            input.BaseVertices.Length, input.TwinVertices.Length, deltas, diagnostics);

    private static SseBodyWeightResolutionResult Empty(
        SseBodyWeightResolutionRequest request,
        ImmutableArray<Diagnostic> diagnostics,
        Sha256Hash? hash = null,
        SseBodyWeightInput? input = null) =>
        new(request.Edition, request.InputPath, hash, false, false, input?.Gender,
            input?.WeightPercent, null, input?.BaseDigit, null, null,
            input?.BaseVertices.Length ?? 0, input?.TwinVertices.Length ?? 0,
            ImmutableArray<SseBodyWeightDelta>.Empty, diagnostics);

    private static class BodyWeightManifestCodec
    {
        internal static bool TryParse(
            byte[] bytes,
            GameEdition requestedEdition,
            out SseBodyWeightInput? input,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            input = null;
            try
            {
                if (!PresetJsonSupport.TryParse(bytes, out var document, out var shapeDiagnostics) || document is null)
                {
                    diagnostics.AddRange(shapeDiagnostics.Select(item => item with { Code = "body-weight-json-invalid" }));
                    return false;
                }

                using (document)
                {
                    var root = document.RootElement;
                    RequireFields(root, ["version", "game", "gender", "weightPercent", "weightSliderFlags", "baseDigit", "baseVertices", "twinVertices"]);
                    var version = ReadInt(root, "version", 1, 1);
                    var gameText = ReadString(root, "game");
                    if (!GameEditionExtensions.TryParseWireName(gameText, out var edition))
                        throw new FormatException("game must be fallout4 or skyrimse.");
                    if (edition != requestedEdition)
                        diagnostics.Add(new Diagnostic("body-weight-game-mismatch", DiagnosticSeverity.Error,
                            "The manifest game does not match --game."));
                    var genderText = ReadString(root, "gender");
                    if (!Enum.TryParse<SseBodyWeightGender>(genderText, ignoreCase: true, out var gender))
                        throw new FormatException("gender must be male or female.");
                    var weightPercent = ReadFiniteProperty(root, "weightPercent", -10_000F, 10_000F);
                    var flags = ReadByte(root, "weightSliderFlags");
                    var baseDigit = ReadInt(root, "baseDigit", 0, 1);
                    var baseVertices = ReadVertices(root.GetProperty("baseVertices"), "baseVertices");
                    var twinVertices = ReadVertices(root.GetProperty("twinVertices"), "twinVertices");
                    input = new SseBodyWeightInput(version, edition, gender, weightPercent, flags,
                        baseDigit, baseVertices, twinVertices);
                    return !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
                }
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new Diagnostic("body-weight-json-invalid", DiagnosticSeverity.Error, exception.Message));
            }
            catch (FormatException exception)
            {
                diagnostics.Add(new Diagnostic("body-weight-format-invalid", DiagnosticSeverity.Error, exception.Message));
            }
            return false;
        }

        private static ImmutableArray<SseBodyWeightVector> ReadVertices(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxVertices)
                throw new FormatException($"{path} must contain at most {MaxVertices} three-number arrays.");
            var result = ImmutableArray.CreateBuilder<SseBodyWeightVector>(element.GetArrayLength());
            foreach (var vertex in element.EnumerateArray())
            {
                if (vertex.ValueKind != JsonValueKind.Array || vertex.GetArrayLength() != 3)
                    throw new FormatException($"{path} entries must be three-number arrays.");
                var values = vertex.EnumerateArray()
                    .Select(value => ReadFinite(value, path, -MaxCoordinate, MaxCoordinate)).ToArray();
                result.Add(new SseBodyWeightVector(values[0], values[1], values[2]));
            }
            return result.ToImmutable();
        }

        private static int ReadInt(JsonElement root, string name, int minimum, int maximum)
        {
            if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) ||
                result < minimum || result > maximum)
                throw new FormatException($"{name} must be an integer from {minimum} to {maximum}.");
            return result;
        }

        private static byte ReadByte(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || !value.TryGetByte(out var result))
                throw new FormatException($"{name} must be an unsigned byte.");
            return result;
        }

        private static float ReadFiniteProperty(JsonElement root, string name, float minimum, float maximum) =>
            !root.TryGetProperty(name, out var value)
                ? throw new FormatException($"{name} is required.")
                : ReadFinite(value, name, minimum, maximum);

        private static float ReadFinite(JsonElement value, string name, float minimum, float maximum)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var result) ||
                !float.IsFinite(result) || result < minimum || result > maximum)
                throw new FormatException($"{name} must be a finite number from {minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
            return result == 0 ? 0 : result;
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new FormatException($"{name} must be a string.");
            var result = value.GetString() ?? string.Empty;
            if (result.Length == 0 || result.Length > 64 || result.Any(char.IsControl))
                throw new FormatException($"{name} must be printable and at most 64 characters.");
            return result;
        }

        private static void RequireFields(JsonElement root, params string[] required)
        {
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Body-weight manifest root must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new FormatException($"Duplicate field '{property.Name}'.");
            foreach (var name in required)
                if (!names.Contains(name)) throw new FormatException($"Required field '{name}' is missing.");
            if (names.Any(name => !required.Contains(name, StringComparer.Ordinal)))
                throw new FormatException("Body-weight manifest contains an unknown field.");
        }
    }
}
