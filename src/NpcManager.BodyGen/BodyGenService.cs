using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.BodyGen;

public sealed class BodyGenService(IWorkspacePolicy policy, WorkspacePath labRoot) : IBodyGenService
{
    private const int MaxInputBytes = 1 * 1024 * 1024;
    private const int MaxMorphs = 512;

    public async ValueTask<BodyGenBuildResult> BuildAsync(BodyGenBuildRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Failed(request, diagnostics.ToImmutable());

        byte[] sourceBytes;
        try
        {
            var info = new FileInfo(request.MorphsPath.Value);
            if (info.Length > MaxInputBytes)
            {
                diagnostics.Add(new Diagnostic("bodygen-input-size-limit", DiagnosticSeverity.Error,
                    $"BodyGen input exceeds the {MaxInputBytes} byte safety limit."));
                return Failed(request, diagnostics.ToImmutable());
            }

            sourceBytes = await File.ReadAllBytesAsync(request.MorphsPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("bodygen-input-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Failed(request, diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("bodygen-input-read-denied", DiagnosticSeverity.Error, exception.Message));
            return Failed(request, diagnostics.ToImmutable());
        }

        var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(sourceBytes)));
        var morphs = ParseMorphs(sourceBytes, request.Edition, diagnostics);
        if (morphs.IsDefaultOrEmpty || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Failed(request, diagnostics.ToImmutable(), sourceHash);

        return await BuildTypedCoreAsync(new BodyGenTypedBuildRequest(request.Edition, request.Plugin,
            request.NpcFormId, request.ModName, morphs, request.OutputRoot), sourceHash, diagnostics, cancellationToken);
    }

    public ValueTask<BodyGenBuildResult> BuildTypedAsync(BodyGenTypedBuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateTypedRequest(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return ValueTask.FromResult(Failed(request, diagnostics.ToImmutable()));
        return BuildTypedCoreAsync(request, null, diagnostics, cancellationToken);
    }

    private static async ValueTask<BodyGenBuildResult> BuildTypedCoreAsync(BodyGenTypedBuildRequest request,
        Sha256Hash? sourceHash, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        var morphs = request.Morphs.Sort(BodyGenMorphComparer.Instance);
        var templateName = BuildTemplateName(request);
        var templateText = BuildTemplate(templateName, morphs);
        var morphText = BuildMorphMapping(request, templateName);
        var relativePaths = RelativePaths(request.Edition, request.Plugin, request.ModName);
        var absolutePaths = relativePaths.Select(path => new WorkspacePath(Path.Combine(
            request.OutputRoot.Value, path.Value.Replace('/', Path.DirectorySeparatorChar)))).ToImmutableArray();

        if (absolutePaths.Any(path => File.Exists(path.Value)))
        {
            diagnostics.Add(new Diagnostic("bodygen-output-exists", DiagnosticSeverity.Error,
                "BodyGen output files already exist; the builder never overwrites an existing destination."));
            return Failed(request, diagnostics.ToImmutable(), sourceHash, templateName);
        }
        if (absolutePaths.Any(path => HasReparsePointInExistingPath(request.OutputRoot.Value, path.Value)))
        {
            diagnostics.Add(new Diagnostic("bodygen-output-reparse-refused", DiagnosticSeverity.Error,
                "BodyGen output paths may not traverse an existing reparse point."));
            return Failed(request, diagnostics.ToImmutable(), sourceHash, templateName);
        }

        var promoted = new List<string>(absolutePaths.Length);
        var temporary = new List<string>(absolutePaths.Length);
        var completed = false;
        try
        {
            for (var index = 0; index < absolutePaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = absolutePaths[index].Value;
                var parent = Path.GetDirectoryName(destination) ?? throw new IOException("BodyGen destination has no parent directory.");
                Directory.CreateDirectory(parent);
                var tempPath = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
                temporary.Add(tempPath);
                await File.WriteAllTextAsync(tempPath, index == 0 ? templateText : morphText,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
                File.Move(tempPath, destination);
                promoted.Add(destination);
            }

            var files = absolutePaths.Select(path => CreateArtifact(path, request.OutputRoot)).ToImmutableArray();
            completed = true;
            return new BodyGenBuildResult(true, request.Edition, request.Plugin, request.NpcFormId, templateName,
                sourceHash, files, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("bodygen-output-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Failed(request, diagnostics.ToImmutable(), sourceHash, templateName);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("bodygen-output-write-denied", DiagnosticSeverity.Error, exception.Message));
            return Failed(request, diagnostics.ToImmutable(), sourceHash, templateName);
        }
        finally
        {
            foreach (var path in temporary) DeleteIfPresent(path);
            if (!completed)
                foreach (var path in promoted) DeleteIfPresent(path);
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(BodyGenBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.NpcFormId.Value == 0)
            diagnostics.Add(new Diagnostic("bodygen-formid-invalid", DiagnosticSeverity.Error, "NPC FormID must be non-zero."));
        if (request.NpcFormId.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("bodygen-formid-load-order-unresolved", DiagnosticSeverity.Error,
                "BodyGen requires a plugin-local 24-bit FormID; resolve any load-order byte before building."));
        if (request.Edition == GameEdition.Fallout4 && !IsSafeSegment(request.ModName))
            diagnostics.Add(new Diagnostic("bodygen-mod-name-invalid", DiagnosticSeverity.Error,
                "Fallout 4 BodyGen mod name must be a single safe non-empty path segment."));
        if (!IsSafePluginName(request.Plugin.Value))
            diagnostics.Add(new Diagnostic("bodygen-plugin-invalid", DiagnosticSeverity.Error,
                "BodyGen plugin name contains a path or INI target delimiter."));
        if (!request.MorphsPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("bodygen-input-outside-lab", DiagnosticSeverity.Error,
                "BodyGen input must remain under the K-only lab root."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("bodygen-output-outside-lab", DiagnosticSeverity.Error,
                "BodyGen output must remain under the K-only lab root."));
        if (!File.Exists(request.MorphsPath.Value))
            diagnostics.Add(new Diagnostic("bodygen-input-missing", DiagnosticSeverity.Error,
                "The explicit BodyGen input file does not exist."));
        else
        {
            try
            {
                if (File.GetAttributes(request.MorphsPath.Value).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("bodygen-input-reparse-refused", DiagnosticSeverity.Error,
                        "BodyGen input files may not be reparse points."));
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("bodygen-input-stat-failed", DiagnosticSeverity.Error, exception.Message)); }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("bodygen-input-stat-denied", DiagnosticSeverity.Error, exception.Message)); }
        }
        var parent = Path.GetDirectoryName(request.MorphsPath.Value);
        if (parent is null)
            diagnostics.Add(new Diagnostic("bodygen-input-parent-invalid", DiagnosticSeverity.Error, "BodyGen input has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateTypedRequest(BodyGenTypedBuildRequest request)
    {
        var diagnostics = ValidateCommon(request.Edition, request.Plugin, request.NpcFormId, request.ModName, request.OutputRoot).ToBuilder();
        if (request.Morphs.IsDefaultOrEmpty || request.Morphs.Length > MaxMorphs)
            diagnostics.Add(new Diagnostic("bodygen-morphs-invalid", DiagnosticSeverity.Error,
                $"BodyGen input morphs must contain 1 to {MaxMorphs} entries."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var morph in request.Morphs)
        {
            if (!IsSafeMorphName(morph.Name) || !IsSupportedMorphValue(request.Edition, morph.Value))
                diagnostics.Add(new Diagnostic("bodygen-morph-invalid", DiagnosticSeverity.Error,
                    $"Morph '{morph.Name}' requires a safe name and a supported finite value."));
            else if (!names.Add(morph.Name))
                diagnostics.Add(new Diagnostic("bodygen-morph-duplicate", DiagnosticSeverity.Error,
                    $"Morph name '{morph.Name}' occurs more than once."));
        }
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateCommon(GameEdition edition, PluginName plugin, FormId npcFormId,
        string modName, WorkspacePath outputRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (npcFormId.Value == 0)
            diagnostics.Add(new Diagnostic("bodygen-formid-invalid", DiagnosticSeverity.Error, "NPC FormID must be non-zero."));
        if (npcFormId.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("bodygen-formid-load-order-unresolved", DiagnosticSeverity.Error,
                "BodyGen requires a plugin-local 24-bit FormID; resolve any load-order byte before building."));
        if (edition == GameEdition.Fallout4 && !IsSafeSegment(modName))
            diagnostics.Add(new Diagnostic("bodygen-mod-name-invalid", DiagnosticSeverity.Error,
                "Fallout 4 BodyGen mod name must be a single safe non-empty path segment."));
        if (!IsSafePluginName(plugin.Value))
            diagnostics.Add(new Diagnostic("bodygen-plugin-invalid", DiagnosticSeverity.Error,
                "BodyGen plugin name contains a path or INI target delimiter."));
        if (!outputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("bodygen-output-outside-lab", DiagnosticSeverity.Error,
                "BodyGen output must remain under the K-only lab root."));
        diagnostics.AddRange(policy.Evaluate(labRoot, outputRoot));
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<BodyGenMorph> ParseMorphs(
        byte[] bytes,
        GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (HasDuplicateKeys(document.RootElement, diagnostics)) return ImmutableArray<BodyGenMorph>.Empty;
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetInt(document.RootElement, "schemaVersion", out var schemaVersion) || schemaVersion != 1)
            {
                diagnostics.Add(new Diagnostic("bodygen-input-schema-unsupported", DiagnosticSeverity.Error,
                    "BodyGen input schemaVersion must be 1."));
                return ImmutableArray<BodyGenMorph>.Empty;
            }
            if (!TryGet(document.RootElement, "morphs", out var morphsElement) || morphsElement.ValueKind != JsonValueKind.Array ||
                morphsElement.GetArrayLength() is 0 or > MaxMorphs)
            {
                diagnostics.Add(new Diagnostic("bodygen-morphs-invalid", DiagnosticSeverity.Error,
                    $"BodyGen input morphs must contain 1 to {MaxMorphs} entries."));
                return ImmutableArray<BodyGenMorph>.Empty;
            }

            var values = ImmutableArray.CreateBuilder<BodyGenMorph>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var element in morphsElement.EnumerateArray())
            {
                var path = $"$.morphs[{index++}]";
                if (element.ValueKind != JsonValueKind.Object || !TryGetString(element, "name", out var name) ||
                    !IsSafeMorphName(name) || !TryGetSingle(element, "value", out var value) ||
                    !IsSupportedMorphValue(edition, value))
                {
                    diagnostics.Add(new Diagnostic("bodygen-morph-invalid", DiagnosticSeverity.Error,
                        $"{path} requires a safe morph name and a supported finite value."));
                    continue;
                }
                if (!names.Add(name))
                {
                    diagnostics.Add(new Diagnostic("bodygen-morph-duplicate", DiagnosticSeverity.Error,
                        $"Morph name '{name}' occurs more than once."));
                    continue;
                }
                values.Add(new BodyGenMorph(name, value == 0 ? 0 : value));
            }
            return values.ToImmutable().Sort(BodyGenMorphComparer.Instance);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("bodygen-input-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return ImmutableArray<BodyGenMorph>.Empty;
        }
    }

    private static string BuildTemplate(string templateName, ImmutableArray<BodyGenMorph> morphs) =>
        $"{templateName}={string.Join(',', morphs.Select(morph => $"{morph.Name}@{FormatValue(morph.Value)}"))}{Environment.NewLine}";

    private static string BuildTemplateName(BodyGenTypedBuildRequest request)
    {
        if (request.EditorId is not null)
            return $"NPCM_{SanitizeTemplateName(request.EditorId.Value.Value)}";

        var fallback = request.NpcFormId.Value.ToString("X8", CultureInfo.InvariantCulture);
        return $"NpcManager_{fallback}";
    }

    private static string BuildMorphMapping(BodyGenTypedBuildRequest request, string templateName)
    {
        var formId = request.NpcFormId.Value.ToString(
            request.Edition == GameEdition.SkyrimSpecialEdition ? "X6" : "X8",
            CultureInfo.InvariantCulture);
        return $"{request.Plugin.Value}|{formId}={templateName}{Environment.NewLine}";
    }

    private static ImmutableArray<AssetPath> RelativePaths(
        GameEdition edition,
        PluginName plugin,
        string modName) => edition switch
        {
            GameEdition.Fallout4 =>
            [new AssetPath($"F4SE/Plugins/F4EE/BodyGen/{modName}/templates.ini"), new AssetPath($"F4SE/Plugins/F4EE/BodyGen/{modName}/morphs.ini")],
            GameEdition.SkyrimSpecialEdition =>
            [new AssetPath($"meshes/actors/character/BodyGenData/{plugin.Value}/templates.ini"), new AssetPath($"meshes/actors/character/BodyGenData/{plugin.Value}/morphs.ini")],
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
        };

    private static BodyGenFileArtifact CreateArtifact(WorkspacePath path, WorkspacePath outputRoot)
    {
        var relative = new AssetPath(Path.GetRelativePath(outputRoot.Value, path.Value));
        var bytes = File.ReadAllBytes(path.Value);
        return new BodyGenFileArtifact(relative, path, bytes.Length,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static string FormatValue(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string SanitizeTemplateName(string value)
    {
        if (string.IsNullOrEmpty(value)) return "Unnamed";
        var characters = value.Select(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'
                ? character
                : '_').ToArray();
        return characters.Length == 0 ? "Unnamed" : new string(characters);
    }

    private static bool IsSafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        value is not "." and not ".." && !value.Any(char.IsControl) &&
        !value.Any(character => "<>:\"/\\|?*".Contains(character)) &&
        !value.EndsWith('.') && !value.EndsWith(' ');

    private static bool IsSafePluginName(string value) => IsSafeSegment(value) &&
        !value.Any(character => "=,@#".Contains(character));

    private static bool IsSafeMorphName(string value) => IsSafeSegment(value) && !value.Any(character => ",|=@:#/\\".Contains(character));

    private static bool IsSupportedMorphValue(GameEdition edition, float value) =>
        float.IsFinite(value) && (edition != GameEdition.Fallout4 || value is >= -1F and <= 1F);

    private static bool HasReparsePointInExistingPath(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private static bool HasDuplicateKeys(JsonElement element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var found = false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    diagnostics.Add(new Diagnostic("bodygen-input-duplicate-key", DiagnosticSeverity.Error,
                        $"Duplicate JSON property '{property.Name}'."));
                    found = true;
                }
                found |= HasDuplicateKeys(property.Value, diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) found |= HasDuplicateKeys(item, diagnostics);
        return found;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { } parsed)
        { value = parsed; return true; }
        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = default;
        return TryGet(element, name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryGetSingle(JsonElement element, string name, out float value)
    {
        value = default;
        return TryGet(element, name, out var property) && property.TryGetSingle(out value);
    }

    private static BodyGenBuildResult Failed(BodyGenBuildRequest request, ImmutableArray<Diagnostic> diagnostics,
        Sha256Hash? sourceHash = null, string? templateName = null) =>
        new(false, request.Edition, request.Plugin, request.NpcFormId, templateName ?? string.Empty, sourceHash,
            ImmutableArray<BodyGenFileArtifact>.Empty, diagnostics);

    private static BodyGenBuildResult Failed(BodyGenTypedBuildRequest request, ImmutableArray<Diagnostic> diagnostics,
        Sha256Hash? sourceHash = null, string? templateName = null) =>
        new(false, request.Edition, request.Plugin, request.NpcFormId, templateName ?? string.Empty, sourceHash,
            ImmutableArray<BodyGenFileArtifact>.Empty, diagnostics);

    private static void DeleteIfPresent(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class BodyGenMorphComparer : IComparer<BodyGenMorph>
    {
        public static BodyGenMorphComparer Instance { get; } = new();

        public int Compare(BodyGenMorph? left, BodyGenMorph? right) =>
            StringComparer.Ordinal.Compare(left?.Name, right?.Name);
    }
}
