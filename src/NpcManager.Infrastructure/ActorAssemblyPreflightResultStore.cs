using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// The physical authority returned by an Actor Assembly preflight publication.
/// The JSON bytes are the exact bytes written and retained-read before the
/// protocol terminal is emitted.
/// </summary>
public sealed record ActorAssemblyPreflightResultDocument(
    ActorAssemblyPreflightArtifact Artifact,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json)
{
    public ActorAssemblyPreflightArtifact Result => Artifact;
}

/// <summary>
/// Holds the pinned owned file after no-overwrite promotion. Disposing this
/// lease releases the handle but preserves the committed artifact. The
/// protocol adapter transfers the lease to its terminal envelope so the file
/// remains pinned through terminal emission.
/// </summary>
public sealed class ActorAssemblyPreflightResultLease : IDisposable, IAsyncDisposable
{
    private FaceGeomHairRegionsOwnedFile? owned;

    internal ActorAssemblyPreflightResultLease(
        ActorAssemblyPreflightResultDocument document,
        FaceGeomHairRegionsOwnedFile owned)
    {
        Document = document ??
            throw new ArgumentNullException(nameof(document));
        this.owned = owned ??
            throw new ArgumentNullException(nameof(owned));
    }

    public ActorAssemblyPreflightResultDocument Document { get; }

    public ActorAssemblyPreflightArtifact Artifact => Document.Artifact;

    public WorkspacePath Path => Document.Path;

    public long Size => Document.Size;

    public string Sha256 => Document.Sha256;

    public ImmutableArray<byte> Utf8Json => Document.Utf8Json;

    /// <summary>
    /// Transfers ownership of the retained pinned handle to the caller.
    /// </summary>
    public IDisposable TransferLease() =>
        Interlocked.Exchange(ref owned, null) ??
        throw new ObjectDisposedException(nameof(ActorAssemblyPreflightResultLease));

