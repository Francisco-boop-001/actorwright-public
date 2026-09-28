using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Admits exact preset bytes and publishes one strict inspection receipt using
/// retained no-follow handles. The source preset remains the workflow artifact;
/// this receipt is separate application-owned evidence about that source.
/// </summary>
public sealed class PresetInspectionArtifactStore
{
    private const int MaximumPresetBytes = 4 * 1024 * 1024;
    private const int MaximumReceiptBytes = 8 * 1024 * 1024;
    private const string SourceRole = "protocol-v2 preset input";
    private const string ReceiptRole = "protocol-v2 preset inspection receipt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
            new PluginNameObjectConverter(),
            new FormIdObjectConverter()
        }
    };

    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public PresetInspectionArtifactStore(WorkspacePath workspaceRoot)
    {
        this.workspaceRoot = workspaceRoot;
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot);
    }

    internal PresetInspectionArtifactStore(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsPathInspector inspector)
    {
        this.workspaceRoot = workspaceRoot;
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(
            workspaceRoot,
            inspector);
    }

    public async ValueTask<PresetExactInputDocument> ReadExactAsync(
        WorkspacePath path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        RequireUpperSha256(expectedSha256);
        RequireAbsoluteWorkspacePath(path, SourceRole);
        byte[] bytes;
        try
        {
            boundary.RequireExistingFile(path, SourceRole);
            bytes = await boundary.ReadExactFileAsync(
                path,
                MaximumPresetBytes,
                SourceRole,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Failure(
                ClassifyPathFailure(exception),
                "The preset input must be one existing ordinary file beneath the exact workspace.",
                exception);
        }

        string observed = Hash(bytes);
        if (!string.Equals(
                observed,
                expectedSha256,
                StringComparison.Ordinal))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionInputHashMismatch,
                "The preset input SHA-256 does not match --input-sha256.");
        return new PresetExactInputDocument(
            path,
            bytes.LongLength,
            observed,
            bytes.ToImmutableArray());
    }

    public async ValueTask<PresetInspectionArtifactDocument> WriteNewAsync(
        PresetInspectionReceipt receipt,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(
                receipt.Schema,
                PresetInspectionSchemas.CurrentDocument,
                StringComparison.Ordinal))
            throw Failure(
                "preset-inspection-schema-unsupported",
                $"Unsupported preset inspection schema '{receipt.Schema}'.");
        ValidateReceipt(receipt, PresetInspectionSchemas.CurrentDocument);
        byte[] canonical = ComputeCanonicalBytesOrRefused(
            receipt,
            ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
            "The preset inspection receipt could not be canonically serialized within its admitted boundary.");
        RequireFreshDestination(destination);

        string parent = Path.GetDirectoryName(destination.Value) ??
            throw Failure(
                ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The preset inspection output has no parent directory.");
        var temporary = new WorkspacePath(Path.Combine(
            parent,
            $".preset-inspection-{Guid.NewGuid():N}.tmp"));
        FaceGeomHairRegionsOwnedFile? owned = null;
        bool promoted = false;
        try
        {
            owned = boundary.CreateOwnedFile(
                temporary,
                destination,
                ReceiptRole);
            byte[] staged = await owned.WriteAndReadbackAsync(
                canonical,
                MaximumReceiptBytes,
                cancellationToken);
            RequireExactBytes(staged, canonical, "staged");
            _ = ParseExact(staged);

            cancellationToken.ThrowIfCancellationRequested();
            owned.PromoteNoOverwrite();
            promoted = true;

            // Promotion is the commit point. Cancellation must not interrupt
            // retained readback, typed validation, or the independent pinned
            // reload of the committed destination.
            byte[] retained = await owned.ReadbackExactAsync(
                MaximumReceiptBytes,
                CancellationToken.None);
            RequireExactBytes(retained, canonical, "promoted retained-handle");
            _ = ParseExact(retained);
            owned.Dispose();
            owned = null;

            byte[] reopened = boundary.ReadExactFile(
                destination,
                MaximumReceiptBytes,
                ReceiptRole);
            RequireExactBytes(reopened, canonical, "promoted pinned-reload");
            PresetInspectionReceipt reloaded = ParseExact(reopened);
            return new PresetInspectionArtifactDocument(
                reloaded,
                destination,
                reopened.LongLength,
                Hash(reopened),
                reopened.ToImmutableArray());
        }
        catch (OperationCanceledException exception) when (!promoted)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        catch (PresetInspectionArtifactException exception)
        {
            if (!promoted)
            {
                CleanupOrThrow(owned, exception);
                throw;
            }
            throw new PresetInspectionArtifactException(
                exception.Code,
                exception.Message,
                exception,
                promoted: true);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException)
        {
            if (!promoted)
                CleanupOrThrow(owned, exception);
            throw Failure(
                ClassifyWriteFailure(exception),
                "The preset inspection receipt transaction was refused.",
                exception,
                promoted);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public PresetInspectionArtifactDocument Load(
        WorkspacePath path,
        string expectedSha256)
    {
        RequireUpperSha256(expectedSha256);
        RequireAbsoluteWorkspacePath(path, ReceiptRole);
        byte[] bytes;
        try
        {
            bytes = boundary.ReadExactFile(
                path,
                MaximumReceiptBytes,
                ReceiptRole);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Failure(
                ClassifyPathFailure(exception),
                "The preset inspection receipt must be one existing ordinary file.",
                exception);
        }
        string observed = Hash(bytes);
        if (!string.Equals(observed, expectedSha256, StringComparison.Ordinal))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection receipt SHA-256 does not match its binding.");
        PresetInspectionReceipt receipt = ParseExact(bytes);
        return new PresetInspectionArtifactDocument(
            receipt,
            path,
            bytes.LongLength,
            observed,
            bytes.ToImmutableArray());
    }

    internal static byte[] ComputeCanonicalBytes(
        PresetInspectionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return CanonicalJsonWriter.Serialize(
            receipt,
            JsonOptions,
            MaximumReceiptBytes);
    }

    private PresetInspectionReceipt ParseExact(byte[] bytes)
    {
        string schema = ReadExactSchema(bytes);
        return schema switch
        {
            PresetInspectionSchemas.LegacyDocument =>
                ParseSchema1Exact(bytes),
            PresetInspectionSchemas.CurrentDocument =>
                ParseSchema2Canonical(bytes),
            _ => throw Failure(
                "preset-inspection-schema-unsupported",
                $"Unsupported preset inspection schema '{schema}'.")
        };
    }

    private PresetInspectionReceipt ParseSchema1Exact(byte[] bytes)
    {
        PresetInspectionReceipt receipt = DeserializeStrict(
            bytes,
            PresetInspectionSchemas.LegacyDocument);
        byte[] canonical = SerializeLegacy(receipt);
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The schema-1 preset inspection receipt is not exact Actorwright JSON.");
        return receipt;
    }

    private PresetInspectionReceipt ParseSchema2Canonical(byte[] bytes)
    {
        PresetInspectionReceipt receipt = DeserializeStrict(
            bytes,
            PresetInspectionSchemas.CurrentDocument);
        byte[] canonical = ComputeCanonicalBytesOrRefused(
            receipt,
            ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed,
            "The schema-2 preset inspection receipt could not be reproduced as canonical Actorwright JSON.");
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed,
                "The schema-2 preset inspection receipt is not canonical Actorwright JSON.");
        return receipt;
    }

    private static byte[] ComputeCanonicalBytesOrRefused(
        PresetInspectionReceipt receipt,
        string code,
        string message)
    {
        try
        {
            return ComputeCanonicalBytes(receipt);
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                NotSupportedException)
        {
            throw Failure(code, message, exception);
        }
    }

    private PresetInspectionReceipt DeserializeStrict(
        byte[] bytes,
        string expectedSchema)
    {
        try
        {
            PresetInspectionReceipt receipt =
                JsonSerializer.Deserialize<PresetInspectionReceipt>(
                    bytes,
                    JsonOptions) ??
                throw new JsonException("The preset inspection receipt is null.");
            ValidateReceipt(receipt, expectedSchema);
            return receipt;
        }
        catch (PresetInspectionArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                NotSupportedException)
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection receipt is not strict typed JSON.",
                exception);
        }
    }

    private static string ReadExactSchema(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = JsonOptions.MaxDepth
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException(
                    "The preset inspection receipt root must be an object.");
            JsonProperty[] schemas = document.RootElement.EnumerateObject()
                .Where(item => string.Equals(
                    item.Name,
                    "schema",
                    StringComparison.Ordinal))
                .ToArray();
            if (schemas.Length != 1 ||
                schemas[0].Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(schemas[0].Value.GetString()))
                throw new JsonException(
                    "The preset inspection receipt needs one exact schema string.");
            return schemas[0].Value.GetString()!;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException)
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection receipt has no exact schema discriminator.",
                exception);
        }
    }

    private static byte[] SerializeLegacy(PresetInspectionReceipt receipt)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumReceiptBytes)
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection receipt exceeds its admitted size.");
        if (Array.IndexOf(bytes, (byte)'\r') >= 0)
            bytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(bytes)
                    .Replace("\r\n", "\n", StringComparison.Ordinal));
        return bytes;
    }

    private void ValidateReceipt(
        PresetInspectionReceipt receipt,
        string expectedSchema)
    {
        if (!string.Equals(
                receipt.Schema,
                expectedSchema,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.Format, "racemenu-jslot",
                StringComparison.Ordinal) ||
            !string.Equals(receipt.Edition, "skyrimse",
                StringComparison.Ordinal) ||
            receipt.Appearance is null || receipt.Diagnostics.IsDefault ||
            receipt.IsValid != !receipt.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection receipt violates its closed schema.");
        RequireUpperSha256(receipt.SourceSha256);
        WorkspacePath source;
        try
        {
            source = new WorkspacePath(receipt.SourcePath);
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The receipt source path is invalid.",
                exception);
        }
        RequireAbsoluteWorkspacePath(source, "preset inspection source binding");
    }

    private void RequireFreshDestination(WorkspacePath destination)
    {
        RequireAbsoluteWorkspacePath(destination, ReceiptRole);
        if (File.Exists(destination.Value) || Directory.Exists(destination.Value))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionOutputExists,
                "The preset inspection output must be fresh.");
        try
        {
            boundary.RequireNewFile(destination, ReceiptRole);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Failure(
                exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
                    ? ProtocolV2DiagnosticCodes.ReparsePointRefused
                    : exception.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                        ? ProtocolV2DiagnosticCodes.PresetInspectionOutputExists
                        : ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The preset inspection output must be a fresh ordinary file with an existing ordinary parent.",
                exception);
        }
    }

    private void RequireAbsoluteWorkspacePath(WorkspacePath path, string role)
    {
        if (!Path.IsPathFullyQualified(path.Value) ||
            !string.Equals(Path.GetPathRoot(path.Value), @"K:\",
                StringComparison.OrdinalIgnoreCase) ||
            path.Value.IndexOf(':', 2) >= 0)
            throw Failure(
                path.Value.IndexOf(':', 2) >= 0
                    ? ProtocolV2DiagnosticCodes.AlternateDataStreamRefused
                    : ProtocolV2DiagnosticCodes.PresetInspectionPathRefused,
                $"The {role} must use one absolute ordinary K-local path.");
        if (!path.IsUnder(workspaceRoot))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPathRefused,
                $"The {role} must remain beneath the exact workspace.");
    }

    private static void RequireUpperSha256(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionValidationFailed,
                "Preset inspection SHA-256 bindings must be uppercase hexadecimal.");
    }

    private static void RequireExactBytes(
        byte[] observed,
        byte[] expected,
        string phase)
    {
        if (!observed.AsSpan().SequenceEqual(expected))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                $"The {phase} preset inspection readback changed.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string ClassifyPathFailure(Exception exception) =>
        exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
            ? ProtocolV2DiagnosticCodes.ReparsePointRefused
            : ProtocolV2DiagnosticCodes.PresetInspectionPathRefused;

    private static string ClassifyWriteFailure(Exception exception) =>
        exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
            ? ProtocolV2DiagnosticCodes.ReparsePointRefused
            : exception.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                ? ProtocolV2DiagnosticCodes.PresetInspectionOutputExists
                : ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed;

    private static void CleanupOrThrow(
        FaceGeomHairRegionsOwnedFile? owned,
        Exception? primary)
    {
        if (owned is null)
            return;
        if (!owned.TryDelete(out string? cleanupFailure))
            throw Failure(
                ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed,
                "The preset inspection temporary could not be removed: " +
                cleanupFailure,
                primary);
    }

    private static PresetInspectionArtifactException Failure(
        string code,
        string message,
        Exception? inner = null,
        bool promoted = false) => new(code, message, inner, promoted);
}

