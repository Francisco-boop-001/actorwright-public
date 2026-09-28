using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Actorwright.Build
{
    public static class PackageVerificationEvidenceFileSystem
    {
        private const uint FileReadAttributes = 0x00000080;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint Synchronize = 0x00100000;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileNameNormalized = 0;
        private const uint VolumeNameDos = 0;
        private const uint ObjectCaseInsensitive = 0x00000040;
        private const uint FileDirectoryFile = 0x00000001;
        private const uint FileSequentialOnly = 0x00000004;
        private const uint FileNonDirectoryFile = 0x00000040;
        private const uint FileWriteThrough = 0x00000002;
        private const uint FileSynchronousIoNonalert = 0x00000020;
        private const uint NtFileOpenReparsePoint = 0x00200000;
        private const uint NtFileOpen = 1;
        private const uint NtFileCreate = 2;
        private const int FileRenameInformationClass = 10;
        private const int MaxManifestBytes = 16 * 1024 * 1024;
        private const string EvidenceDirectoryName = "package-verification";
        private const string Canonicalization =
            "powershell-converttojson-depth-100-compress-lf";

        public static string Write(
            string projectRoot,
            string packageRoot,
            string canonicalVerificationJson,
            string testHook)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException(
                    "Package verification evidence requires Windows handles.");
            if (canonicalVerificationJson == null)
                throw new ArgumentNullException("canonicalVerificationJson");

            string artifactsPath = Canonical(
                Path.Combine(Canonical(projectRoot), "artifacts"));
            string packagePath = Canonical(packageRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string packageLeaf = Path.GetFileName(packagePath);
            if (String.IsNullOrWhiteSpace(packageLeaf) ||
                !Regex.IsMatch(packageLeaf, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
                throw new InvalidDataException(
                    "The package output leaf is not safe for an evidence filename.");

            string evidencePath = Canonical(
                Path.Combine(artifactsPath, EvidenceDirectoryName));
            if (!String.Equals(
                    Path.GetDirectoryName(evidencePath),
                    artifactsPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The package verification evidence root escaped repository artifacts.");

            using (SafeFileHandle artifacts = OpenExistingDirectory(artifactsPath))
            using (SafeFileHandle package = OpenExistingDirectory(packagePath))
            {
                Identity artifactsIdentity = AssertHandle(
                    artifacts, artifactsPath, true, "repository artifacts root");
                Identity packageIdentity = AssertHandle(
                    package, packagePath, true, "package root");

                using (SafeFileHandle manifest = OpenExistingFileRelative(
                    package, "manifest.json"))
                {
                    string manifestPath = Canonical(
                        Path.Combine(packagePath, "manifest.json"));
                    Identity manifestIdentity = AssertHandle(
                        manifest, manifestPath, false, "package manifest");

                    if (String.Equals(
                            testHook,
                            "AttemptManifestSwap",
                            StringComparison.Ordinal))
                        AssertManifestDeleteAccessIsBlocked(manifestPath);

                    byte[] manifestBytes = ReadAll(
                        manifest, MaxManifestBytes, "package manifest");
                    string manifestSha256 = Sha256(manifestBytes);
                    string verificationSha256 = Sha256(
                        new UTF8Encoding(false, true).GetBytes(
                            canonicalVerificationJson));
                    byte[] evidenceBytes = BuildEvidence(
                        packagePath,
                        manifestSha256,
                        verificationSha256,
                        canonicalVerificationJson);

                    using (SafeFileHandle evidence = OpenOrCreateDirectoryRelative(
                        artifacts, EvidenceDirectoryName))
                    {
                        Identity evidenceIdentity = AssertHandle(
                            evidence, evidencePath, true,
                            "package verification evidence root");
                        AssertUnchanged(
                            artifacts, artifactsPath, true, artifactsIdentity,
                            "repository artifacts root");
                        AssertUnchanged(
                            package, packagePath, true, packageIdentity,
                            "package root");
                        AssertUnchanged(
                            manifest, manifestPath, false, manifestIdentity,
                            "package manifest");

                        string destinationName = packageLeaf + ".json";
                        string destinationPath = Canonical(
                            Path.Combine(evidencePath, destinationName));
                        string temporaryName = "." + destinationName +
                            ".tmp-" + Guid.NewGuid().ToString("N");
                        SafeFileHandle temporary = null;
                        bool committed = false;
                        try
                        {
                            temporary = CreateNewFileRelative(
                                evidence, temporaryName);
                            string temporaryPath = Canonical(
                                Path.Combine(evidencePath, temporaryName));
                            Identity temporaryIdentity = AssertHandle(
                                temporary, temporaryPath, false,
                                "owned evidence temporary");
                            WriteAndFlush(temporary, evidenceBytes);
                            byte[] readBack = ReadAll(
                                temporary,
                                checked(evidenceBytes.Length + 1),
                                "owned evidence temporary");
                            if (!BytesEqual(evidenceBytes, readBack))
                                throw new InvalidDataException(
                                    "Durable package verification evidence readback differed.");
                            AssertUnchanged(
                                temporary, temporaryPath, false,
                                temporaryIdentity, "owned evidence temporary");

                            AssertUnchanged(
                                artifacts, artifactsPath, true,
                                artifactsIdentity, "repository artifacts root");
                            AssertUnchanged(
                                evidence, evidencePath, true,
                                evidenceIdentity,
                                "package verification evidence root");
                            AssertUnchanged(
                                package, packagePath, true,
                                packageIdentity, "package root");
                            AssertUnchanged(
                                manifest, manifestPath, false,
                                manifestIdentity, "package manifest");

                            if (String.Equals(
                                    testHook,
                                    "FailBeforePublish",
                                    StringComparison.Ordinal))
                                throw new IOException(
                                    "Test hook forced failure before publication.");

                            RenameNoOverwrite(
                                temporary, evidence, destinationName);
                            Identity publishedSourceIdentity = AssertHandle(
                                temporary, destinationPath, false,
                                "published package verification evidence source");
                            byte[] publishedSourceBytes = ReadAll(
                                temporary,
                                checked(evidenceBytes.Length + 1),
                                "published package verification evidence source");
                            AssertExactBytes(
                                evidenceBytes,
                                publishedSourceBytes,
                                "retained published evidence source");
                            using (SafeFileHandle published =
                                OpenPublishedFileRelative(
                                    evidence, destinationName))
                            {
                                Identity publishedDestinationIdentity =
                                    AssertHandle(
                                        published,
                                        destinationPath,
                                        false,
                                        "published package verification evidence destination");
                                if (!publishedDestinationIdentity.Equals(
                                        publishedSourceIdentity))
                                    throw new InvalidDataException(
                                        "The handle-relative published destination did not identify the retained source.");
                                byte[] publishedDestinationBytes = ReadAll(
                                    published,
                                    checked(evidenceBytes.Length + 1),
                                    "published package verification evidence destination");
                                AssertExactBytes(
                                    publishedSourceBytes,
                                    publishedDestinationBytes,
                                    "published source and destination");
                            }
                            AssertUnchanged(
                                artifacts, artifactsPath, true,
                                artifactsIdentity, "repository artifacts root");
                            AssertUnchanged(
                                evidence, evidencePath, true,
                                evidenceIdentity,
                                "package verification evidence root");
                            AssertUnchanged(
                                package, packagePath, true,
                                packageIdentity, "package root");
                            AssertUnchanged(
                                manifest, manifestPath, false,
                                manifestIdentity, "package manifest");
                            committed = true;
                            return destinationPath;
                        }
                        finally
                        {
                            if (temporary != null)
                            {
                                try
                                {
                                    if (!committed)
                                        DeleteExactHandle(temporary);
                                }
                                finally
                                {
                                    temporary.Dispose();
                                }
                            }
                        }
                    }
                }
            }
        }

        private static byte[] BuildEvidence(
            string packageRoot,
            string manifestSha256,
            string verificationSha256,
            string canonicalVerificationJson)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"schemaVersion\":1");
            json.Append(",\"artifactKind\":\"actorwright-package-verification\"");
            json.Append(",\"packageRoot\":").Append(Quote(packageRoot));
            json.Append(",\"packageManifestSha256\":").Append(
                Quote(manifestSha256));
            json.Append(",\"verifierResultEncoding\":\"utf-8\"");
            json.Append(",\"verifierResultCanonicalization\":").Append(
                Quote(Canonicalization));
            json.Append(",\"verifierResultSha256\":").Append(
                Quote(verificationSha256));
            json.Append(",\"verifierResultJson\":").Append(
                Quote(canonicalVerificationJson));
            json.Append(",\"verifierResult\":").Append(
                canonicalVerificationJson);
            json.Append("}\n");
            return new UTF8Encoding(false, true).GetBytes(json.ToString());
        }

        private static void AssertManifestDeleteAccessIsBlocked(
            string manifestPath)
        {
            // A Windows rename/swap requires DELETE access. Probe that access
            // directly so this test seam cannot mutate the sealed package even
            // when the sharing assumption is unexpectedly false.
            SafeFileHandle deleteProbe = CreateFileW(
                ToNativePath(manifestPath),
                DeleteAccess | FileReadAttributes,
                (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!deleteProbe.IsInvalid)
            {
                deleteProbe.Dispose();
                throw new InvalidDataException(
                    "The retained manifest handle did not block DELETE access required for a path swap.");
            }
            int error = Marshal.GetLastWin32Error();
            deleteProbe.Dispose();
            if (error != 32)
                ThrowIoError(
                    error,
                    "The manifest swap access probe did not reach the expected sharing refusal");
        }

        private static Identity AssertHandle(
            SafeFileHandle handle,
            string expectedPath,
            bool expectDirectory,
            string description)
        {
            Identity identity = ReadIdentity(handle);
            bool isDirectory =
                (identity.Attributes & FileAttributes.Directory) != 0;
            if (isDirectory != expectDirectory ||
                (identity.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The " + description + " must be one ordinary " +
                    (expectDirectory ? "directory." : "file."));
            string finalPath = Canonical(GetFinalDosPath(handle));
            string expected = Canonical(expectedPath);
            if (expectDirectory)
            {
                finalPath = finalPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                expected = expected.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            if (!String.Equals(
                    finalPath, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The " + description + " resolved to an unexpected path.");
            return identity;
        }

        private static void AssertUnchanged(
            SafeFileHandle handle,
            string expectedPath,
            bool expectDirectory,
            Identity expectedIdentity,
            string description)
        {
            Identity actual = AssertHandle(
                handle, expectedPath, expectDirectory, description);
            if (!actual.Equals(expectedIdentity))
                throw new InvalidDataException(
                    "The " + description + " identity changed during publication.");
        }

        private static SafeFileHandle OpenExistingDirectory(string path)
        {
            SafeFileHandle handle = CreateFileW(
                ToNativePath(path),
                GenericRead | FileReadAttributes,
                (uint)(FileShare.Read | FileShare.Write),
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            ThrowIoError(error, "Opening retained directory failed");
            return null;
        }

        private static SafeFileHandle OpenExistingFileRelative(
            SafeFileHandle parent,
            string name)
        {
            return OpenRelative(
                parent,
                name,
                GenericRead | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileNonDirectoryFile | FileSequentialOnly |
                    FileSynchronousIoNonalert | NtFileOpenReparsePoint,
                NtFileOpen,
                false);
        }

        private static SafeFileHandle OpenPublishedFileRelative(
            SafeFileHandle parent,
            string name)
        {
            // The retained source requests read/write/delete. Windows share
            // compatibility therefore requires this read-only observer to
            // share all three, while the retained source's FileShare.Read
            // continues to deny outsiders write/delete access.
            return OpenRelative(
                parent,
                name,
                GenericRead | FileReadAttributes | Synchronize,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                FileNonDirectoryFile | FileSequentialOnly |
                    FileSynchronousIoNonalert | NtFileOpenReparsePoint,
                NtFileOpen,
                false);
        }

        private static SafeFileHandle CreateNewFileRelative(
            SafeFileHandle parent,
            string name)
        {
            return OpenRelative(
                parent,
                name,
                GenericRead | GenericWrite | DeleteAccess |
                    FileReadAttributes | Synchronize,
                FileShare.Read,
                FileNonDirectoryFile | FileWriteThrough |
                    FileSynchronousIoNonalert | NtFileOpenReparsePoint,
                NtFileCreate,
                false);
        }

        private static SafeFileHandle OpenOrCreateDirectoryRelative(
            SafeFileHandle parent,
            string name)
        {
            SafeFileHandle handle;
            if (TryOpenRelative(
                    parent,
                    name,
                    GenericRead | FileReadAttributes | Synchronize,
                    FileShare.Read | FileShare.Write,
                    FileDirectoryFile | FileSynchronousIoNonalert |
                        NtFileOpenReparsePoint,
                    NtFileOpen,
                    out handle))
                return handle;

            try
            {
                return OpenRelative(
                    parent,
                    name,
                    GenericRead | DeleteAccess | FileReadAttributes |
                        Synchronize,
                    FileShare.Read | FileShare.Write,
                    FileDirectoryFile | FileSynchronousIoNonalert |
                        NtFileOpenReparsePoint,
                    NtFileCreate,
                    false);
            }
            catch (IOException)
            {
                if (TryOpenRelative(
                        parent,
                        name,
                        GenericRead | FileReadAttributes | Synchronize,
                        FileShare.Read | FileShare.Write,
                        FileDirectoryFile | FileSynchronousIoNonalert |
                            NtFileOpenReparsePoint,
                        NtFileOpen,
                        out handle))
                    return handle;
                throw;
            }
        }

        private static SafeFileHandle OpenRelative(
            SafeFileHandle parent,
            string name,
            uint desiredAccess,
            FileShare share,
            uint createOptions,
            uint createDisposition,
            bool allowMissing)
        {
            SafeFileHandle handle;
            int error = OpenRelativeCore(
                parent, name, desiredAccess, share, createOptions,
                createDisposition, out handle);
            if (error == 0)
                return handle;
            if (allowMissing && (error == 2 || error == 3))
                return null;
            ThrowIoError(error, "Handle-relative open failed");
            return null;
        }

        private static bool TryOpenRelative(
            SafeFileHandle parent,
            string name,
            uint desiredAccess,
            FileShare share,
            uint createOptions,
            uint createDisposition,
            out SafeFileHandle handle)
        {
            int error = OpenRelativeCore(
                parent, name, desiredAccess, share, createOptions,
                createDisposition, out handle);
            if (error == 0)
                return true;
            if (error == 2 || error == 3)
                return false;
            ThrowIoError(error, "Handle-relative open failed");
            return false;
        }

        private static int OpenRelativeCore(
            SafeFileHandle parent,
            string name,
            uint desiredAccess,
            FileShare share,
            uint createOptions,
            uint createDisposition,
            out SafeFileHandle handle)
        {
            ValidateSegment(name);
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
            IntPtr unicodePointer = IntPtr.Zero;
            handle = null;
            try
            {
                UnicodeString unicode = new UnicodeString();
                unicode.Length = checked((ushort)(name.Length * 2));
                unicode.MaximumLength = checked((ushort)((name.Length + 1) * 2));
                unicode.Buffer = nameBuffer;
                unicodePointer = Marshal.AllocHGlobal(
                    Marshal.SizeOf(typeof(UnicodeString)));
                Marshal.StructureToPtr(unicode, unicodePointer, false);
                ObjectAttributes attributes = new ObjectAttributes();
                attributes.Length = Marshal.SizeOf(typeof(ObjectAttributes));
                attributes.RootDirectory = parent.DangerousGetHandle();
                attributes.ObjectName = unicodePointer;
                attributes.Attributes = ObjectCaseInsensitive;
                IoStatusBlock ioStatus;
                int status = NtCreateFile(
                    out handle,
                    desiredAccess,
                    ref attributes,
                    out ioStatus,
                    IntPtr.Zero,
                    (uint)FileAttributes.Normal,
                    (uint)share,
                    createDisposition,
                    createOptions,
                    IntPtr.Zero,
                    0);
                if (status >= 0 && handle != null && !handle.IsInvalid)
                    return 0;
                int error = unchecked((int)RtlNtStatusToDosError(status));
                if (handle != null)
                {
                    handle.Dispose();
                    handle = null;
                }
                return error;
            }
            finally
            {
                if (unicodePointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(unicodePointer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private static void RenameNoOverwrite(
            SafeFileHandle source,
            SafeFileHandle destinationParent,
            string destinationName)
        {
            ValidateSegment(destinationName);
            int nameBytes = checked(destinationName.Length * 2);
            int nameOffset = checked((int)Marshal.OffsetOf(
                typeof(FileRenameInformation), "FileName"));
            int bufferSize = checked(nameOffset + nameBytes);
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                for (int index = 0; index < bufferSize; index++)
                    Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteInt32(buffer, 0, 0);
                Marshal.WriteIntPtr(
                    buffer,
                    checked((int)Marshal.OffsetOf(
                        typeof(FileRenameInformation), "RootDirectory")),
                    destinationParent.DangerousGetHandle());
                Marshal.WriteInt32(
                    buffer,
                    checked((int)Marshal.OffsetOf(
                        typeof(FileRenameInformation), "FileNameLength")),
                    nameBytes);
                char[] characters = destinationName.ToCharArray();
                Marshal.Copy(
                    characters,
                    0,
                    IntPtr.Add(buffer, nameOffset),
                    characters.Length);
                IoStatusBlock ioStatus;
                int status = NtSetInformationFile(
                    source,
                    out ioStatus,
                    buffer,
                    (uint)bufferSize,
                    FileRenameInformationClass);
                if (status < 0)
                    ThrowIoError(
                        unchecked((int)RtlNtStatusToDosError(status)),
                        "Handle-relative no-overwrite rename failed");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void DeleteExactHandle(SafeFileHandle handle)
        {
            FileDispositionInformation disposition =
                new FileDispositionInformation();
            disposition.DeleteFile = true;
            if (!SetFileInformationByHandle(
                    handle,
                    FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf(typeof(FileDispositionInformation))))
                ThrowIoError(
                    Marshal.GetLastWin32Error(),
                    "Identity-bound evidence cleanup failed");
        }

        private static void WriteAndFlush(
            SafeFileHandle handle,
            byte[] bytes)
        {
            using (SafeFileHandle borrowed = new SafeFileHandle(
                handle.DangerousGetHandle(), false))
            using (FileStream stream = new FileStream(
                borrowed, FileAccess.ReadWrite, 4096, false))
            {
                stream.SetLength(0);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static byte[] ReadAll(
            SafeFileHandle handle,
            int maximumBytes,
            string description)
        {
            using (SafeFileHandle borrowed = new SafeFileHandle(
                handle.DangerousGetHandle(), false))
            using (FileStream stream = new FileStream(
                borrowed, FileAccess.Read, 4096, false))
            {
                long length = stream.Length;
                if (length < 0 || length > maximumBytes)
                    throw new InvalidDataException(
                        "The " + description + " exceeded its size limit.");
                byte[] result = new byte[checked((int)length)];
                stream.Seek(0, SeekOrigin.Begin);
                int offset = 0;
                while (offset < result.Length)
                {
                    int read = stream.Read(
                        result, offset, result.Length - offset);
                    if (read == 0)
                        throw new EndOfStreamException(
                            "The " + description + " was truncated during read.");
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException(
                        "The " + description + " changed during read.");
                return result;
            }
        }

        private static Identity ReadIdentity(SafeFileHandle handle)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                ThrowIoError(
                    Marshal.GetLastWin32Error(),
                    "Reading retained handle identity failed");
            return new Identity(
                (FileAttributes)information.FileAttributes,
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                    information.FileIndexLow);
        }

        private static string GetFinalDosPath(SafeFileHandle handle)
        {
            uint required = GetFinalPathNameByHandle(
                handle, null, 0, FileNameNormalized | VolumeNameDos);
            if (required == 0)
                ThrowIoError(
                    Marshal.GetLastWin32Error(),
                    "Reading retained handle path failed");
            char[] buffer = new char[checked((int)required + 1)];
            uint written = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Length,
                FileNameNormalized | VolumeNameDos);
            if (written == 0 || written >= buffer.Length)
                ThrowIoError(
                    Marshal.GetLastWin32Error(),
                    "Reading retained handle path failed");
            string path = new String(buffer, 0, checked((int)written));
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                return "\\\\" + path.Substring(8);
            if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                return path.Substring(4);
            return path;
        }

        private static string Canonical(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A required path was empty.");
            return Path.GetFullPath(path);
        }

        private static string ToNativePath(string path)
        {
            string canonical = Canonical(path);
            return canonical.StartsWith("\\\\?\\", StringComparison.Ordinal)
                ? canonical
                : "\\\\?\\" + canonical;
        }

        private static void ValidateSegment(string name)
        {
            if (String.IsNullOrWhiteSpace(name) ||
                name.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                name.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                throw new InvalidDataException(
                    "A handle-relative name must be one ordinary path segment.");
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha256 = SHA256.Create())
                return BitConverter.ToString(
                    sha256.ComputeHash(bytes)).Replace("-", "");
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }

        private static void AssertExactBytes(
            byte[] expected,
            byte[] actual,
            string description)
        {
            if (expected.Length != actual.Length ||
                !String.Equals(
                    Sha256(expected),
                    Sha256(actual),
                    StringComparison.Ordinal) ||
                !BytesEqual(expected, actual))
                throw new InvalidDataException(
                    "The " + description +
                    " length, SHA-256, or exact bytes differed.");
        }

        private static string Quote(string value)
        {
            StringBuilder escaped = new StringBuilder(value.Length + 2);
            escaped.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            escaped.Append("\\u").Append(
                                ((int)character).ToString("x4"));
                        else
                            escaped.Append(character);
                        break;
                }
            }
            escaped.Append('"');
            return escaped.ToString();
        }

        private static void ThrowIoError(int error, string operation)
        {
            if (error == 5 || error == 32)
                throw new UnauthorizedAccessException(
                    operation + ": " + new Win32Exception(error).Message);
            throw new IOException(
                operation + ": " + new Win32Exception(error).Message);
        }

        private struct Identity : IEquatable<Identity>
        {
            public readonly FileAttributes Attributes;
            private readonly uint volumeSerialNumber;
            private readonly ulong fileIndex;

            public Identity(
                FileAttributes attributes,
                uint volumeSerialNumber,
                ulong fileIndex)
            {
                Attributes = attributes;
                this.volumeSerialNumber = volumeSerialNumber;
                this.fileIndex = fileIndex;
            }

            public bool Equals(Identity other)
            {
                // Attributes are revalidated separately on every handle check.
                // Windows may legitimately change Archive during write/rename;
                // stable object identity is volume serial plus file index.
                return volumeSerialNumber == other.volumeSerialNumber &&
                    fileIndex == other.fileIndex;
            }
        }

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
        private static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            int fileInformationClass);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass informationClass,
            ref FileDispositionInformation information,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true,
            ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] path,
            uint pathLength,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileRenameInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool ReplaceIfExists;
            public IntPtr RootDirectory;
            public uint FileNameLength;
            public char FileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        private enum FileInfoByHandleClass
        {
            FileRenameInfo = 3,
            FileDispositionInfo = 4
        }
    }
}
