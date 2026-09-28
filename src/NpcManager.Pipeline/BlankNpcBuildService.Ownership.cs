using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private static bool TryRegisterOwnedFile(
        OwnedFileLedger ledger,
        WorkspacePath path,
        Sha256Hash expectedHash,
        string diagnosticCode,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!ledger.TryRegister(path, expectedHash, out var error))
        {
            diagnostics.Add(new Diagnostic(diagnosticCode, DiagnosticSeverity.Error,
                $"Could not claim the generated {role} as an immutable transaction file: {error}"));
            return false;
        }
        return true;
    }

    internal sealed class OwnedFileLedger(WorkspacePath outputRoot) : IDisposable
    {
        private readonly List<OwnedFile> files = [];
        private bool disposed;

        public bool TryRegister(
            WorkspacePath path,
            Sha256Hash expectedHash,
            out string error)
        {
            if (disposed)
            {
                error = "The transaction ledger is already closed.";
                return false;
            }
            if (!path.IsUnder(outputRoot) ||
                string.Equals(path.Value, outputRoot.Value, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Path '{path.Value}' is outside the exact transaction root.";
                return false;
            }
            if (files.Any(item => string.Equals(item.Path.Value, path.Value,
                    StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Path '{path.Value}' was registered more than once.";
                return false;
            }

            var handle = OpenFileHandle(path.Value, GenericRead | FileReadAttributes,
                FileShare.Read, FileFlagOpenReparsePoint);
            if (handle.IsInvalid)
            {
                var nativeError = Marshal.GetLastWin32Error();
                handle.Dispose();
                error = new Win32Exception(nativeError).Message;
                return false;
            }
            if (!TryReadHandleIdentity(handle, out var identity, out var identityError))
            {
                handle.Dispose();
                error = identityError;
                return false;
            }
            if (identity.Attributes.HasFlag(FileAttributes.Directory) ||
                identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                handle.Dispose();
                error = $"Path '{path.Value}' is not an ordinary file.";
                return false;
            }
            string finalPath;
            try
            {
                finalPath = CanonicalPath(GetFinalDosPath(handle));
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or ArgumentException)
            {
                handle.Dispose();
                error = $"The final file identity could not be resolved: {exception.Message}";
                return false;
            }
            if (!string.Equals(finalPath, CanonicalPath(path.Value), StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                error = $"Path resolved as '{finalPath}' instead of '{path.Value}'.";
                return false;
            }

            var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            Sha256Hash actualHash;
            try
            {
                actualHash = HashStream(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            if (actualHash != expectedHash)
            {
                stream.Dispose();
                error = $"Hash {actualHash} does not match generated hash {expectedHash}.";
                return false;
            }
            files.Add(new OwnedFile(path, expectedHash, identity, stream));
            error = string.Empty;
            return true;
        }

        public ImmutableArray<Diagnostic> RollbackFiles()
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var file in files.AsEnumerable().Reverse())
            {
                try
                {
                    var currentHash = HashStream(file.Stream);
                    if (currentHash != file.ExpectedHash)
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-file-drift", DiagnosticSeverity.Error,
                            $"Owned file '{file.Path.Value}' changed after admission and was left untouched."));
                        file.Stream.Dispose();
                        continue;
                    }

                    file.Stream.Dispose();
                    var deleteHandle = OpenFileHandle(file.Path.Value,
                        GenericRead | FileReadAttributes | DeleteAccess,
                        FileShare.Read,
                        FileFlagOpenReparsePoint);
                    if (deleteHandle.IsInvalid)
                    {
                        var nativeError = Marshal.GetLastWin32Error();
                        deleteHandle.Dispose();
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-file-reopen", DiagnosticSeverity.Error,
                            $"Owned file '{file.Path.Value}' could not be reopened for identity-bound deletion: " +
                            new Win32Exception(nativeError).Message));
                        continue;
                    }

                    using var deleteStream = new FileStream(deleteHandle, FileAccess.Read,
                        64 * 1024, isAsync: false);
                    if (!TryReadHandleIdentity(deleteStream.SafeFileHandle, out var deleteIdentity,
                            out var identityError) ||
                        !SameFile(file.Identity, deleteIdentity) ||
                        deleteIdentity.Attributes.HasFlag(FileAttributes.Directory) ||
                        deleteIdentity.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        !string.Equals(CanonicalPath(GetFinalDosPath(deleteStream.SafeFileHandle)),
                            CanonicalPath(file.Path.Value), StringComparison.OrdinalIgnoreCase) ||
                        HashStream(deleteStream) != file.ExpectedHash)
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-file-identity", DiagnosticSeverity.Error,
                            $"Owned file '{file.Path.Value}' changed identity before deletion and was left untouched. " +
                            identityError));
                        continue;
                    }

                    var disposition = new FileDispositionInformation { DeleteFile = true };
                    if (!SetFileInformationByHandle(deleteStream.SafeFileHandle,
                            FileInfoByHandleClass.FileDispositionInfo,
                            ref disposition,
                            (uint)Marshal.SizeOf<FileDispositionInformation>()))
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-rollback-file-delete", DiagnosticSeverity.Error,
                            $"Owned file '{file.Path.Value}' could not be removed: " +
                            new Win32Exception(Marshal.GetLastWin32Error()).Message));
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    diagnostics.Add(new Diagnostic("blank-npc-rollback-file-exception", DiagnosticSeverity.Error,
                        $"Owned file '{file.Path.Value}' could not be rolled back safely: {exception.Message}"));
                }
                finally
                {
                    file.Stream.Dispose();
                }
            }
            disposed = true;
            return diagnostics.ToImmutable();
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (var file in files) file.Stream.Dispose();
            disposed = true;
        }

        private static Sha256Hash HashStream(Stream stream)
        {
            stream.Position = 0;
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
            stream.Position = 0;
            return hash;
        }

        private static bool SameFile(FileHandleIdentity left, FileHandleIdentity right) =>
            left.VolumeSerialNumber == right.VolumeSerialNumber && left.FileId == right.FileId;

        private sealed record OwnedFile(
            WorkspacePath Path,
            Sha256Hash ExpectedHash,
            FileHandleIdentity Identity,
            FileStream Stream);
    }
}