internal sealed class PluginNameObjectConverter : JsonConverter<PluginName>
{
    public override PluginName Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject ||
            !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
            !reader.ValueTextEquals("value") ||
            !reader.Read() || reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                "A plugin name must be an exact value object.");
        var value = new PluginName(reader.GetString() ??
            throw new JsonException("A plugin name value is null."));
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
            throw new JsonException(
                "A plugin name value object has additional members.");
        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        PluginName value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteEndObject();
    }
}

internal sealed class FormIdObjectConverter : JsonConverter<FormId>
{
    public override FormId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject ||
            !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
            !reader.ValueTextEquals("value") ||
            !reader.Read() || reader.TokenType != JsonTokenType.Number ||
            !reader.TryGetUInt32(out uint value))
            throw new JsonException(
                "A FormID must be an exact unsigned value object.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
            throw new JsonException(
                "A FormID value object has additional members.");
        return new FormId(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        FormId value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteEndObject();
    }
}

public sealed class PresetInspectionArtifactException : IOException
{
    public PresetInspectionArtifactException(
        string code,
        string message,
        Exception? innerException = null,
        bool promoted = false) : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException(
                "A diagnostic code is required.",
                nameof(code))
            : code;
        Promoted = promoted;
    }

    public string Code { get; }

    public bool Promoted { get; }
}
