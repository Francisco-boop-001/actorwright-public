using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Canonical non-game-facing persistence for recoverable reference-authoring
/// authority documents.
/// </summary>
public sealed partial class ReferencePresetSessionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    long requestedMaximumDocumentBytes =
        256L * 1024 * 1024)
    : IReferencePresetSessionService
{
    private const long DefaultMaximumDocumentBytes =
        256L * 1024 * 1024;
    private readonly long maximumDocumentBytes =
        ValidateMaximumDocumentBytes(
            requestedMaximumDocumentBytes);
    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    internal static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            WriteIndented = false
        };
        options.Converters.Add(
            new ImmutableInt32ArrayJsonConverter());
        options.Converters.Add(
            new ImmutableVector2ArrayJsonConverter());
        options.Converters.Add(
            new ImmutableVector3ArrayJsonConverter());
        options.Converters.Add(
            new ImmutableTriDeltaArrayJsonConverter());
        options.Converters.Add(new WorkspacePathConverter());
        options.Converters.Add(new Sha256HashConverter());
        options.Converters.Add(new AssetPathConverter());
        options.Converters.Add(new PluginNameConverter());
        options.Converters.Add(new FormIdConverter());
        options.Converters.Add(new FormReferenceConverter());
        options.Converters.Add(new Vector2Converter());
        options.Converters.Add(new Vector3Converter());
        return options;
    }

    public async ValueTask<ReferencePresetSessionWriteResult> WriteAsync(
        ReferencePresetSessionWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateDestination(
            request.DestinationPath, diagnostics);
        ValidateDocument(request.Document, diagnostics);
        if (HasErrors(diagnostics))
            return new ReferencePresetSessionWriteResult(
                false, null, diagnostics.ToImmutable());

        byte[] bytes;
        try
        {
            bytes = SerializeCanonical(request.Document);
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            InvalidOperationException or
            ReferenceSessionSizeLimitException)
        {
            diagnostics.Add(exception is
                    ReferenceSessionSizeLimitException
                ? Error(
                    "reference-session-size-limit",
                    exception.Message)
                : Error(
                    "reference-session-serialize",
                    exception.Message));
            return new ReferencePresetSessionWriteResult(
                false, null, diagnostics.ToImmutable());
        }
        return await WriteBytesAsync(request.DestinationPath, bytes, typed: true, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ReferencePresetSessionWriteResult> WriteBytesAsync(
        WorkspacePath destinationPath, byte[] bytes, bool typed,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > maximumDocumentBytes)
        {
            diagnostics.Add(Error(
                "reference-session-size-limit",
                $"Session documents may not exceed {maximumDocumentBytes} bytes."));
            return new ReferencePresetSessionWriteResult(
                false, null, diagnostics.ToImmutable());
        }
        Sha256Hash hash = Hash(bytes);
        string destination = destinationPath.Value;
        string temporary = destination + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                    bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            byte[] reopened = await File.ReadAllBytesAsync(
                temporary, cancellationToken)
                .ConfigureAwait(false);
            Sha256Hash reopenedHash = Hash(reopened);
            bool exactBytes =
                reopened.AsSpan().SequenceEqual(bytes);
            bool canonicalBytes =
                IsCanonical(reopened);
            ReferencePresetSessionDocument? parsed = typed ? Deserialize(reopened) : null;
            if ((typed && parsed is null) ||
                !exactBytes ||
                !canonicalBytes ||
                reopenedHash != hash)
            {
                diagnostics.Add(Error(
                    "reference-session-readback-mismatch",
                    $"The temporary session document did not reopen as identical canonical typed JSON (written={hash}, reopened={reopenedHash}, exactBytes={exactBytes}, canonicalBytes={canonicalBytes})."));
                TryDelete(temporary);
                return new ReferencePresetSessionWriteResult(
                    false, null, diagnostics.ToImmutable());
            }
            if (typed) ValidateDocument(parsed!, diagnostics);
            if (HasErrors(diagnostics))
            {
                TryDelete(temporary);
                return new ReferencePresetSessionWriteResult(
                    false, null, diagnostics.ToImmutable());
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            diagnostics.Add(new Diagnostic(
                "reference-session-written",
                DiagnosticSeverity.Info,
                "Wrote, reopened, and atomically promoted one canonical reference session document."));
            return new ReferencePresetSessionWriteResult(
                true, hash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            InvalidOperationException)
        {
            TryDelete(temporary);
            diagnostics.Add(Error(
                "reference-session-write-failed",
                exception.Message));
            return new ReferencePresetSessionWriteResult(
                false, null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<ReferencePresetSessionReadResult> ReadAsync(
        ReferencePresetSessionReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateSource(request.SourcePath, diagnostics);
        if (HasErrors(diagnostics))
            return RefusedRead(diagnostics);
        try
        {
            var info = new FileInfo(request.SourcePath.Value);
            if (info.Length > maximumDocumentBytes)
            {
                diagnostics.Add(Error(
                    "reference-session-size-limit",
                    $"Session documents may not exceed {maximumDocumentBytes} bytes."));
                return RefusedRead(diagnostics);
            }
            byte[] bytes = await File.ReadAllBytesAsync(
                request.SourcePath.Value,
                cancellationToken).ConfigureAwait(false);
            Sha256Hash hash = Hash(bytes);
            ReferencePresetSessionDocument? document =
                Deserialize(bytes);
            if (document is null)
            {
                diagnostics.Add(Error(
                    "reference-session-json",
                    "The session document is absent after JSON parsing."));
                return RefusedRead(diagnostics);
            }
            Sha256Hash canonicalHash = Hash(SerializeCanonical(document));
            if (hash != request.ExpectedSha256 && canonicalHash != request.ExpectedSha256)
            {
                diagnostics.Add(Error(
                    "reference-session-hash-mismatch",
                    $"Session hash binding failed: expectedSha256={request.ExpectedSha256.Value}; " +
                    $"receivedSha256={hash.Value}; canonicalSha256={canonicalHash.Value}."));
                return RefusedRead(diagnostics);
            }
            ValidateDocument(document, diagnostics);
            if (document.Kind != request.ExpectedKind)
            {
                diagnostics.Add(Error(
                    "reference-session-kind-mismatch",
                    $"Session kind is {document.Kind}, expected {request.ExpectedKind}."));
            }
            if (HasErrors(diagnostics))
                return RefusedRead(diagnostics);
            diagnostics.Add(new Diagnostic(
                "reference-session-read",
                DiagnosticSeverity.Info,
                "Reopened one reference session document bound by its raw or canonical SHA-256."));
            return new ReferencePresetSessionReadResult(
                document, canonicalHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException or
            InvalidOperationException or
            ArgumentException or
            ReferenceSessionSizeLimitException)
        {
            diagnostics.Add(Error(
                "reference-session-read-failed",
                exception.Message));
            return RefusedRead(diagnostics);
        }
    }

    private void ValidateDestination(
        WorkspacePath destination,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.Evaluate(
            labRoot, destination));
        if (File.Exists(destination.Value) ||
            Directory.Exists(destination.Value))
        {
            diagnostics.Add(Error(
                "reference-session-output-exists",
                "Session output must be an absent path."));
        }
        string? parent = Path.GetDirectoryName(
            destination.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "reference-session-output-parent",
                "Session output parent must already exist."));
        }
        if (!destination.Value.EndsWith(
                ".json", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "reference-session-extension",
                "Session documents must use the .json extension."));
        }
    }

    private void ValidateSource(
        WorkspacePath source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!source.IsUnder(labRoot))
        {
            diagnostics.Add(Error(
                "reference-session-input-outside-lab",
                "Session input must remain under the K-only lab root."));
        }
        if (!File.Exists(source.Value))
        {
            diagnostics.Add(Error(
                "reference-session-input-missing",
                "The exact session input does not exist."));
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, source));
    }

    private static void ValidateDocument(
        ReferencePresetSessionDocument document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document is null)
        {
            diagnostics.Add(Error(
                "reference-session-document",
                "A typed session document is required."));
            return;
        }
        bool[] present =
        [
            document.Intake is not null,
            document.InferenceProposal is not null,
            document.ReviewedDesign is not null,
            document.ResourceSnapshot is not null,
            document.AuthoringProposal is not null,
            document.VerifiedPreset is not null,
            document.VerifiedNpcHandoff is not null
        ];
        int expected = (int)document.Kind;
        if (!Enum.IsDefined(document.Kind) ||
            expected < 0 ||
            expected >= present.Length ||
            present.Count(value => value) != 1 ||
            !present[expected])
        {
            diagnostics.Add(Error(
                "reference-session-payload",
                "Session kind must match exactly one populated authority payload."));
        }
        int? schemaVersion = document.Kind switch
        {
            ReferencePresetSessionDocumentKind.Intake =>
                document.Intake?.SchemaVersion,
            ReferencePresetSessionDocumentKind.InferenceProposal =>
                document.InferenceProposal?.SchemaVersion,
            ReferencePresetSessionDocumentKind.ReviewedDesign =>
                document.ReviewedDesign?.SchemaVersion,
            ReferencePresetSessionDocumentKind.ResourceSnapshot =>
                document.ResourceSnapshot?.SchemaVersion,
            ReferencePresetSessionDocumentKind.AuthoringProposal =>
                document.AuthoringProposal?.SchemaVersion,
            ReferencePresetSessionDocumentKind.VerifiedPreset =>
                document.VerifiedPreset?.SchemaVersion,
            ReferencePresetSessionDocumentKind.VerifiedNpcHandoff =>
                document.VerifiedNpcHandoff?.SchemaVersion,
            _ => null
        };
        if (schemaVersion != 1)
        {
            diagnostics.Add(Error(
                "reference-session-schema",
                "Reference session payloads must use schema version 1."));
        }
    }

    private byte[] SerializeCanonical(
        ReferencePresetSessionDocument document)
    {
        byte[] serialized;
        using (var serializedStream =
               new SizeLimitedMemoryStream(
                   maximumDocumentBytes))
        {
            JsonSerializer.Serialize(
                serializedStream,
                document,
                JsonOptions);
            serialized = serializedStream.ToArray();
        }
        using JsonDocument parsed = JsonDocument.Parse(serialized);
        return SerializeCanonicalJson(parsed.RootElement);
    }

    private byte[] SerializeCanonicalJson(JsonElement element)
    {
        using var stream = new SizeLimitedMemoryStream(
            maximumDocumentBytes);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
            WriteCanonical(element, writer);
        return stream.ToArray();
    }

    private static bool IsCanonical(byte[] serialized)
    {
        using JsonDocument parsed =
            JsonDocument.Parse(serialized);
        using var stream =
            new CanonicalComparingStream(serialized);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            WriteCanonical(parsed.RootElement, writer);
        }
        return stream.IsExactMatch;
    }

    private static long ValidateMaximumDocumentBytes(
        long value)
    {
        if (value <= 0 ||
            value > DefaultMaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"The session byte ceiling must be between 1 and {DefaultMaximumDocumentBytes}.");
        }
        return value;
    }

    private sealed class SizeLimitedMemoryStream(
        long maximumBytes) : MemoryStream
    {
        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            EnsureWithinLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(
            ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWithinLimit(1);
            base.WriteByte(value);
        }

        private void EnsureWithinLimit(int count)
        {
            long next = checked(Position + count);
            if (next > maximumBytes)
            {
                throw new ReferenceSessionSizeLimitException(
                    $"Session documents may not exceed {maximumBytes} bytes.");
            }
        }
    }

    private sealed class ReferenceSessionSizeLimitException(
        string message) : Exception(message);

    private sealed class CanonicalComparingStream(
        byte[] expected) : Stream
    {
        private long position;
        private bool matches = true;

        public bool IsExactMatch =>
            matches &&
            position == expected.LongLength;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => position;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            Compare(buffer.AsSpan(offset, count));

        public override void Write(
            ReadOnlySpan<byte> buffer) =>
            Compare(buffer);

        public override void WriteByte(byte value)
        {
            Span<byte> single = stackalloc byte[1];
            single[0] = value;
            Compare(single);
        }

        private void Compare(ReadOnlySpan<byte> bytes)
        {
            if (position > expected.LongLength ||
                bytes.Length >
                expected.LongLength - position)
            {
                matches = false;
                position = checked(
                    position + bytes.Length);
                return;
            }
            if (matches &&
                !expected.AsSpan(
                        checked((int)position),
                        bytes.Length)
                    .SequenceEqual(bytes))
            {
                matches = false;
            }
            position = checked(position + bytes.Length);
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }

    private static ReferencePresetSessionDocument? Deserialize(
        byte[] bytes) =>
        JsonSerializer.Deserialize<
            ReferencePresetSessionDocument>(
            bytes, JsonOptions);

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in
                         element.EnumerateObject()
                             .OrderBy(item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in
                         element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(
                    $"Unsupported JSON kind {element.ValueKind}.");
        }
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ReferencePresetSessionReadResult RefusedRead(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            // A uniquely named unpromoted temporary remains non-authoritative.
        }
    }

    private sealed class WorkspacePathConverter
        : JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "Workspace path is null."));

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashConverter
        : JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "SHA-256 is null."));

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class AssetPathConverter
        : JsonConverter<AssetPath>
    {
        public override AssetPath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "Asset path is null."));

        public override void Write(
            Utf8JsonWriter writer,
            AssetPath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PluginNameConverter
        : JsonConverter<PluginName>
    {
        public override PluginName Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new(reader.GetString() ??
                throw new JsonException(
                    "Plugin name is null."));

        public override void Write(
            Utf8JsonWriter writer,
            PluginName value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class FormIdConverter
        : JsonConverter<FormId>
    {
        public override FormId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string text = reader.GetString() ??
                          throw new JsonException(
                              "FormID is null.");
            if (!FormId.TryParse(text, out FormId value))
                throw new JsonException(
                    "FormID is invalid.");
            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Value.ToString(
                    "X8",
                    System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FormReferenceConverter
        : JsonConverter<FormReference>
    {
        public override FormReference Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string text = reader.GetString() ??
                          throw new JsonException(
                              "Form reference is null.");
            int separator = text.LastIndexOf('|');
            if (separator <= 0 ||
                separator == text.Length - 1 ||
                !FormId.TryParse(
                    text[(separator + 1)..],
                    out FormId formId))
                throw new JsonException(
                    "Form reference is invalid.");
            return new FormReference(
                new PluginName(text[..separator]), formId);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormReference value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Plugin.Value + "|" +
                value.FormId.Value.ToString(
                    "X8",
                    System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class Vector2Converter
        : JsonConverter<Vector2>
    {
        public override Vector2 Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType !=
                JsonTokenType.StartArray ||
                !reader.Read())
                throw new JsonException(
                    "Vector2 must be an array.");
            float x = reader.GetSingle();
            if (!reader.Read())
                throw new JsonException(
                    "Vector2 Y is absent.");
            float y = reader.GetSingle();
            if (!reader.Read() ||
                reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException(
                    "Vector2 must contain exactly two values.");
            return new Vector2(x, y);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Vector2 value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteEndArray();
        }
    }

    private sealed class Vector3Converter
        : JsonConverter<Vector3>
    {
        public override Vector3 Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType !=
                JsonTokenType.StartArray ||
                !reader.Read())
                throw new JsonException(
                    "Vector3 must be an array.");
            float x = reader.GetSingle();
            if (!reader.Read())
                throw new JsonException(
                    "Vector3 Y is absent.");
            float y = reader.GetSingle();
            if (!reader.Read())
                throw new JsonException(
                    "Vector3 Z is absent.");
            float z = reader.GetSingle();
            if (!reader.Read() ||
                reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException(
                    "Vector3 must contain exactly three values.");
            return new Vector3(x, y, z);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Vector3 value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }
}
