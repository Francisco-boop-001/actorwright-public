using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record DesktopEvidenceStoreWriteResult(
    bool Written,
    WorkspacePath? Path,
    string? Error);

public sealed class DesktopEvidenceFileStore
{
    private const int MaximumCrashReports = 32;
    private const int MaximumFailureRecords = 128;
    private const int MaximumCrashReportBytes = 64 * 1024;
    private const int MaximumFailureRecordBytes = 16 * 1024;
    private const long MaximumRetainedBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan RetentionLockTimeout =
        TimeSpan.FromSeconds(10);
    private readonly WorkspacePath workspaceRoot;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;
    private readonly string retentionMutexName;

    public DesktopEvidenceFileStore(WorkspacePath workspaceRoot)
    {
        this.workspaceRoot =
            ActorwrightWorkspace.RequireAdmittedRoot(workspaceRoot);
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(
            this.workspaceRoot);
        byte[] rootHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(
                this.workspaceRoot.Value).ToUpperInvariant()));
        retentionMutexName =
            @"Local\ActorwrightDesktopEvidence-" +
            Convert.ToHexString(rootHash);
    }

    public DesktopEvidenceStoreWriteResult WriteCrashReport(
        WorkspacePath reportRoot,
        ReadOnlyMemory<byte> record) =>
        WriteRecord(
            reportRoot,
            "crash-",
            "desktop crash report",
            MaximumCrashReports,
            MaximumCrashReportBytes,
            record);

    public DesktopEvidenceStoreWriteResult WriteFailureEvidence(
        ReadOnlyMemory<byte> record) =>
        WriteRecord(
            ActorwrightWorkspace.WorkRoot(
                workspaceRoot,
                "desktop-failure-evidence"),
            "failure-",
            "desktop failure evidence",
            MaximumFailureRecords,
            MaximumFailureRecordBytes,
            record);

    private DesktopEvidenceStoreWriteResult WriteRecord(
        WorkspacePath reportRoot,
        string filePrefix,
        string role,
        int maximumRetainedRecords,
        int maximumRecordBytes,
        ReadOnlyMemory<byte> record)
    {
        Mutex? mutex = null;
        bool acquired = false;
        try
        {
            mutex = new Mutex(false, retentionMutexName);
            try
            {
                acquired = mutex.WaitOne(RetentionLockTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
                return Refused(
                    "The desktop evidence retention lock could not be acquired.");
            return WriteRecordLocked(
                reportRoot,
                filePrefix,
                role,
                maximumRetainedRecords,
                maximumRecordBytes,
                record);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException)
        {
            return Refused(
                "The desktop evidence retention lock could not be acquired.");
        }
        finally
        {
            if (acquired)
                mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    private DesktopEvidenceStoreWriteResult WriteRecordLocked(
        WorkspacePath reportRoot,
        string filePrefix,
        string role,
        int maximumRetainedRecords,
        int maximumRecordBytes,
        ReadOnlyMemory<byte> record)
    {
        try
        {
            if (!reportRoot.IsUnder(workspaceRoot) ||
                Path.GetRelativePath(
                    workspaceRoot.Value,
                    reportRoot.Value) == ".")
                return Refused(
                    $"The {role} must remain below the admitted K-local lab root.");
            if (record.IsEmpty || record.Length > maximumRecordBytes)
                return Refused(
                    $"The {role} record exceeds its admitted byte bound.");

            WorkspacePath protectedRoot;
            try
            {
                protectedRoot = ActorwrightWorkspace.ResolveProtectedRoot(
                    workspaceRoot);
            }
            catch (ProtectedRootConfigurationException)
            {
                return Refused(
                    "The configured protected root is invalid; desktop evidence was not written.");
            }

            if (reportRoot.IsUnder(protectedRoot) ||
                protectedRoot.IsUnder(reportRoot))
                return Refused(
                    "The desktop evidence root overlaps the configured protected root.");
            if (File.Exists(reportRoot.Value))
                return Refused(
                    $"The {role} root is occupied by a file.");

            EnsureDirectoryChain(reportRoot, role);
            using FaceGeomHairRegionsPinnedDirectory directory =
                fileSystem.OpenDirectory(reportRoot, role);
            ImmutableArray<FaceGeomHairRegionsPinnedTreeEntry> entries =
                directory.EnumerateTree();
            long retainedByteCount = 0;
            var retained = new List<(WorkspacePath Path, string Name, int Bytes)>();
            foreach (FaceGeomHairRegionsPinnedTreeEntry entry in entries)
            {
                if (entry.IsDirectory ||
                    !string.Equals(
                        Path.GetDirectoryName(entry.Path.Value),
                        Path.TrimEndingDirectorySeparator(reportRoot.Value),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileName(entry.Path.Value);
                if (!IsOwnedRecordName(name, filePrefix))
                    continue;

                byte[] retainedBytes = directory.ReadExactFile(
                        entry.Path,
                        maximumRecordBytes,
                        $"{role} retention record");
                retainedByteCount += retainedBytes.Length;
                retained.Add((entry.Path, name, retainedBytes.Length));
            }
            retained.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            int removeCount = 0;
            while (retained.Count - removeCount + 1 > maximumRetainedRecords ||
                   retainedByteCount + record.Length > MaximumRetainedBytes)
            {
                if (removeCount >= retained.Count ||
                    !directory.DeleteOwnedFile(
                        retained[removeCount].Path,
                        out _))
                    return Refused(
                        $"The {role} retention limit could not be maintained.");
                retainedByteCount -= retained[removeCount].Bytes;
                removeCount++;
            }

            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            string fileName =
                $"{filePrefix}{timestamp:yyyyMMddTHHmmssfffffffZ}-" +
                $"{Environment.ProcessId}-{Guid.NewGuid():N}.json";
            WorkspacePath destination = new(Path.Combine(
                reportRoot.Value,
                fileName));
            WorkspacePath temporary = new(Path.Combine(
                reportRoot.Value,
                $".{fileName}.{Guid.NewGuid():N}.tmp"));
            using FaceGeomHairRegionsOwnedFile owned =
                directory.CreateOwnedFile(
                    temporary,
                    destination,
                    role);
            try
            {
                byte[] observed = owned.WriteAndReadback(
                    record,
                    maximumRecordBytes);
                if (!record.Span.SequenceEqual(observed))
                    throw new IOException(
                        $"The {role} readback did not match its write.");
                owned.PromoteNoOverwrite();
            }
            catch
            {
                if (!owned.RenameCommitted)
                    _ = owned.TryDelete(out _);
                throw;
            }

            return new DesktopEvidenceStoreWriteResult(
                true,
                destination,
                null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            return Refused(
                exception is UnauthorizedAccessException
                    ? "The desktop evidence path was refused by workspace confinement."
                    : "The desktop evidence record could not be safely written.");
        }
    }

    private void EnsureDirectoryChain(
        WorkspacePath directoryPath,
        string role)
    {
        string relative = Path.GetRelativePath(
            workspaceRoot.Value,
            directoryPath.Value);
        string current = workspaceRoot.Value;
        foreach (string segment in relative.Split(
                     [
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     ],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var path = new WorkspacePath(current);
            if (Directory.Exists(current))
            {
                using FaceGeomHairRegionsPinnedDirectory opened =
                    fileSystem.OpenDirectory(path, role);
                continue;
            }

            try
            {
                using FaceGeomHairRegionsPinnedDirectory created =
                    fileSystem.CreateOwnedDirectory(path, role);
            }
            catch (Exception exception) when (
                (exception is IOException or
                    UnauthorizedAccessException or
                    Win32Exception) && Directory.Exists(current))
            {
                using FaceGeomHairRegionsPinnedDirectory opened =
                    fileSystem.OpenDirectory(path, role);
            }
        }
    }

    private static bool IsOwnedRecordName(
        string name,
        string filePrefix)
    {
        if (!name.StartsWith(filePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(".json", StringComparison.Ordinal))
            return false;

        string stem = Path.GetFileNameWithoutExtension(name);
        string[] components = stem[filePrefix.Length..].Split('-');
        return components.Length == 3 &&
               DateTimeOffset.TryParseExact(
                   components[0],
                   "yyyyMMddTHHmmssfffffffZ",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out _) &&
               int.TryParse(
                   components[1],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out int processId) &&
               processId > 0 &&
               Guid.TryParseExact(components[2], "N", out _);
    }

    private static DesktopEvidenceStoreWriteResult Refused(
        string error) =>
        new(false, null, error);
}
