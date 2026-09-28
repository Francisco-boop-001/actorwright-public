using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class QualifiedFaceGeomCarrierService
{
    private const int MaxEvidenceJsonBytes = 2 * 1024 * 1024;
    private const string CanonicalEvidencePath =
        "evidence/facegeom-carrier-materialization.json";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new AssetPathEvidenceJsonConverter(),
            new Sha256HashEvidenceJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyEvidenceFileAsync(
        WorkspacePath packageRoot,
        AssetPath evidenceFile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (string.IsNullOrWhiteSpace(evidenceFile.Value) ||
            !string.Equals(evidenceFile.Value, CanonicalEvidencePath,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-path-invalid",
                DiagnosticSeverity.Error,
                $"Durable FaceGeom evidence must use {CanonicalEvidencePath}."));
            return RefusedEvidenceVerification(null, null, null, diagnostics);
        }

        ValidateKLocalPath(packageRoot, "package root", diagnostics);
        if (!packageRoot.IsUnder(_labRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-package-outside-lab",
                DiagnosticSeverity.Error,
                "Durable FaceGeom evidence may be read only below the K-local lab root."));
        if (!Directory.Exists(packageRoot.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-package-missing",
                DiagnosticSeverity.Error,
                "The durable evidence package root does not exist."));
        if (!HasErrors(diagnostics))
        {
            diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, packageRoot));
            AddReparseDiagnostic(diagnostics, _labRoot, packageRoot, "evidence-package-root");
        }
        if (HasErrors(diagnostics))
            return RefusedEvidenceVerification(null, null, null, diagnostics);

        WorkspacePath evidencePath;
        try
        {
            evidencePath = new WorkspacePath(Path.Combine(
                packageRoot.Value,
                evidenceFile.Value.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-path-invalid",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedEvidenceVerification(null, null, null, diagnostics);
        }

        if (!evidencePath.IsUnder(packageRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-outside-package",
                DiagnosticSeverity.Error,
                "The durable FaceGeom evidence file escaped its package root."));
        diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, evidencePath));
        if (!File.Exists(evidencePath.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-missing",
                DiagnosticSeverity.Error,
                "The durable FaceGeom evidence file does not exist."));
        else
        {
            AddReparseDiagnostic(diagnostics, _labRoot, evidencePath, "facegeom-evidence-file");
            var length = new FileInfo(evidencePath.Value).Length;
            if (length is <= 0 or > MaxEvidenceJsonBytes)
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-size",
                    DiagnosticSeverity.Error,
                    $"The durable FaceGeom evidence file must contain 1 to {MaxEvidenceJsonBytes} bytes."));
        }
        if (HasErrors(diagnostics))
            return RefusedEvidenceVerification(null, null, null, diagnostics);

        try
        {
            await using var stream = new FileStream(
                evidencePath.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxEvidenceJsonBytes)
                throw new InvalidDataException(
                    $"The opened durable FaceGeom evidence must contain 1 to " +
                    $"{MaxEvidenceJsonBytes} bytes.");
            using (var document = await JsonDocument.ParseAsync(stream,
                       new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 64
                       }, cancellationToken))
            {
                RejectDuplicateEvidenceKeys(document.RootElement);
            }
            stream.Position = 0;
            var evidence = await JsonSerializer.DeserializeAsync<
                QualifiedFaceGeomCarrierMaterializationEvidence>(
                stream,
                EvidenceJsonOptions,
                cancellationToken);
            if (evidence is null)
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-empty",
                    DiagnosticSeverity.Error,
                    "The durable FaceGeom evidence file did not contain an evidence object."));
                return RefusedEvidenceVerification(null, null, null, diagnostics);
            }
            return await VerifyEvidenceAsync(packageRoot, evidence, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
                                          InvalidDataException or
                                          ArgumentException or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-file-invalid",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedEvidenceVerification(null, null, null, diagnostics);
        }
    }

    private static void RejectDuplicateEvidenceKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate durable FaceGeom evidence field '{property.Name}' is refused.");
                RejectDuplicateEvidenceKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicateEvidenceKeys(item);
        }
    }

    private sealed class AssetPathEvidenceJsonConverter : JsonConverter<AssetPath>
    {
        public override AssetPath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(ReadValue(ref reader, "asset path"));

        public override void Write(
            Utf8JsonWriter writer,
            AssetPath value,
            JsonSerializerOptions options) =>
            WriteValue(writer, value.Value);
    }

    private sealed class Sha256HashEvidenceJsonConverter : JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(ReadValue(ref reader, "SHA-256"));

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            WriteValue(writer, value.Value);
    }

    private static string ReadValue(ref Utf8JsonReader reader, string role)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? throw new JsonException($"The {role} value is null.");
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"The {role} must be a value object.");

        string? value = null;
        var valueSeen = false;
        var ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                ended = true;
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"The {role} value object is malformed.");
            var property = reader.GetString();
            if (!reader.Read()) throw new JsonException($"The {role} value object is truncated.");
            if (string.Equals(property, "value", StringComparison.Ordinal))
            {
                if (valueSeen)
                    throw new JsonException($"The {role} value object has a duplicate value property.");
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException($"The {role} value must be a string.");
                value = reader.GetString();
                valueSeen = true;
            }
            else
            {
                throw new JsonException($"The {role} value object contains an unsupported property.");
            }
        }
        if (!ended)
            throw new JsonException($"The {role} value object is truncated.");
        return value ?? throw new JsonException($"The {role} value object has no value property.");
    }

    private static void WriteValue(Utf8JsonWriter writer, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }
}
