using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Persists one immutable main-workspace review session only after every
/// child-artifact handoff and the serialized temporary file revalidate.
/// </summary>
public sealed class SkyrimMainWorkspaceSessionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath projectRoot)
    : ISkyrimMainWorkspaceSessionService
{
    private const string SchemaVersion = "1";
    private const int MaximumSessionBytes = 4 * 1024 * 1024;
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private readonly WorkspacePath _sessionRoot = new(Path.Combine(
        projectRoot.Value,
        "03-builds",
        "work",
        "main-workspace-sessions"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new WorkspacePathJsonConverter(),
            new Sha256HashJsonConverter(),
            new PluginNameJsonConverter(),
            new FormIdJsonConverter()
        }
    };

    public async ValueTask<SkyrimMainWorkspaceSessionResult>
        SaveAsync(
            SkyrimMainWorkspaceSessionSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Session);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics =
            ValidateDestination(request.Destination, mustExist: false)
                .ToBuilder();
        if (File.Exists(request.Destination.Value))
            diagnostics.Add(Error(
                "main-workspace-session-exists",
                "Main-workspace sessions never overwrite an existing document."));
        diagnostics.AddRange(await ValidateSessionAsync(
            request.Session, cancellationToken));
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        string temporary = request.Destination.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_sessionRoot.Value);
            diagnostics.AddRange(
                ValidateDestination(
                    request.Destination, mustExist: false));
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                request.Session, JsonOptions);
            if (bytes.Length is <= 0 or > MaximumSessionBytes)
                return Refused([
                    Error(
                        "main-workspace-session-size",
                        "Serialized sessions are empty or exceed 4 MiB.")
                ]);
            await File.WriteAllBytesAsync(
                    temporary, bytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            SessionDecode readback = await DecodeAsync(
                temporary,
                expectedHash: Hash(bytes),
                cancellationToken);
            if (readback.Session is null ||
                readback.Sha256 != Hash(bytes) ||
                !Equivalent(request.Session, readback.Session) ||
                HasErrors(readback.Diagnostics))
                throw new InvalidDataException(
                    "Temporary session failed exact semantic and hash readback.");

            File.Move(
                temporary,
                request.Destination.Value,
                overwrite: false);
            SessionDecode committed = await DecodeAsync(
                request.Destination.Value,
                Hash(bytes),
                CancellationToken.None);
            if (committed.Session is null ||
                committed.Sha256 != Hash(bytes) ||
                !Equivalent(request.Session, committed.Session) ||
                HasErrors(committed.Diagnostics))
            {
                TryDelete(request.Destination.Value);
                throw new InvalidDataException(
                    "Committed session failed exact semantic and hash readback.");
            }
            return new SkyrimMainWorkspaceSessionResult(
                true,
                committed.Session,
                request.Destination,
                committed.Sha256,
                committed.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or
            InvalidDataException or ArgumentException)
        {
            return Refused([
                Error(
                    "main-workspace-session-write",
                    exception.Message)
            ]);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async ValueTask<SkyrimMainWorkspaceSessionResult>
        ReadAsync(
            SkyrimMainWorkspaceSessionReadRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics =
            ValidateDestination(request.Path, mustExist: true)
                .ToBuilder();
        if (!IsHashValid(request.ExpectedSha256))
            diagnostics.Add(Error(
                "main-workspace-session-expected-hash",
                "Session readback requires one non-default expected SHA-256."));
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        try
        {
            SessionDecode decoded = await DecodeAsync(
                request.Path.Value,
                request.ExpectedSha256,
                cancellationToken);
            diagnostics.AddRange(decoded.Diagnostics);
            if (decoded.Session is null ||
                decoded.Sha256 is null ||
                HasErrors(diagnostics))
                return Refused(diagnostics);
            return new SkyrimMainWorkspaceSessionResult(
                true,
                decoded.Session,
                request.Path,
                decoded.Sha256,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or
            InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error(
                "main-workspace-session-read",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private async ValueTask<SessionDecode> DecodeAsync(
        string path,
        Sha256Hash expectedHash,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (info.Length is <= 0 or > MaximumSessionBytes)
            return new SessionDecode(
                null,
                null,
                [
                    Error(
                        "main-workspace-session-size",
                        "The session is empty or exceeds 4 MiB.")
                ]);
        byte[] bytes = await File.ReadAllBytesAsync(
                path, cancellationToken).ConfigureAwait(false);
        Sha256Hash actualHash = Hash(bytes);
        if (actualHash != expectedHash)
            return new SessionDecode(
                null,
                actualHash,
                [
                    Error(
                        "main-workspace-session-hash-changed",
                        "The session bytes do not match the expected SHA-256.")
                ]);
        SkyrimMainWorkspaceSession? session =
            JsonSerializer.Deserialize<SkyrimMainWorkspaceSession>(
                bytes, JsonOptions);
        if (session is null)
            return new SessionDecode(
                null,
                actualHash,
                [
                    Error(
                        "main-workspace-session-schema",
                        "The session document is empty.")
                ]);
        ImmutableArray<Diagnostic> diagnostics =
            await ValidateSessionAsync(session, cancellationToken);
        return new SessionDecode(
            HasErrors(diagnostics) ? null : session,
            actualHash,
            diagnostics);
    }

    private async ValueTask<ImmutableArray<Diagnostic>>
        ValidateSessionAsync(
            SkyrimMainWorkspaceSession session,
            CancellationToken cancellationToken)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!string.Equals(
                session.SchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal))
            diagnostics.Add(Error(
                "main-workspace-session-schema",
                "Only main-workspace session schema 1 is accepted."));
        if (!IsHashValid(session.IntakeFingerprint))
            diagnostics.Add(Error(
                "main-workspace-session-intake-hash",
                "The session requires a non-default intake fingerprint."));
        if (session.RuntimeAuthority)
            diagnostics.Add(Error(
                "main-workspace-session-runtime-authority",
                "Static workbench sessions may never claim runtime authority."));
        if (session.Selection.IsDefault ||
            session.Drafts.IsDefault ||
            session.Artifacts.IsDefault)
            diagnostics.Add(Error(
                "main-workspace-session-collections",
                "Session collections must be explicit immutable arrays."));

        var selected =
            new HashSet<SkyrimMainWorkspaceIdentity>();
        foreach (SkyrimMainWorkspaceIdentity? identity in
                 session.Selection)
        {
            if (!ValidateIdentity(identity, diagnostics))
                continue;
            if (!selected.Add(identity))
                diagnostics.Add(Error(
                    "main-workspace-session-selection-duplicate",
                    $"Selection contains duplicate identity '{identity}'."));
        }
        var draftIdentities =
            new HashSet<SkyrimMainWorkspaceIdentity>();
        foreach (SkyrimMainWorkspaceDraft draft in
                 session.Drafts)
        {
            if (draft is null)
            {
                diagnostics.Add(Error(
                    "main-workspace-session-draft-null",
                    "Session drafts may not contain null."));
                continue;
            }
            if (!ValidateIdentity(draft.Identity, diagnostics))
                continue;
            if (!selected.Contains(draft.Identity))
                diagnostics.Add(Error(
                    "main-workspace-session-draft-unselected",
                    $"Draft '{draft.Identity}' is not selected."));
            if (!draftIdentities.Add(draft.Identity))
                diagnostics.Add(Error(
                    "main-workspace-session-draft-duplicate",
                    $"Draft '{draft.Identity}' occurs more than once."));
        }

        var artifactKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimMainWorkspaceArtifactHandoff artifact in
                 session.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null)
            {
                diagnostics.Add(Error(
                    "main-workspace-session-artifact-null",
                    "Session artifacts may not contain null."));
                continue;
            }
            if (!ValidateIdentity(artifact.Identity, diagnostics))
                continue;
            if (!selected.Contains(artifact.Identity))
                diagnostics.Add(Error(
                    "main-workspace-artifact-unselected",
                    $"Artifact '{artifact.Kind}' is not bound to a selected identity."));
            if (string.IsNullOrWhiteSpace(artifact.Kind) ||
                artifact.Kind.Length > 128 ||
                artifact.Kind != artifact.Kind.Trim() ||
                artifact.Kind.Any(char.IsControl))
                diagnostics.Add(Error(
                    "main-workspace-artifact-kind",
                    "Artifact kinds must be trimmed, non-empty, and at most 128 characters."));
            if (!artifactKeys.Add(
                    $"{artifact.Identity}|{artifact.Kind}"))
                diagnostics.Add(Error(
                    "main-workspace-artifact-duplicate",
                    $"Artifact '{artifact.Kind}' occurs more than once for '{artifact.Identity}'."));
            if (artifact.RuntimeAuthority)
                diagnostics.Add(Error(
                    "main-workspace-artifact-runtime-authority",
                    "Static child artifacts may never claim runtime authority."));
            await ValidateArtifactFileAsync(
                artifact.Path,
                artifact.Sha256,
                "artifact",
                diagnostics,
                cancellationToken);

            bool hasProposalPath = artifact.ProposalPath.HasValue;
            bool hasProposalHash = artifact.ProposalSha256.HasValue;
            if (hasProposalPath != hasProposalHash)
                diagnostics.Add(Error(
                    "main-workspace-artifact-proposal-pair",
                    "Proposal path and SHA-256 must be supplied together."));
            else if (artifact.ProposalPath is { } proposalPath &&
                     artifact.ProposalSha256 is { } proposalHash)
                await ValidateArtifactFileAsync(
                    proposalPath,
                    proposalHash,
                    "proposal",
                    diagnostics,
                    cancellationToken);
        }
        return diagnostics.ToImmutable();
    }

    private async ValueTask ValidateArtifactFileAsync(
        WorkspacePath path,
        Sha256Hash expectedHash,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
        {
            diagnostics.Add(Error(
                "main-workspace-artifact-path",
                $"The {role} path is missing."));
            return;
        }
        if (!path.IsUnder(projectRoot))
        {
            diagnostics.Add(Error(
                "main-workspace-artifact-path",
                $"The {role} must remain under the exact project root."));
            return;
        }
        diagnostics.AddRange(
            policy.EvaluateReadRoot(projectRoot, path));
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error(
                "main-workspace-artifact-missing",
                $"The {role} file no longer exists."));
            return;
        }
        FileInfo info = new(path.Value);
        if (info.Length is <= 0 or > MaximumArtifactBytes)
        {
            diagnostics.Add(Error(
                "main-workspace-artifact-size",
                $"The {role} file is empty or exceeds 512 MiB."));
            return;
        }
        if (!IsHashValid(expectedHash))
        {
            diagnostics.Add(Error(
                "main-workspace-artifact-hash",
                $"The {role} requires a non-default SHA-256."));
            return;
        }
        Sha256Hash actual = await HashFileAsync(
            path.Value, cancellationToken);
        if (actual != expectedHash)
            diagnostics.Add(Error(
                "main-workspace-artifact-hash-changed",
                $"The {role} file changed after child verification."));
    }

    private ImmutableArray<Diagnostic> ValidateDestination(
        WorkspacePath path,
        bool mustExist)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        string? parent = Path.GetDirectoryName(path.Value);
        if (!projectRoot.IsUnder(labRoot) ||
            !path.IsUnder(_sessionRoot) ||
            parent is null ||
            !string.Equals(
                Path.GetFullPath(parent),
                _sessionRoot.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !path.Value.EndsWith(
                ".npc-workspace.json",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "main-workspace-session-path",
                "Sessions must use a fresh .npc-workspace.json file in the fixed project session directory."));
        diagnostics.AddRange(
            mustExist
                ? policy.EvaluateReadRoot(projectRoot, path)
                : policy.Evaluate(projectRoot, path));
        if (mustExist && !File.Exists(path.Value))
            diagnostics.Add(Error(
                "main-workspace-session-missing",
                "The requested main-workspace session does not exist."));
        return diagnostics.ToImmutable();
    }

    private static bool ValidateIdentity(
        SkyrimMainWorkspaceIdentity? identity,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (identity is null ||
            string.IsNullOrWhiteSpace(
                identity.OwnerPlugin.Value) ||
            string.IsNullOrWhiteSpace(
                identity.WinningProvider.Value) ||
            identity.FormId.Value == 0 ||
            identity.Signature is not ("NPC_" or "LVLN"))
            diagnostics.Add(Error(
                "main-workspace-session-identity",
                "Session identities require owner, winner, non-zero FormID, and NPC_/LVLN signature."));
        else
            return true;
        return false;
    }

    private static bool Equivalent(
        SkyrimMainWorkspaceSession left,
        SkyrimMainWorkspaceSession right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.IntakeFingerprint == right.IntakeFingerprint &&
        left.Selection.SequenceEqual(right.Selection) &&
        left.Drafts.SequenceEqual(right.Drafts) &&
        left.Artifacts.SequenceEqual(right.Artifacts) &&
        left.RuntimeAuthority == right.RuntimeAuthority;

    private static bool IsHashValid(Sha256Hash hash) =>
        hash.Value is { Length: 64 } &&
        hash.Value.Any(character => character != '0');

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream, cancellationToken)));
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static SkyrimMainWorkspaceSessionResult Refused(
        IEnumerable<Diagnostic> diagnostics) =>
        new(
            false,
            null,
            null,
            null,
            diagnostics.ToImmutableArray());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A unique temporary sibling is never accepted as authority.
        }
    }

    private sealed record SessionDecode(
        SkyrimMainWorkspaceSession? Session,
        Sha256Hash? Sha256,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed class WorkspacePathJsonConverter
        : JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value
                ? new WorkspacePath(value)
                : throw new JsonException(
                    "Workspace paths must be absolute strings.");

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashJsonConverter
        : JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value
                ? new Sha256Hash(value)
                : throw new JsonException(
                    "SHA-256 values must be strings.");

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class PluginNameJsonConverter
        : JsonConverter<PluginName>
    {
        public override PluginName Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value
                ? new PluginName(value)
                : throw new JsonException(
                    "Plugin names must be strings.");

        public override void Write(
            Utf8JsonWriter writer,
            PluginName value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class FormIdJsonConverter
        : JsonConverter<FormId>
    {
        public override FormId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value &&
            FormId.TryParse(value, out FormId formId)
                ? formId
                : throw new JsonException(
                    "FormIDs must be hexadecimal strings.");

        public override void Write(
            Utf8JsonWriter writer,
            FormId value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