    public void Dispose()
    {
        FaceGeomHairRegionsOwnedFile? current =
            Interlocked.Exchange(ref owned, null);
        current?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ActorAssemblyPreflightResultStoreException : IOException
{
    public ActorAssemblyPreflightResultStoreException(
        string code,
        string message,
        Exception? innerException = null,
        bool promoted = false)
        : base(message, innerException)
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

/// <summary>
/// Persists every typed Actor Assembly outcome, including Unknown, as one
/// strict legacy-compatible result document. The result is deliberately an
/// external artifact only: this store does not create a workflow successor or
/// a protocol envelope binding.
/// </summary>
public sealed class ActorAssemblyPreflightResultStore
{
    private const int MaximumResultBytes = 8 * 1024 * 1024;
    private const string Role = "protocol-v2 Actor Assembly preflight result";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IWorkspacePolicy workspacePolicy;
    private readonly WorkspacePath labRoot;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public ActorAssemblyPreflightResultStore(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
    {
        this.workspacePolicy = workspacePolicy ??
            throw new ArgumentNullException(nameof(workspacePolicy));
        this.labRoot = labRoot;
        if (!string.Equals(
                Path.GetPathRoot(labRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputOutsideKDrive,
                "The Actor Assembly result lab root must remain on K:.");

        ImmutableArray<Diagnostic> rootDiagnostics =
            workspacePolicy.Evaluate(labRoot, labRoot)
                .Where(item => item.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
        if (!rootDiagnostics.IsEmpty)
            throw Refused(
                rootDiagnostics[0].Code,
                rootDiagnostics[0].Message);
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(labRoot);
    }

    public async ValueTask<ActorAssemblyPreflightResultLease> WriteNewAsync(
        ActorAssemblyPreflightArtifact artifact,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireFreshDestination(destination);

        byte[] canonical;
        try
        {
            canonical = SerializeCanonical(artifact);
            ValidateCanonical(canonical);
        }
        catch (ActorAssemblyPreflightResultStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                JsonException or NotSupportedException)
        {
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "The Actor Assembly result could not be canonically serialized.",
                exception);
        }

        string parent = Path.GetDirectoryName(destination.Value) ??
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputParentMissing,
                "The Actor Assembly result output has no parent directory.");
        var temporary = new WorkspacePath(Path.Combine(
            parent,
            $".actor-assembly-preflight-{Guid.NewGuid():N}.tmp"));
        FaceGeomHairRegionsOwnedFile? owned = null;
        bool promoted = false;
        try
        {
            owned = boundary.CreateOwnedFile(temporary, destination, Role);
            byte[] staged = await owned.WriteAndReadbackAsync(
                canonical,
                MaximumResultBytes,
                cancellationToken);
            RequireExactReadback(staged, canonical, "staged");
            ValidateCanonical(staged);

            cancellationToken.ThrowIfCancellationRequested();
            owned.PromoteNoOverwrite();
            promoted = true;

            // Promotion is the commit point. Keep using the retained handle;
            // reopening the path here would cross the live lease boundary.
            byte[] retained = await owned.ReadbackExactAsync(
                MaximumResultBytes,
                CancellationToken.None);
            RequireExactReadback(retained, canonical, "promoted retained-handle");
            ValidateCanonical(retained);

            string sha256 = Hash(retained);
            var document = new ActorAssemblyPreflightResultDocument(
                artifact,
                destination,
                retained.LongLength,
                sha256,
                retained.ToImmutableArray());
            var resultLease = new ActorAssemblyPreflightResultLease(document, owned);
            owned = null;
            return resultLease;
        }
        catch (OperationCanceledException exception) when (!promoted)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        catch (ActorAssemblyPreflightResultStoreException exception)
        {
            if (!promoted)
            {
                CleanupOrThrow(owned, exception);
                throw;
            }

            throw new ActorAssemblyPreflightResultStoreException(
                exception.Code,
                exception.Message,
                exception,
                promoted: true);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException)
        {
            if (!promoted)
                CleanupOrThrow(owned, exception);
            throw Refused(
                ClassifyWriteFailure(exception),
                promoted
                    ? "The committed Actor Assembly result failed retained verification."
                    : "The Actor Assembly result transaction was refused.",
                exception,
                promoted);
        }
        finally
        {
            // A successful return transfers ownership to the result lease. A
            // failed transaction releases the handle; pre-commit cleanup above
            // also removes the exact temporary through its retained handle.
            owned?.Dispose();
        }
    }

    public ValueTask<ActorAssemblyPreflightResultLease> PersistAsync(
        ActorAssemblyPreflightArtifact artifact,
        WorkspacePath destination,
        CancellationToken cancellationToken) =>
        WriteNewAsync(artifact, destination, cancellationToken);

    private void RequireFreshDestination(WorkspacePath destination)
    {
        if (!Path.IsPathFullyQualified(destination.Value) ||
            !string.Equals(
                Path.GetPathRoot(destination.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase) ||
            destination.Value.IndexOf(':', 2) >= 0 ||
            HasDotSegment(destination.Value))
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputOutsideKDrive,
                "The Actor Assembly result output must be one absolute ordinary K-local path.");
        if (!destination.IsUnder(labRoot))
            throw Refused(
                ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace,
                "The Actor Assembly result output must remain beneath the exact lab root.");

        ImmutableArray<Diagnostic> diagnostics = workspacePolicy
            .Evaluate(labRoot, destination)
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (!diagnostics.IsEmpty)
            throw Refused(diagnostics[0].Code, diagnostics[0].Message);
        if (File.Exists(destination.Value) || Directory.Exists(destination.Value))
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputExists,
                "The Actor Assembly result output must be fresh and must not overwrite an existing path.");
        try
        {
            boundary.RequireNewFile(destination, Role);
        }
        catch (ActorAssemblyPreflightResultStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                ClassifyPathFailure(exception),
                "The Actor Assembly result output must be a fresh ordinary file with an existing ordinary parent.",
                exception);
        }
    }

    private static byte[] SerializeCanonical(
        ActorAssemblyPreflightArtifact artifact)
    {
        object value = ProjectArtifact(artifact);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumResultBytes)
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "The Actor Assembly result exceeds its admitted size.");
        return bytes;
    }

    private static object ProjectArtifact(
        ActorAssemblyPreflightArtifact artifact) => new
    {
        schemaVersion = artifact.SchemaVersion,
        artifactKind = artifact.ArtifactKind,
        admitted = artifact.Admitted,
        outcome = WireOutcome(artifact.Outcome),
        contractSha256 = UpperHash(artifact.ContractSha256),
        packageManifestSha256 = UpperHash(artifact.PackageManifestSha256),
        baseNpcEvidence = ProjectBaseEvidence(artifact.BaseNpcEvidence),
        placedReferenceEvidence = artifact.PlacedReferenceEvidence is null
            ? null
            : ProjectPlacedEvidence(artifact.PlacedReferenceEvidence),
        diagnosticTarget = artifact.DiagnosticTarget,
        checks = artifact.Checks.Select(ProjectCheck).ToImmutableArray(),
        noWrite = artifact.NoWrite,
        runtimeAuthority = artifact.RuntimeAuthority
    };

    private static object ProjectBaseEvidence(
        ActorAssemblyBaseNpcEvidence evidence) => new
    {
        plugin = evidence.Plugin.Value,
        declaredFormId = evidence.DeclaredFormId.ToString(),
        typedRecord = ProjectRecordObservation(evidence.TypedRecord),
        rawRecord = ProjectRecordObservation(evidence.RawRecord),
        outcome = WireOutcome(evidence.Outcome)
    };

    private static object ProjectPlacedEvidence(
        ActorAssemblyPlacedReferenceEvidence evidence) => new
    {
        plugin = evidence.Plugin.Value,
        declaredFormId = evidence.DeclaredFormId.ToString(),
        typedRecord = ProjectRecordObservation(evidence.TypedRecord),
        rawRecord = ProjectRecordObservation(evidence.RawRecord),
        typedBase = ProjectReferenceObservation(evidence.TypedBase),
        rawNameBase = ProjectReferenceObservation(evidence.RawNameBase),
        outcome = WireOutcome(evidence.Outcome)
    };

    private static object ProjectRecordObservation(
        ActorAssemblyRecordObservation observation) =>
        observation.Status == ActorAssemblyObservationStatus.Found
            ? new
            {
                status = "found",
                signature = observation.Signature!,
                formId = observation.FormId!.Value.ToString()
            }
            : new
            {
                status = WireObservation(observation.Status),
                reason = observation.Reason ??
                    "No record evidence was available."
            };

    private static object ProjectReferenceObservation(
        ActorAssemblyReferenceObservation observation) =>
        observation.Status == ActorAssemblyObservationStatus.Found
            ? new
            {
                status = "found",
                plugin = observation.Plugin!.Value.Value,
                formId = observation.FormId!.Value.ToString()
            }
            : new
            {
                status = WireObservation(observation.Status),
                reason = observation.Reason ??
                    "No reference evidence was available."
            };

    private static object ProjectCheck(ActorAssemblyCheck check) => new
    {
        code = check.Code,
        outcome = WireOutcome(check.Outcome),
        message = check.Message,
        evidence = check.Evidence.Select(item => new
        {
            kind = WireEvidenceKind(item.Kind),
            value = item.Value,
            sha256 = item.Sha256 is null
                ? null
                : item.Sha256.Value.Value.ToUpperInvariant()
        }).ToImmutableArray()
    };

    private static string WireOutcome(ActorAssemblyOutcome value) =>
        value switch
        {
            ActorAssemblyOutcome.Pass => "pass",
            ActorAssemblyOutcome.Blocked => "blocked",
            ActorAssemblyOutcome.Unknown => "unknown",
            ActorAssemblyOutcome.NotApplicable => "notApplicable",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string WireObservation(
        ActorAssemblyObservationStatus value) =>
        value switch
        {
            ActorAssemblyObservationStatus.Found => "found",
            ActorAssemblyObservationStatus.Missing => "missing",
            ActorAssemblyObservationStatus.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static string WireEvidenceKind(
        ActorAssemblyEvidenceKind value) =>
        value switch
        {
            ActorAssemblyEvidenceKind.Contract => "contract",
            ActorAssemblyEvidenceKind.Manifest => "manifest",
            ActorAssemblyEvidenceKind.File => "file",
            ActorAssemblyEvidenceKind.PackageFile => "packageFile",
            ActorAssemblyEvidenceKind.Record => "record",
            ActorAssemblyEvidenceKind.Decision => "decision",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    private static void ValidateCanonical(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumResultBytes)
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "The Actor Assembly result is outside its admitted byte range.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            ValidateResult(document.RootElement);
        }
        catch (ActorAssemblyPreflightResultStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or
                FormatException)
        {
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "The Actor Assembly result failed strict JSON validation.",
                exception);
        }
    }

    private static void ValidateResult(JsonElement root)
    {
        RequireObjectProperties(
            root,
            "result",
            [
                "schemaVersion", "artifactKind", "admitted", "outcome",
                "contractSha256", "packageManifestSha256", "baseNpcEvidence",
                "diagnosticTarget", "checks", "noWrite", "runtimeAuthority"
            ],
            ["placedReferenceEvidence"]);
        RequireInt(root, "schemaVersion", 1);
        RequireString(root, "artifactKind", value =>
            value == ActorAssemblyPreflightSchemas.LegacyResultArtifactKind);
        RequireBoolean(root, "admitted", true);
        RequireOutcome(root, "outcome");
        RequireUpperSha256(root, "contractSha256");
        RequireUpperSha256(root, "packageManifestSha256");
        ValidateBaseEvidence(root.GetProperty("baseNpcEvidence"));
        if (root.TryGetProperty("placedReferenceEvidence", out JsonElement placed))
            ValidatePlacedEvidence(placed);
        RequireNonEmptyString(root, "diagnosticTarget");
        ValidateChecks(root.GetProperty("checks"));
        RequireBoolean(root, "noWrite", true);
        RequireBoolean(root, "runtimeAuthority", false);
    }

    private static void ValidateBaseEvidence(JsonElement value)
    {
        RequireObjectProperties(value, "baseNpcEvidence",
            ["plugin", "declaredFormId", "typedRecord", "rawRecord", "outcome"], []);
        RequireNonEmptyString(value, "plugin");
        RequireNonEmptyString(value, "declaredFormId");
        ValidateRecordObservation(value.GetProperty("typedRecord"));
        ValidateRecordObservation(value.GetProperty("rawRecord"));
        RequireOutcome(value, "outcome");
    }

    private static void ValidatePlacedEvidence(JsonElement value)
    {
        RequireObjectProperties(value, "placedReferenceEvidence",
            [
                "plugin", "declaredFormId", "typedRecord", "rawRecord",
                "typedBase", "rawNameBase", "outcome"
            ], []);
        RequireNonEmptyString(value, "plugin");
        RequireNonEmptyString(value, "declaredFormId");
        ValidateRecordObservation(value.GetProperty("typedRecord"));
        ValidateRecordObservation(value.GetProperty("rawRecord"));
        ValidateReferenceObservation(value.GetProperty("typedBase"));
        ValidateReferenceObservation(value.GetProperty("rawNameBase"));
        RequireOutcome(value, "outcome");
    }

    private static void ValidateRecordObservation(JsonElement value)
    {
        RequireObject(value, "record observation");
        string status = RequireNonEmptyString(value, "status");
        if (status == "found")
        {
            RequireObjectProperties(value, "found record observation",
                ["status", "signature", "formId"], []);
            RequireNonEmptyString(value, "signature");
            RequireNonEmptyString(value, "formId");
        }
        else if (status is "missing" or "unknown")
        {
            RequireObjectProperties(value, "unavailable record observation",
                ["status", "reason"], []);
            RequireNonEmptyString(value, "reason");
        }
        else
        {
            throw Invalid("Unknown record observation status.");
        }
    }

    private static void ValidateReferenceObservation(JsonElement value)
    {
        RequireObject(value, "reference observation");
        string status = RequireNonEmptyString(value, "status");
        if (status == "found")
        {
            RequireObjectProperties(value, "found reference observation",
                ["status", "plugin", "formId"], []);
            RequireNonEmptyString(value, "plugin");
            RequireNonEmptyString(value, "formId");
        }
        else if (status is "missing" or "unknown")
        {
            RequireObjectProperties(value, "unavailable reference observation",
                ["status", "reason"], []);
            RequireNonEmptyString(value, "reason");
        }
        else
        {
            throw Invalid("Unknown reference observation status.");
        }
    }

    private static void ValidateChecks(JsonElement value)
    {
        RequireArray(value, "checks", 0, 4096);
        foreach (JsonElement check in value.EnumerateArray())
        {
            RequireObjectProperties(check, "check",
                ["code", "outcome", "message", "evidence"], []);
            RequireNonEmptyString(check, "code");
            RequireOutcome(check, "outcome");
            RequireNonEmptyString(check, "message");
            JsonElement evidence = check.GetProperty("evidence");
            RequireArray(evidence, "check evidence", 0, 4096);
            foreach (JsonElement item in evidence.EnumerateArray())
            {
                RequireObjectProperties(item, "check evidence item",
                    ["kind", "value"], ["sha256"]);
                string kind = RequireNonEmptyString(item, "kind");
                if (kind is not ("contract" or "manifest" or "file" or
                    "packageFile" or "record" or "decision"))
                    throw Invalid("Unknown Actor Assembly evidence kind.");
                RequireNonEmptyString(item, "value");
                if (item.TryGetProperty("sha256", out _))
                    RequireUpperSha256(item, "sha256");
            }
        }
    }

    private static void RequireObjectProperties(
        JsonElement value,
        string role,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        RequireObject(value, role);
        var allowed = required.Concat(optional)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw Invalid($"Unknown property '{property.Name}' in {role}.");
            if (!seen.Add(property.Name))
                throw Invalid($"Duplicate property '{property.Name}' in {role}.");
        }
        foreach (string name in required)
            if (!value.TryGetProperty(name, out _))
                throw Invalid($"Missing property '{name}' in {role}.");
    }

