using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record AgentWorkflowBundleDocument(
    AgentWorkflowBundle Bundle,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json);

internal sealed class AgentWorkflowBundleDocumentLease(
    AgentWorkflowBundleDocument document,
    IDisposable artifactLease) : IDisposable
{
    private IDisposable? artifactLease = artifactLease;

    internal AgentWorkflowBundleDocument Document { get; } = document;

    public void Dispose() => Interlocked.Exchange(
        ref artifactLease,
        null)?.Dispose();
}

public sealed class AgentWorkflowCodecException : IOException
{
    public AgentWorkflowCodecException(
        string code,
        string message,
        Exception? innerException = null) :
        base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class AgentWorkflowBundleCodec
{
    private const int MaximumBundleBytes = 4 * 1024 * 1024;
    private const string Role = "agent workflow bundle";
    private const uint GenericRead = 0x8000_0000;
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters =
        {
            new WorkspacePathJsonConverter(),
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private readonly IWorkspacePolicy workspacePolicy;
    private readonly WorkspacePath labRoot;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;
    private readonly JsonSerializerOptions jsonOptions = JsonOptions;

    public AgentWorkflowBundleCodec(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        this.workspacePolicy = workspacePolicy;
        this.labRoot = labRoot;
        if (!string.Equals(
                Path.GetPathRoot(labRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
            throw Refused(
                "workflow-path-outside-lab",
                "The workflow lab root must remain on K:.");
        RequirePolicyAllowed(labRoot, write: true);
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(labRoot);
    }

    public byte[] ComputeCanonicalBytes(AgentWorkflowBundle bundle)
    {
        try
        {
            AgentWorkflowContractValidation.Validate(bundle);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                bundle,
                jsonOptions);
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                bytes = Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes)
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
            if (bytes.Length is <= 0 or > MaximumBundleBytes)
                throw Refused(
                    "workflow-file-size-invalid",
                    "The canonical workflow bundle is outside the admitted 1..4 MiB range.");
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                throw Refused(
                    "workflow-json-noncanonical",
                    "Canonical workflow JSON must use LF line endings.");
            return bytes;
        }
        catch (AgentWorkflowCodecException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                NotSupportedException)
        {
            throw Refused(
                "workflow-contract-invalid",
                "The workflow bundle contract is invalid.",
                exception);
        }
    }

    public AgentWorkflowBundleDocument Load(
        WorkspacePath path,
        string expectedSha256)
    {
        RequireHash(expectedSha256);
        RequireWithinLab(path);
        RequirePolicyAllowed(path, write: false);

        byte[] bytes;
        try
        {
            bytes = ReadPinnedBundleExact(path);
        }
        catch (AgentWorkflowCodecException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(
                "1..",
                StringComparison.Ordinal))
        {
            throw Refused(
                "workflow-file-size-invalid",
                "The workflow bundle is outside the admitted 1..4 MiB range.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "workflow-file-not-ordinary",
                "The workflow bundle must be one existing ordinary file.",
                exception);
        }

        string observedSha256 = Hash(bytes);
        if (!string.Equals(
                observedSha256,
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "workflow-hash-mismatch",
                "The workflow bundle SHA-256 does not match the expected digest.");

        AgentWorkflowBundle bundle = ParseStrict(bytes);
        ValidatePhysicalBindings(bundle);
        byte[] canonical = ComputeCanonicalBytes(bundle);
        if (!canonical.AsSpan().SequenceEqual(bytes))
            throw Refused(
                "workflow-json-noncanonical",
                "The workflow bundle is not canonical Actorwright JSON.");

        return new AgentWorkflowBundleDocument(
            bundle,
            path,
            bytes.LongLength,
            observedSha256,
            bytes.ToImmutableArray());
    }

    private static byte[] ReadPinnedBundleExact(WorkspacePath path)
    {
        using PinnedWorkflowRead source = OpenPinnedRead(
            path,
            "workflow bundle");
        return source.ReadBundleBytes(MaximumBundleBytes);
    }

    private static string ToNativePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : @"\\?\" + path;

    public AgentWorkflowBundleDocument WriteNew(
        AgentWorkflowBundle bundle,
        WorkspacePath output)
    {
        using AgentWorkflowBundleDocumentLease retained =
            WriteNewRetained(bundle, output);
        return retained.Document;
    }

    internal AgentWorkflowBundleDocumentLease WriteNewRetained(
        AgentWorkflowBundle bundle,
        WorkspacePath output)
    {
        AdmitFreshOutput(output);

        ValidatePhysicalBindings(bundle);
        byte[] canonical = ComputeCanonicalBytes(bundle);
        string expectedSha256 = Hash(canonical);
        var temporary = new WorkspacePath(
            output.Value + ".tmp-" + Guid.NewGuid().ToString("N"));
        FaceGeomHairRegionsOwnedFile? staged = null;
        try
        {
            staged = fileSystem.CreateOwned(
                temporary,
                output,
                Role);
            byte[] readback = staged.WriteAndReadbackAsync(
                    canonical,
                    MaximumBundleBytes,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!readback.AsSpan().SequenceEqual(canonical) ||
                !string.Equals(
                    Hash(readback),
                    expectedSha256,
                    StringComparison.Ordinal))
                throw Refused(
                    "workflow-staged-readback-mismatch",
                    "The staged workflow bundle failed exact readback.");
            staged.PromoteNoOverwrite();
            byte[] promotedReadback = staged.ReadbackExactAsync(
                    MaximumBundleBytes,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!promotedReadback.AsSpan().SequenceEqual(canonical) ||
                !string.Equals(
                    Hash(promotedReadback),
                    expectedSha256,
                    StringComparison.Ordinal))
                throw Refused(
                    "workflow-promoted-readback-mismatch",
                    "The promoted workflow bundle changed during publication.");
            AgentWorkflowBundle reopenedBundle = ParseStrict(
                promotedReadback);
            ValidatePhysicalBindings(reopenedBundle);
            byte[] reopenedCanonical = ComputeCanonicalBytes(
                reopenedBundle);
            if (!reopenedCanonical.AsSpan().SequenceEqual(promotedReadback))
                throw Refused(
                    "workflow-json-noncanonical",
                    "The promoted workflow bundle is not canonical Actorwright JSON.");
            var document = new AgentWorkflowBundleDocument(
                reopenedBundle,
                output,
                promotedReadback.LongLength,
                expectedSha256,
                promotedReadback.ToImmutableArray());
            var retained = new AgentWorkflowBundleDocumentLease(
                document,
                staged);
            staged = null;
            return retained;
        }
        catch (Exception exception)
        {
            AgentWorkflowCodecException primary = NormalizeWriteFailure(
                exception,
                output);
            if (staged is not null &&
                !staged.TryDelete(out string? cleanupFailure))
                throw Refused(
                    primary.Code,
                    $"{primary.Message} Cleanup also failed: {cleanupFailure}",
                    primary);
            throw primary;
        }
        finally
        {
            staged?.Dispose();
        }
    }

    public void AdmitFreshOutput(WorkspacePath output)
    {
        RequireWithinLab(output);
        string? parentValue = Path.GetDirectoryName(output.Value);
        if (string.IsNullOrWhiteSpace(parentValue) ||
            !Directory.Exists(parentValue))
            throw Refused(
                "workflow-parent-missing",
                "The workflow output parent must already exist.");
        var parent = new WorkspacePath(parentValue);
        RequirePolicyAllowed(parent, write: true);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(parent.Value);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Refused(
                "workflow-output-parent-refused",
                "The workflow output parent could not be admitted as an ordinary directory.",
                exception);
        }
        if ((attributes &
             (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            (attributes & FileAttributes.Directory) == 0)
            throw Refused(
                "workflow-output-parent-refused",
                "The workflow output parent must be an ordinary non-reparse directory.");
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            throw Refused(
                "workflow-output-exists",
                "The workflow output must be a fresh path.");
    }

    private AgentWorkflowBundle ParseStrict(byte[] bytes)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            EnsureStrictJsonShape(
                parsed.RootElement,
                typeof(AgentWorkflowBundle),
                "$");
            AgentWorkflowBundle bundle =
                JsonSerializer.Deserialize<AgentWorkflowBundle>(
                    bytes,
                    jsonOptions) ??
                throw Refused(
                    "workflow-json-invalid",
                    "The workflow bundle JSON is empty.");
            try
            {
                AgentWorkflowContractValidation.Validate(bundle);
            }
            catch (ArgumentException exception)
            {
                throw Refused(
                    "workflow-contract-invalid",
                    "The decoded workflow bundle violates its closed contract.",
                    exception);
            }
            return bundle;
        }
        catch (AgentWorkflowCodecException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Refused(
                "workflow-json-invalid",
                "The workflow bundle is not valid strict JSON.",
                exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw Refused(
                "workflow-contract-invalid",
                "The decoded workflow bundle violates its closed contract.",
                exception);
        }
    }

    private static void EnsureStrictJsonShape(
        JsonElement element,
        Type declaredType,
        string path)
    {
        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            if (element.ValueKind != JsonValueKind.Array)
                return;
            Type itemType = type.GetGenericArguments()[0];
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                EnsureStrictJsonShape(item, itemType, $"{path}[{index++}]");
            return;
        }
        if (element.ValueKind != JsonValueKind.Object ||
            type == typeof(WorkspacePath))
            return;

        JsonTypeInfo metadata = JsonOptions.GetTypeInfo(type);
        if (metadata.Kind != JsonTypeInfoKind.Object)
            return;
        var properties = metadata.Properties.ToDictionary(
            property => property.Name,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw Refused(
                    "workflow-json-duplicate-property",
                    $"Duplicate JSON property '{path}.{property.Name}'.");
            if (!properties.TryGetValue(
                    property.Name,
                    out JsonPropertyInfo? propertyInfo))
                throw Refused(
                    "workflow-json-unknown-property",
                    $"Unknown JSON property '{path}.{property.Name}'.");
            EnsureStrictJsonShape(
                property.Value,
                propertyInfo.PropertyType,
                $"{path}.{property.Name}");
        }
    }

    private void ValidatePhysicalBindings(AgentWorkflowBundle bundle)
    {
        try
        {
            AgentWorkflowContractValidation.Validate(bundle);
        }
        catch (ArgumentException exception)
        {
            throw Refused(
                "workflow-contract-invalid",
                "The workflow bundle contract is invalid.",
                exception);
        }

        foreach (WorkflowArtifactBinding artifact in bundle.Artifacts)
        {
            RequireWithinLab(artifact.Path);
            RequirePolicyAllowed(artifact.Path, write: false);
            try
            {
                if (artifact.Size <= 0)
                    throw Refused(
                        "workflow-artifact-binding-mismatch",
                        $"Workflow artifact '{artifact.Kind}' has an unsupported physical size binding.");
                using PinnedWorkflowRead source = OpenPinnedRead(
                    artifact.Path,
                    $"workflow artifact '{artifact.Kind}'");
                long physicalSize = source.Length;
                if (physicalSize != artifact.Size ||
                    !string.Equals(
                        source.ComputeSha256(),
                        artifact.Sha256,
                        StringComparison.Ordinal) ||
                    source.Length != physicalSize)
                    throw Refused(
                        "workflow-artifact-binding-mismatch",
                        $"Workflow artifact '{artifact.Kind}' changed size or SHA-256.");
                if (string.Equals(
                        artifact.Kind,
                        WorkflowArtifactKinds.NpcFinishCoreProposal,
                        StringComparison.Ordinal))
                {
                    byte[] proposalBytes = source.ReadBundleBytes(
                        MaximumBundleBytes);
                    SkyrimNpcFinishCoreProposal proposal =
                        SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                            proposalBytes,
                            labRoot);
                    byte[] canonical =
                        SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                            proposal,
                            labRoot);
                    string embeddedSemanticSha256 =
                        SkyrimNpcFinishCoreDocumentCodec
                            .HashProposalWithoutSelf(proposalBytes)
                            .Value;
                    string bindingSemanticSha256 =
                        embeddedSemanticSha256.ToUpperInvariant();
                    if (!canonical.AsSpan().SequenceEqual(proposalBytes) ||
                        !string.Equals(
                            proposal.ProposalSha256?.Value,
                            embeddedSemanticSha256,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            artifact.SemanticSha256,
                            bindingSemanticSha256,
                            StringComparison.Ordinal))
                        throw Refused(
                            "workflow-artifact-semantic-hash-mismatch",
                            "The Finish Core proposal semantic SHA-256 does not match its canonical bytes.");
                }
                source.VerifyRetainedIdentity();
            }
            catch (AgentWorkflowCodecException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or
                    UnauthorizedAccessException)
            {
                throw Refused(
                    "workflow-artifact-binding-mismatch",
                    $"Workflow artifact '{artifact.Kind}' is absent, nonordinary, or changed.",
                    exception);
            }
        }
    }

    private void RequireWithinLab(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot))
            throw Refused(
                "workflow-path-outside-lab",
                "Workflow paths must remain under the exact K-local lab root.");
    }

    private void RequirePolicyAllowed(WorkspacePath path, bool write)
    {
        ImmutableArray<Diagnostic> diagnostics = write
            ? workspacePolicy.Evaluate(labRoot, path)
            : workspacePolicy.EvaluateReadRoot(labRoot, path);
        Diagnostic? refusal = diagnostics.FirstOrDefault(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (refusal is null)
            return;
        string code = string.Equals(
            refusal.Code,
            "reparse-point-refused",
            StringComparison.Ordinal)
            ? "workflow-reparse-refused"
            : "workflow-path-refused";
        throw Refused(code, refusal.Message);
    }

    private static void RequireHash(string sha256)
    {
        if (sha256 is null || sha256.Length != 64 ||
            sha256.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F'))))
            throw Refused(
                "workflow-hash-invalid",
                "The expected workflow digest must be one uppercase SHA-256.");
    }

    private static AgentWorkflowCodecException NormalizeWriteFailure(
        Exception exception,
        WorkspacePath output)
    {
        if (exception is AgentWorkflowCodecException codecException)
            return codecException;
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            return Refused(
                "workflow-output-exists",
                "The workflow output became occupied before no-overwrite promotion.",
                exception);
        return Refused(
            "workflow-write-failed",
            "The workflow bundle fresh-write transaction failed.",
            exception);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static AgentWorkflowCodecException Refused(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed class WorkspacePathJsonConverter :
        JsonConverter<WorkspacePath>
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

    private static PinnedWorkflowRead OpenPinnedRead(
        WorkspacePath path,
        string role)
    {
        string canonical = FaceGeomHairRegionsPinnedFileSystem.Canonical(
            path.Value);
        SafeFileHandle handle = CreateFileW(
            ToNativePath(canonical),
            GenericRead | FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint |
            FileFlagOverlapped |
            FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(new Win32Exception(error).Message);
        }
        try
        {
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidatePinnedHandle(handle, canonical, role);
            var stream = new FileStream(
                handle,
                FileAccess.Read,
                64 * 1024,
                isAsync: true);
            handle = null!;
            return new PinnedWorkflowRead(
                canonical,
                role,
                identity,
                stream);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static FaceGeomHairRegionsWindowsFileIdentity
        ValidatePinnedHandle(
            SafeFileHandle handle,
            string canonical,
            string role)
    {
        FaceGeomHairRegionsWindowsFileIdentity identity =
            FaceGeomHairRegionsWindowsHandleApi.ReadIdentity(handle);
        if ((identity.Attributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw new UnauthorizedAccessException(
                $"The {role} handle is not an ordinary file.");
        string finalPath = FaceGeomHairRegionsPinnedFileSystem.Canonical(
            FaceGeomHairRegionsWindowsHandleApi.GetFinalDosPath(handle));
        if (!string.Equals(
                finalPath,
                canonical,
                StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"The {role} handle resolved to a different path.");
        return identity;
    }

    private sealed class PinnedWorkflowRead(
        string path,
        string role,
        FaceGeomHairRegionsWindowsFileIdentity identity,
        FileStream stream) : IDisposable
    {
        public long Length => stream.Length;

        public byte[] ReadBundleBytes(int maximumBytes)
        {
            long length = stream.Length;
            if (length is <= 0 || length > maximumBytes)
                throw new InvalidDataException(
                    $"Pinned file length {length} is outside the admitted 1..{maximumBytes} byte range.");
            byte[] bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)length));
            stream.Position = 0;
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw new IOException(
                    "The pinned workflow bundle grew during its exact read.");
            VerifyRetainedIdentity();
            return bytes;
        }

        public string ComputeSha256()
        {
            stream.Position = 0;
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public void VerifyRetainedIdentity()
        {
            FaceGeomHairRegionsWindowsFileIdentity observed =
                ValidatePinnedHandle(
                    stream.SafeFileHandle,
                    path,
                    role);
            if (observed.VolumeSerialNumber != identity.VolumeSerialNumber ||
                observed.FileId != identity.FileId)
                throw new UnauthorizedAccessException(
                    $"The {role} changed filesystem identity during verification.");
        }

        public void Dispose() => stream.Dispose();
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
