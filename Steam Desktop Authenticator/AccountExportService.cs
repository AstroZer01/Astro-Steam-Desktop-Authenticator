using Newtonsoft.Json;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.Windows.Compatibility;

namespace Steam_Desktop_Authenticator
{
    public enum SecretQrFormat
    {
        KeePassXC,
        Bitwarden
    }

    public static class AccountExportService
    {
        private const int MaximumExportFilenameStemLength = 96;

        private static readonly HashSet<string> WindowsReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM\u00B9", "COM\u00B2", "COM\u00B3",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
        };

        private static readonly JsonSerializerSettings ExportJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        private sealed class ExportableSessionDto
        {
            [JsonProperty("SteamID")]
            public ulong SteamID { get; set; }

            [JsonProperty("AccessToken", NullValueHandling = NullValueHandling.Ignore)]
            public string AccessToken { get; set; }

            [JsonProperty("RefreshToken", NullValueHandling = NullValueHandling.Ignore)]
            public string RefreshToken { get; set; }

            [JsonProperty("SessionID", NullValueHandling = NullValueHandling.Ignore)]
            public string SessionID { get; set; }
        }

        private sealed class ExportableMaFileDto
        {
            [JsonProperty("shared_secret")]
            public string SharedSecret { get; set; }

            [JsonProperty("serial_number")]
            public string SerialNumber { get; set; }

            [JsonProperty("revocation_code")]
            public string RevocationCode { get; set; }

            [JsonProperty("uri")]
            public string URI { get; set; }

            [JsonProperty("server_time")]
            public long ServerTime { get; set; }

            [JsonProperty("account_name")]
            public string AccountName { get; set; }

            [JsonProperty("token_gid")]
            public string TokenGID { get; set; }

            [JsonProperty("identity_secret")]
            public string IdentitySecret { get; set; }

            [JsonProperty("secret_1")]
            public string Secret1 { get; set; }

            [JsonProperty("status")]
            public int Status { get; set; }

            [JsonProperty("device_id")]
            public string DeviceID { get; set; }

            [JsonProperty("phone_number_hint", NullValueHandling = NullValueHandling.Ignore)]
            public string PhoneNumberHint { get; set; }

            [JsonProperty("confirm_type", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int ConfirmType { get; set; }

            [JsonProperty("fully_enrolled")]
            public bool FullyEnrolled { get; set; }

            [JsonProperty("Session")]
            public ExportableSessionDto Session { get; set; }
        }

        private sealed class ExportManifestDto
        {
            [JsonProperty("encrypted")]
            public bool Encrypted { get; set; }

            [JsonProperty("first_run")]
            public bool FirstRun { get; set; } = false;

            [JsonProperty("entries")]
            public List<ExportManifestEntryDto> Entries { get; set; } = new List<ExportManifestEntryDto>();
        }

        private sealed class ExportManifestEntryDto
        {
            [JsonProperty("encryption_iv")]
            public string IV { get; set; }

            [JsonProperty("encryption_salt")]
            public string Salt { get; set; }

            [JsonProperty("filename")]
            public string Filename { get; set; }

            [JsonProperty("steamid")]
            public ulong SteamID { get; set; }
        }

        public static string SerializeAllowlistedMaFile(SteamGuardAccount account, bool includeSession)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            ulong steamId = account.Session?.SteamID ?? 0;
            ExportableMaFileDto dto = new ExportableMaFileDto
            {
                SharedSecret = account.SharedSecret,
                SerialNumber = account.SerialNumber,
                RevocationCode = account.RevocationCode,
                URI = account.URI,
                ServerTime = account.ServerTime,
                AccountName = account.AccountName,
                TokenGID = account.TokenGID,
                IdentitySecret = account.IdentitySecret,
                Secret1 = account.Secret1,
                Status = account.Status,
                DeviceID = account.DeviceID,
                PhoneNumberHint = account.PhoneNumberHint,
                ConfirmType = account.ConfirmType,
                FullyEnrolled = account.FullyEnrolled,
                Session = new ExportableSessionDto
                {
                    SteamID = steamId,
                    AccessToken = includeSession ? account.Session?.AccessToken : null,
                    RefreshToken = includeSession ? account.Session?.RefreshToken : null,
                    SessionID = includeSession ? account.Session?.SessionID : null
                }
            };

            return JsonConvert.SerializeObject(dto, ExportJsonSettings);
        }

