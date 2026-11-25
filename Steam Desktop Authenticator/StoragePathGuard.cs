using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    internal static class StoragePathGuard
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
        private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileNameNormalized = 0x0;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int Win32LongPathThreshold = 248;

        private static readonly char[] DirectorySeparators =
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        };

        private static long directoryValidationCount;
        private static long canonicalDirectoryVerificationCount;

        internal static long DirectoryValidationCount => Interlocked.Read(ref directoryValidationCount);
        internal static long CanonicalDirectoryVerificationCount => Interlocked.Read(ref canonicalDirectoryVerificationCount);

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
