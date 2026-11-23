using Newtonsoft.Json;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

            string fullDestination = Path.GetFullPath(destinationPath);
            string liveMaDir = Path.GetFullPath(Path.Combine(Manifest.GetExecutableDir(), "maFiles"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string liveMaDirWithSep = liveMaDir + Path.DirectorySeparatorChar;

            if (String.Equals(fullDestination, liveMaDir, StringComparison.OrdinalIgnoreCase) ||
                fullDestination.StartsWith(liveMaDirWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Exporting into the live maFiles folder is not allowed. Choose a separate folder.");
            }

            if (File.Exists(fullDestination))
            {
                FileInfo destFile = new FileInfo(fullDestination);
                if ((destFile.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected export destination file uses an unsupported symbolic link or reparse point.");
            }

            string parentDir = Path.GetDirectoryName(fullDestination);
            if (String.IsNullOrWhiteSpace(parentDir))
                throw new InvalidDataException("The selected export destination directory is invalid.");

            DirectoryInfo current = new DirectoryInfo(parentDir);
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected export path traverses an unsupported symbolic link or junction.");

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
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

            string stem = sb.ToString().Trim().TrimEnd('.', ' ');
            if (String.IsNullOrEmpty(stem))
                stem = steamId != 0 ? steamId.ToString(CultureInfo.InvariantCulture) : "account";

            // Check stem before first '.' against ALL Windows reserved device names (including COM¹-COM³, LPT¹-LPT³).
            int firstDotIndex = stem.IndexOf('.');
            string deviceCandidate = firstDotIndex >= 0 ? stem.Substring(0, firstDotIndex) : stem;
            deviceCandidate = deviceCandidate.TrimEnd(' ');
            if (WindowsReservedDeviceNames.Contains(deviceCandidate))
            {
                stem = "_" + stem;
            }

            string suffix = steamId != 0 ? "_" + steamId.ToString(CultureInfo.InvariantCulture) : "_account";
            int maxBaseLength = Math.Max(8, MaximumExportFilenameStemLength - suffix.Length - 8);
            if (stem.Length > maxBaseLength)
                stem = stem.Substring(0, maxBaseLength).TrimEnd('.', ' ');
            if (String.IsNullOrEmpty(stem))
                stem = steamId != 0 ? steamId.ToString(CultureInfo.InvariantCulture) : "account";

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

        public static void ExportPlainMaFile(SteamGuardAccount account, string destinationFilePath, bool includeSession)
        {
            if (account == null)
                throw new ArgumentNullException(nameof(account));

            ValidateExportDestination(destinationFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFilePath)));
            string json = SerializeAllowlistedMaFile(account, includeSession);
            PendingTransferMarker.WriteAllTextAtomicallyFlushed(Path.GetFullPath(destinationFilePath), json);
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
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationZipPath));
            string tempZipPath = fullDestinationZipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ExportManifestDto exportManifest = new ExportManifestDto
                {
                    Encrypted = encrypted,
                    FirstRun = false
                };

                HashSet<string> usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                UTF8Encoding utf8NoBom = new UTF8Encoding(false);

                using (FileStream fs = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
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

                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(fullDestinationZipPath))
                    File.Replace(tempZipPath, fullDestinationZipPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempZipPath, fullDestinationZipPath);
            }
            catch
            {
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
                throw;
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