        public static void ValidateExportDestination(string destinationPath)
        {
            if (String.IsNullOrWhiteSpace(destinationPath))
                throw new InvalidDataException("Choose a valid export destination path.");

            string trimmedInput = destinationPath.Trim();
            string normalizedInput = trimmedInput.Replace('/', '\\');
            if (HasDisallowedWindowsDevicePrefix(normalizedInput))
            {
                throw new InvalidDataException("Device and extended-length Windows path prefixes are not allowed for export destinations.");
            }

            string fullDestination = Path.GetFullPath(trimmedInput);
            if (HasDisallowedWindowsDevicePrefix(fullDestination))
            {
                throw new InvalidDataException("Device and extended-length Windows path prefixes are not allowed for export destinations.");
            }
            string liveMaDir = Path.GetFullPath(Path.Combine(Manifest.GetExecutableDir(), "maFiles"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string liveMaDirWithSep = liveMaDir + Path.DirectorySeparatorChar;

            if (String.Equals(fullDestination, liveMaDir, StringComparison.OrdinalIgnoreCase) ||
                fullDestination.StartsWith(liveMaDirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
            }

            WindowsPathIdentity liveMaIdentity = default;
            bool hasLiveMaIdentity = Directory.Exists(liveMaDir) &&
                TryGetWindowsPathIdentity(liveMaDir, isDirectory: true, out liveMaIdentity);

            if (File.Exists(fullDestination))
            {
                FileInfo destFile = new FileInfo(fullDestination);
                if ((destFile.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected export destination file uses an unsupported symbolic link or reparse point.");
            }
            else if (Directory.Exists(fullDestination))
            {
                DirectoryInfo destDir = new DirectoryInfo(fullDestination);
                if ((destDir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected export destination uses an unsupported symbolic link or junction.");
                if (hasLiveMaIdentity &&
                    TryGetWindowsPathIdentity(fullDestination, isDirectory: true, out WindowsPathIdentity destIdentity) &&
                    destIdentity.Equals(liveMaIdentity))
                {
                    throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
                }
            }

            string parentDir = Path.GetDirectoryName(fullDestination);
            if (String.IsNullOrWhiteSpace(parentDir))
                throw new InvalidDataException("The selected export destination directory is invalid.");

            DirectoryInfo current = new DirectoryInfo(parentDir);
            while (current != null)
            {
                if (current.Exists)
                {
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The selected export path traverses an unsupported symbolic link or junction.");

                    if (hasLiveMaIdentity &&
                        TryGetWindowsPathIdentity(current.FullName, isDirectory: true, out WindowsPathIdentity ancestorIdentity) &&
                        ancestorIdentity.Equals(liveMaIdentity))
                    {
                        throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
                    }
                }

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        private static bool HasDisallowedWindowsDevicePrefix(string path)
        {
            return !String.IsNullOrEmpty(path) &&
                (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                 path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                 path.StartsWith(@"\??\", StringComparison.Ordinal));
        }

        internal sealed class PinnedExportDirectory : IDisposable
        {
            private readonly List<SafeFileHandle> pinnedHandles;
            private readonly SafeFileHandle leafHandle;

            public string VerifiedDirectoryPath { get; }

            public PinnedExportDirectory(string verifiedDirectoryPath, List<SafeFileHandle> pinnedHandles, SafeFileHandle leafHandle)
            {
                VerifiedDirectoryPath = verifiedDirectoryPath;
                this.pinnedHandles = pinnedHandles ?? new List<SafeFileHandle>();
                this.leafHandle = leafHandle;
            }

            public void VerifyStillAtExpectedPath()
            {
                if (leafHandle == null || leafHandle.IsInvalid)
                    return;

                string currentFinalPath = GetNormalizedFinalPath(leafHandle);
                if (!String.Equals(currentFinalPath, VerifiedDirectoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The export destination directory changed during export.");
                }
            }

            public void Dispose()
            {
                for (int i = pinnedHandles.Count - 1; i >= 0; i--)
                {
                    try { pinnedHandles[i]?.Dispose(); } catch { }
                }
                pinnedHandles.Clear();
            }
        }

        internal static PinnedExportDirectory OpenAndPinDestinationDirectory(string destinationFilePath)
        {
            string fullDestination = Path.GetFullPath(destinationFilePath);
            string parentDir = Path.GetDirectoryName(fullDestination);
            if (String.IsNullOrWhiteSpace(parentDir))
                throw new InvalidDataException("The selected export destination directory is invalid.");

            Directory.CreateDirectory(parentDir);
            ValidateExportDestination(fullDestination);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new PinnedExportDirectory(parentDir, null, null);

            string liveMaDir = Path.GetFullPath(Path.Combine(Manifest.GetExecutableDir(), "maFiles"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            WindowsPathIdentity liveMaIdentity = default;
            bool hasLiveMaIdentity = Directory.Exists(liveMaDir) &&
                TryGetWindowsPathIdentity(liveMaDir, isDirectory: true, out liveMaIdentity);
            string canonicalLiveMaDir = Directory.Exists(liveMaDir) &&
                TryGetNormalizedFinalPathForPath(liveMaDir, isDirectory: true, out string resolvedLiveMaDir)
                ? resolvedLiveMaDir
                : liveMaDir;
            string canonicalLiveMaDirWithSep = canonicalLiveMaDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Collect every directory from the first non-root ancestor down to parentDir
            // so we verify and pin the entire path chain against rename/delete/redirect.
            List<string> chain = new List<string>();
            DirectoryInfo cursor = new DirectoryInfo(parentDir);
            while (cursor != null)
            {
                DirectoryInfo parent = cursor.Parent;
                if (parent == null || String.Equals(parent.FullName, cursor.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                chain.Add(cursor.FullName);
                cursor = parent;
            }
            chain.Reverse();

            List<SafeFileHandle> handles = new List<SafeFileHandle>();
            try
            {
                SafeFileHandle leafHandle = null;
                for (int i = 0; i < chain.Count; i++)
                {
                    string dirPath = chain[i];
                    bool isLeaf = i == chain.Count - 1;

                    SafeFileHandle handle = CreateFileW(
                        dirPath,
                        FileListDirectory,
                        FileShareReadWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                        IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        throw new IOException("Could not open and pin the export destination directory path.");
                    }

                    handles.Add(handle);

                    if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION dirInfo))
                        throw new IOException("Could not verify the export destination directory identity.");
                    if ((dirInfo.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The selected export destination directory uses an unsupported symbolic link or junction.");

                    if (hasLiveMaIdentity)
                    {
                        WindowsPathIdentity openDirIdentity = new WindowsPathIdentity(dirInfo.VolumeSerialNumber, dirInfo.FileIndexHigh, dirInfo.FileIndexLow);
                        if (openDirIdentity.Equals(liveMaIdentity))
                            throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
                    }

                    if (isLeaf)
                        leafHandle = handle;
                }

                if (leafHandle == null)
                {
                    // Destination is directly under a drive root (e.g. C:\export.maFile); open root handle to verify.
                    leafHandle = CreateFileW(
                        parentDir,
                        FileListDirectory,
                        FileShareReadWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                        IntPtr.Zero);
                    if (leafHandle.IsInvalid)
                    {
                        leafHandle.Dispose();
                        throw new IOException("Could not open and verify the export destination directory.");
                    }
                    handles.Add(leafHandle);
                }

                string verifiedFinalPath = GetNormalizedFinalPath(leafHandle);
                if (String.Equals(verifiedFinalPath, canonicalLiveMaDir, StringComparison.OrdinalIgnoreCase) ||
                    verifiedFinalPath.StartsWith(canonicalLiveMaDirWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
                }

                return new PinnedExportDirectory(verifiedFinalPath, handles, leafHandle);
            }
            catch
            {
                for (int i = handles.Count - 1; i >= 0; i--)
                {
                    try { handles[i]?.Dispose(); } catch { }
                }
                throw;
            }
        }

        private static void EnsureDestinationFileIsNotReparsePoint(string fullDestinationPath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists(fullDestinationPath))
                return;

            using (SafeFileHandle handle = CreateFileW(
                fullDestinationPath,
                0,
                FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return;

                if (GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info) &&
                    (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The selected export destination file uses an unsupported symbolic link or reparse point.");
                }
            }
        }

        private static string GetNormalizedFinalPath(SafeFileHandle handle)
        {
            const uint FileNameNormalized = 0x0;
            uint requiredChars = GetFinalPathNameByHandleW(handle, null, 0, FileNameNormalized);
            if (requiredChars == 0)
                throw new IOException("Could not resolve the verified export destination path.");

            StringBuilder sb = new StringBuilder((int)requiredChars + 1);
            uint written = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, FileNameNormalized);
            if (written == 0 || written >= sb.Capacity)
                throw new IOException("Could not resolve the verified export destination path.");

            string finalPath = sb.ToString();
            if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                finalPath = @"\\" + finalPath.Substring(8);
            else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                finalPath = finalPath.Substring(4);

            return finalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool TryGetNormalizedFinalPathForPath(string path, bool isDirectory, out string normalizedPath)
        {
            normalizedPath = null;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            using (SafeFileHandle handle = CreateFileW(
                path,
                0,
                FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                isDirectory ? FileFlagBackupSemantics : 0u,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return false;
                try
                {
                    normalizedPath = GetNormalizedFinalPath(handle);
                    return !String.IsNullOrEmpty(normalizedPath);
                }
                catch
                {
                    return false;
                }
            }
        }

        private readonly struct WindowsPathIdentity : IEquatable<WindowsPathIdentity>
        {
            public readonly uint VolumeSerialNumber;
            public readonly uint FileIndexHigh;
            public readonly uint FileIndexLow;

            public WindowsPathIdentity(uint volumeSerialNumber, uint fileIndexHigh, uint fileIndexLow)
            {
                VolumeSerialNumber = volumeSerialNumber;
                FileIndexHigh = fileIndexHigh;
                FileIndexLow = fileIndexLow;
            }

            public bool Equals(WindowsPathIdentity other)
            {
                return VolumeSerialNumber == other.VolumeSerialNumber &&
                    FileIndexHigh == other.FileIndexHigh &&
                    FileIndexLow == other.FileIndexLow;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
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

        private const uint FileListDirectory = 0x00000001;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
        private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint OpenExisting = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle hFile,
            StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);

        private static bool TryGetWindowsPathIdentity(string path, bool isDirectory, out WindowsPathIdentity identity)
        {
            identity = default;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            using (SafeFileHandle handle = CreateFileW(
                path,
                0,
                FileShareAll,
                IntPtr.Zero,
                OpenExisting,
                isDirectory ? FileFlagBackupSemantics : 0u,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return false;

                if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                    return false;

                identity = new WindowsPathIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
                return true;
            }
        }

        public static string SanitizeExportFilename(
            string rawName,
            ulong steamId,
            ISet<string> usedNamesCaseInsensitive = null,
            string extension = ".maFile")
        {
            string safeExtension = String.IsNullOrWhiteSpace(extension) ? ".maFile" : extension;
            if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
                safeExtension = "." + safeExtension;

            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(rawName))
            {
                foreach (char ch in rawName)
                {
                    if (Char.IsControl(ch) || Array.IndexOf(invalidChars, ch) >= 0 || ch == '/' || ch == '\\')
                        continue;
                    sb.Append(ch);
                }
            }

            string suffix = steamId != 0 ? "_" + steamId.ToString(CultureInfo.InvariantCulture) : "_account";
            int maxBaseLength = Math.Max(8, MaximumExportFilenameStemLength - suffix.Length - 8);

            string stem = sb.ToString().Trim().TrimEnd('.', ' ');
            if (stem.Length > maxBaseLength)
                stem = stem.Substring(0, maxBaseLength).TrimEnd('.', ' ');
            if (String.IsNullOrEmpty(stem))
                stem = steamId != 0 ? steamId.ToString(CultureInfo.InvariantCulture) : "account";

            // Check stem before first '.' against ALL Windows reserved device names (including COM¹-COM³, LPT¹-LPT³)
            // AFTER truncation/trimming so long inputs like "CON" + spaces + "x" cannot truncate back to "CON".
            int firstDotIndex = stem.IndexOf('.');
            string deviceCandidate = firstDotIndex >= 0 ? stem.Substring(0, firstDotIndex) : stem;
            deviceCandidate = deviceCandidate.TrimEnd(' ');
            if (WindowsReservedDeviceNames.Contains(deviceCandidate))
            {
                stem = "_" + stem;
                if (stem.Length > maxBaseLength)
                    stem = stem.Substring(0, maxBaseLength).TrimEnd('.', ' ');
            }

            string candidate = stem + safeExtension;
            if (usedNamesCaseInsensitive == null)
                return candidate;

            if (usedNamesCaseInsensitive.Add(candidate))
                return candidate;

            candidate = stem + suffix + safeExtension;
            if (usedNamesCaseInsensitive.Add(candidate))
                return candidate;

            for (int i = 2; i < 10000; i++)
            {
                candidate = stem + suffix + "_" + i.ToString(CultureInfo.InvariantCulture) + safeExtension;
                if (usedNamesCaseInsensitive.Add(candidate))
                    return candidate;
            }

            candidate = Guid.NewGuid().ToString("N") + safeExtension;
            usedNamesCaseInsensitive.Add(candidate);
            return candidate;
        }

        private static void WriteAtomicallyIntoPinnedDirectory(
            PinnedExportDirectory pinnedDir,
            string destinationFileName,
            Action<FileStream> writeContent)
        {
            if (String.IsNullOrWhiteSpace(destinationFileName) ||
                destinationFileName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new InvalidDataException("The selected export filename is invalid.");
            }

            string verifiedDestination = Path.Combine(pinnedDir.VerifiedDirectoryPath, destinationFileName);
            EnsureDestinationFileIsNotReparsePoint(verifiedDestination);

            string tempPath = Path.Combine(
                pinnedDir.VerifiedDirectoryPath,
                destinationFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    writeContent(fs);
                    fs.Flush(flushToDisk: true);
                }

                pinnedDir.VerifyStillAtExpectedPath();
                EnsureDestinationFileIsNotReparsePoint(verifiedDestination);

                if (File.Exists(verifiedDestination))
                    File.Replace(tempPath, verifiedDestination, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, verifiedDestination);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        public static void ExportPlainMaFile(SteamGuardAccount account, string destinationFilePath, bool includeSession)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            ValidateExportDestination(destinationFilePath);
            string fullPath = Path.GetFullPath(destinationFilePath);
            string fileName = Path.GetFileName(fullPath);
            using (PinnedExportDirectory pinnedDir = OpenAndPinDestinationDirectory(fullPath))
            {
                string json = SerializeAllowlistedMaFile(account, includeSession);
                byte[] utf8Bytes = new UTF8Encoding(false).GetBytes(json);
                WriteAtomicallyIntoPinnedDirectory(pinnedDir, fileName, fs => fs.Write(utf8Bytes, 0, utf8Bytes.Length));
            }
        }

        public static void ExportEncryptedZip(
            IReadOnlyList<SteamGuardAccount> accounts,
            string destinationZipPath,
            string exportPassKey,
            bool includeSession)
        {
            if (accounts == null || accounts.Count == 0)
                throw new ArgumentException("At least one account is required for export.", nameof(accounts));
            if (String.IsNullOrEmpty(exportPassKey))
                throw new ArgumentException("An encryption passkey is required for encrypted backup export.", nameof(exportPassKey));

            ValidateExportDestination(destinationZipPath);
            ExportZipInternal(accounts, Path.GetFullPath(destinationZipPath), encrypted: true, exportPassKey: exportPassKey, includeSession: includeSession);
        }

        public static void ExportPlainZip(
            IReadOnlyList<SteamGuardAccount> accounts,
            string destinationZipPath,
            bool includeSession)
        {
            if (accounts == null || accounts.Count == 0)
                throw new ArgumentException("At least one account is required for export.", nameof(accounts));

            ValidateExportDestination(destinationZipPath);
            ExportZipInternal(accounts, Path.GetFullPath(destinationZipPath), encrypted: false, exportPassKey: null, includeSession: includeSession);
        }

        private static void ExportZipInternal(
            IReadOnlyList<SteamGuardAccount> accounts,
            string fullDestinationZipPath,
            bool encrypted,
            string exportPassKey,
            bool includeSession)
        {
            string fileName = Path.GetFileName(fullDestinationZipPath);
            using (PinnedExportDirectory pinnedDir = OpenAndPinDestinationDirectory(fullDestinationZipPath))
            {
                WriteAtomicallyIntoPinnedDirectory(pinnedDir, fileName, fs =>
                {
                    ExportManifestDto exportManifest = new ExportManifestDto
                    {
                        Encrypted = encrypted,
                        FirstRun = false
                    };

                    HashSet<string> usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    UTF8Encoding utf8NoBom = new UTF8Encoding(false);

                    using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (SteamGuardAccount account in accounts)
                        {
                            if (account == null)
                                continue;

                            ulong steamId = account.Session?.SteamID ?? 0;
                            string baseName = steamId != 0
                                ? steamId.ToString(CultureInfo.InvariantCulture)
                                : account.AccountName;
                            string entryFilename = SanitizeExportFilename(baseName, steamId, usedEntryNames, ".maFile");

                            string maFileContent = SerializeAllowlistedMaFile(account, includeSession);
                            string salt = null;
                            string iv = null;
                            if (encrypted)
                            {
                                salt = FileEncryptor.GetRandomSalt();
                                iv = FileEncryptor.GetInitializationVector();
                                maFileContent = FileEncryptor.EncryptData(exportPassKey, salt, iv, maFileContent);
                                if (maFileContent == null)
                                    throw new InvalidOperationException("Could not encrypt account for ZIP backup.");
                            }

                            ZipArchiveEntry entry = archive.CreateEntry(entryFilename, CompressionLevel.Optimal);
                            using (Stream entryStream = entry.Open())
                            using (StreamWriter writer = new StreamWriter(entryStream, utf8NoBom))
                            {
                                writer.Write(maFileContent);
                            }

                            exportManifest.Entries.Add(new ExportManifestEntryDto
                            {
                                SteamID = steamId,
                                Filename = entryFilename,
                                Salt = salt,
                                IV = iv
                            });
                        }

                        ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                        using (Stream manifestStream = manifestEntry.Open())
                        using (StreamWriter manifestWriter = new StreamWriter(manifestStream, utf8NoBom))
                        {
                            manifestWriter.Write(JsonConvert.SerializeObject(exportManifest, ExportJsonSettings));
                        }
                    }
                });
            }
        }
    }

    public static class SecretQrBuilder
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string EncodeBase32Unpadded(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Secret bytes are required.", nameof(data));

            StringBuilder output = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = data[0];
            int next = 1;
            int bitsLeft = 8;

            while (bitsLeft > 0 || next < data.Length)
            {
                if (bitsLeft < 5)
                {
                    if (next < data.Length)
                    {
                        buffer <<= 8;
                        buffer |= data[next++] & 0xFF;
                        bitsLeft += 8;
                    }
                    else
                    {
                        int pad = 5 - bitsLeft;
                        buffer <<= pad;
                        bitsLeft += pad;
                    }
                }

                int index = (buffer >> (bitsLeft - 5)) & 0x1F;
                bitsLeft -= 5;
                output.Append(Base32Alphabet[index]);
            }

            return output.ToString();
        }

        public static string BuildSecretUri(SteamGuardAccount account, SecretQrFormat format)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));
            if (String.IsNullOrWhiteSpace(account.SharedSecret))
                throw new InvalidDataException("The selected account does not contain a shared secret.");

            byte[] secretBytes;
            try
            {
                secretBytes = Convert.FromBase64String(Regex.Unescape(account.SharedSecret));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The selected account's shared secret is not valid base64.", ex);
            }

            if (secretBytes.Length == 0 || secretBytes.Length > 64)
                throw new InvalidDataException("The selected account's shared secret length is invalid.");

            string base32Secret = EncodeBase32Unpadded(secretBytes);
            switch (format)
            {
                case SecretQrFormat.KeePassXC:
                    string accountLabel = String.IsNullOrWhiteSpace(account.AccountName)
                        ? (account.Session?.SteamID.ToString(CultureInfo.InvariantCulture) ?? "Account")
                        : account.AccountName.Trim();
                    string encodedLabel = Uri.EscapeDataString(accountLabel);
                    return "otpauth://totp/Steam:" + encodedLabel +
                        "?secret=" + base32Secret +
                        "&issuer=Steam&period=30&digits=5&encoder=steam";

                case SecretQrFormat.Bitwarden:
                    return "steam://" + base32Secret;

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), "Unsupported secret QR format.");
            }
        }

        public static Bitmap RenderQrBitmap(string payload, int sizePixels = 280)
        {
            if (String.IsNullOrWhiteSpace(payload))
                throw new ArgumentException("QR payload is required.", nameof(payload));

            int clampedSize = Math.Clamp(sizePixels, 128, 1024);
            BarcodeWriter<Bitmap> writer = new BarcodeWriter<Bitmap>
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new EncodingOptions
                {
                    Height = clampedSize,
                    Width = clampedSize,
                    Margin = 2
                },
                Renderer = new BitmapRenderer()
            };
            return writer.Write(payload);
        }
    }
}
