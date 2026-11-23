using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public static class LoginActionModes
    {
        public const string Manual = "manual";
        public const string ApprovePersistent = "approve_persistent";
        public const string Deny = "deny";
    }

    public class Manifest
    {
        [JsonProperty("encrypted")]
        public bool Encrypted { get; set; }

        [JsonProperty("first_run")]
        public bool FirstRun { get; set; } = true;

        [JsonProperty("first_qr")]
        public bool FirstQR { get; set; } = true;

        [JsonProperty("entries")]
        public List<ManifestEntry> Entries { get; set; }

        [JsonProperty("trade_confirmation_custom_interval_enabled")]
        public bool TradeConfirmationCustomIntervalEnabled { get; set; } = false;

        [JsonProperty("trade_confirmation_check_interval")]
        public int TradeConfirmationCheckInterval { get; set; } = 15;

        [JsonProperty("auto_confirm_market_transactions")]
        public bool AutoConfirmMarketTransactions { get; set; } = false;

        [JsonProperty("auto_confirm_trades")]
        public bool AutoConfirmTrades { get; set; } = false;

        [JsonProperty("trade_confirmation_selected_tab")]
        [JsonConverter(typeof(SafeTradeConfirmationTabJsonConverter))]
        public string TradeConfirmationSelectedTab { get; set; } = TradeConfirmationClassifier.CategoryAll;

        [JsonProperty("minimize_to_tray")]
        public bool MinimizeToTray { get; set; } = true;

        [JsonProperty("check_for_updates")]
        public bool CheckForUpdates { get; set; } = true;

        [JsonProperty("diagnostic_error_logging_enabled")]
        public bool DiagnosticErrorLoggingEnabled { get; set; } = false;

        [JsonProperty("login_action_monitoring_enabled")]
        public bool LoginActionMonitoringEnabled { get; set; } = true;

        [JsonProperty("login_action_mode")]
        public string LoginActionMode { get; set; } = LoginActionModes.Manual;

        [JsonProperty("login_action_auto_allow_ip_enabled")]
        public bool LoginActionAutoAllowIpEnabled { get; set; } = false;

        [JsonProperty("login_action_auto_allow_current_device_ip")]
        public bool LoginActionAutoAllowCurrentDeviceIp { get; set; } = false;

        [JsonProperty("login_action_auto_allow_ip")]
        public string LoginActionAutoAllowIp { get; set; } = String.Empty;

        [JsonProperty("proxy_enabled")]
        public bool ProxyEnabled { get; set; } = false;

        [JsonProperty("proxy_scheme")]
        public string ProxyScheme { get; set; } = "http";

        [JsonProperty("proxy_host")]
        public string ProxyHost { get; set; } = String.Empty;

        [JsonProperty("proxy_port")]
        public int ProxyPort { get; set; } = 0;

        [JsonProperty("proxy_username")]
        public string ProxyUsername { get; set; } = String.Empty;

        [JsonProperty("proxy_password")]
        public string ProxyPassword { get; set; } = String.Empty;

        [JsonProperty("storage_transaction_id", NullValueHandling = NullValueHandling.Ignore)]
        internal string StorageTransactionId { get; set; }

        private static Manifest _manifest { get; set; }
        private static readonly object storageLock = new object();
        private static long storageTransactionCommitCount;
        private long storageRevision;
        // Refresh persistence only needs to reject changes to the account it
        // captured. A manifest-wide revision would make an unrelated account
        // update abort an otherwise safe refresh.
        private readonly Dictionary<ulong, long> accountStorageRevisions = new Dictionary<ulong, long>();
        private const string StorageJournalFilename = ".asda-storage-transaction.json";
        private const string SettingsBackupFilename = ".manifest.settings.bak";
        private const string StorageBackupFilenamePrefix = ".manifest.";
        private const string AccountBackupFilenamePrefix = ".mafile.";
        private const string StorageBackupFilenameSuffix = ".bak";
        private const long MaximumManifestFileSizeBytes = 4 * 1024 * 1024;
        private const long MaximumAccountFileSizeBytes = 4 * 1024 * 1024;
        private const int MaximumAuthenticatorSecretTextLength = 4096;
        private const int ExpectedAuthenticatorSecretBytes = 20;
        private const int MaximumAuthenticatorSecretBytes = 64;
        private const int MaximumDeviceIdLength = 256;
        private const int MaximumAccountNameLength = 256;
        private const int MaximumManifestEntries = 1000;
        private static readonly JsonSerializerSettings storageJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 32,
            DateParseHandling = DateParseHandling.None
        };

        [JsonIgnore]
        public long StorageRevision
        {
            get
            {
                lock (storageLock)
                    return storageRevision;
            }
        }

        internal static long StorageTransactionCommitCount => Interlocked.Read(ref storageTransactionCommitCount);
        internal static Func<string, bool> BeforeDeleteFileForTesting { get; set; }

        public long GetAccountStorageRevision(ulong steamId)
        {
            if (steamId == 0)
                return 0;

            lock (storageLock)
                return GetAccountStorageRevisionLocked(steamId);
        }

        public sealed class UnmanagedMaFileCandidate
        {
            public string FileName { get; set; }
            public ulong SteamID { get; set; }
            public SteamGuardAccount Account { get; set; }
            internal string ContentHash { get; set; }
        }

        public static string GetExecutableDir()
        {
            // Account data lives beside the Launcher when the app is started through it.
            return ApplicationPaths.DataDirectory;
        }

        public static Manifest GetManifest(bool forceLoad = false)
        {
            // Check if already staticly loaded
            if (_manifest != null && !forceLoad)
            {
                return _manifest;
            }

            // Find config dir and manifest file
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string manifestFile = Path.Combine(maDir, "manifest.json");

            try
            {
                if (Directory.Exists(maDir))
                    ValidateStorageDirectory(maDir);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The account-data directory is not a supported local directory.");
                throw new ManifestParseException();
            }
            if (!RecoverPendingStorageTransaction(maDir, manifestFile))
                throw new ManifestRecoveryException();
            if (!RestoreManifestBackupIfNeeded(manifestFile, Path.Combine(maDir, SettingsBackupFilename), false))
                throw new ManifestParseException();

            // If there's no config dir, create it
            if (!Directory.Exists(maDir))
            {
                _manifest = GenerateNewManifest(false);
                return _manifest;
            }

            // If there's no manifest, throw exception
            if (!File.Exists(manifestFile))
            {
                // A failed first save can leave an empty data directory after recovery.
                // Treat it like a clean install rather than permanently rejecting it.
                if (!Directory.EnumerateFiles(maDir, "*.maFile").Any())
                {
                    _manifest = GenerateNewManifest(false);
                    return _manifest;
                }
                throw new ManifestParseException();
            }

            try
            {
                string manifestContents = ReadTextWithLimit(manifestFile, MaximumManifestFileSizeBytes);
                JObject manifestJson;
                using (StringReader stringReader = new StringReader(manifestContents))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = storageJsonSettings.MaxDepth, DateParseHandling = DateParseHandling.None })
                {
                    manifestJson = JObject.Load(jsonReader);
                }
                Manifest loadedManifest = manifestJson.ToObject<Manifest>(JsonSerializer.Create(storageJsonSettings));
                if (loadedManifest == null || loadedManifest.Entries == null)
                    throw new InvalidDataException("The account manifest does not contain a valid entries list.");

                ValidateManifestEntries(loadedManifest, maDir);
                bool migratedLegacyTradeSettings = loadedManifest.MigrateLegacyTradeConfirmationSettings(manifestJson);

                loadedManifest.NormalizeTradeConfirmationSettings();
                loadedManifest.NormalizeLoginActionSettings();

                if (migratedLegacyTradeSettings)
                {
                    loadedManifest.Save();
                }

                if (loadedManifest.Encrypted && loadedManifest.Entries.Count == 0)
                {
                    loadedManifest.Encrypted = false;
                    loadedManifest.Save();
                }

                loadedManifest.RecomputeExistingEntries();

                // Migrate the GUID form used by previous releases while loading
                // the manifest. Arbitrary legacy names are handled by the full
                // startup normalization pass after the UI unlocks encryption.
                StorageResult guidNormalizationResult = loadedManifest.NormalizeAccountFilenames(true);
                if (!guidNormalizationResult.Succeeded)
                    DiagnosticErrorLogger.Log("Authenticator storage", guidNormalizationResult.Exception, "A GUID-named authenticator file could not be normalized during startup.");

                lock (storageLock)
                {
                    DeleteFileBestEffort(
                        Path.Combine(maDir, SettingsBackupFilename),
                        "A stale manifest backup could not be removed after the settings were loaded.");
                }

                _manifest = loadedManifest;
                return loadedManifest;
            }
            catch (Exception)
            {
                _manifest = null;
                throw new ManifestParseException();
            }
        }

        public static Manifest GenerateNewManifest(bool scanDir = false)
        {
            // No directory means no manifest file anyways.
            Manifest newManifest = new Manifest();
            newManifest.Encrypted = false;
            newManifest.TradeConfirmationCustomIntervalEnabled = false;
            newManifest.TradeConfirmationCheckInterval = 15;
            newManifest.AutoConfirmMarketTransactions = false;
            newManifest.AutoConfirmTrades = false;
            newManifest.TradeConfirmationSelectedTab = TradeConfirmationClassifier.CategoryAll;
            newManifest.MinimizeToTray = true;
            newManifest.LoginActionMonitoringEnabled = true;
            newManifest.LoginActionMode = LoginActionModes.Manual;
            newManifest.LoginActionAutoAllowIpEnabled = false;
            newManifest.LoginActionAutoAllowCurrentDeviceIp = false;
            newManifest.LoginActionAutoAllowIp = String.Empty;
            newManifest.ProxyEnabled = false;
            newManifest.ProxyScheme = "http";
            newManifest.ProxyHost = String.Empty;
            newManifest.ProxyPort = 0;
            newManifest.ProxyUsername = String.Empty;
            newManifest.ProxyPassword = String.Empty;
            newManifest.Entries = new List<ManifestEntry>();
            newManifest.FirstRun = true;

            // Take a pre-manifest version and generate a manifest for it.
            if (!scanDir)
            {
                return newManifest;
            }

            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            if (!Directory.Exists(maDir))
            {
                return newManifest;
            }

            ValidateStorageDirectory(maDir);
            DirectoryInfo dir = new DirectoryInfo(maDir);
            var files = dir.GetFiles();

            foreach (var file in files)
            {
                if (!String.Equals(file.Extension, ".maFile", StringComparison.OrdinalIgnoreCase)) continue;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new MaFileEncryptedException();

                string contents = ReadTextWithLimit(file.FullName, MaximumAccountFileSizeBytes);
                try
                {
                    SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(contents, storageJsonSettings);
                    if (account?.Session == null || account.Session.SteamID == 0 ||
                        newManifest.Entries.Any(entry => entry.SteamID == account.Session.SteamID))
                        throw new InvalidDataException("The account file identity is invalid or duplicated.");

                    ManifestEntry newEntry = new ManifestEntry()
                    {
                        Filename = file.Name,
                        SteamID = account.Session.SteamID
                    };
                    newManifest.Entries.Add(newEntry);
                }
                catch (Exception)
                {
                    throw new MaFileEncryptedException();
                }
            }

            if (newManifest.Entries.Count > 0)
            {
                newManifest.Save();
                newManifest.PromptSetupPassKey("This version of SDA has encryption. Please enter a passkey below, or hit cancel to remain unencrypted");
            }

            if (newManifest.Save())
            {
                return newManifest;
            }

            return null;
        }

        public class IncorrectPassKeyException : Exception { }
        public class ManifestNotEncryptedException : Exception { }

        public string PromptForPassKey()
        {
            if (!this.Encrypted)
            {
                throw new ManifestNotEncryptedException();
            }

            bool passKeyValid = false;
            string passKey = null;
            while (!passKeyValid)
            {
                using (InputForm passKeyForm = new InputForm("Please enter your encryption passkey.", true))
                {
                    passKeyForm.ShowInputDialog();
                    if (!passKeyForm.Canceled)
                    {
                        passKey = passKeyForm.txtBox.Text;
                        passKeyValid = this.VerifyPasskey(passKey);
                        if (!passKeyValid)
                        {
                            AstroMessageBox.Show("That passkey is invalid.");
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            return passKey;
        }

        public string PromptSetupPassKey(string initialPrompt = "Enter passkey, or hit cancel to remain unencrypted.")
        {
            string newPassKey;
            using (InputForm newPassKeyForm = new InputForm(initialPrompt, true))
            {
                newPassKeyForm.ShowInputDialog();
                if (newPassKeyForm.Canceled || newPassKeyForm.txtBox.Text.Length == 0)
                {
                    AstroMessageBox.Show("WARNING: You chose to not encrypt your files. Doing so imposes a security risk for yourself. If an attacker were to gain access to your computer, they could completely lock you out of your account and steal all your items.");
                    return null;
                }

                newPassKey = newPassKeyForm.txtBox.Text;
            }

            string confirmPassKey;
            using (InputForm newPassKeyForm2 = new InputForm("Confirm new passkey.", true))
            {
                newPassKeyForm2.ShowInputDialog();
                if (newPassKeyForm2.Canceled)
                {
                    AstroMessageBox.Show("WARNING: You chose to not encrypt your files. Doing so imposes a security risk for yourself. If an attacker were to gain access to your computer, they could completely lock you out of your account and steal all your items.");
                    return null;
                }

                confirmPassKey = newPassKeyForm2.txtBox.Text;
            }

            if (newPassKey != confirmPassKey)
            {
                AstroMessageBox.Show("Passkeys do not match.");
                return null;
            }

            StorageResult encryptionResult = this.ChangeEncryptionKey(null, newPassKey);
            if (!encryptionResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", encryptionResult.Exception, "Setting an encryption passkey failed.");
                AstroMessageBox.Show(encryptionResult.UserMessage ?? "Unable to set passkey.");
                return null;
            }
            else
            {
                AstroMessageBox.Show("Passkey successfully set.");
            }

            return newPassKey;
        }

        public SteamAuth.SteamGuardAccount[] GetAllAccounts(string passKey = null, int limit = -1)
        {
            if (limit < -1)
                throw new ArgumentOutOfRangeException(nameof(limit));

            lock (storageLock)
            {
                if (limit == 0 || (passKey == null && this.Encrypted))
                    return Array.Empty<SteamGuardAccount>();

                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                ValidateStorageDirectory(maDir);
                if (Directory.Exists(maDir) && !RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                {
                    DiagnosticErrorLogger.Log(
                        "Authenticator storage",
                        new IOException("A pending storage transaction could not be recovered before reading accounts."),
                        "The local authenticator accounts could not be loaded safely.");
                    return Array.Empty<SteamGuardAccount>();
                }
                ValidateManifestEntries(this, maDir);

                List<SteamAuth.SteamGuardAccount> accounts = new List<SteamAuth.SteamGuardAccount>();
                foreach (var entry in this.Entries)
                {
                    try
                    {
                        string fileText = ReadTextWithLimit(GetManifestFilePath(maDir, entry), MaximumAccountFileSizeBytes);
                        if (this.Encrypted)
                        {
                            string decryptedText = FileEncryptor.DecryptData(passKey, entry.Salt, entry.IV, fileText);
                            if (decryptedText == null) return Array.Empty<SteamGuardAccount>();
                            fileText = decryptedText;
                        }

                        SteamAuth.SteamGuardAccount account = JsonConvert.DeserializeObject<SteamAuth.SteamGuardAccount>(fileText, storageJsonSettings);
                        if (account?.Session == null || account.Session.SteamID == 0 || account.Session.SteamID != entry.SteamID)
                        {
                            DiagnosticErrorLogger.Log(
                                "Authenticator storage",
                                new InvalidDataException("An account file does not match its manifest entry."),
                                "A local authenticator file was ignored because its identity could not be validated.");
                            continue;
                        }

                        accounts.Add(account);

                        if (limit != -1 && accounts.Count >= limit)
                            break;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log(
                            "Authenticator storage",
                            ex,
                            "A local authenticator file could not be loaded and was ignored.");
                    }
                }

                return accounts.ToArray();
            }
        }

        public static string GetCanonicalMaFileFilename(ulong steamId)
        {
            if (steamId == 0)
                throw new ArgumentOutOfRangeException(nameof(steamId));
            return steamId.ToString(CultureInfo.InvariantCulture) + ".maFile";
        }

        public bool IsSessionMarkedForRenewal(ulong steamId)
        {
            return steamId != 0 && Entries != null && Entries.Any(entry => entry.SteamID == steamId && entry.SessionNeedsRenewal);
        }

        public StorageResult SetSessionNeedsRenewal(ulong steamId, bool needsRenewal)
        {
            if (steamId == 0)
                return StorageResult.Failure(StorageFailureKind.Validation, "The account identity is invalid.");

            lock (storageLock)
            {
                ManifestEntry entry = Entries?.FirstOrDefault(candidate => candidate.SteamID == steamId);
                if (entry == null)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account is not managed by this authenticator.");
                if (entry.SessionNeedsRenewal == needsRenewal)
                    return StorageResult.Success();

                try
                {
                    Manifest stagedManifest = CloneForStorage();
                    ManifestEntry stagedEntry = stagedManifest.Entries.First(candidate => candidate.SteamID == steamId);
                    stagedEntry.SessionNeedsRenewal = needsRenewal;
                    StorageResult result = stagedManifest.SaveWithResult();
                    if (result.Succeeded)
                        CopyStorageStateFrom(stagedManifest);
                    return result;
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Manifest, "The account session state could not be saved.", ex);
                }
            }
        }

        /// <summary>
        /// Copies managed non-canonical files to their canonical names in a
        /// manifest transaction. The ciphertext is copied byte-for-byte so this
        /// operation does not require the encryption passkey.
        /// </summary>
        public StorageResult NormalizeAccountFilenames()
        {
            return NormalizeAccountFilenames(false);
        }

        private StorageResult NormalizeAccountFilenames(bool guidOnly)
        {
            lock (storageLock)
            {
                try
                {
                    string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                    ValidateStorageDirectory(maDir);
                    ValidateManifestEntries(this, maDir);
                    if (Directory.Exists(maDir) && !RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                    {
                        return StorageResult.Failure(
                            StorageFailureKind.Io,
                            "A previous account-data transaction could not be recovered safely. The existing account data was kept unchanged.");
                    }

                    Manifest stagedManifest = CloneForStorage();
                    Dictionary<string, string> stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    List<string> obsoleteFiles = new List<string>();
                    HashSet<string> referencedFilenames = new HashSet<string>(
                        Entries.Select(entry => entry.Filename), StringComparer.OrdinalIgnoreCase);
                    bool hasChanges = false;

                    for (int i = 0; i < Entries.Count; i++)
                    {
                        ManifestEntry entry = Entries[i];
                        string canonicalFilename = GetCanonicalMaFileFilename(entry.SteamID);
                        if (String.Equals(entry.Filename, canonicalFilename, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (guidOnly && !IsGuidMaFileFilename(entry.Filename, entry.SteamID))
                            continue;

                        string sourcePath = GetManifestFilePath(maDir, entry);
                        string canonicalPath = Path.Combine(maDir, canonicalFilename);

                        // A pre-existing canonical file may be an independently
                        // imported account. Never overwrite it while migrating a
                        // GUID file; leaving the GUID reference is recoverable.
                        if (referencedFilenames.Contains(canonicalFilename) || Path.Exists(canonicalPath))
                        {
                            DiagnosticErrorLogger.Log(
                                "Authenticator storage",
                                new IOException("The canonical maFile filename is already in use."),
                                "A non-canonical authenticator file was kept because its canonical filename is occupied.");
                            continue;
                        }

                        string contents = ReadTextWithLimit(sourcePath, MaximumAccountFileSizeBytes);
                        stagedFiles.Add(canonicalFilename, contents);
                        stagedManifest.Entries[i].Filename = canonicalFilename;
                        obsoleteFiles.Add(entry.Filename);
                        hasChanges = true;
                    }

                    if (!hasChanges)
                        return StorageResult.Success();

                    StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
                    if (result.Succeeded)
                        CopyStorageStateFrom(stagedManifest);
                    return result;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "Non-canonical authenticator filenames could not be normalized.");
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The authenticator filenames could not be normalized. The existing account data was kept unchanged.",
                        ex);
                }
            }
        }

        private static bool IsGuidMaFileFilename(string filename, ulong steamId)
        {
            string prefix = steamId.ToString(CultureInfo.InvariantCulture) + ".";
            if (String.IsNullOrWhiteSpace(filename) ||
                !filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase) ||
                filename.Length <= prefix.Length + ".maFile".Length)
                return false;

            string guidText = filename.Substring(prefix.Length, filename.Length - prefix.Length - ".maFile".Length);
            return guidText.Length == 32 && Guid.TryParseExact(guidText, "N", out _);
        }

        /// <summary>
        /// Finds untracked, plaintext, structurally valid maFiles. This method is
        /// intentionally a snapshot operation; callers decide whether and when to
        /// prompt, so it is not used by runtime watchers.
        /// </summary>
        public IReadOnlyList<UnmanagedMaFileCandidate> FindUnmanagedMaFiles()
        {
            lock (storageLock)
            {
                List<UnmanagedMaFileCandidate> candidates = new List<UnmanagedMaFileCandidate>();
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                if (!Directory.Exists(maDir))
                    return candidates;

                ValidateStorageDirectory(maDir);
                if (!RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                    return candidates;
                HashSet<string> managedFilenames = new HashSet<string>(
                    Entries.Select(entry => entry.Filename), StringComparer.OrdinalIgnoreCase);
                HashSet<ulong> managedSteamIds = new HashSet<ulong>(Entries.Select(entry => entry.SteamID));
                HashSet<ulong> duplicateSteamIds = new HashSet<ulong>();
                Dictionary<ulong, UnmanagedMaFileCandidate> bySteamId = new Dictionary<ulong, UnmanagedMaFileCandidate>();

                foreach (string path in Directory.EnumerateFiles(maDir, "*.maFile", SearchOption.TopDirectoryOnly))
                {
                    FileInfo file = new FileInfo(path);
                    if (managedFilenames.Contains(file.Name) ||
                        (file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    try
                    {
                        string contents = ReadTextWithLimit(path, MaximumAccountFileSizeBytes);
                        SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(contents, storageJsonSettings);
                        ulong steamId = account?.Session?.SteamID ?? 0;
                        if (steamId == 0 || managedSteamIds.Contains(steamId))
                            continue;
                        if (!IsValidAutomaticImportAccount(account))
                            throw new InvalidDataException("The authenticator file is missing usable authenticator secrets or device data.");

                        // A duplicate identity is not safe to import automatically:
                        // keep all source files available for manual selection.
                        if (duplicateSteamIds.Contains(steamId))
                            continue;
                        if (bySteamId.ContainsKey(steamId))
                        {
                            bySteamId.Remove(steamId);
                            duplicateSteamIds.Add(steamId);
                            continue;
                        }

                        bySteamId[steamId] = new UnmanagedMaFileCandidate
                        {
                            FileName = file.Name,
                            SteamID = steamId,
                            Account = account,
                            ContentHash = GetContentHash(contents)
                        };
                    }
                    catch (Exception ex)
                    {
                        // Invalid, encrypted, linked, and oversized files remain
                        // available through the explicit manual-import flow.
                        DiagnosticErrorLogger.Log("Authenticator startup import", ex, "A maFile was not eligible for automatic import.");
                    }
                }

                return bySteamId.Values
                    .OrderBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Revalidates and imports a startup candidate set as one storage
        /// transaction. Source files are obsolete files and are removed only after
        /// the new manifest has committed.
        /// </summary>
        public StorageResult ImportUnmanagedMaFiles(IReadOnlyList<UnmanagedMaFileCandidate> candidates, string passKey = null)
        {
            lock (storageLock)
            {
                if (candidates == null || candidates.Count == 0)
                    return StorageResult.Success();
                if (candidates.Count > MaximumManifestEntries)
                    return StorageResult.Failure(StorageFailureKind.Validation, "Too many authenticator files were selected for import.");

                try
                {
                    string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                    ValidateStorageDirectory(maDir);
                    if (Directory.Exists(maDir) && !RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. No files were imported.");
                    if (this.Encrypted && !VerifyPasskey(passKey))
                        return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid. The files were not imported.");

                    ValidateManifestEntries(this, maDir);
                    HashSet<ulong> managedSteamIds = new HashSet<ulong>(Entries.Select(entry => entry.SteamID));
                    HashSet<string> sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<(UnmanagedMaFileCandidate Candidate, SteamGuardAccount Account)> validatedCandidates =
                        new List<(UnmanagedMaFileCandidate Candidate, SteamGuardAccount Account)>();

                    foreach (UnmanagedMaFileCandidate candidate in candidates)
                    {
                        if (candidate == null || String.IsNullOrWhiteSpace(candidate.FileName) ||
                            !sourceNames.Add(candidate.FileName) || managedSteamIds.Contains(candidate.SteamID))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files is no longer available for import.");

                        ValidateManifestFilename(candidate.FileName);
                        string path = Path.Combine(maDir, candidate.FileName);
                        string contents = ReadTextWithLimit(path, MaximumAccountFileSizeBytes);
                        if (!String.Equals(GetContentHash(contents), candidate.ContentHash, StringComparison.Ordinal))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files changed before it could be imported.");

                        SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(contents, storageJsonSettings);
                        if (account?.Session == null || account.Session.SteamID == 0 || account.Session.SteamID != candidate.SteamID ||
                            !IsValidAutomaticImportAccount(account))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files is no longer valid.");
                        if (!managedSteamIds.Add(account.Session.SteamID))
                            return StorageResult.Failure(StorageFailureKind.Validation, "Two selected authenticator files use the same Steam identity.");
                        validatedCandidates.Add((candidate, account));
                    }

                    Manifest stagedManifest = CloneForStorage();
                    Dictionary<string, string> stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    List<string> obsoleteFiles = new List<string>();
                    HashSet<string> referencedFilenames = new HashSet<string>(
                        Entries.Select(entry => entry.Filename), StringComparer.OrdinalIgnoreCase);
                    HashSet<string> plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<ulong> updatedSteamIds = new List<ulong>(validatedCandidates.Count);
                    bool usedFallbackGuidFilename = false;

                    foreach ((UnmanagedMaFileCandidate candidate, SteamGuardAccount account) in validatedCandidates)
                    {
                        string salt = null;
                        string iv = null;
                        string contents = JsonConvert.SerializeObject(account, storageJsonSettings);
                        if (this.Encrypted)
                        {
                            salt = FileEncryptor.GetRandomSalt();
                            iv = FileEncryptor.GetInitializationVector();
                            contents = FileEncryptor.EncryptData(passKey, salt, iv, contents);
                            if (contents == null)
                                return StorageResult.Failure(StorageFailureKind.Encryption, "An authenticator file could not be encrypted. No files were imported.");
                        }

                        // Never mutate unmanaged source files in place: keep them untouched until after
                        // the manifest commits so rollback never overwrites non-manifest files.
                        AccountFileTargetPlan plan = PlanAccountFileTarget(
                            maDir,
                            account.Session.SteamID,
                            null,
                            referencedFilenames,
                            plannedTargets,
                            sourceNames);
                        stagedFiles.Add(plan.TargetFilename, contents);
                        obsoleteFiles.Add(candidate.FileName);
                        if (plan.Mode == StagedAccountFileMode.CreateFallbackGuid)
                            usedFallbackGuidFilename = true;

                        stagedManifest.Entries.Add(new ManifestEntry
                        {
                            SteamID = account.Session.SteamID,
                            IV = iv,
                            Salt = salt,
                            Filename = plan.TargetFilename,
                            SessionNeedsRenewal = account.Session.IsRefreshTokenExpired()
                        });
                        updatedSteamIds.Add(account.Session.SteamID);
                    }

                    StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
                    if (result.Succeeded)
                    {
                        CopyStorageStateFrom(stagedManifest, updatedSteamIds);
                        if (usedFallbackGuidFilename)
                        {
                            StorageResult normalizationResult = NormalizeAccountFilenames(true);
                            if (!normalizationResult.Succeeded)
                                DiagnosticErrorLogger.Log("Authenticator startup import", normalizationResult.Exception, "Imported accounts were saved but their canonical filenames could not be completed.");
                        }
                    }
                    return result;
                }
                catch (JsonException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The selected authenticator files are not valid JSON. No files were imported.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The selected authenticator files could not be imported. No files were changed.", ex);
                }
            }
        }

        public StorageResult ChangeEncryptionKey(string oldKey, string newKey)
        {
            lock (storageLock)
            {
                bool toEncrypt = newKey != null;
                if (toEncrypt && String.IsNullOrEmpty(newKey))
                {
                    return StorageResult.Failure(StorageFailureKind.Validation, "A new passkey is required to enable encryption.");
                }

                try
                {
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                if (Directory.Exists(maDir))
                {
                    ValidateStorageDirectory(maDir);
                    if (!RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. Encryption settings were not changed.");
                    }
                }

                if (this.Encrypted)
                {
                    if (!this.VerifyPasskey(oldKey))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "The current passkey is incorrect.");
                    }
                }

                Manifest stagedManifest = CloneForStorage();
                Dictionary<string, string> stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> replacedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string> obsoleteFiles = new List<string>();
                HashSet<string> referencedFilenames = new HashSet<string>(
                    this.Entries.Select(entry => entry.Filename), StringComparer.OrdinalIgnoreCase);
                HashSet<string> plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<ulong> updatedSteamIds = new List<ulong>(this.Entries.Count);
                bool usedFallbackGuidFilename = false;

                for (int i = 0; i < this.Entries.Count; i++)
                {
                    ManifestEntry existingEntry = this.Entries[i];
                    string existingFilename = GetManifestFilePath(maDir, existingEntry);
                    if (!File.Exists(existingFilename))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "One of the local authenticator files is missing. Encryption settings were not changed.");
                    }

                    string fileContents = ReadTextWithLimit(existingFilename, MaximumAccountFileSizeBytes);
                    if (this.Encrypted)
                    {
                        fileContents = FileEncryptor.DecryptData(oldKey, existingEntry.Salt, existingEntry.IV, fileContents);
                        if (fileContents == null)
                        {
                            return StorageResult.Failure(StorageFailureKind.Encryption, "The existing authenticator files could not be decrypted. Encryption settings were not changed.");
                        }
                    }
                    SteamGuardAccount existingAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(fileContents, storageJsonSettings);
                    if (existingAccount?.Session == null || existingAccount.Session.SteamID != existingEntry.SteamID)
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "One of the local authenticator files is invalid. Encryption settings were not changed.");
                    }

                    string newSalt = null;
                    string newIV = null;
                    string stagedContents = fileContents;
                    if (toEncrypt)
                    {
                        newSalt = FileEncryptor.GetRandomSalt();
                        newIV = FileEncryptor.GetInitializationVector();
                        stagedContents = FileEncryptor.EncryptData(newKey, newSalt, newIV, fileContents);
                        if (stagedContents == null)
                        {
                            return StorageResult.Failure(StorageFailureKind.Encryption, "The authenticator files could not be encrypted. Encryption settings were not changed.");
                        }
                    }

                    AccountFileTargetPlan plan = PlanAccountFileTarget(
                        maDir,
                        existingEntry.SteamID,
                        existingEntry.Filename,
                        referencedFilenames,
                        plannedTargets,
                        null);
                    if (plan.Mode == StagedAccountFileMode.ReplaceCanonical)
                    {
                        replacedFiles.Add(plan.TargetFilename, stagedContents);
                    }
                    else
                    {
                        stagedFiles.Add(plan.TargetFilename, stagedContents);
                        obsoleteFiles.Add(existingEntry.Filename);
                        if (plan.Mode == StagedAccountFileMode.CreateFallbackGuid)
                            usedFallbackGuidFilename = true;
                    }

                    stagedManifest.Entries[i].Filename = plan.TargetFilename;
                    stagedManifest.Entries[i].Salt = newSalt;
                    stagedManifest.Entries[i].IV = newIV;
                    updatedSteamIds.Add(existingEntry.SteamID);
                }

                stagedManifest.Encrypted = toEncrypt;
                StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles, replacedFiles);
                if (result.Succeeded)
                {
                    CopyStorageStateFrom(stagedManifest, updatedSteamIds);
                    if (usedFallbackGuidFilename)
                    {
                        StorageResult normalizationResult = NormalizeAccountFilenames(true);
                        if (!normalizationResult.Succeeded)
                            DiagnosticErrorLogger.Log("Authenticator storage", normalizationResult.Exception, "Encryption changed successfully, but canonical authenticator filenames could not be completed.");
                    }
                }
                return result;
                }
                catch (JsonException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The authenticator data could not be prepared for saving. Encryption settings were not changed.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The authenticator files could not be saved. Encryption settings were not changed.", ex);
                }
            }
        }

        public bool VerifyPasskey(string passkey)
        {
            lock (storageLock)
            {
                if (!this.Encrypted || this.Entries.Count == 0) return true;

                try
                {
                    var accounts = this.GetAllAccounts(passkey);
                    return accounts != null && accounts.Length == this.Entries.Count;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "The encryption passkey could not be verified.");
                    return false;
                }
            }
        }

        public bool RemoveAccount(SteamGuardAccount account, bool deleteMaFile = true)
        {
            return RemoveAccount(account, null, deleteMaFile);
        }

        public bool RemoveAccount(SteamGuardAccount account, string passKey, bool deleteMaFile = true)
        {
            lock (storageLock)
            {
                if (account == null)
                    return false;

                try
                {
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                if (Directory.Exists(maDir))
                {
                    ValidateStorageDirectory(maDir);
                    if (!RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                        return false;
                }

                ManifestEntry entry = FindEntryForRemoval(account, passKey);
                if (entry == null)
                    return account.Session != null; // If a session-backed account never existed, did you do what they asked?

                Manifest stagedManifest = CloneForStorage();
                ManifestEntry stagedEntry = stagedManifest.Entries.FirstOrDefault(candidate => String.Equals(candidate.Filename, entry.Filename, StringComparison.OrdinalIgnoreCase));
                if (stagedEntry == null)
                    return false;
                stagedManifest.Entries.Remove(stagedEntry);
                if (stagedManifest.Entries.Count == 0)
                    stagedManifest.Encrypted = false;

                StorageResult result = CommitStorageTransaction(
                    stagedManifest,
                    new Dictionary<string, string>(),
                    deleteMaFile ? new[] { entry.Filename } : Enumerable.Empty<string>());
                if (!result.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", result.Exception, "Removing a local authenticator account failed.");
                    return false;
                }

                CopyStorageStateFrom(stagedManifest);
                return true;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "Removing a local authenticator account failed.");
                    return false;
                }
            }
        }

        private ManifestEntry FindEntryForRemoval(SteamGuardAccount account, string passKey)
        {
            if (account.Session != null)
                return Entries.FirstOrDefault(entry => entry.SteamID == account.Session.SteamID);

            if (String.IsNullOrWhiteSpace(account.AccountName) || (Encrypted && String.IsNullOrEmpty(passKey)))
                return null;

            List<ManifestEntry> matchingEntries = new List<ManifestEntry>();
            string maDir = Path.Combine(GetExecutableDir(), "maFiles");
            foreach (ManifestEntry candidate in Entries)
            {
                try
                {
                    string contents = ReadTextWithLimit(GetManifestFilePath(maDir, candidate), MaximumAccountFileSizeBytes);
                    if (Encrypted)
                    {
                        contents = FileEncryptor.DecryptData(passKey, candidate.Salt, candidate.IV, contents);
                        if (contents == null)
                            return null;
                    }
                    SteamGuardAccount storedAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(contents, storageJsonSettings);
                    if (storedAccount?.Session != null && storedAccount.Session.SteamID == candidate.SteamID &&
                        String.Equals(storedAccount.AccountName, account.AccountName, StringComparison.Ordinal))
                        matchingEntries.Add(candidate);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "Could not inspect a local account while preparing its removal.");
                    continue;
                }
            }

            if (matchingEntries.Count > 1)
            {
                DiagnosticErrorLogger.Log(
                    "Authenticator storage",
                    new InvalidOperationException("Multiple local authenticator records matched the account name."),
                    "The local account removal was canceled because the account name was ambiguous.");
            }

            return matchingEntries.Count == 1 ? matchingEntries[0] : null;
        }

        public StorageResult SaveAccount(
            SteamGuardAccount account,
            bool encrypt,
            string passKey = null,
            long? expectedStorageRevision = null,
            bool allowCreate = true)
        {
            lock (storageLock)
            {
                if (account == null || account.Session == null || account.Session.SteamID == 0)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account data is incomplete and could not be saved.");
                if (expectedStorageRevision.HasValue && expectedStorageRevision.Value != GetAccountStorageRevisionLocked(account.Session.SteamID))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account data changed while the session was being refreshed.");
                if (encrypt && String.IsNullOrEmpty(passKey))
                    return StorageResult.Failure(StorageFailureKind.Validation, "An encryption passkey is required to save this account.");
                if (!encrypt && this.Encrypted)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account must be saved using the existing encryption passkey.");

                try
                {
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                if (Directory.Exists(maDir))
                {
                    ValidateStorageDirectory(maDir);
                    if (!RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. No account data was changed.");
                }

                if (this.Encrypted && !this.VerifyPasskey(passKey))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid. The account was not saved.");

                string salt = null;
                string iV = null;
                string jsonAccount = JsonConvert.SerializeObject(account, storageJsonSettings);
                SteamGuardAccount validatedAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(jsonAccount, storageJsonSettings);
                if (validatedAccount?.Session == null || validatedAccount.Session.SteamID != account.Session.SteamID)
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be validated before saving.");
                if (encrypt)
                {
                    salt = FileEncryptor.GetRandomSalt();
                    iV = FileEncryptor.GetInitializationVector();
                    jsonAccount = FileEncryptor.EncryptData(passKey, salt, iV, jsonAccount);
                    if (jsonAccount == null)
                        return StorageResult.Failure(StorageFailureKind.Encryption, "The account could not be encrypted before saving.");
                }

                Manifest stagedManifest = CloneForStorage();
                ManifestEntry previousEntry = stagedManifest.Entries.FirstOrDefault(entry => entry.SteamID == account.Session.SteamID);
                if (previousEntry == null && !allowCreate)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account is no longer managed by this authenticator.");

                HashSet<string> referencedFilenames = new HashSet<string>(
                    stagedManifest.Entries.Select(entry => entry.Filename), StringComparer.OrdinalIgnoreCase);
                HashSet<string> plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AccountFileTargetPlan plan = PlanAccountFileTarget(
                    maDir,
                    account.Session.SteamID,
                    previousEntry?.Filename,
                    referencedFilenames,
                    plannedTargets,
                    null);

                ManifestEntry newEntry = new ManifestEntry()
                {
                    SteamID = account.Session.SteamID,
                    IV = iV,
                    Salt = salt,
                    Filename = plan.TargetFilename,
                    SessionNeedsRenewal = account.Session.IsRefreshTokenExpired()
                };
                if (previousEntry == null)
                {
                    stagedManifest.Entries.Add(newEntry);
                }
                else
                {
                    int index = stagedManifest.Entries.IndexOf(previousEntry);
                    stagedManifest.Entries[index] = newEntry;
                }

                Dictionary<string, string> stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> replacedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                IEnumerable<string> obsoleteFiles;
                if (plan.Mode == StagedAccountFileMode.ReplaceCanonical)
                {
                    replacedFiles.Add(plan.TargetFilename, jsonAccount);
                    obsoleteFiles = Enumerable.Empty<string>();
                }
                else
                {
                    stagedFiles.Add(plan.TargetFilename, jsonAccount);
                    obsoleteFiles = previousEntry == null ? Enumerable.Empty<string>() : new[] { previousEntry.Filename };
                }

                stagedManifest.Encrypted = encrypt || stagedManifest.Encrypted;
                StorageResult result = CommitStorageTransaction(
                    stagedManifest,
                    stagedFiles,
                    obsoleteFiles,
                    replacedFiles);
                if (result.Succeeded)
                {
                    CopyStorageStateFrom(stagedManifest, new[] { account.Session.SteamID });
                    if (plan.Mode == StagedAccountFileMode.CreateFallbackGuid)
                    {
                        StorageResult normalizationResult = NormalizeAccountFilenames(true);
                        if (!normalizationResult.Succeeded)
                            DiagnosticErrorLogger.Log("Authenticator storage", normalizationResult.Exception, "The account was saved, but its canonical filename could not be completed.");
                    }
                }
                return result;
                }
                catch (JsonException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be prepared for saving.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The account could not be saved. Check that the data folder is available and try again.", ex);
                }
            }
        }

        public bool Save()
        {
            return SaveWithResult().Succeeded;
        }

        public StorageResult SaveWithResult()
        {
            lock (storageLock)
            {
                try
                {
                    string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                    string manifestFilename = Path.Combine(maDir, "manifest.json");
                    Directory.CreateDirectory(maDir);
                    ValidateStorageDirectory(maDir);
                    ValidateManifestEntries(this, maDir);
                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (!RecoverPendingStorageTransaction(maDir, manifestFilename, retainedObsoleteFilenames))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data save could not be recovered safely. No settings were changed.");
                    }
                    string contents = JsonConvert.SerializeObject(this, storageJsonSettings);
                    if (Encoding.UTF8.GetByteCount(contents) > MaximumManifestFileSizeBytes)
                        throw new InvalidDataException("The account manifest is larger than the supported size limit.");
                    string backupFilename = Path.Combine(maDir, SettingsBackupFilename);
                    WriteAllTextAtomically(manifestFilename, contents, backupFilename);
                    if (retainedObsoleteFilenames.Count > 0)
                    {
                        if (!RebaseRetainedStorageTransaction(maDir, contents) ||
                            !RecoverPendingStorageTransaction(maDir, manifestFilename))
                        {
                            return StorageResult.Failure(
                                StorageFailureKind.Io,
                                "The settings were saved, but a previous account-data cleanup could not be completed safely. Restart the application and try again.");
                        }
                    }
                    DeleteFileBestEffort(backupFilename, "The completed manifest backup could not be removed.");
                    return StorageResult.Success();
                }
                catch (JsonException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The application settings could not be prepared for saving.", ex);
                }
                catch (NotSupportedException ex)
                {
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The selected data folder does not support the safe atomic saves required for authenticator data. Move the data folder to a local drive and try again.",
                        ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Manifest, "The application settings could not be saved. Check that the data folder is available and try again.", ex);
                }
            }
        }

        public StorageResult SaveSettingsWithResult(Action<Manifest> updateSettings)
        {
            if (updateSettings == null)
                throw new ArgumentNullException(nameof(updateSettings));

            lock (storageLock)
            {
                Manifest staged;
                try
                {
                    staged = CloneForStorage();
                    updateSettings(staged);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(
                        StorageFailureKind.Validation,
                        "The updated settings are invalid and were not saved.",
                        ex);
                }

                StorageResult result = staged.SaveWithResult();
                if (result.Succeeded)
                    staged.CopySettingsInto(this);
                return result;
            }
        }

        private Manifest CloneForStorage()
        {
            Manifest clone = JsonConvert.DeserializeObject<Manifest>(JsonConvert.SerializeObject(this, storageJsonSettings), storageJsonSettings);
            if (clone == null)
                throw new JsonSerializationException("The manifest could not be cloned for a storage transaction.");
            clone.Entries ??= new List<ManifestEntry>();
            return clone;
        }

        private void CopyStorageStateFrom(Manifest source, IEnumerable<ulong> updatedSteamIds = null)
        {
            Dictionary<ulong, ManifestEntry> previousEntries = GetManifestEntriesBySteamId(Entries);
            Dictionary<ulong, ManifestEntry> nextEntries = GetManifestEntriesBySteamId(source?.Entries);
            HashSet<ulong> explicitlyUpdatedSteamIds = updatedSteamIds == null
                ? null
                : new HashSet<ulong>(updatedSteamIds.Where(steamId => steamId != 0));
            foreach (ulong steamId in previousEntries.Keys.Concat(nextEntries.Keys).Distinct().ToArray())
            {
                previousEntries.TryGetValue(steamId, out ManifestEntry previousEntry);
                nextEntries.TryGetValue(steamId, out ManifestEntry nextEntry);
                bool contentUpdated = explicitlyUpdatedSteamIds != null && explicitlyUpdatedSteamIds.Contains(steamId);
                if (!contentUpdated && AreManifestEntriesEquivalent(previousEntry, nextEntry))
                    continue;

                if (nextEntry == null)
                    accountStorageRevisions.Remove(steamId);
                else
                    accountStorageRevisions[steamId] = GetAccountStorageRevisionLocked(steamId) + 1;
            }

            Entries = source.Entries;
            Encrypted = source.Encrypted;
            StorageTransactionId = source.StorageTransactionId;
            storageRevision++;
        }

        private long GetAccountStorageRevisionLocked(ulong steamId)
        {
            return accountStorageRevisions.TryGetValue(steamId, out long revision) ? revision : 0;
        }

        private static Dictionary<ulong, ManifestEntry> GetManifestEntriesBySteamId(IEnumerable<ManifestEntry> entries)
        {
            Dictionary<ulong, ManifestEntry> result = new Dictionary<ulong, ManifestEntry>();
            foreach (ManifestEntry entry in entries ?? Enumerable.Empty<ManifestEntry>())
            {
                if (entry != null && entry.SteamID != 0)
                    result[entry.SteamID] = entry;
            }
            return result;
        }

        private static bool AreManifestEntriesEquivalent(ManifestEntry first, ManifestEntry second)
        {
            return first == null
                ? second == null
                : second != null && first.SteamID == second.SteamID &&
                    String.Equals(first.IV, second.IV, StringComparison.Ordinal) &&
                    String.Equals(first.Salt, second.Salt, StringComparison.Ordinal) &&
                    String.Equals(first.Filename, second.Filename, StringComparison.Ordinal) &&
                    first.SessionNeedsRenewal == second.SessionNeedsRenewal;
        }

        private void CopySettingsInto(Manifest destination)
        {
            destination.FirstRun = FirstRun;
            destination.FirstQR = FirstQR;
            destination.TradeConfirmationCustomIntervalEnabled = TradeConfirmationCustomIntervalEnabled;
            destination.TradeConfirmationCheckInterval = TradeConfirmationCheckInterval;
            destination.AutoConfirmMarketTransactions = AutoConfirmMarketTransactions;
            destination.AutoConfirmTrades = AutoConfirmTrades;
            destination.TradeConfirmationSelectedTab = TradeConfirmationSelectedTab;
            destination.MinimizeToTray = MinimizeToTray;
            destination.CheckForUpdates = CheckForUpdates;
            destination.DiagnosticErrorLoggingEnabled = DiagnosticErrorLoggingEnabled;
            destination.LoginActionMonitoringEnabled = LoginActionMonitoringEnabled;
            destination.LoginActionMode = LoginActionMode;
            destination.LoginActionAutoAllowIpEnabled = LoginActionAutoAllowIpEnabled;
            destination.LoginActionAutoAllowCurrentDeviceIp = LoginActionAutoAllowCurrentDeviceIp;
            destination.LoginActionAutoAllowIp = LoginActionAutoAllowIp;
            destination.ProxyEnabled = ProxyEnabled;
            destination.ProxyScheme = ProxyScheme;
            destination.ProxyHost = ProxyHost;
            destination.ProxyPort = ProxyPort;
            destination.ProxyUsername = ProxyUsername;
            destination.ProxyPassword = ProxyPassword;
        }

        private enum StagedAccountFileMode
        {
            CreateCanonical,
            ReplaceCanonical,
            CreateFallbackGuid
        }

        private readonly struct AccountFileTargetPlan
        {
            public AccountFileTargetPlan(StagedAccountFileMode mode, string targetFilename)
            {
                Mode = mode;
                TargetFilename = targetFilename;
            }

            public StagedAccountFileMode Mode { get; }
            public string TargetFilename { get; }
        }

        private static AccountFileTargetPlan PlanAccountFileTarget(
            string maDir,
            ulong steamId,
            string ownedFilename,
            ISet<string> referencedManifestFilenames,
            ISet<string> plannedTargetFilenames,
            ISet<string> batchSourceFilenames)
        {
            string canonicalFilename = GetCanonicalMaFileFilename(steamId);
            string canonicalPath = Path.Combine(maDir, canonicalFilename);

            if (!String.IsNullOrWhiteSpace(ownedFilename) &&
                String.Equals(ownedFilename, canonicalFilename, StringComparison.OrdinalIgnoreCase) &&
                !plannedTargetFilenames.Contains(canonicalFilename) &&
                File.Exists(Path.Combine(maDir, "manifest.json")) &&
                File.Exists(canonicalPath))
            {
                if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");

                plannedTargetFilenames.Add(canonicalFilename);
                return new AccountFileTargetPlan(StagedAccountFileMode.ReplaceCanonical, canonicalFilename);
            }

            if (!referencedManifestFilenames.Contains(canonicalFilename) &&
                !plannedTargetFilenames.Contains(canonicalFilename) &&
                (batchSourceFilenames == null || !batchSourceFilenames.Contains(canonicalFilename)) &&
                !Path.Exists(canonicalPath))
            {
                plannedTargetFilenames.Add(canonicalFilename);
                return new AccountFileTargetPlan(StagedAccountFileMode.CreateCanonical, canonicalFilename);
            }

            string fallbackFilename = steamId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".maFile";
            plannedTargetFilenames.Add(fallbackFilename);
            return new AccountFileTargetPlan(StagedAccountFileMode.CreateFallbackGuid, fallbackFilename);
        }

        private StorageResult CommitStorageTransaction(
            Manifest stagedManifest,
            IReadOnlyDictionary<string, string> stagedFiles,
            IEnumerable<string> obsoleteFiles,
            IReadOnlyDictionary<string, string> replacedFiles = null)
        {
            lock (storageLock)
            {
                Interlocked.Increment(ref storageTransactionCommitCount);
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                string manifestFilename = Path.Combine(maDir, "manifest.json");
                string journalFilename = Path.Combine(maDir, StorageJournalFilename);
                string backupFilename = CreateBackupFilename(maDir);
                List<string> createdFilenames = stagedFiles.Keys.ToList();
                List<string> replacedTargetFilenames = replacedFiles == null
                    ? new List<string>()
                    : replacedFiles.Keys.ToList();
                List<StorageTransactionReplacedFile> replacedFileRecords = new List<StorageTransactionReplacedFile>();
                Dictionary<string, string> createdFileHashes = null;
                List<string> obsoleteFilenames = obsoleteFiles
                    .Where(filename => !String.IsNullOrWhiteSpace(filename))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool manifestCommitted = false;
                bool manifestCommitStarted = false;
                bool journalWritten = false;
                List<string> actuallyCreatedFilenames = new List<string>();
                List<string> trackedRetainedObsoleteFilenames = new List<string>();

                try
                {
                    if (Directory.Exists(maDir))
                        ValidateStorageDirectory(maDir);

                    // A cleanup failure leaves the previous journal intentionally. Complete it
                    // before replacing it so its obsolete files remain recoverable.
                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (!RecoverPendingStorageTransaction(maDir, manifestFilename, retainedObsoleteFilenames))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. No account data was changed.");
                    }

                    HashSet<string> targetFilenameSet = new HashSet<string>(
                        createdFilenames.Concat(replacedTargetFilenames),
                        StringComparer.OrdinalIgnoreCase);
                    if (retainedObsoleteFilenames.Any(filename => targetFilenameSet.Contains(filename)))
                    {
                        throw new IOException("A pending storage cleanup file conflicts with the new transaction target.");
                    }

                    trackedRetainedObsoleteFilenames = retainedObsoleteFilenames
                        .Where(filename => !String.IsNullOrWhiteSpace(filename))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (trackedRetainedObsoleteFilenames.Count > 0)
                    {
                        HashSet<string> trackedRetainedSet = new HashSet<string>(trackedRetainedObsoleteFilenames, StringComparer.OrdinalIgnoreCase);
                        obsoleteFilenames.RemoveAll(filename => trackedRetainedSet.Contains(filename));
                    }

                    CopySettingsInto(stagedManifest);
                    stagedManifest.StorageTransactionId = Guid.NewGuid().ToString("N");
                    ValidateManifestEntries(stagedManifest, maDir);
                    string manifestContents = JsonConvert.SerializeObject(stagedManifest, storageJsonSettings);
                    if (Encoding.UTF8.GetByteCount(manifestContents) > MaximumManifestFileSizeBytes)
                        throw new InvalidDataException("The account manifest is larger than the supported size limit.");
                    Directory.CreateDirectory(maDir);
                    ValidateStorageDirectory(maDir);

                    foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
                    {
                        if (stagedFile.Value == null || Encoding.UTF8.GetByteCount(stagedFile.Value) > MaximumAccountFileSizeBytes)
                            throw new InvalidDataException("An account file is larger than the supported size limit.");
                    }

                    if (replacedFiles != null)
                    {
                        foreach (KeyValuePair<string, string> replacedFile in replacedFiles)
                        {
                            if (replacedFile.Value == null || Encoding.UTF8.GetByteCount(replacedFile.Value) > MaximumAccountFileSizeBytes)
                                throw new InvalidDataException("An account file is larger than the supported size limit.");
                        }
                    }

                    string previousManifestHash = File.Exists(manifestFilename)
                        ? GetContentHash(ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes))
                        : null;
                    Dictionary<string, ManifestEntry> previousManifestEntries = replacedTargetFilenames.Count > 0
                        ? GetManifestEntriesByFilenameForRecovery(manifestFilename)
                        : null;
                    if (replacedTargetFilenames.Count > 0 && String.IsNullOrWhiteSpace(previousManifestHash))
                        throw new FileNotFoundException("The existing account manifest could not be found for in-place replacement.", manifestFilename);

                    HashSet<string> transactionFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string filename in obsoleteFilenames)
                    {
                        ValidateStorageFilename(filename);
                        if (!transactionFilenames.Add(filename))
                            throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
                    }

                    foreach (string filename in trackedRetainedObsoleteFilenames)
                    {
                        ValidateStorageFilename(filename);
                        if (!transactionFilenames.Add(filename))
                            throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
                    }

                    foreach (string filename in createdFilenames)
                    {
                        ValidateStorageFilename(filename);
                        if (!transactionFilenames.Add(filename))
                            throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
                        if (Path.Exists(Path.Combine(maDir, filename)))
                            throw new IOException("The target authenticator filename is already in use.");
                    }

                    foreach (string filename in replacedTargetFilenames)
                    {
                        ValidateStorageFilename(filename);
                        if (!previousManifestEntries.TryGetValue(filename, out ManifestEntry previousEntry))
                            throw new InvalidDataException("A storage transaction attempted to replace a file not owned by the current manifest.");
                        string accountBackupFilename = CreateAccountBackupFilename(previousEntry.SteamID);
                        GetValidatedAccountBackupPath(maDir, accountBackupFilename, previousEntry.SteamID);
                        if (!transactionFilenames.Add(filename) || !transactionFilenames.Add(accountBackupFilename))
                            throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
                        string targetPath = Path.Combine(maDir, filename);
                        if (!File.Exists(targetPath))
                            throw new FileNotFoundException("The existing authenticator file could not be found for replacement.", targetPath);
                        if ((File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("A storage file uses an unsupported link.");
                        replacedFileRecords.Add(new StorageTransactionReplacedFile
                        {
                            Filename = filename,
                            BackupFilename = accountBackupFilename,
                            OriginalHash = GetFileBytesHash(targetPath),
                            ReplacementHash = GetContentHash(replacedFiles[filename])
                        });
                    }

                    createdFileHashes = createdFilenames.Count > 0
                        ? stagedFiles.ToDictionary(
                            pair => pair.Key,
                            pair => GetContentHash(pair.Value),
                            StringComparer.OrdinalIgnoreCase)
                        : null;

                    StorageTransactionJournal journal = new StorageTransactionJournal()
                    {
                        ManifestHash = GetContentHash(manifestContents),
                        PreviousManifestHash = previousManifestHash,
                        CreatedFilenames = createdFilenames,
                        CreatedFileHashes = createdFileHashes,
                        ReplacedFiles = replacedFileRecords.Count > 0 ? replacedFileRecords : null,
                        ObsoleteFilenames = obsoleteFilenames,
                        RetainedObsoleteFilenames = trackedRetainedObsoleteFilenames.Count > 0 ? trackedRetainedObsoleteFilenames : null,
                        BackupFilename = Path.GetFileName(backupFilename)
                    };
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings));
                    journalWritten = true;

                    foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
                    {
                        WriteAllTextAtomically(
                            Path.Combine(maDir, stagedFile.Key),
                            stagedFile.Value,
                            null,
                            AtomicWriteMode.CreateNewOnly);
                        actuallyCreatedFilenames.Add(stagedFile.Key);
                    }

                    foreach (StorageTransactionReplacedFile record in replacedFileRecords)
                    {
                        WriteAllTextAtomically(
                            Path.Combine(maDir, record.Filename),
                            replacedFiles[record.Filename],
                            Path.Combine(maDir, record.BackupFilename),
                            AtomicWriteMode.ReplaceExistingOnly);
                    }

                    manifestCommitStarted = true;
                    WriteAllTextAtomically(manifestFilename, manifestContents, backupFilename);
                    manifestCommitted = true;

                    bool backupDeleted = DeleteFileBestEffort(backupFilename, "The completed manifest backup could not be removed.");
                    if (backupDeleted &&
                        DeleteFilesBestEffort(
                            maDir,
                            replacedFileRecords.Select(record => record.BackupFilename),
                            "A replaced authenticator backup could not be removed after the manifest was committed.") &&
                        DeleteFilesBestEffort(
                            maDir,
                            obsoleteFilenames.Concat(trackedRetainedObsoleteFilenames),
                            "The replaced authenticator file could not be removed after the manifest was committed."))
                    {
                        DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed.");
                    }
                    return StorageResult.Success();
                }
                catch (JsonException ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, actuallyCreatedFilenames, createdFileHashes, replacedFileRecords, trackedRetainedObsoleteFilenames, manifestCommitStarted);
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be prepared for saving.", ex);
                }
                catch (NotSupportedException ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, actuallyCreatedFilenames, createdFileHashes, replacedFileRecords, trackedRetainedObsoleteFilenames, manifestCommitStarted);
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The selected data folder does not support the safe atomic saves required for authenticator data. Move the data folder to a local drive and try again.",
                        ex);
                }
                catch (Exception ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, actuallyCreatedFilenames, createdFileHashes, replacedFileRecords, trackedRetainedObsoleteFilenames, manifestCommitStarted);
                    return StorageResult.Failure(
                        manifestCommitStarted ? StorageFailureKind.Manifest : StorageFailureKind.Io,
                        manifestCommitStarted
                            ? "The account manifest could not be saved. The existing account data was kept unchanged."
                            : "The account files could not be saved. The existing account data was kept unchanged.",
                        ex);
                }
            }
        }

        private static void RollBackUncommittedTransaction(
            string maDir,
            string manifestFilename,
            string journalFilename,
            string backupFilename,
            IReadOnlyList<string> createdFilenames,
            IReadOnlyDictionary<string, string> createdFileHashes,
            IReadOnlyList<StorageTransactionReplacedFile> replacedFiles,
            IReadOnlyList<string> retainedObsoleteFilenames,
            bool manifestCommitStarted)
        {
            if (manifestCommitStarted && !RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, true))
                return;

            if (!RestoreReplacedFilesIfNeeded(maDir, replacedFiles))
                return;

            if (replacedFiles != null &&
                replacedFiles.Count > 0 &&
                !DeleteFilesBestEffort(
                    maDir,
                    replacedFiles.Select(record => record.BackupFilename),
                    "A replaced authenticator backup could not be removed after a failed save."))
            {
                return;
            }

            if (DeleteCreatedFilesForRecovery(maDir, createdFilenames, createdFileHashes))
            {
                bool backupDeleted = String.IsNullOrWhiteSpace(backupFilename) ||
                    DeleteFileBestEffort(backupFilename, "A manifest backup could not be removed after a failed save.");
                if (!backupDeleted)
                    return;

                if (retainedObsoleteFilenames != null &&
                    retainedObsoleteFilenames.Count > 0 &&
                    !DeleteFilesBestEffort(maDir, retainedObsoleteFilenames, "A retained obsolete authenticator file could not be removed after a failed save."))
                {
                    List<string> remainingRetainedObsolete = retainedObsoleteFilenames
                        .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                        .ToList();
                    if (remainingRetainedObsolete.Count > 0)
                    {
                        PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, remainingRetainedObsolete);
                        return;
                    }
                }

                DeleteFileBestEffort(journalFilename, "A storage journal could not be removed after a failed save.");
            }
        }

        private static bool PreserveRetainedObsoleteJournal(
            string manifestFilename,
            string journalFilename,
            IReadOnlyList<string> retainedObsoleteFilenames)
        {
            try
            {
                if (!File.Exists(manifestFilename))
                    return false;

                string currentManifestContents = ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes);
                StorageTransactionJournal retainedJournal = new StorageTransactionJournal()
                {
                    ManifestHash = GetContentHash(currentManifestContents),
                    CreatedFilenames = new List<string>(),
                    ReplacedFiles = null,
                    ObsoleteFilenames = new List<string>(),
                    RetainedObsoleteFilenames = retainedObsoleteFilenames.ToList(),
                    BackupFilename = null
                };
                WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(retainedJournal, storageJsonSettings));
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "Retained authenticator cleanup state could not be preserved after a failed save.");
                return false;
            }
        }

        private static bool RecoverPendingStorageTransaction(string maDir, string manifestFilename, ICollection<string> retainedObsoleteFilenames = null)
        {
            string journalFilename = Path.Combine(maDir, StorageJournalFilename);
            lock (storageLock)
            {
                if (!File.Exists(journalFilename))
                    return true;

                try
                {
                    StorageTransactionJournal journal = JsonConvert.DeserializeObject<StorageTransactionJournal>(ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes), storageJsonSettings);
                    if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                        throw new InvalidDataException("The pending storage journal is invalid.");

                    List<string> created = journal.CreatedFilenames ?? new List<string>();
                    List<StorageTransactionReplacedFile> replaced = journal.ReplacedFiles ?? new List<StorageTransactionReplacedFile>();
                    List<string> obsolete = journal.ObsoleteFilenames ?? new List<string>();
                    List<string> retainedObsolete = journal.RetainedObsoleteFilenames ?? new List<string>();
                    foreach (string filename in created.Concat(obsolete).Concat(retainedObsolete))
                        ValidateStorageFilename(filename);

                    Dictionary<string, string> createdFileHashes = null;
                    if (journal.CreatedFileHashes != null)
                    {
                        createdFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, string> entry in journal.CreatedFileHashes)
                        {
                            ValidateStorageFilename(entry.Key);
                            if (String.IsNullOrWhiteSpace(entry.Value) || !IsBase64WithLength(entry.Value, 32) ||
                                !createdFileHashes.TryAdd(entry.Key, entry.Value))
                            {
                                throw new InvalidDataException("The pending storage journal contains an invalid created file hash entry.");
                            }
                        }

                        if (createdFileHashes.Count != created.Count ||
                            created.Any(filename => !createdFileHashes.ContainsKey(filename)))
                        {
                            throw new InvalidDataException("The pending storage journal created file hashes do not match its created file list.");
                        }
                    }
                    else if (created.Count > 0 && (!String.IsNullOrWhiteSpace(journal.PreviousManifestHash) || replaced.Count > 0))
                    {
                        throw new InvalidDataException("The pending storage journal is missing created file hashes.");
                    }

                    foreach (StorageTransactionReplacedFile record in replaced)
                    {
                        if (record == null ||
                            String.IsNullOrWhiteSpace(record.Filename) ||
                            String.IsNullOrWhiteSpace(record.BackupFilename) ||
                            String.IsNullOrWhiteSpace(record.OriginalHash) ||
                            String.IsNullOrWhiteSpace(record.ReplacementHash) ||
                            !IsBase64WithLength(record.OriginalHash, 32) ||
                            !IsBase64WithLength(record.ReplacementHash, 32))
                        {
                            throw new InvalidDataException("The pending storage journal contains an invalid replaced file entry.");
                        }
                        ValidateStorageFilename(record.Filename);
                        GetValidatedAccountBackupPath(maDir, record.BackupFilename);
                    }

                    string backupFilename = GetValidatedStorageBackupPath(maDir, journal.BackupFilename);
                    if (!File.Exists(manifestFilename) && !String.IsNullOrWhiteSpace(backupFilename) && File.Exists(backupFilename))
                    {
                        string backupContents = ReadTextWithLimit(backupFilename, MaximumManifestFileSizeBytes);
                        Manifest backupManifest = JsonConvert.DeserializeObject<Manifest>(backupContents, storageJsonSettings);
                        ValidateManifestEntries(backupManifest, maDir);
                        if (!String.IsNullOrWhiteSpace(journal.PreviousManifestHash) &&
                            !String.Equals(GetContentHash(backupContents), journal.PreviousManifestHash, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("The pending manifest backup does not match the expected pre-transaction manifest hash.");
                        }
                    }
                    if (!RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, false))
                        return false;

                    string currentManifestHash = File.Exists(manifestFilename)
                        ? GetContentHash(ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes))
                        : null;
                    bool manifestCommitted = currentManifestHash != null &&
                        String.Equals(currentManifestHash, journal.ManifestHash, StringComparison.Ordinal);
                    if (!manifestCommitted)
                    {
                        if (replaced.Count > 0 &&
                            (String.IsNullOrWhiteSpace(journal.PreviousManifestHash) ||
                             currentManifestHash == null ||
                             !String.Equals(currentManifestHash, journal.PreviousManifestHash, StringComparison.Ordinal)))
                        {
                            throw new InvalidDataException("An uncommitted storage transaction with replaced files does not match the current manifest.");
                        }

                        if (!String.IsNullOrWhiteSpace(journal.PreviousManifestHash) &&
                            (currentManifestHash == null ||
                             !String.Equals(currentManifestHash, journal.PreviousManifestHash, StringComparison.Ordinal)))
                        {
                            throw new InvalidDataException("An uncommitted storage transaction does not match the current manifest.");
                        }
                    }

                    Dictionary<string, ManifestEntry> manifestEntries = GetManifestEntriesByFilenameForRecovery(manifestFilename);
                    ValidateStorageTransactionFileOwnership(
                        maDir,
                        created,
                        replaced,
                        obsolete,
                        retainedObsolete,
                        backupFilename,
                        manifestEntries,
                        manifestCommitted);
                    if (manifestCommitted)
                    {
                        bool committedBackupDeleted = backupFilename == null ||
                            DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.");
                        if (!committedBackupDeleted)
                            return false;

                        bool committedReplacedBackupsDeleted = DeleteFilesBestEffort(
                            maDir,
                            replaced.Select(record => record.BackupFilename),
                            "A replaced authenticator backup could not be removed during storage recovery.");
                        if (!committedReplacedBackupsDeleted)
                            return false;

                        List<string> committedObsolete = obsolete.Concat(retainedObsolete).ToList();
                        if (!DeleteFilesBestEffort(maDir, committedObsolete, "An obsolete authenticator file could not be removed during storage recovery."))
                        {
                            List<string> remainingObsolete = committedObsolete
                                .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                                .ToList();
                            if (remainingObsolete.Count > 0)
                            {
                                // The manifest is committed and its pre-commit backups are already gone,
                                // so its newly created/replaced files are live. Store only still-existing
                                // pending deletions in retained_obsolete_filenames so already-deleted files
                                // do not block subsequent canonical saves or normalizations.
                                bool alreadyRetainedOnly =
                                    backupFilename == null &&
                                    created.Count == 0 &&
                                    (journal.CreatedFileHashes == null || journal.CreatedFileHashes.Count == 0) &&
                                    replaced.Count == 0 &&
                                    obsolete.Count == 0 &&
                                    String.IsNullOrWhiteSpace(journal.PreviousManifestHash) &&
                                    retainedObsolete.Count == remainingObsolete.Count;
                                if (!alreadyRetainedOnly)
                                {
                                    journal.PreviousManifestHash = null;
                                    journal.BackupFilename = null;
                                    journal.CreatedFilenames = new List<string>();
                                    journal.CreatedFileHashes = null;
                                    journal.ReplacedFiles = null;
                                    journal.ObsoleteFilenames = new List<string>();
                                    journal.RetainedObsoleteFilenames = remainingObsolete;
                                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings));
                                }
                                if (retainedObsoleteFilenames != null)
                                {
                                    foreach (string filename in remainingObsolete)
                                        retainedObsoleteFilenames.Add(filename);
                                }
                                return true;
                            }
                        }

                        bool committedJournalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.");
                        return committedJournalDeleted && !File.Exists(journalFilename);
                    }

                    if (!RestoreReplacedFilesIfNeeded(maDir, replaced))
                        return false;

                    if (replaced.Count > 0 &&
                        !DeleteFilesBestEffort(
                            maDir,
                            replaced.Select(record => record.BackupFilename),
                            "A replaced authenticator backup could not be removed during storage recovery."))
                    {
                        return false;
                    }

                    if (!DeleteCreatedFilesForRecovery(maDir, created, createdFileHashes))
                    {
                        // The manifest still contains the original data, but the
                        // created files are not safe to leave untracked. Keep the
                        // journal and fail closed so recovery can retry them later.
                        return false;
                    }

                    bool uncommittedBackupDeleted = backupFilename == null ||
                        DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.");
                    if (!uncommittedBackupDeleted)
                        return false;

                    if (retainedObsolete.Count > 0 &&
                        !DeleteFilesBestEffort(maDir, retainedObsolete, "A retained obsolete authenticator file could not be removed during storage recovery."))
                    {
                        List<string> remainingRetainedObsolete = retainedObsolete
                            .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                            .ToList();
                        if (remainingRetainedObsolete.Count > 0)
                        {
                            if (!PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, remainingRetainedObsolete))
                                return false;
                            if (retainedObsoleteFilenames != null)
                            {
                                foreach (string filename in remainingRetainedObsolete)
                                    retainedObsoleteFilenames.Add(filename);
                            }
                            return true;
                        }
                    }

                    bool journalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.");
                    return journalDeleted && !File.Exists(journalFilename);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "A pending storage transaction could not be recovered automatically.");
                    return false;
                }
            }
        }

        private static bool RebaseRetainedStorageTransaction(string maDir, string manifestContents)
        {
            string journalFilename = Path.Combine(maDir, StorageJournalFilename);
            lock (storageLock)
            {
                try
                {
                    if (!File.Exists(journalFilename))
                        return true;

                    StorageTransactionJournal journal = JsonConvert.DeserializeObject<StorageTransactionJournal>(ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes), storageJsonSettings);
                    if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                        throw new InvalidDataException("The retained storage journal is invalid.");

                    List<string> pendingRetainedObsolete = (journal.ObsoleteFilenames ?? new List<string>())
                        .Concat(journal.RetainedObsoleteFilenames ?? new List<string>())
                        .Where(filename => !String.IsNullOrWhiteSpace(filename))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (string filename in pendingRetainedObsolete)
                        ValidateStorageFilename(filename);
                    pendingRetainedObsolete = pendingRetainedObsolete
                        .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                        .ToList();

                    string backupFilename = GetValidatedStorageBackupPath(maDir, journal.BackupFilename);
                    if (backupFilename != null &&
                        !DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed while rebasing retained storage cleanup."))
                    {
                        return false;
                    }

                    if (journal.ReplacedFiles != null && journal.ReplacedFiles.Count > 0)
                    {
                        foreach (StorageTransactionReplacedFile record in journal.ReplacedFiles)
                        {
                            if (record == null || String.IsNullOrWhiteSpace(record.BackupFilename))
                                throw new InvalidDataException("The retained storage journal contains an invalid replaced file entry.");
                            GetValidatedAccountBackupPath(maDir, record.BackupFilename);
                        }

                        if (!DeleteFilesBestEffort(
                            maDir,
                            journal.ReplacedFiles.Select(record => record.BackupFilename),
                            "A replaced authenticator backup could not be removed while rebasing retained storage cleanup."))
                        {
                            return false;
                        }
                    }

                    if (pendingRetainedObsolete.Count == 0)
                        return DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed while rebasing retained storage cleanup.") && !File.Exists(journalFilename);

                    journal.ManifestHash = GetContentHash(manifestContents);
                    journal.PreviousManifestHash = null;
                    journal.CreatedFilenames = new List<string>();
                    journal.CreatedFileHashes = null;
                    journal.ReplacedFiles = null;
                    journal.ObsoleteFilenames = new List<string>();
                    journal.RetainedObsoleteFilenames = pendingRetainedObsolete;
                    journal.BackupFilename = null;
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings));
                    return true;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "The retained storage cleanup could not be associated with the saved manifest.");
                    return false;
                }
            }
        }

        private static bool DeleteCreatedFilesForRecovery(
            string maDir,
            IReadOnlyList<string> createdFilenames,
            IReadOnlyDictionary<string, string> createdFileHashes)
        {
            if (createdFilenames == null || createdFilenames.Count == 0)
                return true;

            if (createdFileHashes == null)
            {
                return DeleteFilesBestEffort(
                    maDir,
                    createdFilenames,
                    "A temporary authenticator file could not be removed during storage recovery.");
            }

            bool succeeded = true;
            foreach (string filename in createdFilenames)
            {
                string fullPath = Path.Combine(maDir, filename);
                try
                {
                    if (!File.Exists(fullPath))
                        continue;

                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        DiagnosticErrorLogger.Log(
                            "Authenticator storage",
                            new InvalidDataException("An uncommitted created file path is a link and was not deleted during recovery."),
                            "Preserved an unrecognized link at a planned authenticator path during recovery.");
                        continue;
                    }

                    // Hold a non-writable handle across hashing and deletion so no writer
                    // can replace the file between verification and unlink.
                    using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan))
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        string actualHash = Convert.ToBase64String(sha256.ComputeHash(stream));
                        if (createdFileHashes.TryGetValue(filename, out string expectedHash) &&
                            String.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                        {
                            File.Delete(fullPath);
                        }
                        else
                        {
                            DiagnosticErrorLogger.Log(
                                "Authenticator storage",
                                new IOException("An uncommitted created file path contains different bytes than staged by this transaction."),
                                "Preserved an existing file that was not created by the uncommitted storage transaction.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "A temporary authenticator file could not be removed during storage recovery.");
                }
            }

            return succeeded;
        }

        private static bool RestoreReplacedFilesIfNeeded(string maDir, IReadOnlyList<StorageTransactionReplacedFile> replacedFiles)
        {
            if (replacedFiles == null || replacedFiles.Count == 0)
                return true;

            foreach (StorageTransactionReplacedFile record in replacedFiles)
            {
                string backupPath = Path.Combine(maDir, record.BackupFilename);
                string targetPath = Path.Combine(maDir, record.Filename);
                try
                {
                    if (File.Exists(backupPath))
                    {
                        string backupHash = GetFileBytesHash(backupPath);
                        if (!String.Equals(backupHash, record.OriginalHash, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("A replaced authenticator backup did not match the expected original file hash.");
                        }

                        if (File.Exists(targetPath))
                        {
                            string targetHash = GetFileBytesHash(targetPath);
                            if (String.Equals(targetHash, record.OriginalHash, StringComparison.Ordinal))
                                continue;

                            if (!String.Equals(targetHash, record.ReplacementHash, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException("A live authenticator file did not match the uncommitted replacement recorded in the storage journal.");
                            }
                        }
                        else if (Path.Exists(targetPath))
                        {
                            throw new InvalidDataException("The target authenticator path is not a regular file.");
                        }

                        RestoreFileBytesAtomically(backupPath, targetPath);
                    }
                    else
                    {
                        if (Path.Exists(backupPath))
                            throw new InvalidDataException("A replaced authenticator backup path is not a regular file.");
                        if (!File.Exists(targetPath))
                            throw new FileNotFoundException("The expected original authenticator file is missing during rollback.", targetPath);

                        string targetHash = GetFileBytesHash(targetPath);
                        if (!String.Equals(targetHash, record.OriginalHash, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("A replaced authenticator file could not be verified as original because its backup is missing.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "A replaced authenticator file could not be restored safely.");
                    return false;
                }
            }

            return true;
        }

        private static bool RestoreManifestBackupIfNeeded(string manifestFilename, string backupFilename, bool overwriteManifest)
        {
            if (String.IsNullOrWhiteSpace(backupFilename) || !File.Exists(backupFilename))
                return !overwriteManifest || File.Exists(manifestFilename);
            if (!overwriteManifest && File.Exists(manifestFilename))
                return true;

            try
            {
                WriteAllTextAtomically(manifestFilename, ReadTextWithLimit(backupFilename, MaximumManifestFileSizeBytes));
                return File.Exists(manifestFilename);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The manifest backup could not be restored safely.");
                return false;
            }
        }

        private static string GetValidatedStorageBackupPath(string maDir, string filename)
        {
            if (String.IsNullOrWhiteSpace(filename))
                return null;

            return GetValidatedGuidBackupPath(
                maDir,
                filename,
                StorageBackupFilenamePrefix,
                "A storage transaction contained an invalid manifest backup filename.");
        }

        private static string GetValidatedAccountBackupPath(string maDir, string filename, ulong? expectedSteamId = null)
        {
            const string errorMessage = "A storage transaction contained an invalid account backup filename.";
            if (String.IsNullOrWhiteSpace(filename) ||
                !String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) ||
                !filename.StartsWith(AccountBackupFilenamePrefix, StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(StorageBackupFilenameSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(errorMessage);
            }

            int innerStart = AccountBackupFilenamePrefix.Length;
            int innerLength = filename.Length - innerStart - StorageBackupFilenameSuffix.Length;
            if (innerLength <= 33)
                throw new InvalidDataException(errorMessage);

            string inner = filename.Substring(innerStart, innerLength);
            int separatorIndex = inner.IndexOf('.');
            if (separatorIndex <= 0 || separatorIndex != inner.LastIndexOf('.'))
                throw new InvalidDataException(errorMessage);

            string steamIdText = inner.Substring(0, separatorIndex);
            string guidText = inner.Substring(separatorIndex + 1);
            if (!UInt64.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsedSteamId) ||
                parsedSteamId == 0 ||
                !String.Equals(parsedSteamId.ToString(CultureInfo.InvariantCulture), steamIdText, StringComparison.Ordinal) ||
                guidText.Length != 32 ||
                !Guid.TryParseExact(guidText, "N", out _))
            {
                throw new InvalidDataException(errorMessage);
            }

            if (expectedSteamId.HasValue && parsedSteamId != expectedSteamId.Value)
                throw new InvalidDataException("A storage transaction account backup did not match the target account identity.");

            return Path.Combine(maDir, filename);
        }

        private static string GetValidatedGuidBackupPath(string maDir, string filename, string prefix, string errorMessage)
        {
            if (!String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) ||
                !filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(StorageBackupFilenameSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(errorMessage);

            int guidStart = prefix.Length;
            int guidLength = filename.Length - guidStart - StorageBackupFilenameSuffix.Length;
            if (guidLength != 32 || !Guid.TryParseExact(filename.Substring(guidStart, guidLength), "N", out _))
                throw new InvalidDataException(errorMessage);

            return Path.Combine(maDir, filename);
        }

        private static Dictionary<string, ManifestEntry> GetManifestEntriesByFilenameForRecovery(string manifestFilename)
        {
            Dictionary<string, ManifestEntry> entries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(manifestFilename))
                return entries;

            Manifest currentManifest = JsonConvert.DeserializeObject<Manifest>(
                ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes),
                storageJsonSettings);
            ValidateManifestEntries(currentManifest, Path.GetDirectoryName(manifestFilename));

            foreach (ManifestEntry entry in currentManifest.Entries)
            {
                if (!entries.TryAdd(entry.Filename, entry))
                    throw new InvalidDataException("The current account manifest contains duplicate account filenames.");
            }

            return entries;
        }

        private static void ValidateStorageTransactionFileOwnership(
            string maDir,
            IEnumerable<string> created,
            IEnumerable<StorageTransactionReplacedFile> replaced,
            IEnumerable<string> obsolete,
            IEnumerable<string> retainedObsolete,
            string backupFilename,
            IReadOnlyDictionary<string, ManifestEntry> manifestEntries,
            bool manifestCommitted)
        {
            HashSet<string> transactionFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string filename in created.Concat(obsolete).Concat(retainedObsolete))
            {
                if (!transactionFilenames.Add(filename))
                    throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
            }

            foreach (StorageTransactionReplacedFile record in replaced)
            {
                if (!transactionFilenames.Add(record.Filename) || !transactionFilenames.Add(record.BackupFilename))
                    throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
                if (!manifestEntries.TryGetValue(record.Filename, out ManifestEntry entry))
                    throw new InvalidDataException("A storage transaction referenced a replaced file not present in the manifest.");
                GetValidatedAccountBackupPath(maDir, record.BackupFilename, entry.SteamID);
            }

            if (backupFilename != null && !transactionFilenames.Add(Path.GetFileName(backupFilename)))
                throw new InvalidDataException("A storage transaction reused its manifest backup filename.");

            IEnumerable<string> filesThatWouldBeDeleted = (manifestCommitted ? obsolete : created).Concat(retainedObsolete);
            foreach (string filename in filesThatWouldBeDeleted)
            {
                if (manifestEntries.ContainsKey(filename))
                    throw new InvalidDataException("A storage transaction attempted to delete a live authenticator file.");
            }
        }

        private enum AtomicWriteMode
        {
            CreateOrReplace,
            CreateNewOnly,
            ReplaceExistingOnly
        }

        private static void WriteAllTextAtomically(
            string filename,
            string contents,
            string backupFilename = null,
            AtomicWriteMode mode = AtomicWriteMode.CreateOrReplace)
        {
            string temporaryFilename = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporaryFilename, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (mode == AtomicWriteMode.CreateNewOnly)
                {
                    File.Move(temporaryFilename, filename);
                }
                else if (mode == AtomicWriteMode.ReplaceExistingOnly || File.Exists(filename))
                {
                    ReplaceFileAtomically(temporaryFilename, filename, backupFilename);
                }
                else
                {
                    File.Move(temporaryFilename, filename);
                }
            }
            finally
            {
                if (File.Exists(temporaryFilename))
                    File.Delete(temporaryFilename);
            }
        }

        private static void ReplaceFileAtomically(string temporaryFilename, string destinationFilename, string backupFilename)
        {
            try
            {
                File.Replace(temporaryFilename, destinationFilename, backupFilename);
            }
            catch (NotSupportedException ex)
            {
                throw new NotSupportedException("The selected data folder does not support the atomic file replacement required to safely save authenticator data.", ex);
            }
            catch (IOException ex) when (IsAtomicReplacementUnsupported(ex))
            {
                throw new NotSupportedException("The selected data folder does not support the atomic file replacement required to safely save authenticator data.", ex);
            }
        }

        private static string CreateBackupFilename(string maDir)
        {
            return Path.Combine(maDir, StorageBackupFilenamePrefix + Guid.NewGuid().ToString("N") + StorageBackupFilenameSuffix);
        }

        private static string CreateAccountBackupFilename(ulong steamId)
        {
            if (steamId == 0)
                throw new ArgumentOutOfRangeException(nameof(steamId));
            return AccountBackupFilenamePrefix + steamId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + StorageBackupFilenameSuffix;
        }

        private static bool IsAtomicReplacementUnsupported(IOException exception)
        {
            const int ErrorInvalidFunction = unchecked((int)0x80070001);
            const int ErrorNotSupported = unchecked((int)0x80070032);
            return exception.HResult == ErrorInvalidFunction || exception.HResult == ErrorNotSupported;
        }

        private static void RestoreFileBytesAtomically(string sourcePath, string destinationPath)
        {
            FileInfo sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
                throw new FileNotFoundException("The expected backup storage file does not exist.", sourcePath);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A storage backup file uses an unsupported link.");

            if (File.Exists(destinationPath))
            {
                if ((File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A storage file uses an unsupported link.");
            }
            else if (Path.Exists(destinationPath))
            {
                throw new InvalidDataException("The destination authenticator path is not a regular file.");
            }

            string temporaryFilename = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (FileStream destinationStream = new FileStream(temporaryFilename, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    sourceStream.CopyTo(destinationStream, 81920);
                    destinationStream.Flush(true);
                }

                if (File.Exists(destinationPath))
                    ReplaceFileAtomically(temporaryFilename, destinationPath, null);
                else
                    File.Move(temporaryFilename, destinationPath);
            }
            finally
            {
                if (File.Exists(temporaryFilename))
                    File.Delete(temporaryFilename);
            }
        }

        private static string GetFileBytesHash(string filename)
        {
            FileInfo fileInfo = new FileInfo(filename);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("The expected storage file does not exist.", filename);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A storage file uses an unsupported link.");

            using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(stream));
            }
        }

        private static string GetContentHash(string contents)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(contents)));
            }
        }

        private static bool IsValidAutomaticImportAccount(SteamGuardAccount account)
        {
            return account != null && account.FullyEnrolled && account.Session != null && account.Session.SteamID != 0 &&
                !String.IsNullOrWhiteSpace(account.AccountName) && account.AccountName.Length <= MaximumAccountNameLength &&
                IsValidAuthenticatorSecret(account.SharedSecret) &&
                IsValidAuthenticatorSecret(account.IdentitySecret) &&
                !String.IsNullOrWhiteSpace(account.DeviceID) &&
                account.DeviceID.Length <= MaximumDeviceIdLength;
        }

        private static bool IsValidAuthenticatorSecret(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > MaximumAuthenticatorSecretTextLength)
                return false;

            try
            {
                byte[] decoded = Convert.FromBase64String(value);
                return decoded.Length == ExpectedAuthenticatorSecretBytes;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string ReadTextWithLimit(string filename, long maximumBytes)
        {
            FileInfo fileInfo = new FileInfo(filename);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("The expected storage file does not exist.", filename);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A storage file uses an unsupported link.");
            if (fileInfo.Length > maximumBytes)
                throw new InvalidDataException("A storage file is larger than the supported size limit.");

            using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
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

        private static string GetManifestFilePath(string maDir, ManifestEntry entry)
        {
            if (entry == null)
                throw new InvalidDataException("The account manifest contains an empty account entry.");

            ValidateStorageDirectory(maDir);
            ValidateManifestFilename(entry.Filename);
            string root = Path.GetFullPath(maDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, entry.Filename));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An account manifest entry points outside the account-data directory.");

            return candidate;
        }

        private static void ValidateStorageDirectory(string directoryPath)
        {
            string fullPath = Path.GetFullPath(directoryPath);
            DirectoryInfo current = new DirectoryInfo(fullPath);
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The account-data directory uses an unsupported link.");

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        private static void ValidateManifestEntries(Manifest manifest, string maDir)
        {
            if (manifest == null || manifest.Entries == null || manifest.Entries.Count > MaximumManifestEntries)
                throw new InvalidDataException("The account manifest contains an invalid entries list.");

            HashSet<ulong> steamIds = new HashSet<ulong>();
            HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestEntry entry in manifest.Entries)
            {
                if (entry == null || entry.SteamID == 0 || !steamIds.Add(entry.SteamID))
                    throw new InvalidDataException("The account manifest contains an invalid or duplicate Steam identity.");

                ValidateManifestFilename(entry.Filename);
                if (!filenames.Add(entry.Filename))
                    throw new InvalidDataException("The account manifest contains duplicate account filenames.");

                GetManifestFilePath(maDir, entry);
                bool hasSalt = !String.IsNullOrWhiteSpace(entry.Salt);
                bool hasIv = !String.IsNullOrWhiteSpace(entry.IV);
                if (manifest.Encrypted)
                {
                    if (!hasSalt || !hasIv || !IsBase64WithLength(entry.Salt, 8) || !IsBase64WithLength(entry.IV, 16))
                        throw new InvalidDataException("An encrypted account entry is missing valid encryption metadata.");
                }
                else if (hasSalt || hasIv)
                {
                    throw new InvalidDataException("An unencrypted account entry contains encryption metadata.");
                }
            }
        }

        private static void ValidateManifestFilename(string filename)
        {
            if (String.IsNullOrWhiteSpace(filename) ||
                filename.Length > 255 ||
                filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) ||
                !filename.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The account manifest contains an invalid account filename.");
            }
        }

        private static bool IsBase64WithLength(string value, int expectedLength)
        {
            try
            {
                return Convert.FromBase64String(value).Length == expectedLength;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void ValidateStorageFilename(string filename)
        {
            try
            {
                ValidateManifestFilename(filename);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("A storage transaction contained an invalid filename.");
            }
        }

        private static bool DeleteFilesBestEffort(string directory, IEnumerable<string> filenames, string context)
        {
            bool succeeded = true;
            foreach (string filename in filenames)
            {
                try
                {
                    string fullPath = Path.Combine(directory, filename);
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, context);
                }
            }
            return succeeded;
        }

        private static bool DeleteFileBestEffort(string filename, string context)
        {
            try
            {
                if (File.Exists(filename))
                {
                    Func<string, bool> beforeDelete = BeforeDeleteFileForTesting;
                    if (beforeDelete != null && !beforeDelete(filename))
                        throw new IOException("Simulated file deletion failure for testing.");
                    File.Delete(filename);
                }
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, context);
                return false;
            }
        }

        private sealed class StorageTransactionReplacedFile
        {
            [JsonProperty("filename")]
            public string Filename { get; set; }

            [JsonProperty("backup_filename")]
            public string BackupFilename { get; set; }

            [JsonProperty("original_hash")]
            public string OriginalHash { get; set; }

            [JsonProperty("replacement_hash")]
            public string ReplacementHash { get; set; }
        }

        private sealed class StorageTransactionJournal
        {
            [JsonProperty("manifest_hash")]
            public string ManifestHash { get; set; }

            [JsonProperty("previous_manifest_hash", NullValueHandling = NullValueHandling.Ignore)]
            public string PreviousManifestHash { get; set; }

            [JsonProperty("created_filenames")]
            public List<string> CreatedFilenames { get; set; }

            [JsonProperty("created_file_hashes", NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, string> CreatedFileHashes { get; set; }

            [JsonProperty("replaced_files", NullValueHandling = NullValueHandling.Ignore)]
            public List<StorageTransactionReplacedFile> ReplacedFiles { get; set; }

            [JsonProperty("obsolete_filenames")]
            public List<string> ObsoleteFilenames { get; set; }

            [JsonProperty("retained_obsolete_filenames", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> RetainedObsoleteFilenames { get; set; }

            [JsonProperty("backup_filename")]
            public string BackupFilename { get; set; }
        }

        private void RecomputeExistingEntries()
        {
            List<ManifestEntry> newEntries = new List<ManifestEntry>();
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");

            foreach (var entry in this.Entries)
            {
                string filename = GetManifestFilePath(maDir, entry);
                if (File.Exists(filename))
                {
                    newEntries.Add(entry);
                }
            }

            this.Entries = newEntries;

            if (this.Entries.Count == 0)
            {
                this.Encrypted = false;
            }
        }

        public void NormalizeLoginActionSettings()
        {
            if (LoginActionMode != LoginActionModes.Manual &&
                LoginActionMode != LoginActionModes.ApprovePersistent &&
                LoginActionMode != LoginActionModes.Deny)
            {
                LoginActionMode = LoginActionModes.Manual;
            }

            LoginActionAutoAllowIp = (LoginActionAutoAllowIp ?? String.Empty).Trim();
            bool hasValidAdditionalIp = String.IsNullOrEmpty(LoginActionAutoAllowIp) ||
                (IPAddress.TryParse(LoginActionAutoAllowIp, out IPAddress parsedIp) &&
                 parsedIp.AddressFamily == AddressFamily.InterNetwork);
            if (LoginActionMode != LoginActionModes.Deny || !hasValidAdditionalIp)
            {
                LoginActionAutoAllowIpEnabled = false;
                LoginActionAutoAllowCurrentDeviceIp = false;
            }
            else if (!LoginActionAutoAllowIpEnabled)
            {
                LoginActionAutoAllowCurrentDeviceIp = false;
            }
        }

        public void NormalizeTradeConfirmationSettings()
        {
            TradeConfirmationCheckInterval = Math.Clamp(TradeConfirmationCheckInterval, 3, 3600);
            TradeConfirmationSelectedTab = TradeConfirmationClassifier.NormalizeSelectableTab(TradeConfirmationSelectedTab);
        }

        private bool MigrateLegacyTradeConfirmationSettings(JObject manifestJson)
        {
            bool hasCurrentSettings = manifestJson.Properties().Any(property =>
                String.Equals(property.Name, "trade_confirmation_custom_interval_enabled", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(property.Name, "trade_confirmation_check_interval", StringComparison.OrdinalIgnoreCase));
            if (hasCurrentSettings)
            {
                return false;
            }

            JToken periodicCheckingToken = manifestJson.GetValue("periodic_checking", StringComparison.OrdinalIgnoreCase);
            if (periodicCheckingToken?.Type != JTokenType.Boolean)
            {
                return false;
            }

            TradeConfirmationCustomIntervalEnabled = periodicCheckingToken.Value<bool>();
            if (TradeConfirmationCustomIntervalEnabled)
            {
                JToken legacyIntervalToken = manifestJson.GetValue("periodic_checking_interval", StringComparison.OrdinalIgnoreCase);
                if (legacyIntervalToken?.Type == JTokenType.Integer &&
                    Int32.TryParse(legacyIntervalToken.ToString(), out int legacyInterval))
                {
                    TradeConfirmationCheckInterval = legacyInterval;
                }
            }
            else
            {
                TradeConfirmationCheckInterval = 15;
            }

            // The monitor now always scans every account, so the former
            // periodic_checking_checkall setting intentionally has no equivalent.
            return true;
        }

        public bool MoveEntry(int from, int to)
        {
            return MoveEntryWithResult(from, to).Succeeded;
        }

        public StorageResult MoveEntryBySteamIdOffsetWithResult(ulong steamId, int offset)
        {
            if (steamId == 0)
                return StorageResult.Failure(StorageFailureKind.Validation, "The selected account identity is invalid.");

            lock (storageLock)
            {
                if (Entries == null)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The selected account position is out of range.");

                int from = Entries.FindIndex(entry => entry != null && entry.SteamID == steamId);
                if (from < 0)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is no longer available to reorder.");
                if (offset == 0)
                    return StorageResult.Success();

                int to = from + offset;
                if (to < 0 || to >= Entries.Count)
                    return StorageResult.Success();

                return MoveEntryWithResult(from, to);
            }
        }

        public StorageResult MoveEntryWithResult(int from, int to)
        {
            lock (storageLock)
            {
                if (Entries == null || from < 0 || to < 0 || from >= Entries.Count || to >= Entries.Count)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The selected account position is out of range.");
                if (from == to)
                    return StorageResult.Success();

                try
                {
                    Manifest stagedManifest = CloneForStorage();
                    ManifestEntry selected = stagedManifest.Entries[from];
                    stagedManifest.Entries.RemoveAt(from);
                    stagedManifest.Entries.Insert(to, selected);
                    StorageResult result = CommitStorageTransaction(
                        stagedManifest,
                        new Dictionary<string, string>(),
                        Enumerable.Empty<string>());
                    if (result.Succeeded)
                        CopyStorageStateFrom(stagedManifest);
                    return result;
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Manifest, "The account order could not be saved.", ex);
                }
            }
        }

        public class ManifestEntry
        {
            [JsonProperty("encryption_iv")]
            public string IV { get; set; }

            [JsonProperty("encryption_salt")]
            public string Salt { get; set; }

            [JsonProperty("filename")]
            public string Filename { get; set; }

            [JsonProperty("steamid")]
            public ulong SteamID { get; set; }

            [JsonProperty("session_needs_renewal")]
            public bool SessionNeedsRenewal { get; set; }
        }
    }
}
