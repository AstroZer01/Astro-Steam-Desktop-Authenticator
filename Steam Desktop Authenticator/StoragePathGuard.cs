using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    internal static class StoragePathGuard
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileReadData = 0x00000001;
        private const uint FileReadAttributes = 0x00000080;
        private const uint DeleteAccess = 0x00010000;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileShareNone = 0;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
        private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint CreateNew = 1;
        private const uint OpenExisting = 3;
        private const uint FileFlagWriteThrough = 0x80000000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileAttributeReadOnly = 0x00000001;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileNameNormalized = 0x0;
        private const int FileRenameInfoClass = 3;
        private const int FileDispositionInfoClass = 4;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorFileExists = 80;
        private const int ErrorAlreadyExists = 183;
        private const int Win32LongPathThreshold = 248;
        private const byte InPlaceOverwriteSentinelByte = 0xFF;
        internal const string RestoredTargetTempPrefix = ".mafile-restore.";
        internal const string StagedFileTempPrefix = ".mafile-stage.";
        internal const string StagingTempSuffix = ".tmp";

        private static readonly char[] DirectorySeparators =
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        };

        private static long directoryValidationCount;
        private static long canonicalDirectoryVerificationCount;

        internal static long DirectoryValidationCount => Interlocked.Read(ref directoryValidationCount);
        internal static long CanonicalDirectoryVerificationCount => Interlocked.Read(ref canonicalDirectoryVerificationCount);
        internal static Action<string> OnBeforeFlushReplacementForTest { get; set; }
        internal static Action<string> OnBeforePublishRestoredMissingTargetForTest { get; set; }
        internal static Action<string> OnBeforeCreateQuarantineMarkerForTest { get; set; }

        internal sealed class PinnedStorageDirectory : IDisposable
        {
            private SafeFileHandle handle;

            public string ValidatedRoot { get; }
            public string CanonicalDirectoryPath { get; }

            internal PinnedStorageDirectory(SafeFileHandle handle, string validatedRoot, string canonicalDirectoryPath)
            {
                this.handle = handle;
                ValidatedRoot = validatedRoot;
                CanonicalDirectoryPath = canonicalDirectoryPath;
            }

            public void VerifyStillPinned()
            {
                if (handle == null || handle.IsInvalid || handle.IsClosed)
                    throw new InvalidDataException("The account-data directory lock handle is no longer valid.");

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
                {
                    int infoError = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The account-data directory attributes could not be verified.",
                        infoError != 0 ? new Win32Exception(infoError) : null);
                }

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("The account-data directory uses an unsupported link.");

                if ((fileInfo.FileAttributes & FileAttributeDirectory) == 0)
                    throw new InvalidDataException("The account-data directory is not a valid directory.");

                string currentCanonical = TryGetFinalPathNameByHandle(handle, out int win32Error);
                if (String.IsNullOrEmpty(currentCanonical))
                    throw new InvalidDataException(
                        "The account-data directory canonical path could not be verified.",
                        win32Error != 0 ? new Win32Exception(win32Error) : null);

                string expected = CanonicalDirectoryPath.TrimEnd(DirectorySeparators);
                string actual = currentCanonical.TrimEnd(DirectorySeparators);
                if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The account-data directory changed during a storage operation.");
            }

            public bool TryVerifyStillPinned(out Exception failure)
            {
                try
                {
                    VerifyStillPinned();
                    failure = null;
                    return true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    return false;
                }
            }

            public void Dispose()
            {
                SafeFileHandle toDispose = Interlocked.Exchange(ref handle, null);
                toDispose?.Dispose();
            }
        }

        internal readonly struct StorageFileIdentity : IEquatable<StorageFileIdentity>
        {
            public uint VolumeSerialNumber { get; }
            public ulong FileIndex { get; }

            public StorageFileIdentity(uint volumeSerialNumber, ulong fileIndex)
            {
                VolumeSerialNumber = volumeSerialNumber;
                FileIndex = fileIndex;
            }

            public bool IsValid => VolumeSerialNumber != 0 && FileIndex != 0;

            public string ToToken()
            {
                return VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture) + ":" +
                    FileIndex.ToString("x16", CultureInfo.InvariantCulture);
            }

            public static bool TryParseToken(string token, out StorageFileIdentity identity)
            {
                identity = default;
                if (String.IsNullOrWhiteSpace(token) || token.Length != 25 || token[8] != ':')
                    return false;

                if (!UInt32.TryParse(token.Substring(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint volumeSerial) ||
                    !UInt64.TryParse(token.Substring(9, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong fileIndex))
                {
                    return false;
                }

                identity = new StorageFileIdentity(volumeSerial, fileIndex);
                return identity.IsValid;
            }

            public bool Equals(StorageFileIdentity other)
            {
                return VolumeSerialNumber == other.VolumeSerialNumber && FileIndex == other.FileIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is StorageFileIdentity other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(VolumeSerialNumber, FileIndex);
            }
        }

        internal sealed class LockedReplacementFile : IDisposable
        {
            private FileStream targetStream;

            public string TargetPath { get; }
            public string BackupPath { get; }
            public string OriginalHash { get; }
            public StorageFileIdentity TargetIdentity { get; }
            public bool Overwritten { get; private set; }

            internal LockedReplacementFile(
                FileStream targetStream,
                string targetPath,
                string backupPath,
                string originalHash,
                StorageFileIdentity targetIdentity)
            {
                this.targetStream = targetStream;
                TargetPath = targetPath;
                BackupPath = backupPath;
                OriginalHash = originalHash;
                TargetIdentity = targetIdentity;
            }

            public void OverwriteAndFlush(byte[] replacementBytes)
            {
                if (targetStream == null)
                    throw new ObjectDisposedException(nameof(LockedReplacementFile));
                if (replacementBytes == null)
                    throw new ArgumentNullException(nameof(replacementBytes));

                // Mark overwritten before mutating any bytes so a mid-write, truncate, or flush
                // failure always forces verified restoration from backup during rollback.
                Overwritten = true;

                if (replacementBytes.Length == 0)
                {
                    targetStream.Seek(0, SeekOrigin.Begin);
                    targetStream.SetLength(0);
                    OnBeforeFlushReplacementForTest?.Invoke(TargetPath);
                    targetStream.Flush(true);
                    return;
                }

                // Two-phase in-place overwrite with a durable leading sentinel (0xFF, invalid in
                // both UTF-8 JSON and Base64 ciphertext):
                //   1. Flush 0xFF at byte 0 so any crash during body write or truncation leaves
                //      an unambiguously in-progress file (even for block-aligned Base64 ciphertext).
                //   2. Write bytes [1..N-1], set final length, and flush to disk.
                //   3. Overwrite byte 0 with replacementBytes[0] and flush so the final single-byte
                //      write transitions directly from sentinel-marked to ReplacementHash.
                targetStream.Seek(0, SeekOrigin.Begin);
                targetStream.WriteByte(InPlaceOverwriteSentinelByte);
                targetStream.Flush(true);

                if (replacementBytes.Length > 1)
                {
                    targetStream.Seek(1, SeekOrigin.Begin);
                    targetStream.Write(replacementBytes, 1, replacementBytes.Length - 1);
                }
                targetStream.SetLength(replacementBytes.Length);
                OnBeforeFlushReplacementForTest?.Invoke(TargetPath);
                targetStream.Flush(true);

                targetStream.Seek(0, SeekOrigin.Begin);
                targetStream.WriteByte(replacementBytes[0]);
                targetStream.Flush(true);
            }

            public void RestoreFromVerifiedBackup(string expectedCanonicalDirectory)
            {
                if (targetStream == null)
                    throw new ObjectDisposedException(nameof(LockedReplacementFile));

                using (FileStream backupStream = OpenVerifiedRegularFileForRead(
                    BackupPath,
                    expectedCanonicalDirectory,
                    requireSingleLink: true,
                    out _))
                {
                    string backupHash = ComputeStreamSha256(backupStream);
                    if (!String.Equals(backupHash, OriginalHash, StringComparison.Ordinal))
                        throw new InvalidDataException("A replaced authenticator backup did not match the expected original file hash.");

                    RestoreStreamWithLeadingSentinel(backupStream, targetStream);
                    Overwritten = false;
                }
            }

            public void Dispose()
            {
                FileStream stream = Interlocked.Exchange(ref targetStream, null);
                stream?.Dispose();
            }
        }

        internal sealed class StagedTempFile : IDisposable
        {
            private FileStream tempStream;
            private bool disposed;

            public string TargetPath { get; }
            public string TempPath { get; }
            public string ContentHash { get; }
            public StorageFileIdentity FileIdentity { get; }
            public bool Published { get; private set; }

            internal StagedTempFile(
                FileStream tempStream,
                string targetPath,
                string tempPath,
                string contentHash,
                StorageFileIdentity fileIdentity)
            {
                this.tempStream = tempStream;
                TargetPath = targetPath;
                TempPath = tempPath;
                ContentHash = contentHash;
                FileIdentity = fileIdentity;
            }

            public void PublishCreateNew(PinnedStorageDirectory pinnedDir)
            {
                if (disposed || tempStream == null)
                    throw new ObjectDisposedException(nameof(StagedTempFile));
                if (pinnedDir == null)
                    throw new ArgumentNullException(nameof(pinnedDir));

                pinnedDir.VerifyStillPinned();

                // Bind publication directly to the still-open exclusive staged handle using
                // SetFileInformationByHandle(FileRenameInfo) with ReplaceIfExists = 0 while
                // pinnedDir holds the parent directory pinned, preventing source-path or
                // parent-directory swaps and preserving the staged file's VolumeSerialNumber/FileIndex.
                RenameHandleCreateNew(tempStream.SafeFileHandle, TargetPath);
                Published = true;

                FileStream streamToClose = Interlocked.Exchange(ref tempStream, null);
                streamToClose?.Dispose();
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                FileStream streamToClose = Interlocked.Exchange(ref tempStream, null);
                if (streamToClose == null)
                    return;

                try
                {
                    if (!Published)
                    {
                        FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
                        SetFileInformationByHandle(
                            streamToClose.SafeFileHandle,
                            FileDispositionInfoClass,
                            ref disposition,
                            (uint)Marshal.SizeOf<FileDispositionInfo>());
                    }
                }
                catch
                {
                    // Best-effort handle-bound unlink of unreferenced staged temp file.
                }
                finally
                {
                    streamToClose.Dispose();
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo lpFileInformation,
            uint dwBufferSize);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            IntPtr lpFileInformation,
            uint dwBufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle hFile,
            [Out] char[] lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetLongPathNameW(
            string lpszShortPath,
            [Out] char[] lpszLongPath,
            uint cchBuffer);

        internal static string ValidateDirectoryAndGetRoot(string directoryPath)
        {
            Interlocked.Increment(ref directoryValidationCount);
            if (String.IsNullOrWhiteSpace(directoryPath))
                throw new InvalidDataException("The account-data directory path is invalid.");

            string fullPath = StripNonRootTrailingSeparators(Path.GetFullPath(directoryPath));
            DirectoryInfo current = new DirectoryInfo(fullPath);
            while (current != null)
            {
                FileAttributes attributes = current.Attributes;
                if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The account-data directory uses an unsupported link.");

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }

            return fullPath.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar;
        }

        internal static string GetValidatedCanonicalDirectoryPath(string directoryPath)
        {
            Interlocked.Increment(ref canonicalDirectoryVerificationCount);
            if (String.IsNullOrWhiteSpace(directoryPath))
                throw new InvalidDataException("The account-data directory path is invalid.");

            string fullPath = Path.GetFullPath(directoryPath);
            string openPath = StripNonRootTrailingSeparators(fullPath);
            string win32OpenPath = ToWin32OpenPath(openPath);

            using (SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                0,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The account-data directory could not be opened for verification.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                return VerifyDirectoryHandleAndGetCanonicalPath(handle, win32OpenPath);
            }
        }

        internal static PinnedStorageDirectory TryPinExistingDirectory(string directoryPath, out string validatedRoot)
        {
            validatedRoot = ValidateDirectoryAndGetRoot(directoryPath);
            if (!Directory.Exists(directoryPath))
                return null;

            return PinExistingDirectoryCore(directoryPath, validatedRoot);
        }

        internal static PinnedStorageDirectory PinExistingDirectory(string directoryPath)
        {
            string validatedRoot = ValidateDirectoryAndGetRoot(directoryPath);
            return PinExistingDirectoryCore(directoryPath, validatedRoot);
        }

        internal static PinnedStorageDirectory CreateAndPinDirectory(string directoryPath)
        {
            string validatedRoot = ValidateDirectoryAndGetRoot(directoryPath);
            string normalizedTarget = StripNonRootTrailingSeparators(Path.GetFullPath(directoryPath));

            // Walk upward to find the nearest existing ancestor directory and collect missing segments.
            var missingSegments = new System.Collections.Generic.Stack<string>();
            string existingAncestor = normalizedTarget;
            while (!Directory.Exists(existingAncestor))
            {
                string parent = Path.GetDirectoryName(existingAncestor);
                if (String.IsNullOrWhiteSpace(parent) ||
                    String.Equals(parent, existingAncestor, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The account-data directory parent does not exist.");
                }

                string segment = Path.GetFileName(existingAncestor);
                if (String.IsNullOrWhiteSpace(segment))
                    throw new InvalidDataException("The account-data directory path is invalid.");

                missingSegments.Push(segment);
                existingAncestor = StripNonRootTrailingSeparators(parent);
            }

            // Pin the nearest existing ancestor first, then create and pin each missing segment
            // step by step so no ancestor can be replaced with a junction during creation.
            PinnedStorageDirectory currentPin = PinExistingDirectoryCore(
                existingAncestor,
                existingAncestor.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar);
            try
            {
                string currentPath = existingAncestor;
                while (missingSegments.Count > 0)
                {
                    currentPin.VerifyStillPinned();
                    string nextPath = Path.Combine(currentPath, missingSegments.Pop());
                    Directory.CreateDirectory(nextPath);
                    string nextRoot = nextPath.TrimEnd(DirectorySeparators) + Path.DirectorySeparatorChar;
                    PinnedStorageDirectory nextPin = PinExistingDirectoryCore(nextPath, nextRoot);
                    currentPin.Dispose();
                    currentPin = nextPin;
                    currentPath = nextPath;
                }

                // Ensure the final returned pin exposes the validated root for directoryPath.
                if (!String.Equals(currentPin.ValidatedRoot, validatedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    currentPin.Dispose();
                    return PinExistingDirectoryCore(normalizedTarget, validatedRoot);
                }

                return currentPin;
            }
            catch
            {
                currentPin.Dispose();
                throw;
            }
        }

        private static PinnedStorageDirectory PinExistingDirectoryCore(string directoryPath, string validatedRoot)
        {
            Interlocked.Increment(ref canonicalDirectoryVerificationCount);
            string fullPath = Path.GetFullPath(directoryPath);
            string openPath = StripNonRootTrailingSeparators(fullPath);
            string win32OpenPath = ToWin32OpenPath(openPath);

            // Omit FILE_SHARE_DELETE so Windows prevents renaming or deleting
            // the storage directory or any of its ancestor directories while pinned.
            SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                FileListDirectory,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                throw new InvalidDataException(
                    "The account-data directory could not be opened and pinned for verification.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            try
            {
                string canonicalPath = VerifyDirectoryHandleAndGetCanonicalPath(handle, win32OpenPath);
                return new PinnedStorageDirectory(handle, validatedRoot, canonicalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static string ReadTextWithLimit(string filename, long maximumBytes, string expectedCanonicalDirectory)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(filename))
                throw new InvalidDataException("The expected storage file path is invalid.");

            string fullPath = Path.GetFullPath(filename);
            string win32OpenPath = ToWin32OpenPath(fullPath);

            // Open with FILE_FLAG_OPEN_REPARSE_POINT so file symlinks are never traversed during open.
            SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                    throw new FileNotFoundException("The expected storage file does not exist.", filename);

                throw new IOException(
                    "The expected storage file could not be opened safely.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            bool handleOwnedByStream = false;
            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "A storage file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
                if ((fileInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage file path points to a directory.");

                long fileSize = ((long)fileInfo.FileSizeHigh << 32) | fileInfo.FileSizeLow;
                if (fileSize > maximumBytes)
                    throw new InvalidDataException("A storage file is larger than the supported size limit.");

                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);

                using (FileStream stream = new FileStream(handle, FileAccess.Read, 81920, isAsync: false))
                {
                    handleOwnedByStream = true;
                    using (MemoryStream contents = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920];
                        int bytesRead;
                        long totalBytes = 0;
                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            totalBytes += bytesRead;
                            if (totalBytes > maximumBytes)
                                throw new InvalidDataException("A storage file is larger than the supported size limit.");
                            contents.Write(buffer, 0, bytesRead);
                        }

                        return new UTF8Encoding(false, true).GetString(contents.ToArray());
                    }
                }
            }
            finally
            {
                if (!handleOwnedByStream)
                    handle.Dispose();
            }
        }

        internal static bool TryInspectCanonicalTargetForInPlaceReplacement(
            string canonicalPath,
            string expectedCanonicalDirectory,
            long maxBackupBytes)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(canonicalPath))
                return false;

            string fullPath = Path.GetFullPath(canonicalPath);
            string win32OpenPath = ToWin32OpenPath(fullPath);

            using (SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                FileReadAttributes,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                        return false;
                    return false;
                }

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
                    return false;

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");

                if ((fileInfo.FileAttributes & FileAttributeDirectory) != 0)
                    return false;

                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);

                if ((fileInfo.FileAttributes & FileAttributeReadOnly) != 0)
                    return false;

                // Multiply-linked files must never be mutated in place so external hard links stay untouched.
                if (fileInfo.NumberOfLinks != 1)
                    return false;

                long fileSize = ((long)fileInfo.FileSizeHigh << 32) | fileInfo.FileSizeLow;
                if (fileSize > maxBackupBytes)
                    return false;

                StorageFileIdentity identity = GetFileIdentity(fileInfo);
                return identity.IsValid;
            }
        }

        internal static LockedReplacementFile OpenAndBackUpForInPlaceReplacement(
            string targetPath,
            string backupPath,
            string expectedCanonicalDirectory,
            long maxBackupBytes)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(targetPath))
                throw new InvalidDataException("The target storage file path is invalid.");
            if (String.IsNullOrWhiteSpace(backupPath))
                throw new InvalidDataException("The backup storage file path is invalid.");

            string targetFullPath = Path.GetFullPath(targetPath);
            string backupFullPath = Path.GetFullPath(backupPath);
            string win32TargetPath = ToWin32OpenPath(targetFullPath);

            // Hold an exclusive read/write handle on the target file across backup creation,
            // journal persistence, in-place overwrite, and manifest commit so no external process
            // can substitute, rename, unlink, or modify the target between verification and commit.
            SafeFileHandle targetHandle = CreateFileW(
                win32TargetPath,
                GenericRead | GenericWrite,
                FileShareNone,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagWriteThrough,
                IntPtr.Zero);

            if (targetHandle == null || targetHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                targetHandle?.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                    throw new FileNotFoundException("The existing authenticator file could not be found for replacement.", targetPath);

                throw new IOException(
                    "The existing authenticator file could not be opened exclusively for safe replacement.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            FileStream targetStream = null;
            bool backupCreated = false;
            try
            {
                if (!GetFileInformationByHandle(targetHandle, out ByHandleFileInformation targetInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The target storage file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((targetInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
                if ((targetInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage file path points to a directory.");
                if (targetInfo.NumberOfLinks != 1)
                    throw new InvalidDataException("A storage file has multiple hard links and cannot be replaced in place.");

                long fileSize = ((long)targetInfo.FileSizeHigh << 32) | targetInfo.FileSizeLow;
                if (fileSize > maxBackupBytes)
                    throw new InvalidDataException("The existing storage file exceeds the maximum supported backup size.");

                StorageFileIdentity targetIdentity = GetFileIdentity(targetInfo);
                if (!targetIdentity.IsValid)
                    throw new NotSupportedException("The selected data folder does not provide verifiable file identities required for in-place replacement.");

                VerifyOpenedFileWithinCanonicalDirectory(targetHandle, Path.GetFileName(targetFullPath), expectedCanonicalDirectory);

                targetStream = new FileStream(targetHandle, FileAccess.ReadWrite, 81920, isAsync: false);
                targetHandle = null;

                string originalHash = CopyStreamToCreateNewVerifiedFileAndHash(
                    targetStream,
                    backupFullPath,
                    expectedCanonicalDirectory,
                    maxBackupBytes,
                    out backupCreated);

                return new LockedReplacementFile(
                    targetStream,
                    targetFullPath,
                    backupFullPath,
                    originalHash,
                    targetIdentity);
            }
            catch
            {
                targetStream?.Dispose();
                targetHandle?.Dispose();
                if (backupCreated)
                {
                    try
                    {
                        DeleteRegularFileWithinCanonicalDirectory(backupFullPath, expectedCanonicalDirectory);
                    }
                    catch
                    {
                        // Best-effort cleanup of pre-journal backup on failure.
                    }
                }
                throw;
            }
        }

        internal static StagedTempFile StageNewFileWithIdentity(
            string targetPath,
            string contents,
            string expectedCanonicalDirectory)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(targetPath))
                throw new InvalidDataException("The target storage file path is invalid.");
            if (contents == null)
                throw new ArgumentNullException(nameof(contents));

            string targetFullPath = Path.GetFullPath(targetPath);
            string targetParentDir = Path.GetDirectoryName(targetFullPath);
            string tempFullPath = Path.Combine(
                targetParentDir,
                StagedFileTempPrefix + Guid.NewGuid().ToString("N") + StagingTempSuffix);
            string win32TempPath = ToWin32OpenPath(tempFullPath);

            // Hold an exclusive handle with DELETE access across write, flush, and handle-bound
            // FileRenameInfo publication so no external process can swap the temp file path.
            SafeFileHandle tempHandle = CreateFileW(
                win32TempPath,
                GenericWrite | FileReadAttributes | DeleteAccess,
                FileShareNone,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);

            if (tempHandle == null || tempHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                tempHandle?.Dispose();
                throw new IOException(
                    "The temporary storage file could not be created safely.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            FileStream tempStream = null;
            bool tempVerified = false;
            try
            {
                if (!GetFileInformationByHandle(tempHandle, out ByHandleFileInformation tempInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The temporary storage file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((tempInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A temporary storage file uses an unsupported link.");
                if ((tempInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A temporary storage file path points to a directory.");

                VerifyOpenedFileWithinCanonicalDirectory(tempHandle, Path.GetFileName(tempFullPath), expectedCanonicalDirectory);
                tempVerified = true;

                StorageFileIdentity identity = GetFileIdentity(tempInfo);
                byte[] utf8Bytes = new UTF8Encoding(false).GetBytes(contents);
                string contentHash;
                using (SHA256 sha256 = SHA256.Create())
                    contentHash = Convert.ToBase64String(sha256.ComputeHash(utf8Bytes));

                tempStream = new FileStream(tempHandle, FileAccess.Write, 81920, isAsync: false);
                tempHandle = null;
                tempStream.Write(utf8Bytes, 0, utf8Bytes.Length);
                tempStream.Flush(true);

                return new StagedTempFile(tempStream, targetFullPath, tempFullPath, contentHash, identity);
            }
            catch
            {
                if (tempVerified)
                {
                    try
                    {
                        SafeFileHandle handleToDelete = tempStream != null ? tempStream.SafeFileHandle : tempHandle;
                        if (handleToDelete != null && !handleToDelete.IsInvalid && !handleToDelete.IsClosed)
                        {
                            FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
                            SetFileInformationByHandle(
                                handleToDelete,
                                FileDispositionInfoClass,
                                ref disposition,
                                (uint)Marshal.SizeOf<FileDispositionInfo>());
                        }
                    }
                    catch
                    {
                        // Best-effort handle-bound cleanup of failed temp file.
                    }
                }
                tempStream?.Dispose();
                tempHandle?.Dispose();
                throw;
            }
        }

        private static void RenameHandleCreateNew(
            SafeFileHandle handle,
            string targetFullPath)
        {
            if (String.IsNullOrWhiteSpace(targetFullPath))
                throw new InvalidDataException("The target storage file path is invalid.");

            string win32TargetPath = ToWin32OpenPath(Path.GetFullPath(targetFullPath));
            byte[] nameBytes = Encoding.Unicode.GetBytes(win32TargetPath);
            int rootDirectoryOffset = IntPtr.Size;
            int fileNameLengthOffset = IntPtr.Size * 2;
            int fileNameOffset = fileNameLengthOffset + sizeof(uint);
            int totalSize = fileNameOffset + nameBytes.Length + 2;

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                for (int i = 0; i < totalSize; i++)
                    Marshal.WriteByte(buffer, i, 0);

                // ReplaceIfExists = 0 (strictly fail if destination already exists);
                // Win32 SetFileInformationByHandle(FileRenameInfo) requires RootDirectory = NULL
                // with a full destination path.
                Marshal.WriteByte(buffer, 0, 0);
                Marshal.WriteIntPtr(buffer, rootDirectoryOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, fileNameOffset), nameBytes.Length);

                if (!SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)totalSize))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The staged authenticator file could not be published to its target path.",
                        error != 0 ? new Win32Exception(error) : null);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static void RestoreReplacedFileFromVerifiedBackup(
            string backupPath,
            string targetPath,
            string expectedOriginalHash,
            string expectedReplacementHash,
            StorageFileIdentity? expectedTargetIdentity,
            string expectedCanonicalDirectory,
            Func<Stream, bool> isIntactAccountFile = null)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");

            using (FileStream backupStream = OpenVerifiedRegularFileForRead(
                backupPath,
                expectedCanonicalDirectory,
                requireSingleLink: true,
                out _))
            {
                string backupHash = ComputeStreamSha256(backupStream);
                if (!String.Equals(backupHash, expectedOriginalHash, StringComparison.Ordinal))
                    throw new InvalidDataException("A replaced authenticator backup did not match the expected original file hash.");

                string targetFullPath = Path.GetFullPath(targetPath);
                string win32TargetPath = ToWin32OpenPath(targetFullPath);

                SafeFileHandle targetHandle = CreateFileW(
                    win32TargetPath,
                    GenericRead | GenericWrite,
                    FileShareNone,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagWriteThrough,
                    IntPtr.Zero);

                if (targetHandle != null && !targetHandle.IsInvalid)
                {
                    bool handleOwnedByStream = false;
                    try
                    {
                        if (!GetFileInformationByHandle(targetHandle, out ByHandleFileInformation targetInfo))
                        {
                            int error = Marshal.GetLastWin32Error();
                            throw new InvalidDataException(
                                "The target storage file attributes could not be verified during rollback.",
                                error != 0 ? new Win32Exception(error) : null);
                        }

                        if ((targetInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                            throw new InvalidDataException("A storage file uses an unsupported link.");
                        if ((targetInfo.FileAttributes & FileAttributeDirectory) != 0)
                            throw new InvalidDataException("The target authenticator path is not a regular file.");
                        if (targetInfo.NumberOfLinks != 1)
                            throw new InvalidDataException("A live authenticator file has multiple hard links and cannot be restored in place.");

                        VerifyOpenedFileWithinCanonicalDirectory(targetHandle, Path.GetFileName(targetFullPath), expectedCanonicalDirectory);
                        StorageFileIdentity actualIdentity = GetFileIdentity(targetInfo);

                        using (FileStream targetStream = new FileStream(targetHandle, FileAccess.ReadWrite, 81920, isAsync: false))
                        {
                            handleOwnedByStream = true;
                            string targetHash = ComputeStreamSha256(targetStream);
                            if (String.Equals(targetHash, expectedOriginalHash, StringComparison.Ordinal))
                                return;

                            if (expectedTargetIdentity.HasValue && !actualIdentity.Equals(expectedTargetIdentity.Value))
                                throw new InvalidDataException("A live authenticator file identity did not match the uncommitted replacement recorded in the storage journal.");

                            if (!String.Equals(targetHash, expectedReplacementHash, StringComparison.Ordinal))
                            {
                                // Allow recovery of an interrupted partial/corrupt in-place write when
                                // the exact locked target file identity matches and the on-disk bytes
                                // are empty, carry our leading in-place write sentinel (0xFF), or are
                                // not a valid intact account file (never clobbering intact live data).
                                bool isRecoverablePartialWrite = false;
                                if (expectedTargetIdentity.HasValue && actualIdentity.Equals(expectedTargetIdentity.Value))
                                {
                                    if (targetStream.Length == 0)
                                    {
                                        isRecoverablePartialWrite = true;
                                    }
                                    else
                                    {
                                        targetStream.Seek(0, SeekOrigin.Begin);
                                        if (targetStream.ReadByte() == InPlaceOverwriteSentinelByte)
                                        {
                                            isRecoverablePartialWrite = true;
                                        }
                                        else if (isIntactAccountFile != null)
                                        {
                                            targetStream.Seek(0, SeekOrigin.Begin);
                                            isRecoverablePartialWrite = !isIntactAccountFile(targetStream);
                                        }
                                    }
                                }

                                if (!isRecoverablePartialWrite)
                                    throw new InvalidDataException("A live authenticator file did not match the uncommitted replacement recorded in the storage journal.");
                            }

                            RestoreStreamWithLeadingSentinel(backupStream, targetStream);
                        }
                    }
                    finally
                    {
                        if (!handleOwnedByStream)
                            targetHandle.Dispose();
                    }

                    return;
                }

                int openError = Marshal.GetLastWin32Error();
                targetHandle?.Dispose();
                if (openError != ErrorFileNotFound)
                {
                    if (Directory.Exists(targetFullPath))
                        throw new InvalidDataException("The target authenticator path is not a regular file.");

                    throw new IOException(
                        "The target authenticator file could not be opened exclusively for rollback.",
                        openError != 0 ? new Win32Exception(openError) : null);
                }

                // Target file is missing: stage the full verified backup into a temp file and publish
                // via handle-bound FileRenameInfo (ReplaceIfExists = 0) so a crash mid-copy never
                // leaves an incomplete target file with a new FileIndex at targetFullPath.
                backupStream.Seek(0, SeekOrigin.Begin);
                StageStreamAndPublishCreateNew(backupStream, targetFullPath, expectedCanonicalDirectory);
            }
        }

        internal static void VerifyFileMatchesOriginalHash(
            string filePath,
            string expectedOriginalHash,
            string expectedCanonicalDirectory)
        {
            using (FileStream stream = OpenVerifiedRegularFileForRead(
                filePath,
                expectedCanonicalDirectory,
                requireSingleLink: true,
                out _))
            {
                // Do not require the original FileIndex here: if an earlier interrupted recovery
                // recreated a missing target from backup and deleted the backup before journal
                // cleanup, the recreated file has a new FileIndex while matching OriginalHash.
                string actualHash = ComputeStreamSha256(stream);
                if (!String.Equals(actualHash, expectedOriginalHash, StringComparison.Ordinal))
                    throw new InvalidDataException("A replaced authenticator file could not be verified as original because its backup is missing.");
            }
        }

        internal static bool DeleteFileIfVerifiedExclusive(
            string filePath,
            string expectedHash,
            StorageFileIdentity? expectedIdentity,
            string expectedCanonicalDirectory,
            out string preservedReason,
            Func<string, bool> beforeDeleteHook = null)
        {
            preservedReason = null;
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");

            string fullPath = Path.GetFullPath(filePath);
            string win32OpenPath = ToWin32OpenPath(fullPath);

            // Open exclusively (dwShareMode = 0) with FILE_FLAG_OPEN_REPARSE_POINT so no other
            // process can write, rename, or replace the file between verification and unlink.
            SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                FileReadData | FileReadAttributes | DeleteAccess,
                FileShareNone,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                    return true;
                if (Directory.Exists(fullPath))
                {
                    preservedReason = "directory";
                    return true;
                }

                throw new IOException(
                    "The temporary authenticator file could not be opened exclusively for verified deletion.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            bool handleOwnedByStream = false;
            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The temporary authenticator file attributes could not be inspected.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((fileInfo.FileAttributes & FileAttributeDirectory) != 0)
                {
                    preservedReason = "directory";
                    return true;
                }

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    preservedReason = "reparse_point";
                    return true;
                }

                // Always verify the opened handle resolves inside the pinned canonical storage directory
                // so a substituted parent directory can never redirect deletion outside maFiles.
                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);

                // Explicit hard-link policy: never unlink a multiply-linked file during uncommitted cleanup.
                if (fileInfo.NumberOfLinks != 1)
                {
                    preservedReason = "multiple_links";
                    return true;
                }

                if (expectedIdentity.HasValue && !GetFileIdentity(fileInfo).Equals(expectedIdentity.Value))
                {
                    preservedReason = "identity_mismatch";
                    return true;
                }

                using (FileStream stream = new FileStream(handle, FileAccess.Read, 81920, isAsync: false))
                {
                    handleOwnedByStream = true;
                    string actualHash = ComputeStreamSha256(stream);
                    if (!String.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    {
                        preservedReason = "hash_mismatch";
                        return true;
                    }

                    if (beforeDeleteHook != null && !beforeDeleteHook(fullPath))
                        throw new IOException("Simulated file deletion failure for testing.");

                    FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(
                        handle,
                        FileDispositionInfoClass,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInfo>()))
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new IOException(
                            "The verified temporary authenticator file could not be marked for deletion.",
                            error != 0 ? new Win32Exception(error) : null);
                    }
                }

                return true;
            }
            finally
            {
                if (!handleOwnedByStream)
                    handle.Dispose();
            }
        }

        internal static void DeleteRegularFileWithinCanonicalDirectory(
            string filePath,
            string expectedCanonicalDirectory,
            Func<string, bool> beforeDeleteHook = null)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(filePath))
                return;

            string fullPath = Path.GetFullPath(filePath);
            string win32OpenPath = ToWin32OpenPath(fullPath);

            using (SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                FileReadAttributes | DeleteAccess,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                        return;

                    throw new IOException(
                        "The storage file could not be opened for safe deletion.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The storage file attributes could not be verified before deletion.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
                if ((fileInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage file path points to a directory.");

                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);

                if (beforeDeleteHook != null && !beforeDeleteHook(fullPath))
                    throw new IOException("Simulated file deletion failure for testing.");

                FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
                if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        "The storage file could not be marked for deletion.",
                        error != 0 ? new Win32Exception(error) : null);
                }
            }
        }

        internal static string ComputeFileSha256(
            string filePath,
            string expectedCanonicalDirectory,
            out StorageFileIdentity identity)
        {
            using (FileStream stream = OpenVerifiedRegularFileForRead(
                filePath,
                expectedCanonicalDirectory,
                requireSingleLink: false,
                out ByHandleFileInformation fileInfo))
            {
                identity = GetFileIdentity(fileInfo);
                return ComputeStreamSha256(stream);
            }
        }

        internal static string GetFileIdentityToken(string filePath, string expectedCanonicalDirectory)
        {
            using (FileStream stream = OpenVerifiedRegularFileForRead(
                filePath,
                expectedCanonicalDirectory,
                requireSingleLink: false,
                out ByHandleFileInformation fileInfo))
            {
                StorageFileIdentity identity = GetFileIdentity(fileInfo);
                if (!identity.IsValid)
                    throw new InvalidDataException("The storage file identity could not be verified.");
                return identity.ToToken();
            }
        }

        private static FileStream OpenVerifiedRegularFileForRead(
            string filePath,
            string expectedCanonicalDirectory,
            bool requireSingleLink,
            out ByHandleFileInformation fileInfo)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(filePath))
                throw new InvalidDataException("The expected storage file path is invalid.");

            string fullPath = Path.GetFullPath(filePath);
            string win32OpenPath = ToWin32OpenPath(fullPath);

            SafeFileHandle handle = CreateFileW(
                win32OpenPath,
                GenericRead,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                    throw new FileNotFoundException("The expected storage file does not exist.", filePath);

                throw new IOException(
                    "The expected storage file could not be opened safely.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            try
            {
                if (!GetFileInformationByHandle(handle, out fileInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "A storage file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
                if ((fileInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage file path points to a directory.");
                if (requireSingleLink && fileInfo.NumberOfLinks != 1)
                    throw new InvalidDataException("A storage file has multiple hard links.");

                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);
                return new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static string CopyStreamToCreateNewVerifiedFileAndHash(
            Stream sourceStream,
            string destinationFullPath,
            string expectedCanonicalDirectory,
            long maxBytes,
            out bool fileCreated)
        {
            fileCreated = false;
            string win32DestPath = ToWin32OpenPath(destinationFullPath);

            SafeFileHandle destHandle = CreateFileW(
                win32DestPath,
                GenericWrite | FileReadAttributes,
                FileShareNone,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);

            if (destHandle == null || destHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                destHandle?.Dispose();
                throw new IOException(
                    "The backup storage file could not be reserved exclusively.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            bool handleOwnedByStream = false;
            try
            {
                if (!GetFileInformationByHandle(destHandle, out ByHandleFileInformation destInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The backup storage file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((destInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A backup storage file uses an unsupported link.");
                if ((destInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A backup storage file path points to a directory.");

                VerifyOpenedFileWithinCanonicalDirectory(destHandle, Path.GetFileName(destinationFullPath), expectedCanonicalDirectory);
                fileCreated = true;

                using (FileStream destStream = new FileStream(destHandle, FileAccess.Write, 81920, isAsync: false))
                using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    handleOwnedByStream = true;
                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    long totalBytes = 0;
                    while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        totalBytes += bytesRead;
                        if (totalBytes > maxBytes)
                            throw new InvalidDataException("The existing storage file exceeds the maximum supported backup size.");
                        hasher.AppendData(buffer, 0, bytesRead);
                        destStream.Write(buffer, 0, bytesRead);
                    }

                    destStream.Flush(true);
                    return Convert.ToBase64String(hasher.GetHashAndReset());
                }
            }
            finally
            {
                if (!handleOwnedByStream)
                    destHandle.Dispose();
            }
        }

        internal static void CreateExclusiveMarkerFileIfMissing(
            string markerPath,
            string expectedCanonicalDirectory,
            string contents)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");
            if (String.IsNullOrWhiteSpace(markerPath))
                throw new InvalidDataException("The storage marker file path is invalid.");

            string fullPath = Path.GetFullPath(markerPath);
            string win32Path = ToWin32OpenPath(fullPath);

            OnBeforeCreateQuarantineMarkerForTest?.Invoke(fullPath);

            // Publish the quarantine marker directly with a single CREATE_NEW call so there is
            // no temporary-file rename window between creation and visibility on disk.
            SafeFileHandle handle = CreateFileW(
                win32Path,
                GenericWrite | FileReadAttributes,
                FileShareNone,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                if (error == ErrorFileExists || error == ErrorAlreadyExists)
                    return;

                throw new IOException(
                    "The storage quarantine marker file could not be created.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            bool handleOwnedByStream = false;
            try
            {
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The storage quarantine marker file attributes could not be verified.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((info.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage marker file uses an unsupported link.");
                if ((info.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage marker file path points to a directory.");

                VerifyOpenedFileWithinCanonicalDirectory(handle, Path.GetFileName(fullPath), expectedCanonicalDirectory);

                using (FileStream stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false))
                {
                    handleOwnedByStream = true;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(contents ?? String.Empty);
                    if (bytes.Length > 0)
                        stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            finally
            {
                if (!handleOwnedByStream)
                    handle.Dispose();
            }
        }

        private static void RestoreStreamWithLeadingSentinel(FileStream sourceStream, FileStream targetStream)
        {
            sourceStream.Seek(0, SeekOrigin.Begin);
            if (sourceStream.Length == 0)
            {
                targetStream.Seek(0, SeekOrigin.Begin);
                targetStream.SetLength(0);
                targetStream.Flush(true);
                return;
            }

            int firstByte = sourceStream.ReadByte();
            if (firstByte < 0)
                throw new EndOfStreamException("The verified backup stream ended unexpectedly.");

            targetStream.Seek(0, SeekOrigin.Begin);
            targetStream.WriteByte(InPlaceOverwriteSentinelByte);
            targetStream.Flush(true);

            targetStream.Seek(1, SeekOrigin.Begin);
            CopyStreamExact(sourceStream, targetStream);
            targetStream.SetLength(sourceStream.Length);
            targetStream.Flush(true);

            targetStream.Seek(0, SeekOrigin.Begin);
            targetStream.WriteByte((byte)firstByte);
            targetStream.Flush(true);
        }

        private static void StageStreamAndPublishCreateNew(
            Stream sourceStream,
            string destinationFullPath,
            string expectedCanonicalDirectory)
        {
            string parentDirectory = Path.GetDirectoryName(destinationFullPath);
            string tempFullPath = Path.Combine(
                parentDirectory,
                RestoredTargetTempPrefix + Guid.NewGuid().ToString("N") + StagingTempSuffix);
            string win32TempPath = ToWin32OpenPath(tempFullPath);

            SafeFileHandle tempHandle = CreateFileW(
                win32TempPath,
                GenericWrite | FileReadAttributes | DeleteAccess,
                FileShareNone,
                IntPtr.Zero,
                CreateNew,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);

            if (tempHandle == null || tempHandle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                tempHandle?.Dispose();
                throw new IOException(
                    "The temporary authenticator file could not be created safely during rollback.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            FileStream tempStream = null;
            bool tempVerified = false;
            bool published = false;
            try
            {
                if (!GetFileInformationByHandle(tempHandle, out ByHandleFileInformation tempInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidDataException(
                        "The temporary authenticator file attributes could not be verified during rollback.",
                        error != 0 ? new Win32Exception(error) : null);
                }

                if ((tempInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
                if ((tempInfo.FileAttributes & FileAttributeDirectory) != 0)
                    throw new InvalidDataException("A storage file path points to a directory.");

                VerifyOpenedFileWithinCanonicalDirectory(tempHandle, Path.GetFileName(tempFullPath), expectedCanonicalDirectory);
                tempVerified = true;

                tempStream = new FileStream(tempHandle, FileAccess.Write, 81920, isAsync: false);
                tempHandle = null;

                CopyStreamExact(sourceStream, tempStream);
                tempStream.Flush(true);

                OnBeforePublishRestoredMissingTargetForTest?.Invoke(destinationFullPath);

                using (PinnedStorageDirectory pinnedDir = TryPinExistingDirectory(parentDirectory, out _))
                {
                    if (pinnedDir == null ||
                        !String.Equals(pinnedDir.CanonicalDirectoryPath, expectedCanonicalDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The account-data directory canonical path changed during rollback.");
                    }

                    pinnedDir.VerifyStillPinned();
                    RenameHandleCreateNew(tempStream.SafeFileHandle, destinationFullPath);
                    published = true;
                }
            }
            catch
            {
                if (tempVerified && !published)
                {
                    try
                    {
                        SafeFileHandle handleToDelete = tempStream != null ? tempStream.SafeFileHandle : tempHandle;
                        if (handleToDelete != null && !handleToDelete.IsInvalid && !handleToDelete.IsClosed)
                        {
                            FileDispositionInfo disposition = new FileDispositionInfo { DeleteFile = 1 };
                            SetFileInformationByHandle(
                                handleToDelete,
                                FileDispositionInfoClass,
                                ref disposition,
                                (uint)Marshal.SizeOf<FileDispositionInfo>());
                        }
                    }
                    catch
                    {
                        // Best-effort handle-bound cleanup of unpublished rollback temp file.
                    }
                }
                throw;
            }
            finally
            {
                tempStream?.Dispose();
                tempHandle?.Dispose();
            }
        }

        private static string ComputeStreamSha256(Stream stream)
        {
            using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hasher.AppendData(buffer, 0, bytesRead);
                }
                return Convert.ToBase64String(hasher.GetHashAndReset());
            }
        }

        private static void CopyStreamExact(Stream source, Stream destination)
        {
            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, bytesRead);
            }
        }

        private static StorageFileIdentity GetFileIdentity(in ByHandleFileInformation fileInfo)
        {
            ulong fileIndex = ((ulong)fileInfo.FileIndexHigh << 32) | fileInfo.FileIndexLow;
            return new StorageFileIdentity(fileInfo.VolumeSerialNumber, fileIndex);
        }

        private static string VerifyDirectoryHandleAndGetCanonicalPath(
            SafeFileHandle handle,
            string win32OpenPath)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation fileInfo))
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidDataException(
                    "The account-data directory attributes could not be verified.",
                    error != 0 ? new Win32Exception(error) : null);
            }

            if ((fileInfo.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new InvalidDataException("The account-data directory uses an unsupported link.");

            if ((fileInfo.FileAttributes & FileAttributeDirectory) == 0)
                throw new InvalidDataException("The account-data directory is not a valid directory.");

            string canonicalPath = TryGetFinalPathNameByHandle(handle, out int finalPathError);
            if (String.IsNullOrEmpty(canonicalPath))
                throw new InvalidDataException(
                    "The account-data directory canonical path could not be verified.",
                    finalPathError != 0 ? new Win32Exception(finalPathError) : null);

            VerifyCanonicalDirectoryMatchesExpectedPath(win32OpenPath, canonicalPath);
            return canonicalPath;
        }

        private static void VerifyCanonicalDirectoryMatchesExpectedPath(string win32OpenPath, string canonicalDirectoryPath)
        {
            string longFullPath = GetLongPathNameOrThrow(win32OpenPath);
            string pathRoot = Path.GetPathRoot(longFullPath);
            if (String.IsNullOrWhiteSpace(pathRoot))
                throw new InvalidDataException("The account-data directory root could not be verified.");

            string relativePath = longFullPath.Length > pathRoot.Length
                ? longFullPath.Substring(pathRoot.Length).Trim(DirectorySeparators)
                : String.Empty;

            string rootOpenPath = ToWin32RootOpenPath(pathRoot);
            string canonicalRoot;
            int rootError = 0;
            using (SafeFileHandle rootHandle = CreateFileW(
                rootOpenPath,
                0,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero))
            {
                if (rootHandle == null || rootHandle.IsInvalid)
                {
                    rootError = Marshal.GetLastWin32Error();
                    canonicalRoot = null;
                }
                else
                {
                    canonicalRoot = TryGetFinalPathNameByHandle(rootHandle, out rootError);
                }
            }

            if (String.IsNullOrEmpty(canonicalRoot))
                throw new InvalidDataException(
                    "The account-data directory volume root could not be verified.",
                    rootError != 0 ? new Win32Exception(rootError) : null);

            string trimmedCanonicalRoot = canonicalRoot.TrimEnd(DirectorySeparators);
            string expectedCanonical = String.IsNullOrEmpty(relativePath)
                ? trimmedCanonicalRoot
                : trimmedCanonicalRoot + Path.DirectorySeparatorChar + relativePath;
            string actualCanonical = canonicalDirectoryPath.TrimEnd(DirectorySeparators);

            if (!String.Equals(actualCanonical, expectedCanonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The account-data directory resolved through an unsupported link.");
        }

        internal static void VerifyOpenedFileWithinCanonicalDirectory(
            SafeFileHandle fileHandle,
            string expectedFilename,
            string expectedCanonicalDirectory)
        {
            if (String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                throw new InvalidDataException("The account-data directory canonical path could not be verified.");

            string finalFilePath = TryGetFinalPathNameByHandle(fileHandle, out int win32Error);
            if (String.IsNullOrEmpty(finalFilePath))
                throw new InvalidDataException(
                    "A storage file path could not be verified.",
                    win32Error != 0 ? new Win32Exception(win32Error) : null);

            string normalizedExpectedDir = expectedCanonicalDirectory.TrimEnd(DirectorySeparators);
            int lastSeparator = finalFilePath.LastIndexOfAny(DirectorySeparators);
            if (lastSeparator <= 0)
                throw new InvalidDataException("A storage file resolved outside the account-data directory.");

            string actualDir = finalFilePath.Substring(0, lastSeparator);
            string actualName = finalFilePath.Substring(lastSeparator + 1);
            if (!String.Equals(actualDir, normalizedExpectedDir, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(actualName, expectedFilename, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A storage file resolved outside the account-data directory.");
            }
        }

        private static string TryGetFinalPathNameByHandle(SafeFileHandle handle, out int win32Error)
        {
            win32Error = 0;
            if (handle == null || handle.IsInvalid)
                return null;

            int bufferLength = 512;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                char[] buffer = ArrayPool<char>.Shared.Rent(bufferLength);
                try
                {
                    uint written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized);
                    if (written == 0)
                    {
                        win32Error = Marshal.GetLastWin32Error();
                        return null;
                    }

                    if (written < (uint)buffer.Length)
                        return new string(buffer, 0, (int)written);

                    bufferLength = checked((int)written + 1);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
            }

            return null;
        }

        private static string GetLongPathNameOrThrow(string win32Path)
        {
            int bufferLength = Math.Max(512, win32Path.Length + 64);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                char[] buffer = ArrayPool<char>.Shared.Rent(bufferLength);
                try
                {
                    uint written = GetLongPathNameW(win32Path, buffer, (uint)buffer.Length);
                    if (written == 0)
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new InvalidDataException(
                            "The account-data directory path could not be normalized.",
                            error != 0 ? new Win32Exception(error) : null);
                    }

                    if (written < (uint)buffer.Length)
                        return new string(buffer, 0, (int)written);

                    bufferLength = checked((int)written + 1);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
            }

            throw new InvalidDataException("The account-data directory path could not be normalized.");
        }

        internal static string StripNonRootTrailingSeparators(string fullPath)
        {
            if (String.IsNullOrWhiteSpace(fullPath))
                return fullPath;

            string root = Path.GetPathRoot(fullPath);
            if (String.IsNullOrEmpty(root))
                return fullPath.TrimEnd(DirectorySeparators);

            string trimmed = fullPath.TrimEnd(DirectorySeparators);
            string trimmedRoot = root.TrimEnd(DirectorySeparators);
            if (trimmed.Length <= trimmedRoot.Length)
            {
                // Preserve a trailing separator on drive and UNC share roots so CreateFileW opens the root directory.
                return trimmedRoot + Path.DirectorySeparatorChar;
            }

            return trimmed;
        }

        internal static string ToWin32OpenPath(string normalizedPath)
        {
            if (String.IsNullOrWhiteSpace(normalizedPath))
                return normalizedPath;

            if (normalizedPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return normalizedPath;
            }

            if (normalizedPath.Length < Win32LongPathThreshold)
                return normalizedPath;

            if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
                return @"\\?\UNC\" + normalizedPath.Substring(2);

            return @"\\?\" + normalizedPath;
        }

        internal static string ToWin32RootOpenPath(string pathRoot)
        {
            if (String.IsNullOrWhiteSpace(pathRoot))
                return pathRoot;

            string withSeparator = pathRoot.EndsWith(Path.DirectorySeparatorChar) || pathRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? pathRoot
                : pathRoot + Path.DirectorySeparatorChar;
            return ToWin32OpenPath(withSeparator);
        }
    }
}
