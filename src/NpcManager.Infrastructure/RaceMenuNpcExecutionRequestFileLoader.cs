using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict, bounded loader for the prepared request file shared by CLI and
/// desktop. It reads only ordinary files below the configured K-local root,
/// binds the exact bytes by SHA-256, and rejects ambiguous JSON.
/// </summary>
public sealed partial class RaceMenuNpcExecutionRequestFileLoader(
    WorkspacePath workspaceRoot,
    IApplicationProviderResourceRegistry? applicationProviderRegistry = null) :
    IRaceMenuNpcExecutionRequestFileLoader
{
    private const int MaximumRequestBytes = 1 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
        RaceMenuNpcExecutionRequestFileLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Sha256Hash? actualHash = null;
        long? byteLength = null;
        try
        {
            ValidatePath(request.RequestFile);
            if (HasReparsePath(request.RequestFile))
                throw new UnauthorizedAccessException(
                    "Preset NPC request path may not traverse a reparse point.");

            byte[] bytes;
            await using (var stream = new FileStream(request.RequestFile.Value,
                             FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > MaximumRequestBytes)
                    throw new InvalidDataException(
                        "Preset NPC request must contain 1 byte to 1 MiB.");
                byteLength = stream.Length;
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
            }

            actualHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (actualHash != request.ExpectedSha256)
                throw new InvalidDataException(
                    $"Preset NPC request hash {actualHash} does not match {request.ExpectedSha256}.");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateKeys(document.RootElement);
            if (document.RootElement.TryGetProperty("wholeSkinAuthority", out var wholeSkin) &&
                wholeSkin.ValueKind != JsonValueKind.Object)
                throw new JsonException("wholeSkinAuthority must be an object containing manifestPath and manifestSha256 when supplied.");
            var dto = JsonSerializer.Deserialize<ExecutionRequestDto>(bytes, JsonOptions) ??
                      throw new JsonException("Preset NPC request is empty.");
            RaceMenuNpcExecutionRequestFileLoadResult? migration =
                TryCreateMigrationRequired(
                    request,
                    dto,
                    bytes,
                    actualHash.Value,
                    byteLength.Value);
            if (migration is not null) return migration;
            var executionRequest = ToRequest(dto);
            var diagnostics = ImmutableArray.Create(new Diagnostic(
                "preset-npc-request-loaded", DiagnosticSeverity.Info,
                "The prepared preset-to-NPC request was hash-bound and parsed without ambiguity."));
            return new RaceMenuNpcExecutionRequestFileLoadResult(
                RaceMenuNpcExecutionRequestFileLoadStatus.Loaded,
                request.RequestFile, request.ExpectedSha256, actualHash, byteLength,
                executionRequest, diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProductProviderUnavailableException exception)
        {
            return Refused(request, actualHash, byteLength,
                RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused,
                "product-provider-unavailable", exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused(request, actualHash, byteLength,
                RaceMenuNpcExecutionRequestFileLoadStatus.SecurityRefused,
                "preset-npc-request-security-refused", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or JsonException or
                                           InvalidDataException or ArgumentException or
                                           FormatException or OverflowException)
        {
            return Refused(request, actualHash, byteLength,
                RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused,
                "preset-npc-request-invalid", exception.Message);
        }
    }

    private void ValidatePath(WorkspacePath requestFile)
    {
        var root = Path.GetPathRoot(requestFile.Value);
        if (!requestFile.IsUnder(workspaceRoot) ||
            !string.Equals(root, @"K:\", StringComparison.OrdinalIgnoreCase) ||
            requestFile.Value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            requestFile.Value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            requestFile.Value.StartsWith(@"\\", StringComparison.Ordinal) ||
            requestFile.Value.IndexOf(':', 2) >= 0)
            throw new UnauthorizedAccessException(
                "Preset NPC request must be an ordinary file under the K-only workspace root.");
        if (!File.Exists(requestFile.Value) || Directory.Exists(requestFile.Value))
            throw new InvalidDataException(
                "Preset NPC request must be an existing ordinary K-local file.");
    }

    private bool HasReparsePath(WorkspacePath path)
    {
        var current = path.Value;
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (string.Equals(current, workspaceRoot.Value, StringComparison.OrdinalIgnoreCase))
                return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return true;
            current = parent;
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}' is not accepted.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static RaceMenuNpcExecutionRequestFileLoadResult Refused(
        RaceMenuNpcExecutionRequestFileLoadRequest request,
        Sha256Hash? actualHash,
        long? byteLength,
        RaceMenuNpcExecutionRequestFileLoadStatus status,
        string code,
        string message) =>
        new(status, request.RequestFile, request.ExpectedSha256, actualHash, byteLength,
            null, [new Diagnostic(code, DiagnosticSeverity.Error, message)]);
}
