using System.Collections.Immutable;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class LocalOperationJournal : ILocalOperationJournal
{
    private const int MaximumEntryCount = 256;
    private const long MaximumTotalBytes = 16L * 1024 * 1024;
    private const int MaximumDiagnostics = 256;
    private const int MaximumArtifactHashes = 256;
    private const long MaximumDurationMilliseconds =
        30L * 24 * 60 * 60 * 1000;
    private const int Sha256Length = 64;

    private static readonly FrozenSet<string> AllowedDiagnosticCodes =
        ProtocolDiagnosticClassifier.RegisteredLegacySecurityCodes
            .Concat(ProtocolV2DiagnosticCodes.All)
            .Concat(
            [
                "usage-error",
                SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid,
                SkyrimNpcDialogueDiagnosticCodes.ManifestHashMismatch,
                SkyrimNpcDialogueDiagnosticCodes.LineDuplicate,
                SkyrimNpcDialogueDiagnosticCodes.LineTextEmpty,
                SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown,
                SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved,
                SkyrimNpcDialogueDiagnosticCodes.PluginTypeRefused,
                SkyrimNpcDialogueDiagnosticCodes.BudgetExceeded,
                SkyrimNpcDialogueDiagnosticCodes.CooldownAdvisory,
                SkyrimNpcDialogueDiagnosticCodes.SourceHashMismatch,
                SkyrimNpcDialogueDiagnosticCodes.SourceDialogueUnsupported,
                SkyrimNpcDialogueDiagnosticCodes.SynthesisIncomplete,
                SkyrimNpcDialogueDiagnosticCodes.OutputExists,
                SkyrimNpcDialogueDiagnosticCodes.LipToolMissing,
                SkyrimNpcDialogueDiagnosticCodes.LipToolFailed,
                SkyrimNpcDialogueDiagnosticCodes.VerifyRecordMissing,
                SkyrimNpcDialogueDiagnosticCodes.VerifyAssetMissing,
                SkyrimNpcDialogueDiagnosticCodes.VerifyLinkUnresolved,
                SkyrimNpcDialogueDiagnosticCodes.VerifyForeignOverride,
                SkyrimNpcDialogueDiagnosticCodes.VerifyLightRange,
                SkyrimNpcDialogueDiagnosticCodes.VerifySeqMissing,
                SkyrimNpcDialogueDiagnosticCodes.VerifyScriptMismatch,
                SkyrimNpcDialogueDiagnosticCodes.VerifyDocumentBinding,
                SkyrimNpcDialogueDiagnosticCodes.TemplateUnknown,
                SkyrimNpcDialogueDiagnosticCodes.ProfileInvalid,
                "operation-journal-record-refused",
                "operation-journal-access-denied",
                "operation-journal-lock-timeout",
                "operation-journal-reparse-refused",
                "operation-journal-retention-failed",
                "operation-journal-write-failed",
            ])
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly WorkspacePath labRoot;
    private readonly LocalOperationJournalFileSystem hooks;

    public LocalOperationJournal(WorkspacePath labRoot)
        : this(labRoot, new LocalOperationJournalFileSystem())
    {
    }

    internal LocalOperationJournal(
        WorkspacePath labRoot,
        LocalOperationJournalFileSystem hooks)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The local operation journal requires Windows handle-relative storage.");
        this.labRoot = labRoot;
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public async ValueTask<OperationJournalAppendResult> AppendAsync(
        OperationJournalRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryProject(record, out PersistedOperationJournalRecord? persisted))
            return RecordRefusal();
        PersistedOperationJournalRecord safeRecord = persisted ??
            throw new InvalidOperationException(
                "A successful journal projection omitted its record.");

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            safeRecord, JsonOptions);
        if (content.Length == 0 || content.LongLength > MaximumTotalBytes)
            return RecordRefusal();

        WorkspacePath journalRoot = new(Path.Combine(
            labRoot.Value, ".actorwright", "operations"));
        WorkspacePath coordinationPath = new(Path.Combine(
            labRoot.Value, ".actorwright", "operation-journal.lock"));
        WorkspacePath protectedRoot;
        try
        {
            protectedRoot = ActorwrightWorkspace.ResolveProtectedRoot(labRoot);
        }
        catch (ProtectedRootConfigurationException)
        {
            return WriteFailure();
        }
        if (journalRoot.IsUnder(protectedRoot) ||
            protectedRoot.IsUnder(journalRoot) ||
            coordinationPath.IsUnder(protectedRoot) ||
            protectedRoot.IsUnder(coordinationPath))
        {
            return WriteFailure();
        }

        JournalOwnedFile? owned = null;
        try
        {
            using JournalDirectoryLease directories =
                JournalDirectoryLease.OpenOrCreate(labRoot);
            using JournalCoordinationLease coordination =
                await JournalCoordinationLease.AcquireAsync(
                    directories.CoordinationPath,
                    cancellationToken);

            hooks.BeforeCreateEntry(directories.OperationsPath);
            directories.Validate();
            cancellationToken.ThrowIfCancellationRequested();

            long sequence = directories.AllocateNextSequence();
            string destinationName = EntryName(
                safeRecord.RequestDigest, sequence);
            string temporaryName =
                ".tmp-" + Guid.NewGuid().ToString("N");
            owned = directories.CreateOwnedFile(
                temporaryName, destinationName);
            await owned.WriteAsync(content, cancellationToken);
            hooks.AfterDurableFlush();

            cancellationToken.ThrowIfCancellationRequested();
            owned.PromoteNoOverwrite();
            byte[] readback = await owned.ReadbackAsync(
                cancellationToken);
            hooks.AfterReopen();
            PersistedOperationJournalRecord? reparsed =
                JsonSerializer.Deserialize<PersistedOperationJournalRecord>(
                    readback, JsonOptions);
            if (reparsed is null || !RecordsEqual(safeRecord, reparsed))
                throw new JsonException(
                    "The promoted journal record did not reparse identically.");

            Diagnostic? retentionWarning = directories.ApplyRetention(
                owned, hooks);
            owned.ValidateRetained();
            WorkspacePath resultPath = new(owned.Path);
            owned.Dispose();
            owned = null;
            return new OperationJournalAppendResult(
                true, resultPath, retentionWarning);
        }
        catch (OperationCanceledException)
        {
            owned?.TryDeleteExact();
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException or JsonException or
                NotSupportedException or Win32Exception)
        {
            owned?.TryDeleteExact();
            return exception switch
            {
                JournalContainmentException => ReparseRefusal(),
                JournalLockTimeoutException => LockTimeout(),
                UnauthorizedAccessException => AccessDenied(),
                _ => WriteFailure()
            };
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static bool TryProject(
        OperationJournalRecord source,
        out PersistedOperationJournalRecord? persisted)
    {
        persisted = null;
        if (source.Command is null ||
            !AgentCommandRegistry.All.Any(command =>
                string.Equals(command.Name, source.Command,
                    StringComparison.Ordinal)) ||
            source.RequestDigest is null ||
            !IsUpperSha256(source.RequestDigest.AsSpan()) ||
            source.Effects.IsDefault ||
            source.Effects.Length >
                ApplicationEffectVocabulary.MaximumRecordedEffects ||
            source.DiagnosticCodes.IsDefault ||
            source.DiagnosticCodes.Length > MaximumDiagnostics ||
            source.DiagnosticClasses.IsDefault ||
            source.DiagnosticClasses.Length > MaximumDiagnostics ||
            source.ArtifactHashes.IsDefault ||
            source.ArtifactHashes.Length > MaximumArtifactHashes ||
            source.DurationMilliseconds is < 0 or >
                MaximumDurationMilliseconds ||
            !Enum.IsDefined((CommandExitCode)source.ExitCode) ||
            !TryParseOutcome(source.Outcome, out ProtocolOutcome outcome))
        {
            return false;
        }

        var effects = ImmutableArray.CreateBuilder<PersistedJournalEffect>(
            source.Effects.Length);
        foreach (ProtocolEffect? effect in source.Effects)
        {
            if (effect is null || !Enum.IsDefined(effect.Kind) ||
                !ApplicationEffectVocabulary.IsAdmittedStatus(effect.Status) ||
                !ApplicationEffectVocabulary.IsAdmittedScope(effect.Scope) ||
                !ApplicationEffectVocabulary.IsAdmittedPair(
                    effect.Kind, effect.Scope))
            {
                return false;
            }
            effects.Add(new PersistedJournalEffect(
                effect.Kind, effect.Status, effect.Scope));
        }

        if (!TryProjectDiagnosticCodes(
                source.DiagnosticCodes, out ImmutableArray<string> codes) ||
            !TryProjectDiagnosticClasses(
                source.DiagnosticClasses,
                out ImmutableArray<DiagnosticClass> classes) ||
            !TryProjectArtifactHashes(
                source.ArtifactHashes,
                out ImmutableArray<string> hashes))
        {
            return false;
        }

        persisted = new PersistedOperationJournalRecord(
            source.Command,
            source.RequestDigest,
            effects.ToImmutable(),
            codes,
            classes,
            hashes,
            source.DurationMilliseconds,
            outcome,
            source.ExitCode);
        return true;
    }

    private static bool TryProjectDiagnosticCodes(
        ImmutableArray<string> source,
        out ImmutableArray<string> projected)
    {
        projected = [];
        if (source.Any(code =>
                code is null || !AllowedDiagnosticCodes.Contains(code)) ||
            source.Distinct(StringComparer.Ordinal).Count() != source.Length)
        {
            return false;
        }
        projected = source.Order(StringComparer.Ordinal).ToImmutableArray();
        return true;
    }

    private static bool TryProjectDiagnosticClasses(
        ImmutableArray<string> source,
        out ImmutableArray<DiagnosticClass> projected)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticClass>(
            source.Length);
        foreach (string? value in source)
        {
            if (!TryParseDiagnosticClass(
                    value, out DiagnosticClass diagnosticClass))
            {
                projected = [];
                return false;
            }
            builder.Add(diagnosticClass);
        }
        if (builder.Distinct().Count() != builder.Count)
        {
            projected = [];
            return false;
        }
        projected = builder.Order().ToImmutableArray();
        return true;
    }

    private static bool TryProjectArtifactHashes(
        ImmutableArray<string> source,
        out ImmutableArray<string> projected)
    {
        projected = [];
        if (source.Any(hash =>
                hash is null || !IsUpperSha256(hash.AsSpan())) ||
            source.Distinct(StringComparer.Ordinal).Count() != source.Length)
        {
            return false;
        }
        projected = source.Order(StringComparer.Ordinal).ToImmutableArray();
        return true;
    }

    private static bool TryParseOutcome(
        string? value,
        out ProtocolOutcome outcome)
    {
        outcome = value switch
        {
            "succeeded" => ProtocolOutcome.Succeeded,
            "refused" => ProtocolOutcome.Refused,
            "failed" => ProtocolOutcome.Failed,
            "cancelled" => ProtocolOutcome.Cancelled,
            _ => default
        };
        return value is "succeeded" or "refused" or "failed" or
            "cancelled";
    }

    private static bool TryParseDiagnosticClass(
        string? value,
        out DiagnosticClass diagnosticClass)
    {
        diagnosticClass = value switch
        {
            "operation" => DiagnosticClass.Operation,
            "usage" => DiagnosticClass.Usage,
            "security" => DiagnosticClass.Security,
            "validation" => DiagnosticClass.Validation,
            "verification" => DiagnosticClass.Verification,
            "cancellation" => DiagnosticClass.Cancellation,
            _ => default
        };
        return value is "operation" or "usage" or "security" or
            "validation" or "verification" or "cancellation";
    }

    private static bool IsUpperSha256(ReadOnlySpan<char> value)
    {
        if (value.Length != Sha256Length)
            return false;
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool RecordsEqual(
        PersistedOperationJournalRecord left,
        PersistedOperationJournalRecord right) =>
        left.Command == right.Command &&
        left.RequestDigest == right.RequestDigest &&
        left.Effects.SequenceEqual(right.Effects) &&
        left.DiagnosticCodes.SequenceEqual(
            right.DiagnosticCodes, StringComparer.Ordinal) &&
        left.DiagnosticClasses.SequenceEqual(right.DiagnosticClasses) &&
        left.ArtifactHashes.SequenceEqual(
            right.ArtifactHashes, StringComparer.Ordinal) &&
        left.DurationMilliseconds == right.DurationMilliseconds &&
        left.Outcome == right.Outcome &&
        left.ExitCode == right.ExitCode;

    internal static string EntryName(string requestDigest, long sequence) =>
        requestDigest + "-" +
        sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json";

    internal static bool TryParseEntrySequence(
        string name,
        out long sequence)
    {
        sequence = 0;
        const int sequenceLength = 20;
        int expectedLength =
            Sha256Length + 1 + sequenceLength + ".json".Length;
        return name.Length == expectedLength &&
            name[Sha256Length] == '-' &&
            name.EndsWith(".json", StringComparison.Ordinal) &&
            IsUpperSha256(name.AsSpan(0, Sha256Length)) &&
            long.TryParse(
                name.AsSpan(Sha256Length + 1, sequenceLength),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence);
    }

    private static OperationJournalAppendResult RecordRefusal() => new(
        false,
        null,
        Warning(
            "operation-journal-record-refused",
            "The local operation journal record contained an unsafe or unsupported value."));

    private static OperationJournalAppendResult WriteFailure() => new(
        false,
        null,
        Warning(
            "operation-journal-write-failed",
            "The local operation journal could not append a redacted record."));

    private static OperationJournalAppendResult ReparseRefusal() => new(
        false,
        null,
        Warning(
            "operation-journal-reparse-refused",
            "The local operation journal path may not traverse reparse points."));

    private static OperationJournalAppendResult AccessDenied() => new(
        false,
        null,
        Warning(
            "operation-journal-access-denied",
            "The local operation journal could not access its admitted storage path."));

    private static OperationJournalAppendResult LockTimeout() => new(
        false,
        null,
        Warning(
            "operation-journal-lock-timeout",
            "The local operation journal coordination lock remained unavailable."));

    internal static Diagnostic RetentionWarning() => Warning(
        "operation-journal-retention-failed",
        "The local operation journal was appended, but retention could not be completed.");

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private sealed record PersistedJournalEffect(
        AgentEffectKind Kind,
        string Status,
        string Scope);

    private sealed record PersistedOperationJournalRecord(
        string Command,
        string RequestDigest,
        ImmutableArray<PersistedJournalEffect> Effects,
        ImmutableArray<string> DiagnosticCodes,
        ImmutableArray<DiagnosticClass> DiagnosticClasses,
        ImmutableArray<string> ArtifactHashes,
        long DurationMilliseconds,
        ProtocolOutcome Outcome,
        int ExitCode);
}

