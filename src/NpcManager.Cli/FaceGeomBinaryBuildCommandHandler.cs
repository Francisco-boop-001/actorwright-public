using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGeomBinaryBuildCommandHandler(
    IFaceGeomBinaryBuildService service, TextWriter output, TextWriter error)
{
    private static readonly ImmutableHashSet<string> AllowedOptions =
        LegacyCommandOptionCatalog.For("facegen build-geom-nif")
            .Select(option => option.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    internal static string LabRoot => ActorwrightWorkspace.ResolveRoot().Value;

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var unsupportedOption = command.Options.Keys.FirstOrDefault(option =>
            !AllowedOptions.Contains(option));
        if (unsupportedOption is not null)
            return Usage(command.Json,
                $"facegen build-geom-nif does not support --{unsupportedOption}.");

        if (!TryParseEdition(command, out var edition, out var editionError))
            return Usage(command.Json, editionError);
        if (!command.Options.TryGetValue("asset-root", out var assetRoot))
            return Usage(command.Json, "facegen build-geom-nif requires --asset-root <directory>.");
        if (!command.Options.TryGetValue("source", out var source))
            return Usage(command.Json, "facegen build-geom-nif requires --source <relative-or-K-local-NIF>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen build-geom-nif requires --output <new-NIF>.");

        if (!TryParseMode(command.Options.GetValueOrDefault("mode"),
                out var operation, out var modeError))
            return Usage(command.Json, modeError);
        if (operation == FaceGeomBinaryOperation.Bake &&
            command.Options.Keys.Any(IsTransportOnlyOption))
            return Usage(command.Json,
                "transport-only options require --mode transport.");
        FaceGeomTransportProfile? transportProfile = null;
        if (command.Options.TryGetValue("transport-profile", out var profileText))
        {
            if (!TryParseTransportProfile(profileText, out var parsedProfile))
                return Usage(command.Json,
                    "--transport-profile must be complete-carrier|geometry-into-carrier.");
            transportProfile = parsedProfile;
        }

        ImmutableArray<FaceGeomBinaryMorph> morphs = [];
        if (command.Options.TryGetValue("morphs", out var morphText))
        {
            if (!TryReadMorphs(morphText, out morphs, out var morphError))
                return Usage(command.Json, morphError);
        }
        else if (operation == FaceGeomBinaryOperation.Bake)
        {
            return Usage(command.Json,
                "facegen build-geom-nif bake requires --morphs <JSON-array|@K-local-file>.");
        }

        Sha256Hash? sourceSha256 = null;
        if (command.Options.TryGetValue("source-sha256", out var sourceHashText))
        {
            if (!TryParseHash(sourceHashText, out var parsedSourceHash))
                return Usage(command.Json, "--source-sha256 must be a 64-hex SHA-256.");
            sourceSha256 = parsedSourceHash;
        }
        Sha256Hash? carrierSha256 = null;
        if (command.Options.TryGetValue("carrier-sha256", out var carrierHashText))
        {
            if (!TryParseHash(carrierHashText, out var parsedCarrierHash))
                return Usage(command.Json, "--carrier-sha256 must be a 64-hex SHA-256.");
            carrierSha256 = parsedCarrierHash;
        }

        var sourcePath = Path.IsPathFullyQualified(source)
            ? source
            : Path.GetFullPath(Path.Combine(assetRoot, source.Replace('/', Path.DirectorySeparatorChar)));
        var shapeName = command.Options.GetValueOrDefault("shape");
        if (operation == FaceGeomBinaryOperation.Transport)
        {
            if (edition != GameEdition.SkyrimSpecialEdition)
                return Usage(command.Json,
                    "facegen build-geom-nif transport supports only --edition skyrimse.");
            if (sourceSha256 is null || transportProfile is null)
                return Usage(command.Json,
                    "facegen build-geom-nif transport requires --source-sha256 and --transport-profile.");
            if (morphs.Any(morph => morph.Value != 0f))
                return Usage(command.Json,
                    "facegen build-geom-nif transport permits --morphs only when every value is zero.");

            var hasCarrier = command.Options.TryGetValue("carrier", out var carrierText) &&
                             !string.IsNullOrWhiteSpace(carrierText);
            var hasCarrierSha256 = command.Options.ContainsKey("carrier-sha256");
            var hasShape = command.Options.TryGetValue("shape", out var shapeText) &&
                           !string.IsNullOrWhiteSpace(shapeText);
            if (transportProfile == FaceGeomTransportProfile.GeometryIntoCarrier)
            {
                if (!hasCarrier || !hasCarrierSha256 || !hasShape)
                    return Usage(command.Json,
                        "facegen build-geom-nif geometry-into-carrier requires --carrier, --carrier-sha256, and --shape.");
            }
            else if (hasCarrier || hasCarrierSha256 || hasShape ||
                     command.Options.ContainsKey("carrier") || command.Options.ContainsKey("shape"))
            {
                return Usage(command.Json,
                    "facegen build-geom-nif complete-carrier forbids --carrier, --carrier-sha256, and --shape.");
            }
        }

        WorkspacePath? carrierPath = null;
        if (command.Options.TryGetValue("carrier", out var carrier))
        {
            carrierPath = new WorkspacePath(Path.IsPathFullyQualified(carrier)
                ? carrier
                : Path.GetFullPath(Path.Combine(assetRoot,
                    carrier.Replace('/', Path.DirectorySeparatorChar))));
        }
        var result = await service.BuildAsync(new FaceGeomBinaryBuildRequest(
            edition, new WorkspacePath(assetRoot), new WorkspacePath(sourcePath),
            new WorkspacePath(outputPath), morphs, operation, transportProfile,
            sourceSha256, carrierPath, carrierSha256, shapeName), cancellationToken);
        var artifact = result.Artifact;
        var response = new BuildResponse(result.Written, artifact?.ArtifactKind,
            artifact?.Edition, artifact?.OutputPath, artifact?.OutputSha256,
            artifact?.ByteLength, artifact?.VertexCount, artifact?.InputSourceSha256,
            artifact?.TriFiles ?? [], artifact?.Morphs ?? [], artifact?.RuntimeAuthority ?? false,
            artifact?.BaseVertexSha256, artifact?.BakedVertexSha256, artifact?.ImportMode,
            artifact?.CarrierVertexSha256, result.Diagnostics);
        Write(response, command.Json,
            result.Written ? $"facegen build-geom-nif: PASS {outputPath}" : "facegen build-geom-nif: REFUSED");
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        {
            message = "facegen build-geom-nif requires --edition|--game fallout4|skyrimse.";
            return false;
        }
        message = string.Empty;
        return true;
    }

    internal static bool TryReadMorphs(string text, out ImmutableArray<FaceGeomBinaryMorph> morphs,
        out string message)
    {
        morphs = [];
        message = string.Empty;
        try
        {
            var json = text.StartsWith('@')
                ? ReadKLocalFile(text[1..])
                : text;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("morphs", out var nested))
                element = nested;
            if (element.ValueKind != JsonValueKind.Array) throw new ArgumentException("morphs must be a JSON array.");
            var builder = ImmutableArray.CreateBuilder<FaceGeomBinaryMorph>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String || !item.TryGetProperty("value", out var valueElement) ||
                    !valueElement.TryGetSingle(out var value))
                    throw new ArgumentException("each morph requires a string name and numeric value.");
                builder.Add(new FaceGeomBinaryMorph(nameElement.GetString()?.Trim() ?? string.Empty, value));
            }
            morphs = builder.ToImmutable();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            message = $"invalid --morphs: {exception.Message}";
            return false;
        }
    }

    private static bool TryParseMode(string? text,
        out FaceGeomBinaryOperation operation, out string message)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, "bake", StringComparison.OrdinalIgnoreCase))
        {
            operation = FaceGeomBinaryOperation.Bake;
            message = string.Empty;
            return true;
        }
        if (string.Equals(text, "transport", StringComparison.OrdinalIgnoreCase))
        {
            operation = FaceGeomBinaryOperation.Transport;
            message = string.Empty;
            return true;
        }
        operation = default;
        message = "--mode must be bake|transport.";
        return false;
    }

    private static bool TryParseTransportProfile(string text,
        out FaceGeomTransportProfile profile)
    {
        if (string.Equals(text, "complete-carrier", StringComparison.OrdinalIgnoreCase))
        {
            profile = FaceGeomTransportProfile.CompleteCarrier;
            return true;
        }
        if (string.Equals(text, "geometry-into-carrier", StringComparison.OrdinalIgnoreCase))
        {
            profile = FaceGeomTransportProfile.GeometryIntoCarrier;
            return true;
        }
        profile = default;
        return false;
    }

    private static bool IsTransportOnlyOption(string option) => option is
        "transport-profile" or "source-sha256" or "carrier" or
        "carrier-sha256" or "shape";

    private static bool TryParseHash(string text, out Sha256Hash hash)
    {
        try
        {
            hash = new Sha256Hash(text);
            return true;
        }
        catch (ArgumentException)
        {
            hash = default;
            return false;
        }
    }

    internal static string ReadKLocalFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(LabRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("@morphs files must remain under the configured Actorwright workspace.");
        if (!File.Exists(full)) throw new IOException("morphs file does not exist.");
        var info = new FileInfo(full);
        if (info.Length > 4 * 1024 * 1024) throw new ArgumentException("morphs file exceeds the 4 MiB bound.");
        return File.ReadAllText(full);
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record BuildResponse(bool Written, string? ArtifactKind, string? Edition,
        string? OutputPath, string? OutputSha256, long? ByteLength, int? VertexCount,
        string? InputSourceSha256, ImmutableArray<FaceGeomBinarySource> TriFiles,
        ImmutableArray<FaceGeomBinaryMorph> Morphs, bool RuntimeAuthority,
        string? BaseVertexSha256, string? BakedVertexSha256, string? ImportMode,
        string? CarrierVertexSha256,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
