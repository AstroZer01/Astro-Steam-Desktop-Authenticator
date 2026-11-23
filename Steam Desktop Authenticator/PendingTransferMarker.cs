using Newtonsoft.Json;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Steam_Desktop_Authenticator
{
    public sealed class PendingTransferMarkerRecord
    {
        [JsonProperty("steam_id")]
        public ulong SteamId { get; set; }

        [JsonProperty("account_name")]
        public string AccountName { get; set; }

        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("dispatched_at_utc")]
        public DateTime DispatchedAtUtc { get; set; }

        [JsonProperty("transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
        public string TransferAttemptId { get; set; }

        [JsonIgnore]
        public string FilePath { get; set; }
    }

    public sealed class StagedTransferRecoveryRecord
    {
        public string FilePath { get; set; }
        public ulong SteamId { get; set; }
        public DateTime StagedAtUtc { get; set; }
        public string TransferAttemptId { get; set; }
        public string PreviousCommittedTransferAttemptId { get; set; }
        public SteamGuardAccount Account { get; set; }
        public bool RequiresPasskey { get; set; }
    }

    public static class PendingTransferMarker
    {
        private const string MarkerPrefix = ".asda-pending-transfer.";
        private const string MarkerSuffix = ".json";
        private const string RecoveryFilePrefix = "transfer-";
        private const string RecoveryFileSuffix = ".asda-recovery";
        private const int RecoveryEnvelopeVersion = 1;
        private const int RecoveryKdfIterations = 600_000;
        private const int MaximumRecoveryFileBytes = 2 * 1024 * 1024;

        private static readonly Regex MarkerFilenameRegex = new Regex(
            @"^\.asda-pending-transfer\.(\d+)\.json$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly JsonSerializerSettings MarkerJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 16,
            DateParseHandling = DateParseHandling.None
        };

        private sealed class ProtectedRecoveryEnvelope
        {
            [JsonProperty("version")]
            public int Version { get; set; }

            [JsonProperty("mode")]
            public string Mode { get; set; }

            [JsonProperty("data_directory")]
            public string CanonicalDataDirectory { get; set; }

            [JsonProperty("steam_id")]
            public ulong SteamId { get; set; }

            [JsonProperty("staged_at_utc")]
            public DateTime StagedAtUtc { get; set; }

            [JsonProperty("transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
            public string TransferAttemptId { get; set; }

            [JsonProperty("previous_committed_transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
            public string PreviousCommittedTransferAttemptId { get; set; }

            [JsonProperty("salt", NullValueHandling = NullValueHandling.Ignore)]
            public string Salt { get; set; }

            [JsonProperty("nonce", NullValueHandling = NullValueHandling.Ignore)]
            public string Nonce { get; set; }

            [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
            public string Tag { get; set; }

            [JsonProperty("payload")]
            public string Payload { get; set; }
        }

        private sealed class ProtectedRecoveryPayload
        {
            [JsonProperty("data_directory")]
            public string CanonicalDataDirectory { get; set; }

            [JsonProperty("steam_id")]
            public ulong SteamId { get; set; }

            [JsonProperty("transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
            public string TransferAttemptId { get; set; }

            [JsonProperty("previous_committed_transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
            public string PreviousCommittedTransferAttemptId { get; set; }

            [JsonProperty("account")]
            public SteamGuardAccount Account { get; set; }
        }

        public static string GetCanonicalDataDirectory()
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(ApplicationPaths.DataDirectory));
        }

        public static string GetMaFilesDirectory()
        {
            return Path.Combine(GetCanonicalDataDirectory(), "maFiles");
        }

        public static string GetMarkerPath(ulong steamId)
        {
            return Path.Combine(GetMaFilesDirectory(), MarkerPrefix + steamId.ToString(CultureInfo.InvariantCulture) + MarkerSuffix);
        }

        public static string GetRecoveryDirectoryForCurrentDataDirectory()
        {
            string canonical = GetCanonicalDataDirectory();
            string hashHex = ComputeSha256Hex(canonical.ToLowerInvariant());
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Astro Steam Desktop Authenticator",
                "Recovery",
                hashHex);
        }

        public static bool HasMarker(ulong steamId)
        {
            if (steamId == 0)
                return false;

            return File.Exists(GetMarkerPath(steamId));
        }

        public static bool TryWriteMarker(ulong steamId, string accountName, string deviceId, string transferAttemptId = null)
        {
            if (steamId == 0)
                return false;

            try
            {
                string maDir = GetMaFilesDirectory();
                Directory.CreateDirectory(maDir);
                string markerPath = GetMarkerPath(steamId);
                PendingTransferMarkerRecord record = new PendingTransferMarkerRecord
                {
                    SteamId = steamId,
                    AccountName = accountName ?? String.Empty,
                    DeviceId = deviceId ?? String.Empty,
                    DispatchedAtUtc = DateTime.UtcNow,
                    TransferAttemptId = String.IsNullOrWhiteSpace(transferAttemptId) ? Guid.NewGuid().ToString("N") : transferAttemptId
                };

                string json = JsonConvert.SerializeObject(record, MarkerJsonSettings);
                WriteAllTextAtomicallyFlushed(markerPath, json);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "Could not write the pre-dispatch pending transfer marker.");
                return false;
            }
        }

        public static void DeleteMarkerBestEffort(ulong steamId)
        {
            if (steamId == 0)
                return;

            DeleteFileBestEffort(GetMarkerPath(steamId));
        }

        public static List<PendingTransferMarkerRecord> EnumerateMarkers()
        {
            List<PendingTransferMarkerRecord> markers = new List<PendingTransferMarkerRecord>();
            string maDir = GetMaFilesDirectory();
            if (!Directory.Exists(maDir))
                return markers;

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(maDir, MarkerPrefix + "*" + MarkerSuffix, SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(filePath);
                    Match match = MarkerFilenameRegex.Match(fileName);
                    if (!match.Success || !ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamIdFromName) || steamIdFromName == 0)
                        continue;

                    try
                    {
                        string content = File.ReadAllText(filePath, new UTF8Encoding(false, true));
                        PendingTransferMarkerRecord record = JsonConvert.DeserializeObject<PendingTransferMarkerRecord>(content, MarkerJsonSettings);
                        if (record == null)
                            record = new PendingTransferMarkerRecord { SteamId = steamIdFromName };
                        if (record.SteamId == 0)
                            record.SteamId = steamIdFromName;
                        record.FilePath = filePath;
                        markers.Add(record);
                    }
                    catch
                    {
                        markers.Add(new PendingTransferMarkerRecord
                        {
                            SteamId = steamIdFromName,
                            FilePath = filePath
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "Could not enumerate pending transfer markers.");
            }

            return markers;
        }

        public static string TryStageProtectedReplacement(
            SteamGuardAccount account,
            bool manifestEncrypted,
            string passKey,
            out bool stagingFailed,
            string transferAttemptId = null,
            string previousCommittedTransferAttemptId = null)
        {
            stagingFailed = false;
            if (account?.Session == null || account.Session.SteamID == 0)
            {
                stagingFailed = true;
                return null;
            }

            try
            {
                string canonicalDataDir = GetCanonicalDataDirectory();
                string recoveryDir = GetRecoveryDirectoryForCurrentDataDirectory();
                Directory.CreateDirectory(recoveryDir);

                string effectiveAttemptId = String.IsNullOrWhiteSpace(transferAttemptId) ? Guid.NewGuid().ToString("N") : transferAttemptId;
                string normalizedPreviousAttemptId = String.IsNullOrWhiteSpace(previousCommittedTransferAttemptId) ? null : previousCommittedTransferAttemptId;
                string fileName = RecoveryFilePrefix +
                    account.Session.SteamID.ToString(CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N") +
                    RecoveryFileSuffix;
                string fullPath = Path.Combine(recoveryDir, fileName);

                ProtectedRecoveryPayload innerPayload = new ProtectedRecoveryPayload
                {
                    CanonicalDataDirectory = canonicalDataDir,
                    SteamId = account.Session.SteamID,
                    TransferAttemptId = effectiveAttemptId,
                    PreviousCommittedTransferAttemptId = normalizedPreviousAttemptId,
                    Account = account
                };
                byte[] plaintextBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(innerPayload, MarkerJsonSettings));

                ProtectedRecoveryEnvelope envelope;
                byte[] keyBytes = null;
                byte[] passwordBytes = null;
                try
                {
                    if (manifestEncrypted && !String.IsNullOrEmpty(passKey) && AesGcm.IsSupported)
                    {
                        byte[] salt = RandomNumberGenerator.GetBytes(16);
                        byte[] nonce = RandomNumberGenerator.GetBytes(12);
                        byte[] tag = new byte[16];
                        byte[] ciphertext = new byte[plaintextBytes.Length];
                        string saltB64 = Convert.ToBase64String(salt);
                        byte[] aad = BuildRecoveryAad(canonicalDataDir, account.Session.SteamID, saltB64);

                        passwordBytes = Encoding.UTF8.GetBytes(passKey);
                        keyBytes = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, RecoveryKdfIterations, HashAlgorithmName.SHA256, 32);
                        using (AesGcm aesGcm = new AesGcm(keyBytes, 16))
                        {
                            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);
                        }

                        byte[] dpapiWrapped = ProtectedData.Protect(ciphertext, aad, DataProtectionScope.CurrentUser);
                        envelope = new ProtectedRecoveryEnvelope
                        {
                            Version = RecoveryEnvelopeVersion,
                            Mode = "aesgcm-dpapi",
                            CanonicalDataDirectory = canonicalDataDir,
                            SteamId = account.Session.SteamID,
                            StagedAtUtc = DateTime.UtcNow,
                            TransferAttemptId = effectiveAttemptId,
                            PreviousCommittedTransferAttemptId = normalizedPreviousAttemptId,
                            Salt = saltB64,
                            Nonce = Convert.ToBase64String(nonce),
                            Tag = Convert.ToBase64String(tag),
                            Payload = Convert.ToBase64String(dpapiWrapped)
                        };
                    }
                    else
                    {
                        byte[] aad = BuildRecoveryAad(canonicalDataDir, account.Session.SteamID, "dpapi");
                        byte[] dpapiBytes = ProtectedData.Protect(plaintextBytes, aad, DataProtectionScope.CurrentUser);
                        envelope = new ProtectedRecoveryEnvelope
                        {
                            Version = RecoveryEnvelopeVersion,
                            Mode = "dpapi",
                            CanonicalDataDirectory = canonicalDataDir,
                            SteamId = account.Session.SteamID,
                            StagedAtUtc = DateTime.UtcNow,
                            TransferAttemptId = effectiveAttemptId,
                            PreviousCommittedTransferAttemptId = normalizedPreviousAttemptId,
                            Payload = Convert.ToBase64String(dpapiBytes)
                        };
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintextBytes);
                    if (keyBytes != null) CryptographicOperations.ZeroMemory(keyBytes);
                    if (passwordBytes != null) CryptographicOperations.ZeroMemory(passwordBytes);
                }

                string envelopeJson = JsonConvert.SerializeObject(envelope, MarkerJsonSettings);
                WriteAllTextAtomicallyFlushed(fullPath, envelopeJson);
                return fullPath;
            }
            catch (Exception ex)
            {
                stagingFailed = true;
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "Could not stage protected transfer recovery file in LocalAppData.");
                return null;
            }
        }

        public static void DeleteStagedRecoveryBestEffort(string stagedFilePath)
        {
            if (String.IsNullOrWhiteSpace(stagedFilePath))
                return;

            DeleteFileBestEffort(stagedFilePath);
        }

        public static void DeleteAllStagedRecoveriesForSteamIdBestEffort(ulong steamId)
        {
            if (steamId == 0)
                return;

            string recoveryDir = GetRecoveryDirectoryForCurrentDataDirectory();
            if (!Directory.Exists(recoveryDir))
                return;

            string prefix = RecoveryFilePrefix + steamId.ToString(CultureInfo.InvariantCulture) + "-";
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(recoveryDir, prefix + "*" + RecoveryFileSuffix, SearchOption.TopDirectoryOnly))
                {
                    DeleteFileBestEffort(filePath);
                }
            }
            catch
            {
            }
        }

        public static StagedTransferRecoveryRecord TryLoadStagedRecoveryWithPasskey(string filePath, string passKey)
        {
            if (String.IsNullOrWhiteSpace(filePath) || String.IsNullOrEmpty(passKey))
                return null;

            return TryReadStagedRecoveryFile(filePath, GetCanonicalDataDirectory(), passKey);
        }

        public static List<StagedTransferRecoveryRecord> ReconcileAndLoadStagedRecoveries(
            IReadOnlyList<SteamGuardAccount> committedAccounts,
            string passKey,
            Manifest manifest = null)
        {
            List<StagedTransferRecoveryRecord> pendingRecoveries = new List<StagedTransferRecoveryRecord>();
            string canonicalDataDir = GetCanonicalDataDirectory();
            string recoveryDir = GetRecoveryDirectoryForCurrentDataDirectory();
            if (!Directory.Exists(recoveryDir))
                return pendingRecoveries;

            Dictionary<ulong, SteamGuardAccount> committedBySteamId = new Dictionary<ulong, SteamGuardAccount>();
            if (committedAccounts != null)
            {
                foreach (SteamGuardAccount acc in committedAccounts)
                {
                    if (acc?.Session != null && acc.Session.SteamID != 0)
                        committedBySteamId[acc.Session.SteamID] = acc;
                }
            }

            Dictionary<ulong, string> committedAttemptIds = new Dictionary<ulong, string>();
            if (manifest?.Entries != null)
            {
                foreach (Manifest.ManifestEntry entry in manifest.Entries)
                {
                    if (entry != null && entry.SteamID != 0 && !String.IsNullOrWhiteSpace(entry.LastTransferAttemptId))
                        committedAttemptIds[entry.SteamID] = entry.LastTransferAttemptId;
                }
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(recoveryDir, RecoveryFilePrefix + "*" + RecoveryFileSuffix, SearchOption.TopDirectoryOnly))
                {
                    StagedTransferRecoveryRecord loaded = TryReadStagedRecoveryFile(filePath, canonicalDataDir, passKey);
                    if (loaded == null)
                        continue;

                    committedAttemptIds.TryGetValue(loaded.SteamId, out string committedAttemptId);

                    // 1. This exact transfer attempt already committed -> silently clean up redundant staged copy & marker.
                    if (!String.IsNullOrWhiteSpace(loaded.TransferAttemptId) &&
                        !String.IsNullOrWhiteSpace(committedAttemptId) &&
                        String.Equals(committedAttemptId, loaded.TransferAttemptId, StringComparison.Ordinal))
                    {
                        DeleteFileBestEffort(filePath);
                        DeleteMarkerBestEffort(loaded.SteamId);
                        continue;
                    }

                    // 2. Compare committedAttemptId against the baseline captured when this transfer started.
                    //    If committedAttemptId changed to a DIFFERENT attempt ID in between, this staged copy was superseded!
                    if (!String.IsNullOrWhiteSpace(committedAttemptId) &&
                        !String.Equals(committedAttemptId, loaded.PreviousCommittedTransferAttemptId, StringComparison.Ordinal))
                    {
                        DeleteFileBestEffort(filePath);
                        continue;
                    }

                    if (loaded.Account != null &&
                        committedBySteamId.TryGetValue(loaded.SteamId, out SteamGuardAccount committed) &&
                        IsSameAuthenticatorReplacement(committed, loaded.Account))
                    {
                        // Crash happened AFTER SaveAccount committed but before cleanup!
                        // Silently clean up the redundant staged recovery and pending marker.
                        DeleteFileBestEffort(filePath);
                        DeleteMarkerBestEffort(loaded.SteamId);
                        continue;
                    }

                    pendingRecoveries.Add(loaded);
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "Could not scan staged transfer recovery files on startup.");
            }

            return pendingRecoveries;
        }

        public static bool IsSameAuthenticatorReplacement(SteamGuardAccount committed, SteamGuardAccount staged)
        {
            if (committed == null || staged == null)
                return false;

            bool sharedSecretMatches = !String.IsNullOrEmpty(committed.SharedSecret) &&
                String.Equals(committed.SharedSecret, staged.SharedSecret, StringComparison.Ordinal);
            if (!sharedSecretMatches)
                return false;

            if (!String.IsNullOrEmpty(staged.TokenGID) &&
                !String.Equals(committed.TokenGID, staged.TokenGID, StringComparison.Ordinal))
                return false;

            if (!String.IsNullOrEmpty(staged.SerialNumber) &&
                !String.Equals(committed.SerialNumber, staged.SerialNumber, StringComparison.Ordinal))
                return false;

            return true;
        }

        private static StagedTransferRecoveryRecord TryReadStagedRecoveryFile(string filePath, string expectedCanonicalDataDir, string passKey)
        {
            try
            {
                FileInfo info = new FileInfo(filePath);
                if (!info.Exists || info.Length > MaximumRecoveryFileBytes)
                    return null;

                string json = File.ReadAllText(filePath, new UTF8Encoding(false, true));
                ProtectedRecoveryEnvelope envelope = JsonConvert.DeserializeObject<ProtectedRecoveryEnvelope>(json, MarkerJsonSettings);
                if (envelope == null ||
                    envelope.Version != RecoveryEnvelopeVersion ||
                    envelope.SteamId == 0 ||
                    String.IsNullOrWhiteSpace(envelope.Payload))
                {
                    return null;
                }

                // Strictly verify canonical DataDirectory binding!
                if (!String.Equals(envelope.CanonicalDataDirectory, expectedCanonicalDataDir, StringComparison.OrdinalIgnoreCase))
                    return null;

                byte[] plaintextBytes = null;
                byte[] keyBytes = null;
                byte[] passwordBytes = null;
                try
                {
                    if (String.Equals(envelope.Mode, "aesgcm-dpapi", StringComparison.Ordinal))
                    {
                        if (String.IsNullOrEmpty(passKey))
                        {
                            return new StagedTransferRecoveryRecord
                            {
                                FilePath = filePath,
                                SteamId = envelope.SteamId,
                                StagedAtUtc = envelope.StagedAtUtc,
                                TransferAttemptId = envelope.TransferAttemptId,
                                PreviousCommittedTransferAttemptId = envelope.PreviousCommittedTransferAttemptId,
                                RequiresPasskey = true
                            };
                        }

                        byte[] salt = Convert.FromBase64String(envelope.Salt);
                        byte[] nonce = Convert.FromBase64String(envelope.Nonce);
                        byte[] tag = Convert.FromBase64String(envelope.Tag);
                        byte[] aad = BuildRecoveryAad(envelope.CanonicalDataDirectory, envelope.SteamId, envelope.Salt);
                        byte[] dpapiWrapped = Convert.FromBase64String(envelope.Payload);
                        byte[] ciphertext = ProtectedData.Unprotect(dpapiWrapped, aad, DataProtectionScope.CurrentUser);

                        plaintextBytes = new byte[ciphertext.Length];
                        passwordBytes = Encoding.UTF8.GetBytes(passKey);
                        keyBytes = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, RecoveryKdfIterations, HashAlgorithmName.SHA256, 32);
                        using (AesGcm aesGcm = new AesGcm(keyBytes, 16))
                        {
                            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes, aad);
                        }
                    }
                    else if (String.Equals(envelope.Mode, "dpapi", StringComparison.Ordinal))
                    {
                        byte[] aad = BuildRecoveryAad(envelope.CanonicalDataDirectory, envelope.SteamId, "dpapi");
                        byte[] dpapiBytes = Convert.FromBase64String(envelope.Payload);
                        plaintextBytes = ProtectedData.Unprotect(dpapiBytes, aad, DataProtectionScope.CurrentUser);
                    }
                    else
                    {
                        return null;
                    }

                    string innerJson = Encoding.UTF8.GetString(plaintextBytes);
                    ProtectedRecoveryPayload innerPayload = JsonConvert.DeserializeObject<ProtectedRecoveryPayload>(innerJson, MarkerJsonSettings);
                    if (innerPayload?.Account == null ||
                        !String.Equals(innerPayload.CanonicalDataDirectory, expectedCanonicalDataDir, StringComparison.OrdinalIgnoreCase) ||
                        innerPayload.SteamId != envelope.SteamId ||
                        (!String.IsNullOrEmpty(envelope.TransferAttemptId) &&
                         !String.Equals(innerPayload.TransferAttemptId, envelope.TransferAttemptId, StringComparison.Ordinal)) ||
                        (!String.IsNullOrEmpty(envelope.PreviousCommittedTransferAttemptId) &&
                         !String.Equals(innerPayload.PreviousCommittedTransferAttemptId, envelope.PreviousCommittedTransferAttemptId, StringComparison.Ordinal)))
                    {
                        return null;
                    }

                    return new StagedTransferRecoveryRecord
                    {
                        FilePath = filePath,
                        SteamId = envelope.SteamId,
                        StagedAtUtc = envelope.StagedAtUtc,
                        TransferAttemptId = innerPayload.TransferAttemptId ?? envelope.TransferAttemptId,
                        PreviousCommittedTransferAttemptId = innerPayload.PreviousCommittedTransferAttemptId ?? envelope.PreviousCommittedTransferAttemptId,
                        Account = innerPayload.Account,
                        RequiresPasskey = false
                    };
                }
                finally
                {
                    if (plaintextBytes != null) CryptographicOperations.ZeroMemory(plaintextBytes);
                    if (keyBytes != null) CryptographicOperations.ZeroMemory(keyBytes);
                    if (passwordBytes != null) CryptographicOperations.ZeroMemory(passwordBytes);
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator transfer", ex, "A staged transfer recovery file could not be decrypted or verified.");
                return null;
            }
        }

        private static byte[] BuildRecoveryAad(string canonicalDataDirectory, ulong steamId, string discriminator)
        {
            return Encoding.UTF8.GetBytes(
                "ASDA-TRANSFER-RECOVERY-V1:" +
                canonicalDataDirectory.ToLowerInvariant() + ":" +
                steamId.ToString(CultureInfo.InvariantCulture) + ":" +
                discriminator);
        }

        private static string ComputeSha256Hex(string input)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal static void WriteAllTextAtomicallyFlushed(string destinationPath, string contents)
        {
            string tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] utf8 = new UTF8Encoding(false).GetBytes(contents);
                using (FileStream fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(utf8, 0, utf8.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(destinationPath))
                {
                    File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, destinationPath);
                }
            }
            catch
            {
                DeleteFileBestEffort(tempPath);
                throw;
            }
        }

        private static void DeleteFileBestEffort(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