    private static void RequireObject(JsonElement value, string role)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid($"{role} must be an object.");
    }

    private static void RequireArray(
        JsonElement value,
        string role,
        int minimum,
        int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimum ||
            value.GetArrayLength() > maximum)
            throw Invalid($"{role} must contain {minimum}-{maximum} rows.");
    }

    private static string RequireNonEmptyString(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw Invalid($"Property '{name}' must be a non-empty string.");
        string text = value.GetString() ?? string.Empty;
        if (text.Length == 0 || text.Length > 4096)
            throw Invalid($"Property '{name}' must be a non-empty bounded string.");
        return text;
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        Func<string, bool> predicate)
    {
        string value = RequireNonEmptyString(parent, name);
        if (!predicate(value))
            throw Invalid($"Property '{name}' has an inadmissible value.");
    }

    private static void RequireInt(
        JsonElement parent,
        string name,
        int expected)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int actual) || actual != expected)
            throw Invalid($"Property '{name}' must be integer {expected}.");
    }

    private static void RequireBoolean(
        JsonElement parent,
        string name,
        bool expected)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.True &&
            value.ValueKind != JsonValueKind.False ||
            value.GetBoolean() != expected)
            throw Invalid($"Property '{name}' must be {expected}.");
    }

    private static void RequireOutcome(JsonElement parent, string name)
    {
        string outcome = RequireNonEmptyString(parent, name);
        if (outcome is not ("pass" or "blocked" or "unknown" or
            "notApplicable"))
            throw Invalid($"Property '{name}' has an unknown outcome.");
    }

    private static void RequireUpperSha256(
        JsonElement parent,
        string name)
    {
        string value = RequireNonEmptyString(parent, name);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F')))
            throw Invalid($"Property '{name}' must be one uppercase SHA-256.");
    }

    private static string UpperHash(Sha256Hash value) =>
        value.Value.ToUpperInvariant();

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static bool HasDotSegment(string path) =>
        path.Split('\\').Any(segment => segment is "." or "..");

    private static void RequireExactReadback(
        byte[] observed,
        byte[] expected,
        string phase)
    {
        if (!observed.AsSpan().SequenceEqual(expected))
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                $"The {phase} Actor Assembly result readback differs from its canonical bytes.");
    }

    private static void CleanupOrThrow(
        FaceGeomHairRegionsOwnedFile? owned,
        Exception primary)
    {
        if (owned is null)
            return;
        if (!owned.TryDelete(out string? cleanupFailure))
            throw Refused(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                "The Actor Assembly result failed and its exact owned file could not be removed: " +
                cleanupFailure,
                primary);
    }

    private static string ClassifyPathFailure(Exception exception) =>
        exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
            ? ProtocolV2DiagnosticCodes.ReparsePointRefused
            : exception.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                ? ProtocolV2DiagnosticCodes.SchemaOutputExists
                : ProtocolV2DiagnosticCodes.SchemaOutputParentMissing;

    private static string ClassifyWriteFailure(Exception exception) =>
        exception.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)
            ? ProtocolV2DiagnosticCodes.ReparsePointRefused
            : exception.Message.Contains("exist", StringComparison.OrdinalIgnoreCase)
                ? ProtocolV2DiagnosticCodes.SchemaOutputExists
                : ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed;

    private static ActorAssemblyPreflightResultStoreException Refused(
        string code,
        string message,
        Exception? innerException = null,
        bool promoted = false) =>
        new(code, message, innerException, promoted);

    private static ActorAssemblyPreflightResultStoreException Invalid(
        string message) =>
        Refused(
            ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
            message);
}
