using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Validates and optionally writes the typed CharGen/FaceGen options artifact.</summary>
public sealed class FaceGenOptionsService(IWorkspacePolicy policy, WorkspacePath labRoot) :
    IFaceGenOptionsService,
    IFaceGenOptionsDocumentWriter
{
    private const string SchemaVersion = "1";
    private const long MaxFileBytes = 1L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new GameEditionJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenOptionsResult> ValidateAsync(
        FaceGenOptionsRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Input));
        if (!string.Equals(Path.GetExtension(request.Input.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-options-extension", DiagnosticSeverity.Error,
                "CharGen options must use the .json extension."));
        if (!File.Exists(request.Input.Value))
            diagnostics.Add(new Diagnostic("facegen-options-input-missing", DiagnosticSeverity.Error,
                "The CharGen options input file does not exist."));
        else if (HasReparsePath(request.Input.Value, labRoot.Value))
            diagnostics.Add(new Diagnostic("facegen-options-input-reparse", DiagnosticSeverity.Error,
                "The CharGen options input or one of its ancestors is a reparse point."));
        if (request.Apply)
            ValidateOutput(request.Output, diagnostics);
        if (HasErrors(diagnostics)) return Refused(request, diagnostics);

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(request.Input.Value, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facegen-options-read-failed", DiagnosticSeverity.Error,
                $"The CharGen options input could not be read: {exception.Message}"));
            return Refused(request, diagnostics);
        }
        if (bytes.LongLength > MaxFileBytes)
            diagnostics.Add(new Diagnostic("facegen-options-size-limit", DiagnosticSeverity.Error,
                $"The CharGen options input exceeds the {MaxFileBytes} byte safety limit."));
        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        if (request.ExpectedSha256 is not null && request.ExpectedSha256.Value != inputHash)
            diagnostics.Add(new Diagnostic("facegen-options-stale-input", DiagnosticSeverity.Error,
                "The CharGen options input SHA-256 does not match --expected-sha256."));
        if (HasErrors(diagnostics)) return Refused(request, diagnostics, inputHash);

        CharGenOptions? options;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            options = document.RootElement.Deserialize<CharGenOptions>(JsonOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-options-json-invalid", DiagnosticSeverity.Error,
                $"CharGen options JSON is invalid: {exception.Message}"));
            return Refused(request, diagnostics, inputHash);
        }
        catch (NotSupportedException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-options-json-unsupported", DiagnosticSeverity.Error,
                $"CharGen options JSON uses an unsupported value: {exception.Message}"));
            return Refused(request, diagnostics, inputHash);
        }
        if (options is null)
            diagnostics.Add(new Diagnostic("facegen-options-root", DiagnosticSeverity.Error,
                "CharGen options JSON must contain an object."));
        else
            ValidateOptions(options, request.Edition, diagnostics);
        if (HasErrors(diagnostics) || options is null)
            return Refused(request, diagnostics, inputHash, options);

        if (!request.Apply)
            return new FaceGenOptionsResult(true, false, request.Edition, request.Input, request.Output,
                inputHash, null, options, diagnostics.ToImmutable());

        var canonical = Serialize(options);
        try
        {
            WriteAtomically(request.Output!.Value, canonical);
            var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(canonical)));
            return new FaceGenOptionsResult(true, true, request.Edition, request.Input, request.Output,
                inputHash, outputHash, options, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("facegen-options-write-failed", DiagnosticSeverity.Error,
                $"The canonical CharGen options artifact could not be written: {exception.Message}"));
            return Refused(request, diagnostics, inputHash, options);
        }
    }

    public async ValueTask<FaceGenOptionsDocumentWriteResult> WriteAsync(
        FaceGenOptionsDocumentWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        CharGenOptions? acceptedOptions = request.Options;
        if (acceptedOptions is null)
        {
            diagnostics.Add(new Diagnostic(
                "facegen-options-document-required",
                DiagnosticSeverity.Error,
                "A complete accepted CharGen options document is required."));
        }
        else
        {
            ValidateOptions(acceptedOptions, request.Edition, diagnostics);
        }
        ValidateOutput(request.Output, diagnostics);
        if (HasErrors(diagnostics) || acceptedOptions is null)
        {
            return RefusedWrite(request, diagnostics);
        }

        byte[] canonical = Serialize(acceptedOptions);
        var outputHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(canonical)));
        try
        {
            WriteAtomically(request.Output, canonical);
            FaceGenOptionsResult reopened = await ValidateAsync(
                new FaceGenOptionsRequest(
                    request.Edition,
                    request.Output,
                    null,
                    outputHash,
                    false),
                CancellationToken.None).ConfigureAwait(false);
            diagnostics.AddRange(reopened.Diagnostics);
            bool exact = reopened.IsValid && reopened.Options is not null &&
                         Serialize(reopened.Options).AsSpan()
                             .SequenceEqual(canonical);
            if (!exact)
            {
                diagnostics.Add(new Diagnostic(
                    "facegen-options-document-readback",
                    DiagnosticSeverity.Error,
                    "The promoted CharGen options artifact did not reopen as the exact accepted canonical document."));
                DeleteOwnedOutput(request.Output, diagnostics);
                return RefusedWrite(request, diagnostics);
            }

            return new FaceGenOptionsDocumentWriteResult(
                true,
                true,
                request.Edition,
                request.Output,
                outputHash,
                reopened.Options,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            DeleteOwnedOutput(request.Output, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(new Diagnostic(
                "facegen-options-document-write-failed",
                DiagnosticSeverity.Error,
                $"The canonical accepted CharGen options document could not be written: {exception.Message}"));
            DeleteOwnedOutput(request.Output, diagnostics);
            return RefusedWrite(request, diagnostics);
        }
    }

    private void ValidateOutput(WorkspacePath? output, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (output is null)
        {
            diagnostics.Add(new Diagnostic("facegen-options-output-required", DiagnosticSeverity.Error,
                "--apply requires --output <path>."));
            return;
        }
        var outputParent = Path.GetDirectoryName(output.Value.Value);
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent!)));
        if (outputParent is not null && HasReparsePath(outputParent, labRoot.Value))
            diagnostics.Add(new Diagnostic("facegen-options-output-reparse", DiagnosticSeverity.Error,
                "The CharGen options output directory or one of its ancestors is a reparse point."));
        if (!Directory.Exists(outputParent))
            diagnostics.Add(new Diagnostic("facegen-options-output-parent-missing", DiagnosticSeverity.Error,
                "The CharGen options output directory must already exist."));
        if (File.Exists(output.Value.Value))
            diagnostics.Add(new Diagnostic("facegen-options-output-exists", DiagnosticSeverity.Error,
                "The CharGen options output path already exists; writes never overwrite artifacts."));
        if (!string.Equals(Path.GetExtension(output.Value.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegen-options-output-extension", DiagnosticSeverity.Error,
                "CharGen options output must use the .json extension."));
    }

    private static void ValidateOptions(CharGenOptions options, GameEdition requestedEdition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(options.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("facegen-options-schema", DiagnosticSeverity.Error,
                $"Unsupported CharGen options schema '{options.SchemaVersion}'."));
        if (options.Edition != requestedEdition)
            diagnostics.Add(new Diagnostic("facegen-options-game-mismatch", DiagnosticSeverity.Error,
                "The options game does not match --game|--edition."));
        if (options.Edition == GameEdition.Fallout4 && options.BakeSseRaceMenuOverlays)
            diagnostics.Add(new Diagnostic("facegen-options-sse-only", DiagnosticSeverity.Error,
                "BakeSseRaceMenuOverlays is only supported for Skyrim SE."));
        if (options.Edition == GameEdition.SkyrimSpecialEdition &&
            (options.ApplyGhoulHeadRearFix || options.ApplyEyebrowsFixedColor || options.ApplyMouthVanillaFix))
            diagnostics.Add(new Diagnostic("facegen-options-fo4-only", DiagnosticSeverity.Error,
                "Ghoul head-rear, eyebrow fixed-color, and mouth vanilla fixes are Fallout 4-only."));
        if (!Enum.IsDefined(options.DiffuseResolution) || !Enum.IsDefined(options.NormalResolution) ||
            !Enum.IsDefined(options.SpecularResolution))
            diagnostics.Add(new Diagnostic("facegen-options-resolution", DiagnosticSeverity.Error,
                "FaceGen channel resolutions must be inherit, 512, 1024, 2048, 4096, or 8192."));
        if (!options.PerLayerResolution &&
            (options.NormalResolution != options.DiffuseResolution || options.SpecularResolution != options.DiffuseResolution))
            diagnostics.Add(new Diagnostic("facegen-options-all-resolution", DiagnosticSeverity.Error,
                "All resolution mode requires Normal and Specular to follow Diffuse."));
        if (!options.PerLayerResolution)
        {
            var expectedNormal = options.Edition == GameEdition.SkyrimSpecialEdition
                ? FaceGenNormalSpecularCompression.Uncompressed
                : EffectiveNsCompression(options.DiffuseCompression);
            var expectedSpecular = EffectiveNsCompression(options.DiffuseCompression);
            if (options.NormalCompression != expectedNormal || options.SpecularCompression != expectedSpecular)
                diagnostics.Add(new Diagnostic("facegen-options-all-compression", DiagnosticSeverity.Error,
                    "All compression mode requires game-aware Normal/Specular derivation from Diffuse."));
        }
        if (options.Edition == GameEdition.SkyrimSpecialEdition && options.PerLayerResolution &&
            (options.SpecularResolution != FaceGenChannelResolution.Inherit ||
             options.SpecularCompression != FaceGenNormalSpecularCompression.Bc5))
            diagnostics.Add(new Diagnostic("facegen-options-sse-specular", DiagnosticSeverity.Error,
                "Skyrim SE does not bake a Specular channel; its resolution must remain inherit."));
        ValidateConvention(options.Convention, options.Edition, diagnostics);
        ValidateSort(options.TintSort, options.Edition, diagnostics);
    }

    private static FaceGenNormalSpecularCompression EffectiveNsCompression(FaceGenDiffuseCompression diffuse) =>
        diffuse == FaceGenDiffuseCompression.Uncompressed
            ? FaceGenNormalSpecularCompression.Uncompressed
            : FaceGenNormalSpecularCompression.Bc5;

    private static void ValidateConvention(FaceTintConventionSettings? convention, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (convention is null || convention.Diffuse is null || convention.NormalSpecular is null || convention.Swap is null ||
            convention.DiffuseWorkingSpaceByBlend is null)
        {
            diagnostics.Add(new Diagnostic("facegen-options-convention-null", DiagnosticSeverity.Error,
                "FaceTint convention buckets and blend working-space settings are required."));
            return;
        }
        if (convention.SeedConstant.IsDefaultOrEmpty || convention.SeedConstant.Length != 3 ||
            convention.SeedConstant.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
            diagnostics.Add(new Diagnostic("facegen-options-seed-constant", DiagnosticSeverity.Error,
                "SeedConstant must contain exactly three finite values in the range 0..1."));
        ValidateBucket(convention.Diffuse, "diffuse", diagnostics);
        ValidateBucket(convention.NormalSpecular, "normalSpecular", diagnostics);
        ValidateBucket(convention.Swap, "swap", diagnostics);
        ValidateBlendWorkingSpaces(convention.DiffuseWorkingSpaceByBlend, diagnostics);
        if (edition == GameEdition.SkyrimSpecialEdition && convention.Diffuse.MaskChannel != FaceTintMaskChannel.R)
            diagnostics.Add(new Diagnostic("facegen-options-sse-mask-channel", DiagnosticSeverity.Error,
                "Skyrim SE diffuse convention must use the red mask channel."));
    }

    private static void ValidateBucket(FaceTintBucketConvention bucket, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(bucket.WorkingSpace) || !Enum.IsDefined(bucket.CompositeSpace) ||
            !Enum.IsDefined(bucket.SourceSpace) || !Enum.IsDefined(bucket.OutputSpace) ||
            !Enum.IsDefined(bucket.MaskConversion) || !Enum.IsDefined(bucket.Framework) ||
            !Enum.IsDefined(bucket.SoftLight) || !Enum.IsDefined(bucket.MaskChannel))
            diagnostics.Add(new Diagnostic("facegen-options-convention-value", DiagnosticSeverity.Error,
                $"FaceTint convention bucket '{name}' contains an unsupported enum value."));
    }

    private static void ValidateBlendWorkingSpaces(FaceTintBlendWorkingSpaces spaces,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(spaces.Replace) || !Enum.IsDefined(spaces.Multiply) ||
            !Enum.IsDefined(spaces.Overlay) || !Enum.IsDefined(spaces.SoftLight) ||
            !Enum.IsDefined(spaces.HardLight))
            diagnostics.Add(new Diagnostic("facegen-options-blend-working-space", DiagnosticSeverity.Error,
                "Diffuse blend working-space settings contain an unsupported enum value."));
    }

    private static void ValidateSort(FaceTintSortSettings? sort, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sort is null || sort.TintRules.IsDefault || sort.SwapRules.IsDefault)
        {
            diagnostics.Add(new Diagnostic("facegen-options-sort-null", DiagnosticSeverity.Error,
                "Tint order settings and both rule arrays are required."));
            return;
        }
        var tintMax = edition == GameEdition.Fallout4 ? 13 : 4;
        var swapMax = edition == GameEdition.Fallout4 ? 5 : 2;
        ValidateRules(sort.TintRules, tintMax, "tint", diagnostics);
        ValidateRules(sort.SwapRules, swapMax, "swap", diagnostics);
        if (!Enum.IsDefined(sort.SkinTonePlacement))
            diagnostics.Add(new Diagnostic("facegen-options-sort-placement", DiagnosticSeverity.Error,
                "SkinTonePlacement must be positional, firstOfAll, or lastOfAll."));
    }

    private static void ValidateRules(ImmutableArray<FaceTintSortRule> rules, int max, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<int>();
        foreach (var rule in rules)
        {
            if (rule is null || rule.Key < (name == "tint" && max == 13 ? 1 : 0) || rule.Key > max || !seen.Add(rule.Key))
                diagnostics.Add(new Diagnostic("facegen-options-sort-rule", DiagnosticSeverity.Error,
                    $"{name} tint-order rules must use unique keys in the supported range."));
        }
    }

    private static byte[] Serialize(CharGenOptions options) =>
        new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(options, JsonOptions) + Environment.NewLine);

    private static void WriteAtomically(WorkspacePath output, byte[] bytes)
    {
        var temporary = output.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, output.Value, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    diagnostics.Add(new Diagnostic("facegen-options-duplicate-key", DiagnosticSeverity.Error,
                        $"Duplicate JSON property '{path}.{property.Name}'."));
                ValidateDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
                ValidateDuplicateProperties(child, $"{path}[{index++}]", diagnostics);
        }
    }

    private static FaceGenOptionsResult Refused(FaceGenOptionsRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? inputHash = null, CharGenOptions? options = null) =>
        new(false, false, request.Edition, request.Input, request.Output, inputHash, null, options, diagnostics.ToImmutable());

    private static FaceGenOptionsDocumentWriteResult RefusedWrite(
        FaceGenOptionsDocumentWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, request.Edition, request.Output, null, null,
            diagnostics.ToImmutable());

    private static void DeleteOwnedOutput(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(output.Value)) File.Delete(output.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                "facegen-options-document-rollback-failed",
                DiagnosticSeverity.Error,
                $"An invalid accepted-options output could not be removed: {exception.Message}"));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static bool HasReparsePath(string path, string root)
    {
        var current = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length >= normalizedRoot.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return false;
    }
}

file sealed class GameEditionJsonConverter : JsonConverter<GameEdition>
{
    public override GameEdition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !GameEditionExtensions.TryParseWireName(reader.GetString() ?? string.Empty, out var edition))
            throw new JsonException("Game edition must be fallout4 or skyrimse.");
        return edition;
    }

    public override void Write(Utf8JsonWriter writer, GameEdition value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireName());
}