internal class LocalOperationJournalFileSystem
{
    public virtual void BeforeCreateEntry(string operationsPath)
    {
    }

    public virtual void AfterDurableFlush()
    {
    }

    public virtual void AfterReopen()
    {
    }

    public virtual void BeforeRetentionDelete(string path)
    {
    }
}

internal sealed class JournalContainmentException(string message) :
    IOException(message);

internal sealed class JournalLockTimeoutException() : IOException(
    "The local operation journal coordination lock timed out.");

internal sealed class JournalCoordinationLease : IDisposable
{
    private static readonly TimeSpan LockWaitTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(25);

    private readonly SafeFileHandle handle;
    private bool disposed;

    private JournalCoordinationLease(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    public static async ValueTask<JournalCoordinationLease> AcquireAsync(
        string path,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (JournalWindowsMetadata.TryOpenExclusiveLock(
                    path,
                    out SafeFileHandle? handle,
                    out int error))
            {
                try
                {
                    _ = JournalDirectoryLease.ValidateFileHandle(
                        handle!, path);
                    return new JournalCoordinationLease(handle!);
                }
                catch
                {
                    handle?.Dispose();
                    throw;
                }
            }
            if (error is not (32 or 33))
                JournalWindowsMetadata.ThrowOpenError(error);
            if (Stopwatch.GetElapsedTime(started) >= LockWaitTimeout)
                throw new JournalLockTimeoutException();
            await Task.Delay(RetryDelay, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        handle.Dispose();
        disposed = true;
    }
}

internal sealed class JournalDirectoryLease : IDisposable
{
    private readonly List<JournalPinnedDirectory> directories;
    private bool disposed;

    private JournalDirectoryLease(
        List<JournalPinnedDirectory> directories,
        string operationsPath)
    {
        this.directories = directories;
        OperationsPath = operationsPath;
    }

    public string OperationsPath { get; }

    public string CoordinationPath => Path.Combine(
        Path.GetDirectoryName(OperationsPath)!,
        "operation-journal.lock");

    private SafeFileHandle OperationsHandle =>
        !disposed
            ? directories[^1].Handle
            : throw new ObjectDisposedException(nameof(JournalDirectoryLease));

    public static JournalDirectoryLease OpenOrCreate(
        WorkspacePath labRoot)
    {
        string admittedRoot = Canonical(labRoot.Value);
        string driveRoot = Path.GetPathRoot(admittedRoot) ?? string.Empty;
        if (!string.Equals(driveRoot, @"K:\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                "The local operation journal requires a K-local root.");

        var pinned = new List<JournalPinnedDirectory>();
        try
        {
            SafeFileHandle drive =
                FaceGeomHairRegionsWindowsHandleApi.OpenExistingDirectory(
                    driveRoot);
            AddValidatedDirectory(pinned, driveRoot, drive);

            string relative = Path.GetRelativePath(driveRoot, admittedRoot);
            string current = driveRoot;
            foreach (string segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                SafeFileHandle handle =
                    FaceGeomHairRegionsWindowsHandleApi
                        .OpenExistingDirectoryRelative(
                            pinned[^1].Handle, segment);
                AddValidatedDirectory(pinned, current, handle);
            }

            current = Path.Combine(admittedRoot, ".actorwright");
            SafeFileHandle actorwright = OpenOrCreateDirectoryRelative(
                pinned[^1].Handle, ".actorwright");
            AddValidatedDirectory(pinned, current, actorwright);

            current = Path.Combine(current, "operations");
            SafeFileHandle operations = OpenOrCreateDirectoryRelative(
                pinned[^1].Handle, "operations");
            AddValidatedDirectory(pinned, current, operations);

            return new JournalDirectoryLease(pinned, current);
        }
        catch
        {
            DisposeAll(pinned);
            throw;
        }
    }

    public void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (JournalPinnedDirectory directory in directories)
        {
            FaceGeomHairRegionsWindowsFileIdentity actual =
                ValidateDirectoryHandle(directory.Handle, directory.Path);
            if (!SameIdentity(actual, directory.Identity))
                throw new JournalContainmentException(
                    "A retained journal directory identity changed.");
        }
    }

    public long AllocateNextSequence()
    {
        Validate();
        long maximum = -1;
        foreach (FaceGeomHairRegionsWindowsDirectoryEntry entry in
                 FaceGeomHairRegionsWindowsHandleApi
                     .EnumerateDirectory(OperationsHandle))
        {
            if (LocalOperationJournal.TryParseEntrySequence(
                    entry.Name, out long sequence))
                maximum = Math.Max(maximum, sequence);
        }
        if (maximum == long.MaxValue)
            throw new IOException(
                "The local operation journal sequence was exhausted.");
        return maximum + 1;
    }

    public JournalOwnedFile CreateOwnedFile(
        string temporaryName,
        string destinationName)
    {
        Validate();
        SafeFileHandle handle =
            FaceGeomHairRegionsWindowsHandleApi.CreateNewFileRelative(
                OperationsHandle, temporaryName);
        try
        {
            string temporaryPath = Path.Combine(
                OperationsPath, temporaryName);
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidateFileHandle(handle, temporaryPath);
            return new JournalOwnedFile(
                handle,
                identity,
                temporaryPath,
                Path.Combine(OperationsPath, destinationName),
                OperationsHandle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public Diagnostic? ApplyRetention(
        JournalOwnedFile retained,
        LocalOperationJournalFileSystem hooks)
    {
        Validate();
        retained.ValidateRetained();
        ImmutableArray<FaceGeomHairRegionsWindowsDirectoryEntry> entries =
            FaceGeomHairRegionsWindowsHandleApi
                .EnumerateDirectory(OperationsHandle);
        var opened = new List<JournalRetentionFile>();
        bool failed = false;
        try
        {
            foreach (FaceGeomHairRegionsWindowsDirectoryEntry entry in entries)
            {
                if ((entry.Attributes &
                     (FileAttributes.Directory |
                      FileAttributes.ReparsePoint |
                      FileAttributes.Device)) != 0)
                {
                    continue;
                }

                if (string.Equals(
                        entry.Name,
                        Path.GetFileName(retained.Path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    opened.Add(JournalRetentionFile.Protected(retained));
                    continue;
                }

                try
                {
                    opened.Add(OpenRetentionFile(entry.Name));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException or
                        UnauthorizedAccessException or Win32Exception)
                {
                    failed = true;
                }
            }

            if (failed)
                return LocalOperationJournal.RetentionWarning();

            long totalBytes = 0;
            foreach (JournalRetentionFile entry in opened)
                totalBytes = SaturatingAdd(totalBytes, entry.Size);
            int remainingCount = opened.Count;

            foreach (JournalRetentionFile candidate in opened
                         .Where(entry => !entry.IsProtected)
                         .OrderBy(entry => entry.CreationFileTime)
                         .ThenBy(entry => entry.Sequence ?? long.MaxValue)
                         .ThenBy(entry => entry.Name, StringComparer.Ordinal))
            {
                if (remainingCount <= 256 &&
                    totalBytes <= 16L * 1024 * 1024)
                    break;
                try
                {
                    hooks.BeforeRetentionDelete(candidate.Path);
                    candidate.DeleteExact();
                    remainingCount--;
                    totalBytes -= candidate.Size;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException or
                        UnauthorizedAccessException or Win32Exception or
                        NotSupportedException)
                {
                    failed = true;
                }
            }

            retained.ValidateRetained();
            if (remainingCount > 256 || totalBytes > 16L * 1024 * 1024)
                failed = true;
            return failed
                ? LocalOperationJournal.RetentionWarning()
                : null;
        }
        finally
        {
            foreach (JournalRetentionFile entry in opened)
                entry.Dispose();
        }
    }

    private JournalRetentionFile OpenRetentionFile(string name)
    {
        SafeFileHandle handle =
            FaceGeomHairRegionsWindowsHandleApi.OpenOwnedFileRelative(
                OperationsHandle, name);
        try
        {
            string path = Path.Combine(OperationsPath, name);
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidateFileHandle(handle, path);
            JournalFileMetadata metadata =
                JournalWindowsMetadata.Read(handle);
            if (metadata.VolumeSerialNumber != identity.VolumeSerialNumber ||
                metadata.FileId != identity.FileId)
                throw new JournalContainmentException(
                    "Journal retention metadata identity changed.");
            return new JournalRetentionFile(
                handle,
                path,
                name,
                metadata.CreationFileTime,
                metadata.Size,
                LocalOperationJournal.TryParseEntrySequence(
                    name, out long sequence)
                    ? sequence
                    : null);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenOrCreateDirectoryRelative(
        SafeFileHandle parent,
        string name)
    {
        try
        {
            return FaceGeomHairRegionsWindowsHandleApi
                .OpenExistingDirectoryRelative(parent, name);
        }
        catch (IOException)
        {
            try
            {
                return JournalWindowsMetadata
                    .CreateNewDirectoryRelative(parent, name);
            }
            catch (IOException)
            {
                return FaceGeomHairRegionsWindowsHandleApi
                    .OpenExistingDirectoryRelative(parent, name);
            }
        }
    }

    private static void AddValidatedDirectory(
        List<JournalPinnedDirectory> pinned,
        string path,
        SafeFileHandle handle)
    {
        try
        {
            FaceGeomHairRegionsWindowsFileIdentity identity =
                ValidateDirectoryHandle(handle, path);
            pinned.Add(new JournalPinnedDirectory(
                Canonical(path), handle, identity));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FaceGeomHairRegionsWindowsFileIdentity
        ValidateFileHandle(SafeFileHandle handle, string expectedPath)
    {
        FaceGeomHairRegionsWindowsFileIdentity identity =
            FaceGeomHairRegionsWindowsHandleApi.ReadIdentity(handle);
        if ((identity.Attributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw new JournalContainmentException(
                "The journal file handle is not an ordinary file.");
        ValidateFinalPath(handle, expectedPath);
        return identity;
    }

    private static FaceGeomHairRegionsWindowsFileIdentity
        ValidateDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        FaceGeomHairRegionsWindowsFileIdentity identity =
            FaceGeomHairRegionsWindowsHandleApi.ReadIdentity(handle);
        if ((identity.Attributes & FileAttributes.Directory) == 0 ||
            (identity.Attributes &
             (FileAttributes.ReparsePoint |
              FileAttributes.Device)) != 0)
            throw new JournalContainmentException(
                "The journal directory handle is not an ordinary directory.");
        ValidateFinalPath(handle, expectedPath);
        return identity;
    }

    private static void ValidateFinalPath(
        SafeFileHandle handle,
        string expectedPath)
    {
        string actual = Canonical(
            FaceGeomHairRegionsWindowsHandleApi.GetFinalDosPath(handle));
        if (!string.Equals(
                actual,
                Canonical(expectedPath),
                StringComparison.OrdinalIgnoreCase))
            throw new JournalContainmentException(
                "A journal handle resolved outside its admitted path.");
    }

    internal static bool SameIdentity(
        FaceGeomHairRegionsWindowsFileIdentity left,
        FaceGeomHairRegionsWindowsFileIdentity right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileId == right.FileId;

    internal static string Canonical(string path)
    {
        string canonical = Path.GetFullPath(path);
        string root = Path.GetPathRoot(canonical) ?? canonical;
        return string.Equals(
                canonical, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : canonical.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    public void Dispose()
    {
        if (disposed)
            return;
        DisposeAll(directories);
        disposed = true;
    }

    private static void DisposeAll(List<JournalPinnedDirectory> pinned)
    {
        for (int index = pinned.Count - 1; index >= 0; index--)
            pinned[index].Handle.Dispose();
    }

    private sealed record JournalPinnedDirectory(
        string Path,
        SafeFileHandle Handle,
        FaceGeomHairRegionsWindowsFileIdentity Identity);
}

internal sealed class JournalOwnedFile : IDisposable
{
    private readonly FileStream stream;
    private readonly SafeFileHandle operationsHandle;
    private FaceGeomHairRegionsWindowsFileIdentity identity;
    private readonly string destinationPath;
    private bool disposed;

    public JournalOwnedFile(
        SafeFileHandle handle,
        FaceGeomHairRegionsWindowsFileIdentity identity,
        string temporaryPath,
        string destinationPath,
        SafeFileHandle operationsHandle)
    {
        stream = new FileStream(
            handle,
            FileAccess.ReadWrite,
            64 * 1024,
            isAsync: true);
        this.identity = identity;
        Path = temporaryPath;
        this.destinationPath = destinationPath;
        this.operationsHandle = operationsHandle;
    }

    public string Path { get; private set; }

    public FaceGeomHairRegionsWindowsFileIdentity Identity => identity;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        stream.Position = 0;
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (stream.Length != bytes.Length)
            throw new IOException(
                "The durable journal write length changed.");
    }

    public void PromoteNoOverwrite()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        FaceGeomHairRegionsWindowsHandleApi.RenameNoOverwrite(
            stream.SafeFileHandle,
            operationsHandle,
            System.IO.Path.GetFileName(destinationPath));
        Path = destinationPath;
        identity = JournalDirectoryLease.ValidateFileHandle(
            stream.SafeFileHandle, Path);
    }

    public async ValueTask<byte[]> ReadbackAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using SafeFileHandle reopened =
            FaceGeomHairRegionsWindowsHandleApi.ReopenRead(
                stream.SafeFileHandle);
        FaceGeomHairRegionsWindowsFileIdentity reopenedIdentity =
            JournalDirectoryLease.ValidateFileHandle(reopened, Path);
        if (!JournalDirectoryLease.SameIdentity(
                reopenedIdentity, identity))
            throw new JournalContainmentException(
                "The promoted journal readback identity changed.");
        await using var readback = new FileStream(
            reopened,
            FileAccess.Read,
            64 * 1024,
            isAsync: true);
        if (readback.Length <= 0 || readback.Length > int.MaxValue)
            throw new InvalidDataException(
                "The promoted journal readback length was invalid.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)readback.Length));
        await readback.ReadExactlyAsync(bytes, cancellationToken);
        if (readback.ReadByte() != -1)
            throw new IOException(
                "The promoted journal entry grew during readback.");
        return bytes;
    }

    public JournalFileMetadata ReadMetadata()
    {
        ValidateRetained();
        return JournalWindowsMetadata.Read(stream.SafeFileHandle);
    }

    public void ValidateRetained()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        FaceGeomHairRegionsWindowsFileIdentity actual =
            JournalDirectoryLease.ValidateFileHandle(
                stream.SafeFileHandle, Path);
        if (!JournalDirectoryLease.SameIdentity(actual, identity))
            throw new JournalContainmentException(
                "The retained journal file identity changed.");
    }

    public void TryDeleteExact()
    {
        if (disposed)
            return;
        try
        {
            ValidateRetained();
            FaceGeomHairRegionsWindowsHandleApi.DeleteExactHandle(
                stream.SafeFileHandle);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException or Win32Exception)
        {
            _ = exception;
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        stream.Dispose();
        disposed = true;
    }
}

internal sealed class JournalRetentionFile : IDisposable
{
    private readonly SafeFileHandle? handle;
    private bool disposed;

    private JournalRetentionFile(
        SafeFileHandle? handle,
        string path,
        string name,
        long creationFileTime,
        long size,
        long? sequence,
        bool isProtected)
    {
        this.handle = handle;
        Path = path;
        Name = name;
        CreationFileTime = creationFileTime;
        Size = size;
        Sequence = sequence;
        IsProtected = isProtected;
    }

    public JournalRetentionFile(
        SafeFileHandle handle,
        string path,
        string name,
        long creationFileTime,
        long size,
        long? sequence)
        : this(
            handle, path, name, creationFileTime, size, sequence,
            isProtected: false)
    {
    }

    public string Path { get; }

    public string Name { get; }

    public long CreationFileTime { get; }

    public long Size { get; }

    public long? Sequence { get; }

    public bool IsProtected { get; }

    public static JournalRetentionFile Protected(JournalOwnedFile owned)
    {
        JournalFileMetadata metadata = owned.ReadMetadata();
        return new JournalRetentionFile(
            null,
            owned.Path,
            System.IO.Path.GetFileName(owned.Path),
            metadata.CreationFileTime,
            metadata.Size,
            LocalOperationJournal.TryParseEntrySequence(
                System.IO.Path.GetFileName(owned.Path), out long sequence)
                ? sequence
                : null,
            isProtected: true);
    }

    public void DeleteExact()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsProtected || handle is null)
            throw new InvalidOperationException(
                "The retained journal entry may not be deleted.");
        FaceGeomHairRegionsWindowsHandleApi.DeleteExactHandle(handle);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        handle?.Dispose();
        disposed = true;
    }
}

internal readonly record struct JournalFileMetadata(
    uint VolumeSerialNumber,
    ulong FileId,
    long CreationFileTime,
    long Size);

internal static class JournalWindowsMetadata
{
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x0000_0080;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagWriteThrough = 0x8000_0000;
    private const uint ObjectCaseInsensitive = 0x0000_0040;
    private const uint FileDirectoryFile = 0x0000_0001;
    private const uint NtFileOpenReparsePoint = 0x0020_0000;
    private const uint NtFileCreate = 2;

    public static SafeFileHandle CreateNewDirectoryRelative(
        SafeFileHandle parent,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(
                [
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ]) >= 0)
        {
            throw new InvalidDataException(
                "A handle-relative name must be one ordinary path segment.");
        }

        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr unicodePointer = IntPtr.Zero;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = nameBuffer
            };
            unicodePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                unicode, unicodePointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjectCaseInsensitive,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            int status = NtCreateFile(
                out SafeFileHandle handle,
                GenericRead | FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                (uint)FileAttributes.Normal,
                (uint)(FileShare.Read | FileShare.Write),
                NtFileCreate,
                FileDirectoryFile | NtFileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
                return handle;
            int error = unchecked((int)RtlNtStatusToDosError(status));
            handle?.Dispose();
            ThrowOpenError(error);
            throw new IOException("The directory create failure was not raised.");
        }
        finally
        {
            if (unicodePointer != IntPtr.Zero)
                Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    public static bool TryOpenExclusiveLock(
        string path,
        out SafeFileHandle? handle,
        out int error)
    {
        string canonical = JournalDirectoryLease.Canonical(path);
        string native = canonical.StartsWith(
                @"\\?\", StringComparison.Ordinal)
            ? canonical
            : @"\\?\" + canonical;
        SafeFileHandle opened = CreateFileW(
            native,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenAlways,
            FileAttributeNormal |
            FileFlagOpenReparsePoint |
            FileFlagWriteThrough,
            IntPtr.Zero);
        if (!opened.IsInvalid)
        {
            handle = opened;
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        opened.Dispose();
        handle = null;
        return false;
    }

    public static void ThrowOpenError(int error)
    {
        if (error is 5 or 32 or 33)
            throw new UnauthorizedAccessException(
                new Win32Exception(error).Message);
        throw new IOException(new Win32Exception(error).Message);
    }

    public static JournalFileMetadata Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation info))
            throw new IOException(
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        ulong unsignedSize =
            ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        if (unsignedSize > long.MaxValue)
            throw new IOException("A journal file length exceeded Int64.");
        ulong creation =
            ((ulong)info.CreationTimeHigh << 32) | info.CreationTimeLow;
        return new JournalFileMetadata(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            unchecked((long)creation),
            checked((long)unsignedSize));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out FileInformation information);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
