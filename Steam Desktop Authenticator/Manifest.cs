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

        [JsonProperty("allow_saved_steam_passwords", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool AllowSavedSteamPasswords { get; set; } = false;

        [JsonProperty("credentials_store_filename", NullValueHandling = NullValueHandling.Ignore)]
        public string CredentialsStoreFilename { get; set; }

        [JsonIgnore]
        public bool CredentialsStoreMissingWarning { get; private set; }

        private static Manifest _manifest { get; set; }
        private static readonly object storageLock = new object();
        private long storageRevision;
        // Generation-scoped metadata-only cache (never caches plaintext passwords)
        // so 1-second UI/monitor eligibility checks do not re-run 600k PBKDF2.
        private string cachedCredentialMetadataFilename;
        private string cachedCredentialMetadataPassKeyHash;
        private Dictionary<ulong, SavedCredentialStatus> cachedCredentialMetadata;
        private bool cachedCredentialMetadataFailed;
        // Per-instance HMAC proof of a verified passkey (never stores plaintext passkey).
        // Avoids re-decrypting every account file on ordinary SaveAccount writes and ensures
        // an unrelated unreadable account file cannot block healthy encrypted account saves.
        private readonly byte[] passkeyProofKey = RandomNumberGenerator.GetBytes(32);
        private byte[] verifiedPasskeyProof;
        // Refresh persistence only needs to reject changes to the account it
        // captured. A manifest-wide revision would make an unrelated account
        // update abort an otherwise safe refresh.
        private readonly Dictionary<ulong, long> accountStorageRevisions = new Dictionary<ulong, long>();
        private const string StorageJournalFilename = ".asda-storage-transaction.json";
        private const string SettingsBackupFilename = ".manifest.settings.bak";
        private const string StorageBackupFilenamePrefix = ".manifest.";
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

            StoragePathGuard.PinnedStorageDirectory pinnedDir;
            string validatedRoot;
            try
            {
                pinnedDir = StoragePathGuard.TryPinExistingDirectory(maDir, out validatedRoot);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The account-data directory is not a supported local directory.");
                throw new ManifestStorageDirectoryException();
            }

            if (pinnedDir == null)
            {
                // If there's no config dir, return a fresh manifest.
                _manifest = GenerateNewManifest(false);
                return _manifest;
            }

            using (pinnedDir)
            {
                string expectedCanonicalDir = pinnedDir.CanonicalDirectoryPath;

                if (!RecoverPendingStorageTransaction(maDir, manifestFile, expectedCanonicalDir: expectedCanonicalDir, validatedRoot: validatedRoot))
                    throw new ManifestRecoveryException();
                if (!RestoreManifestBackupIfNeeded(manifestFile, Path.Combine(maDir, SettingsBackupFilename), false, expectedCanonicalDir))
                    throw new ManifestParseException();

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
                    string manifestContents = StoragePathGuard.ReadTextWithLimit(manifestFile, MaximumManifestFileSizeBytes, expectedCanonicalDir);
                    JObject manifestJson;
                    using (StringReader stringReader = new StringReader(manifestContents))
                    using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = storageJsonSettings.MaxDepth, DateParseHandling = DateParseHandling.None })
                    {
                        manifestJson = JObject.Load(jsonReader);
                    }
                    Manifest loadedManifest = manifestJson.ToObject<Manifest>(JsonSerializer.Create(storageJsonSettings));
                    if (loadedManifest == null || loadedManifest.Entries == null)
                        throw new InvalidDataException("The account manifest does not contain a valid entries list.");

                    ValidateManifestState(loadedManifest.Entries, loadedManifest.Encrypted, validatedRoot, loadedManifest.CredentialsStoreFilename);
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
                        loadedManifest.AllowSavedSteamPasswords = false;
                        loadedManifest.CredentialsStoreFilename = null;
                        loadedManifest.Save();
                    }

                    if (!loadedManifest.Encrypted &&
                        (loadedManifest.AllowSavedSteamPasswords || !String.IsNullOrWhiteSpace(loadedManifest.CredentialsStoreFilename)))
                    {
                        loadedManifest.AllowSavedSteamPasswords = false;
                        loadedManifest.CredentialsStoreFilename = null;
                        loadedManifest.Save();
                    }

                    int entriesBeforePrune = loadedManifest.Entries.Count;
                    loadedManifest.RecomputeExistingEntries(validatedRoot);
                    if (entriesBeforePrune > 0 && loadedManifest.Entries.Count == 0)
                    {
                        loadedManifest.Encrypted = false;
                        loadedManifest.AllowSavedSteamPasswords = false;
                        loadedManifest.CredentialsStoreFilename = null;
                        loadedManifest.Save();
                    }

                    if (!String.IsNullOrWhiteSpace(loadedManifest.CredentialsStoreFilename))
                    {
                        string credPath = ResolveValidatedCredentialStoreFilePath(validatedRoot, loadedManifest.CredentialsStoreFilename);
                        if (!File.Exists(credPath))
                            loadedManifest.CredentialsStoreMissingWarning = true;
                    }

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
                        SweepOrphanedCredentialStoresLocked(maDir, loadedManifest.CredentialsStoreFilename);
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
            StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
            if (!Directory.Exists(maDir))
            {
                return newManifest;
            }

            string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
            DirectoryInfo dir = new DirectoryInfo(maDir);
            var files = dir.GetFiles();

            foreach (var file in files)
            {
                if (!String.Equals(file.Extension, ".maFile", StringComparison.OrdinalIgnoreCase)) continue;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new MaFileEncryptedException();

                string contents = StoragePathGuard.ReadTextWithLimit(file.FullName, MaximumAccountFileSizeBytes, expectedCanonicalDir);
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

        internal Action OnBeforeVerifyPasskeyProofPublishForTest { get; set; }
        internal Action OnBeforeGetAllAccountsFileReadForTest { get; set; }

        public SteamAuth.SteamGuardAccount[] GetAllAccounts(string passKey = null, int limit = -1)
        {
            if (limit < -1)
                throw new ArgumentOutOfRangeException(nameof(limit));

            for (int attempt = 0; attempt < 3; attempt++)
            {
                ManifestEntry[] entriesSnapshot;
                bool isEncrypted;
                string credentialsStoreFilenameSnapshot;
                long revisionBefore;
                lock (storageLock)
                {
                    isEncrypted = this.Encrypted;
                    if (limit == 0 || (passKey == null && isEncrypted))
                        return Array.Empty<SteamGuardAccount>();
                    if (this.Entries == null)
                        throw new InvalidDataException("The account manifest contains an invalid entries list.");
                    entriesSnapshot = this.Entries.ToArray();
                    credentialsStoreFilenameSnapshot = this.CredentialsStoreFilename;
                    revisionBefore = this.storageRevision;
                }

                SteamAuth.SteamGuardAccount[] accounts = ReadAccountsFromSnapshot(
                    entriesSnapshot,
                    isEncrypted,
                    credentialsStoreFilenameSnapshot,
                    passKey,
                    limit);

                lock (storageLock)
                {
                    if (this.storageRevision == revisionBefore)
                        return accounts;
                }
            }

            // Under sustained concurrent writes, hold storageLock for one consistent read.
            lock (storageLock)
            {
                if (limit == 0 || (passKey == null && this.Encrypted))
                    return Array.Empty<SteamGuardAccount>();
                if (this.Entries == null)
                    throw new InvalidDataException("The account manifest contains an invalid entries list.");

                return ReadAccountsFromSnapshot(
                    this.Entries.ToArray(),
                    this.Encrypted,
                    this.CredentialsStoreFilename,
                    passKey,
                    limit);
            }
        }

        private SteamAuth.SteamGuardAccount[] ReadAccountsFromSnapshot(
            ManifestEntry[] entriesSnapshot,
            bool isEncrypted,
            string credentialsStoreFilenameSnapshot,
            string passKey,
            int limit)
        {
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
            string[] entryPaths = ValidateAndResolveManifestEntries(
                entriesSnapshot,
                isEncrypted,
                validatedRoot,
                credentialsStoreFilenameSnapshot);
            if (entriesSnapshot.Length == 0 || !Directory.Exists(maDir))
                return Array.Empty<SteamGuardAccount>();

            string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
            OnBeforeGetAllAccountsFileReadForTest?.Invoke();

            List<SteamAuth.SteamGuardAccount> accounts = new List<SteamAuth.SteamGuardAccount>();
            for (int i = 0; i < entriesSnapshot.Length; i++)
            {
                ManifestEntry entry = entriesSnapshot[i];
                try
                {
                    string fileText = StoragePathGuard.ReadTextWithLimit(entryPaths[i], MaximumAccountFileSizeBytes, expectedCanonicalDir);
                    if (isEncrypted)
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
                    string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                    string[] entryPaths = ValidateAndResolveManifestEntries(
                        this.Entries,
                        this.Encrypted,
                        validatedRoot,
                        this.CredentialsStoreFilename);
                    string expectedCanonicalDir = null;

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

                        string sourcePath = entryPaths[i];
                        string canonicalPath = Path.Combine(maDir, canonicalFilename);

                        // A pre-existing canonical file may be an independently
                        // imported account. Never overwrite it while migrating a
                        // GUID file; leaving the GUID reference is recoverable.
                        if (referencedFilenames.Contains(canonicalFilename) || File.Exists(canonicalPath))
                        {
                            DiagnosticErrorLogger.Log(
                                "Authenticator storage",
                                new IOException("The canonical maFile filename is already in use."),
                                "A non-canonical authenticator file was kept because its canonical filename is occupied.");
                            continue;
                        }

                        if (expectedCanonicalDir == null)
                            expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);

                        string contents = StoragePathGuard.ReadTextWithLimit(sourcePath, MaximumAccountFileSizeBytes, expectedCanonicalDir);
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

                StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
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
                        string contents = StoragePathGuard.ReadTextWithLimit(path, MaximumAccountFileSizeBytes, expectedCanonicalDir);
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
                if (this.Encrypted && !VerifyPasskey(passKey))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid. The files were not imported.");

                try
                {
                    string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                    string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                    ValidateManifestState(this.Entries, this.Encrypted, validatedRoot, this.CredentialsStoreFilename);
                    string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                    HashSet<ulong> managedSteamIds = new HashSet<ulong>(Entries.Select(entry => entry.SteamID));
                    HashSet<string> sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<SteamGuardAccount> validatedAccounts = new List<SteamGuardAccount>();

                    foreach (UnmanagedMaFileCandidate candidate in candidates)
                    {
                        if (candidate == null || String.IsNullOrWhiteSpace(candidate.FileName) ||
                            !sourceNames.Add(candidate.FileName) || managedSteamIds.Contains(candidate.SteamID))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files is no longer available for import.");

                        string path = ResolveValidatedManifestFilePath(validatedRoot, candidate.FileName);
                        string contents = StoragePathGuard.ReadTextWithLimit(path, MaximumAccountFileSizeBytes, expectedCanonicalDir);
                        if (!String.Equals(GetContentHash(contents), candidate.ContentHash, StringComparison.Ordinal))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files changed before it could be imported.");

                        SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(contents, storageJsonSettings);
                        if (account?.Session == null || account.Session.SteamID == 0 || account.Session.SteamID != candidate.SteamID ||
                            !IsValidAutomaticImportAccount(account))
                            return StorageResult.Failure(StorageFailureKind.Validation, "One of the selected authenticator files is no longer valid.");
                        if (!managedSteamIds.Add(account.Session.SteamID))
                            return StorageResult.Failure(StorageFailureKind.Validation, "Two selected authenticator files use the same Steam identity.");
                        validatedAccounts.Add(account);
                    }

                    Manifest stagedManifest = CloneForStorage();
                    Dictionary<string, string> stagedFiles = new Dictionary<string, string>();
                    List<string> obsoleteFiles = candidates.Select(candidate => candidate.FileName).ToList();
                    foreach (SteamGuardAccount account in validatedAccounts)
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

                        string filename = account.Session.SteamID.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".maFile";
                        stagedFiles.Add(filename, contents);
                        stagedManifest.Entries.Add(new ManifestEntry
                        {
                            SteamID = account.Session.SteamID,
                            IV = iv,
                            Salt = salt,
                            Filename = filename,
                            SessionNeedsRenewal = account.Session.IsRefreshTokenExpired(),
                            AutoConfirmOverrideEnabled = false,
                            AutoConfirmTrades = false,
                            AutoConfirmMarketTransactions = false,
                            TradeConfirmationCheckInterval = null
                        });
                    }

                    StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
                    if (result.Succeeded)
                    {
                        CopyStorageStateFrom(stagedManifest);
                        StorageResult normalizationResult = NormalizeAccountFilenames();
                        if (!normalizationResult.Succeeded)
                            DiagnosticErrorLogger.Log("Authenticator startup import", normalizationResult.Exception, "Imported accounts were saved but their canonical filenames could not be completed.");
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
                if (this.Encrypted)
                {
                    if (!this.VerifyPasskey(oldKey))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "The current passkey is incorrect.");
                    }
                }

                bool toEncrypt = newKey != null;
                if (toEncrypt && String.IsNullOrEmpty(newKey))
                {
                    return StorageResult.Failure(StorageFailureKind.Validation, "A new passkey is required to enable encryption.");
                }

                try
                {
                Manifest stagedManifest = CloneForStorage();
                Dictionary<string, string> stagedFiles = new Dictionary<string, string>();
                List<string> obsoleteFiles = new List<string>();
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                string[] entryPaths = ValidateAndResolveManifestEntries(
                    this.Entries,
                    this.Encrypted,
                    validatedRoot,
                    this.CredentialsStoreFilename);
                string expectedCanonicalDir = null;

                for (int i = 0; i < this.Entries.Count; i++)
                {
                    ManifestEntry existingEntry = this.Entries[i];
                    string existingFilename = entryPaths[i];
                    if (!File.Exists(existingFilename))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "One of the local authenticator files is missing. Encryption settings were not changed.");
                    }

                    if (expectedCanonicalDir == null)
                        expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);

                    string fileContents = StoragePathGuard.ReadTextWithLimit(existingFilename, MaximumAccountFileSizeBytes, expectedCanonicalDir);
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

                    string newFilename = existingEntry.SteamID + "." + Guid.NewGuid().ToString("N") + ".maFile";
                    stagedFiles.Add(newFilename, stagedContents);
                    obsoleteFiles.Add(existingEntry.Filename);
                    stagedManifest.Entries[i].Filename = newFilename;
                    stagedManifest.Entries[i].Salt = newSalt;
                    stagedManifest.Entries[i].IV = newIV;
                }

                stagedManifest.Encrypted = toEncrypt;
                if (!toEncrypt)
                {
                    stagedManifest.AllowSavedSteamPasswords = false;
                    if (!String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    {
                        obsoleteFiles.Add(this.CredentialsStoreFilename);
                    }
                    stagedManifest.CredentialsStoreFilename = null;
                }
                else if (!String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                {
                    string existingCredPath = ResolveValidatedCredentialStoreFilePath(validatedRoot, this.CredentialsStoreFilename);
                    if (File.Exists(existingCredPath))
                    {
                        if (!EncryptedCredentialStore.IsSupported)
                            return StorageResult.Failure(StorageFailureKind.Encryption, "AES-GCM encryption is not supported on this system.");

                        if (expectedCanonicalDir == null)
                            expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);

                        string encryptedCredContents = StoragePathGuard.ReadTextWithLimit(existingCredPath, EncryptedCredentialStore.MaximumEnvelopeBytes, expectedCanonicalDir);
                        Dictionary<ulong, SavedCredentialRecord> records = EncryptedCredentialStore.Decrypt(oldKey, encryptedCredContents);
                        string newCredFilename = EncryptedCredentialStore.CreateNewFilename();
                        string reencryptedCredContents = EncryptedCredentialStore.Encrypt(newKey, records);
                        stagedFiles.Add(newCredFilename, reencryptedCredContents);
                        obsoleteFiles.Add(this.CredentialsStoreFilename);
                        stagedManifest.CredentialsStoreFilename = newCredFilename;
                    }
                    else
                    {
                        stagedManifest.CredentialsStoreFilename = null;
                    }
                }

                StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
                if (result.Succeeded)
                {
                    CopyStorageStateFrom(stagedManifest);
                    if (toEncrypt)
                        RecordVerifiedPasskeyProofLocked(newKey);
                    else
                        ClearVerifiedPasskeyProofLocked();
                    StorageResult normalizationResult = NormalizeAccountFilenames();
                    if (!normalizationResult.Succeeded)
                        DiagnosticErrorLogger.Log("Authenticator storage", normalizationResult.Exception, "Encryption changed successfully, but canonical authenticator filenames could not be completed.");
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

        private byte[] ComputePasskeyProof(string passkey)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(passkey ?? String.Empty);
            try
            {
                using (var hmac = new HMACSHA256(passkeyProofKey))
                {
                    return hmac.ComputeHash(keyBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }

        private void RecordVerifiedPasskeyProofLocked(string passkey)
        {
            if (String.IsNullOrEmpty(passkey))
            {
                verifiedPasskeyProof = null;
                return;
            }
            verifiedPasskeyProof = ComputePasskeyProof(passkey);
        }

        private void ClearVerifiedPasskeyProofLocked()
        {
            verifiedPasskeyProof = null;
        }

        private bool IsPasskeyValidForEncryptedWriteLocked(string passKey, ulong targetSteamId)
        {
            if (!this.Encrypted || this.Entries == null || this.Entries.Count == 0)
                return true;
            if (String.IsNullOrEmpty(passKey))
                return false;

            if (verifiedPasskeyProof != null)
            {
                byte[] candidateProof = ComputePasskeyProof(passKey);
                return CryptographicOperations.FixedTimeEquals(verifiedPasskeyProof, candidateProof);
            }

            // Cold-start validation when VerifyPasskey has not yet run on this Manifest instance:
            // validate against the target entry first, then any readable entry. A single unreadable
            // unrelated file does not block healthy encrypted writes, while a wrong passkey fails closed.
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string validatedRoot;
            string expectedCanonicalDir;
            try
            {
                validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                if (!Directory.Exists(maDir))
                    return false;
                expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "Could not validate the account-data directory during passkey write validation.");
                return false;
            }

            IEnumerable<ManifestEntry> orderedEntries = this.Entries
                .Where(entry => entry != null)
                .OrderByDescending(entry => targetSteamId != 0 && entry.SteamID == targetSteamId);

            foreach (ManifestEntry entry in orderedEntries)
            {
                string filename;
                try
                {
                    filename = ResolveValidatedManifestFilePath(validatedRoot, entry);
                }
                catch
                {
                    continue;
                }
                if (!File.Exists(filename))
                    continue;

                string rawContents;
                try
                {
                    rawContents = StoragePathGuard.ReadTextWithLimit(filename, MaximumAccountFileSizeBytes, expectedCanonicalDir);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "Skipping an unreadable authenticator file during passkey write validation.");
                    continue;
                }

                if (String.IsNullOrEmpty(rawContents) || String.IsNullOrEmpty(entry.Salt) || String.IsNullOrEmpty(entry.IV))
                    continue;

                string decrypted;
                try
                {
                    decrypted = FileEncryptor.DecryptData(passKey, entry.Salt, entry.IV, rawContents);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is CryptographicException)
                {
                    continue;
                }

                if (decrypted == null)
                    continue;

                try
                {
                    SteamGuardAccount parsed = JsonConvert.DeserializeObject<SteamGuardAccount>(decrypted, storageJsonSettings);
                    if (parsed?.Session != null && parsed.Session.SteamID == entry.SteamID)
                    {
                        RecordVerifiedPasskeyProofLocked(passKey);
                        return true;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return false;
        }

        public bool VerifyPasskey(string passkey)
        {
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    long revisionBefore;
                    int expectedEntryCount;
                    lock (storageLock)
                    {
                        if (!this.Encrypted || this.Entries == null || this.Entries.Count == 0)
                            return true;
                        revisionBefore = this.storageRevision;
                        expectedEntryCount = this.Entries.Count;
                    }

                    var accounts = this.GetAllAccounts(passkey);
                    bool valid = accounts != null && accounts.Length == expectedEntryCount;

                    OnBeforeVerifyPasskeyProofPublishForTest?.Invoke();

                    lock (storageLock)
                    {
                        if (!this.Encrypted || this.Entries == null || this.Entries.Count == 0)
                            return true;

                        if (this.storageRevision != revisionBefore)
                        {
                            // Storage mutated concurrently (e.g., passkey rotation or account update);
                            // never publish a potentially stale passkey proof.
                            continue;
                        }

                        if (valid)
                            RecordVerifiedPasskeyProofLocked(passkey);

                        return valid;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The encryption passkey could not be verified.");
                return false;
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

                ManifestEntry entry = FindEntryForRemoval(account, passKey);
                if (entry == null)
                    return account.Session != null; // If a session-backed account never existed, did you do what they asked?

                try
                {
                Manifest stagedManifest = CloneForStorage();
                ManifestEntry stagedEntry = stagedManifest.Entries.FirstOrDefault(candidate => String.Equals(candidate.Filename, entry.Filename, StringComparison.OrdinalIgnoreCase));
                if (stagedEntry == null)
                    return false;
                stagedManifest.Entries.Remove(stagedEntry);
                Dictionary<string, string> stagedFiles = new Dictionary<string, string>();
                List<string> obsoleteFiles = new List<string>();
                if (deleteMaFile)
                    obsoleteFiles.Add(entry.Filename);

                if (stagedManifest.Entries.Count == 0)
                {
                    stagedManifest.Encrypted = false;
                    stagedManifest.AllowSavedSteamPasswords = false;
                    if (!String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                        obsoleteFiles.Add(this.CredentialsStoreFilename);
                    stagedManifest.CredentialsStoreFilename = null;
                }
                else if (!String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                {
                    string maDir = Path.Combine(GetExecutableDir(), "maFiles");
                    string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                    string existingCredPath = ResolveValidatedCredentialStoreFilePath(validatedRoot, this.CredentialsStoreFilename);
                    if (File.Exists(existingCredPath))
                    {
                        if (String.IsNullOrEmpty(passKey) || !IsPasskeyValidForEncryptedWriteLocked(passKey, entry.SteamID))
                        {
                            DiagnosticErrorLogger.Log("Authenticator storage", new InvalidOperationException("Passkey required to update credential store during account removal."), "Removing an account with saved credentials requires a valid passkey.");
                            return false;
                        }

                        string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                        string encryptedCredContents = StoragePathGuard.ReadTextWithLimit(existingCredPath, EncryptedCredentialStore.MaximumEnvelopeBytes, expectedCanonicalDir);
                        Dictionary<ulong, SavedCredentialRecord> records = EncryptedCredentialStore.Decrypt(passKey, encryptedCredContents);
                        if (records.Remove(entry.SteamID))
                        {
                            obsoleteFiles.Add(this.CredentialsStoreFilename);
                            if (records.Count == 0)
                            {
                                stagedManifest.CredentialsStoreFilename = null;
                            }
                            else
                            {
                                string newCredFilename = EncryptedCredentialStore.CreateNewFilename();
                                stagedFiles.Add(newCredFilename, EncryptedCredentialStore.Encrypt(passKey, records));
                                stagedManifest.CredentialsStoreFilename = newCredFilename;
                            }
                        }
                    }
                    else
                    {
                        stagedManifest.CredentialsStoreFilename = null;
                    }
                }

                StorageResult result = CommitStorageTransaction(
                    stagedManifest,
                    stagedFiles,
                    obsoleteFiles);
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
            if (Entries == null || Entries.Count == 0)
                return null;

            string maDir = Path.Combine(GetExecutableDir(), "maFiles");
            string validatedRoot;
            string expectedCanonicalDir;
            try
            {
                validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                if (!Directory.Exists(maDir))
                    return null;
                expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "Could not validate the account-data directory while preparing account removal.");
                return null;
            }
            foreach (ManifestEntry candidate in Entries)
            {
                try
                {
                    string contents = StoragePathGuard.ReadTextWithLimit(ResolveValidatedManifestFilePath(validatedRoot, candidate), MaximumAccountFileSizeBytes, expectedCanonicalDir);
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

        public string GetLastTransferAttemptId(ulong steamId)
        {
            if (steamId == 0)
                return null;

            lock (storageLock)
            {
                return Entries?.FirstOrDefault(entry => entry != null && entry.SteamID == steamId)?.LastTransferAttemptId;
            }
        }

        public StorageResult SaveAccount(
            SteamGuardAccount account,
            bool encrypt,
            string passKey = null,
            long? expectedStorageRevision = null,
            bool allowCreate = true,
            bool requireAllowSavedPasswords = false,
            string expectedCredentialGenerationId = null,
            string credentialPassKey = null,
            string transferAttemptId = null,
            string expectedPreviousTransferAttemptId = null,
            bool enforceTransferAttemptBase = false)
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
                if (requireAllowSavedPasswords && (!this.Encrypted || !this.AllowSavedSteamPasswords))
                    return StorageResult.Failure(StorageFailureKind.Validation, "Saved Steam passwords are no longer enabled.");

                try
                {
                if (this.Encrypted && !IsPasskeyValidForEncryptedWriteLocked(passKey, account.Session.SteamID))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid. The account was not saved.");

                if (expectedCredentialGenerationId != null)
                {
                    if (!this.Encrypted || !this.AllowSavedSteamPasswords || String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                        return StorageResult.Failure(StorageFailureKind.Validation, "The saved credential is no longer active.");

                    string effectiveCredPassKey = credentialPassKey ?? passKey;
                    if (String.IsNullOrEmpty(effectiveCredPassKey))
                        return StorageResult.Failure(StorageFailureKind.Validation, "A passkey is required to verify the saved credential.");

                    SavedCredentialRecord currentRecord = TryLoadSavedCredentialRecordLocked(account.Session.SteamID, effectiveCredPassKey);
                    if (currentRecord == null ||
                        currentRecord.PasswordRejected ||
                        !String.Equals(currentRecord.CredentialGenerationId, expectedCredentialGenerationId, StringComparison.Ordinal))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "The saved credential changed or was rejected while silent re-login was running.");
                    }
                }

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
                if (enforceTransferAttemptBase && previousEntry != null)
                {
                    string currentAttemptId = previousEntry.LastTransferAttemptId;
                    bool matchesBaseline = String.Equals(currentAttemptId ?? String.Empty, expectedPreviousTransferAttemptId ?? String.Empty, StringComparison.Ordinal);
                    bool matchesSameAttempt = !String.IsNullOrWhiteSpace(transferAttemptId) &&
                        String.Equals(currentAttemptId, transferAttemptId, StringComparison.Ordinal);
                    if (!matchesBaseline && !matchesSameAttempt)
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "A newer authenticator transfer has already been saved for this account.");
                    }
                }
                string filename = account.Session.SteamID + "." + Guid.NewGuid().ToString("N") + ".maFile";
                ManifestEntry newEntry = new ManifestEntry()
                {
                    SteamID = account.Session.SteamID,
                    IV = iV,
                    Salt = salt,
                    Filename = filename,
                    SessionNeedsRenewal = account.Session.IsRefreshTokenExpired(),
                    AutoConfirmOverrideEnabled = previousEntry?.AutoConfirmOverrideEnabled ?? false,
                    AutoConfirmTrades = previousEntry?.AutoConfirmTrades ?? false,
                    AutoConfirmMarketTransactions = previousEntry?.AutoConfirmMarketTransactions ?? false,
                    TradeConfirmationCheckInterval = previousEntry?.TradeConfirmationCheckInterval,
                    LastTransferAttemptId = !String.IsNullOrWhiteSpace(transferAttemptId)
                        ? transferAttemptId
                        : previousEntry?.LastTransferAttemptId
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

                stagedManifest.Encrypted = encrypt || stagedManifest.Encrypted;
                StorageResult result = CommitStorageTransaction(
                    stagedManifest,
                    new Dictionary<string, string> { { filename, jsonAccount } },
                    previousEntry == null ? Enumerable.Empty<string>() : new[] { previousEntry.Filename });
                if (result.Succeeded)
                {
                    CopyStorageStateFrom(stagedManifest);
                    if (stagedManifest.Encrypted && !String.IsNullOrEmpty(passKey))
                        RecordVerifiedPasskeyProofLocked(passKey);
                    StorageResult normalizationResult = NormalizeAccountFilenames();
                    if (!normalizationResult.Succeeded)
                        DiagnosticErrorLogger.Log("Authenticator storage", normalizationResult.Exception, "The account was saved, but its canonical filename could not be completed.");
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
                StoragePathGuard.PinnedStorageDirectory pinnedDir = null;
                try
                {
                    string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                    string manifestFilename = Path.Combine(maDir, "manifest.json");
                    pinnedDir = StoragePathGuard.TryPinExistingDirectory(maDir, out string validatedRoot);
                    ValidateManifestState(this.Entries, this.Encrypted, validatedRoot, this.CredentialsStoreFilename);
                    string contents = JsonConvert.SerializeObject(this, storageJsonSettings);
                    if (Encoding.UTF8.GetByteCount(contents) > MaximumManifestFileSizeBytes)
                        throw new InvalidDataException("The account manifest is larger than the supported size limit.");

                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (pinnedDir != null)
                    {
                        if (!RecoverPendingStorageTransaction(
                            maDir,
                            manifestFilename,
                            retainedObsoleteFilenames,
                            pinnedDir.CanonicalDirectoryPath,
                            pinnedDir.ValidatedRoot))
                        {
                            return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data save could not be recovered safely. No settings were changed.");
                        }
                    }
                    else
                    {
                        pinnedDir = StoragePathGuard.CreateAndPinDirectory(maDir);
                    }

                    pinnedDir.VerifyStillPinned();
                    string backupFilename = Path.Combine(maDir, SettingsBackupFilename);
                    WriteAllTextAtomically(manifestFilename, contents, backupFilename, pinnedDir.CanonicalDirectoryPath);
                    if (retainedObsoleteFilenames.Count > 0)
                    {
                        if (!RebaseRetainedStorageTransaction(maDir, contents, pinnedDir.CanonicalDirectoryPath) ||
                            !RecoverPendingStorageTransaction(
                                maDir,
                                manifestFilename,
                                expectedCanonicalDir: pinnedDir.CanonicalDirectoryPath,
                                validatedRoot: pinnedDir.ValidatedRoot))
                        {
                            return StorageResult.Failure(
                                StorageFailureKind.Io,
                                "The settings were saved, but a previous account-data cleanup could not be completed safely. Restart the application and try again.");
                        }
                    }
                    if (pinnedDir.TryVerifyStillPinned(out Exception postCommitPinError))
                    {
                        DeleteFileBestEffort(backupFilename, "The completed manifest backup could not be removed.");
                        SweepOrphanedCredentialStoresLocked(maDir, this.CredentialsStoreFilename);
                    }
                    else
                    {
                        DiagnosticErrorLogger.Log("Authenticator storage", postCommitPinError, "Skipping post-save cleanup because the account-data directory changed after the manifest was committed.");
                    }
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
                finally
                {
                    pinnedDir?.Dispose();
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

                bool savedPasswordsChanged = staged.AllowSavedSteamPasswords != this.AllowSavedSteamPasswords;
                if (savedPasswordsChanged)
                {
                    if (staged.AllowSavedSteamPasswords && !this.Encrypted)
                        return StorageResult.Failure(StorageFailureKind.Validation, "Enable manifest encryption before saving Steam passwords.");
                    if (staged.AllowSavedSteamPasswords && !EncryptedCredentialStore.IsSupported)
                        return StorageResult.Failure(StorageFailureKind.Encryption, "AES-GCM encryption is not supported on this system.");

                    List<string> obsoleteFiles = new List<string>();
                    if (!staged.AllowSavedSteamPasswords && !String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    {
                        obsoleteFiles.Add(this.CredentialsStoreFilename);
                        staged.CredentialsStoreFilename = null;
                    }

                    StorageResult txResult = CommitStorageTransaction(
                        staged,
                        new Dictionary<string, string>(),
                        obsoleteFiles,
                        preserveStagedSettings: true);
                    if (txResult.Succeeded)
                    {
                        staged.CopySettingsInto(this);
                        CopyStorageStateFrom(staged);
                    }
                    return txResult;
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

        private void CopyStorageStateFrom(Manifest source)
        {
            Dictionary<ulong, ManifestEntry> previousEntries = GetManifestEntriesBySteamId(Entries);
            Dictionary<ulong, ManifestEntry> nextEntries = GetManifestEntriesBySteamId(source?.Entries);
            foreach (ulong steamId in previousEntries.Keys.Concat(nextEntries.Keys).Distinct().ToArray())
            {
                previousEntries.TryGetValue(steamId, out ManifestEntry previousEntry);
                nextEntries.TryGetValue(steamId, out ManifestEntry nextEntry);
                if (AreManifestEntriesEquivalent(previousEntry, nextEntry))
                    continue;

                if (nextEntry == null)
                    accountStorageRevisions.Remove(steamId);
                else
                    accountStorageRevisions[steamId] = GetAccountStorageRevisionLocked(steamId) + 1;
            }

            Entries = source.Entries;
            Encrypted = source.Encrypted;
            if (!Encrypted || Entries == null || Entries.Count == 0)
            {
                ClearVerifiedPasskeyProofLocked();
            }
            AllowSavedSteamPasswords = source.AllowSavedSteamPasswords;
            if (!String.Equals(CredentialsStoreFilename, source.CredentialsStoreFilename, StringComparison.Ordinal))
            {
                InvalidateCredentialMetadataCacheLocked();
            }
            CredentialsStoreFilename = source.CredentialsStoreFilename;
            if (String.IsNullOrWhiteSpace(CredentialsStoreFilename))
            {
                CredentialsStoreMissingWarning = false;
                InvalidateCredentialMetadataCacheLocked();
            }
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

        private StorageResult CommitStorageTransaction(
            Manifest stagedManifest,
            IReadOnlyDictionary<string, string> stagedFiles,
            IEnumerable<string> obsoleteFiles,
            bool preserveStagedSettings = false)
        {
            lock (storageLock)
            {
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                string manifestFilename = Path.Combine(maDir, "manifest.json");
                string journalFilename = Path.Combine(maDir, StorageJournalFilename);
                string backupFilename = CreateBackupFilename(maDir);
                List<string> createdFilenames = stagedFiles.Keys.ToList();
                List<string> obsoleteFilenames = obsoleteFiles
                    .Where(filename => !String.IsNullOrWhiteSpace(filename))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool manifestCommitted = false;
                bool manifestCommitStarted = false;
                bool journalWritten = false;
                List<string> trackedRetainedObsoleteFilenames = new List<string>();
                StoragePathGuard.PinnedStorageDirectory pinnedDir = null;

                try
                {
                    pinnedDir = StoragePathGuard.TryPinExistingDirectory(maDir, out string validatedRoot);

                    // A cleanup failure leaves the previous journal intentionally. Complete it
                    // before replacing it so its obsolete files remain recoverable.
                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (pinnedDir != null &&
                        !RecoverPendingStorageTransaction(
                            maDir,
                            manifestFilename,
                            retainedObsoleteFilenames,
                            pinnedDir.CanonicalDirectoryPath,
                            pinnedDir.ValidatedRoot))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. No account data was changed.");
                    }

                    trackedRetainedObsoleteFilenames = retainedObsoleteFilenames
                        .Where(filename => !String.IsNullOrWhiteSpace(filename) &&
                            !createdFilenames.Contains(filename, StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (trackedRetainedObsoleteFilenames.Count > 0)
                    {
                        HashSet<string> trackedRetainedSet = new HashSet<string>(trackedRetainedObsoleteFilenames, StringComparer.OrdinalIgnoreCase);
                        obsoleteFilenames.RemoveAll(filename => trackedRetainedSet.Contains(filename));
                    }

                    if (!preserveStagedSettings)
                        CopySettingsInto(stagedManifest);
                    if (!stagedManifest.Encrypted)
                    {
                        stagedManifest.AllowSavedSteamPasswords = false;
                        if (!String.IsNullOrWhiteSpace(stagedManifest.CredentialsStoreFilename))
                        {
                            if (!obsoleteFilenames.Contains(stagedManifest.CredentialsStoreFilename, StringComparer.OrdinalIgnoreCase))
                                obsoleteFilenames.Add(stagedManifest.CredentialsStoreFilename);
                            stagedManifest.CredentialsStoreFilename = null;
                        }
                    }

                    ValidateManifestState(
                        stagedManifest.Entries,
                        stagedManifest.Encrypted,
                        validatedRoot,
                        stagedManifest.CredentialsStoreFilename);
                    string manifestContents = JsonConvert.SerializeObject(stagedManifest, storageJsonSettings);
                    if (Encoding.UTF8.GetByteCount(manifestContents) > MaximumManifestFileSizeBytes)
                        throw new InvalidDataException("The account manifest is larger than the supported size limit.");

                    foreach (string filename in obsoleteFilenames)
                        ValidateStorageFilename(filename);

                    foreach (string filename in trackedRetainedObsoleteFilenames)
                        ValidateStorageFilename(filename);

                    foreach (string filename in createdFilenames)
                        ValidateStorageFilename(filename);

                    if (pinnedDir == null)
                    {
                        pinnedDir = StoragePathGuard.CreateAndPinDirectory(maDir);
                    }

                    pinnedDir.VerifyStillPinned();
                    StorageTransactionJournal journal = new StorageTransactionJournal()
                    {
                        ManifestHash = GetContentHash(manifestContents),
                        CreatedFilenames = createdFilenames,
                        ObsoleteFilenames = obsoleteFilenames,
                        RetainedObsoleteFilenames = trackedRetainedObsoleteFilenames.Count > 0 ? trackedRetainedObsoleteFilenames : null,
                        BackupFilename = Path.GetFileName(backupFilename)
                    };
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: pinnedDir.CanonicalDirectoryPath);
                    journalWritten = true;

                    foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
                    {
                        if (stagedFile.Value == null || Encoding.UTF8.GetByteCount(stagedFile.Value) > MaximumAccountFileSizeBytes)
                            throw new InvalidDataException("An account file is larger than the supported size limit.");
                        WriteAllTextAtomically(Path.Combine(maDir, stagedFile.Key), stagedFile.Value, expectedCanonicalDirectory: pinnedDir.CanonicalDirectoryPath);
                    }

                    pinnedDir.VerifyStillPinned();
                    manifestCommitStarted = true;
                    WriteAllTextAtomically(manifestFilename, manifestContents, backupFilename, pinnedDir.CanonicalDirectoryPath);
                    manifestCommitted = true;

                    if (!pinnedDir.TryVerifyStillPinned(out Exception postCommitPinError))
                    {
                        // Manifest is already committed on disk; preserve the journal for safe recovery
                        // and report success with cleanup pending so callers still publish staged state.
                        DiagnosticErrorLogger.Log("Authenticator storage", postCommitPinError, "Skipping post-commit cleanup because the account-data directory changed after the manifest was committed.");
                        return StorageResult.Success(obsoleteCleanupPending: true);
                    }

                    bool backupDeleted = DeleteFileBestEffort(backupFilename, "The completed manifest backup could not be removed.");
                    bool obsoleteDeleted = backupDeleted &&
                        DeleteFilesBestEffort(
                            maDir,
                            obsoleteFilenames.Concat(trackedRetainedObsoleteFilenames),
                            "The replaced authenticator file could not be removed after the manifest was committed.");
                    if (obsoleteDeleted)
                    {
                        DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed.");
                    }
                    SweepOrphanedCredentialStoresLocked(maDir, stagedManifest.CredentialsStoreFilename);
                    return StorageResult.Success(obsoleteCleanupPending: !obsoleteDeleted);
                }
                catch (JsonException ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, trackedRetainedObsoleteFilenames, manifestCommitStarted, pinnedDir?.CanonicalDirectoryPath);
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be prepared for saving.", ex);
                }
                catch (NotSupportedException ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, trackedRetainedObsoleteFilenames, manifestCommitStarted, pinnedDir?.CanonicalDirectoryPath);
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The selected data folder does not support the safe atomic saves required for authenticator data. Move the data folder to a local drive and try again.",
                        ex);
                }
                catch (Exception ex)
                {
                    if (journalWritten && !manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, trackedRetainedObsoleteFilenames, manifestCommitStarted, pinnedDir?.CanonicalDirectoryPath);
                    return StorageResult.Failure(
                        manifestCommitStarted ? StorageFailureKind.Manifest : StorageFailureKind.Io,
                        manifestCommitStarted
                            ? "The account manifest could not be saved. The existing account data was kept unchanged."
                            : "The account files could not be saved. The existing account data was kept unchanged.",
                        ex);
                }
                finally
                {
                    pinnedDir?.Dispose();
                }
            }
        }

        private static void RollBackUncommittedTransaction(
            string maDir,
            string manifestFilename,
            string journalFilename,
            string backupFilename,
            IEnumerable<string> createdFilenames,
            IReadOnlyList<string> retainedObsoleteFilenames,
            bool manifestCommitStarted,
            string expectedCanonicalDir)
        {
            if (manifestCommitStarted && !RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, true, expectedCanonicalDir))
                return;

            if (DeleteFilesBestEffort(maDir, createdFilenames, "A temporary authenticator file could not be removed after a failed save."))
            {
                bool backupDeleted = String.IsNullOrWhiteSpace(backupFilename) ||
                    DeleteFileBestEffort(backupFilename, "A manifest backup could not be removed after a failed save.");
                if (!backupDeleted)
                    return;

                if (retainedObsoleteFilenames != null &&
                    retainedObsoleteFilenames.Count > 0 &&
                    !DeleteFilesBestEffort(maDir, retainedObsoleteFilenames, "A retained obsolete authenticator file could not be removed after a failed save."))
                {
                    PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, retainedObsoleteFilenames, expectedCanonicalDir);
                    return;
                }

                DeleteFileBestEffort(journalFilename, "A storage journal could not be removed after a failed save.");
            }
        }

        private static bool PreserveRetainedObsoleteJournal(
            string manifestFilename,
            string journalFilename,
            IReadOnlyList<string> retainedObsoleteFilenames,
            string expectedCanonicalDir)
        {
            try
            {
                if (!File.Exists(manifestFilename))
                    return false;

                string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(Path.GetDirectoryName(manifestFilename));
                string currentManifestContents = StoragePathGuard.ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes, canonicalDir);
                StorageTransactionJournal retainedJournal = new StorageTransactionJournal()
                {
                    ManifestHash = GetContentHash(currentManifestContents),
                    CreatedFilenames = new List<string>(),
                    ObsoleteFilenames = new List<string>(),
                    RetainedObsoleteFilenames = retainedObsoleteFilenames.ToList(),
                    BackupFilename = null
                };
                WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(retainedJournal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "Retained authenticator cleanup state could not be preserved after a failed save.");
                return false;
            }
        }

        private static bool RecoverPendingStorageTransaction(
            string maDir,
            string manifestFilename,
            ICollection<string> retainedObsoleteFilenames = null,
            string expectedCanonicalDir = null,
            string validatedRoot = null)
        {
            string journalFilename = Path.Combine(maDir, StorageJournalFilename);
            lock (storageLock)
            {
                if (!File.Exists(journalFilename))
                    return true;

                try
                {
                    string root = validatedRoot ?? StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                    string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                    StorageTransactionJournal journal = JsonConvert.DeserializeObject<StorageTransactionJournal>(
                        StoragePathGuard.ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes, canonicalDir),
                        storageJsonSettings);
                    if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                        throw new InvalidDataException("The pending storage journal is invalid.");

                    List<string> created = journal.CreatedFilenames ?? new List<string>();
                    List<string> obsolete = journal.ObsoleteFilenames ?? new List<string>();
                    List<string> retainedObsolete = journal.RetainedObsoleteFilenames ?? new List<string>();
                    foreach (string filename in created.Concat(obsolete).Concat(retainedObsolete))
                        ValidateStorageFilename(filename);

                    string backupFilename = GetValidatedStorageBackupPath(maDir, journal.BackupFilename);
                    if (!RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, false, canonicalDir))
                        return false;

                    bool manifestCommitted = File.Exists(manifestFilename) &&
                        String.Equals(
                            GetContentHash(StoragePathGuard.ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes, canonicalDir)),
                            journal.ManifestHash,
                            StringComparison.Ordinal);
                    HashSet<string> manifestFilenames = GetManifestFilenamesForRecovery(manifestFilename, canonicalDir, root);
                    ValidateStorageTransactionFileOwnership(
                        created,
                        obsolete,
                        retainedObsolete,
                        backupFilename,
                        manifestFilenames,
                        manifestCommitted);
                    if (manifestCommitted)
                    {
                        bool committedBackupDeleted = backupFilename == null ||
                            DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.");
                        if (!committedBackupDeleted)
                            return false;

                        List<string> committedObsolete = obsolete.Concat(retainedObsolete).ToList();
                        if (!DeleteFilesBestEffort(maDir, committedObsolete, "An obsolete authenticator file could not be removed during storage recovery."))
                        {
                            // The manifest is committed and its pre-commit backup is already gone,
                            // so its newly created files are live. Store pending deletions in
                            // retained_obsolete_filenames so they survive a subsequent manifest
                            // update even if the process exits before rebasing the journal hash.
                            journal.BackupFilename = null;
                            journal.CreatedFilenames = new List<string>();
                            journal.ObsoleteFilenames = new List<string>();
                            journal.RetainedObsoleteFilenames = committedObsolete;
                            WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                            if (retainedObsoleteFilenames != null)
                            {
                                foreach (string filename in committedObsolete)
                                    retainedObsoleteFilenames.Add(filename);
                            }
                            return true;
                        }

                        bool committedJournalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.");
                        return committedJournalDeleted && !File.Exists(journalFilename);
                    }

                    if (!DeleteFilesBestEffort(maDir, created, "A temporary authenticator file could not be removed during storage recovery."))
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
                        if (!PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, retainedObsolete, canonicalDir))
                            return false;
                        if (retainedObsoleteFilenames != null)
                        {
                            foreach (string filename in retainedObsolete)
                                retainedObsoleteFilenames.Add(filename);
                        }
                        return true;
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

        private static bool RebaseRetainedStorageTransaction(string maDir, string manifestContents, string expectedCanonicalDir = null)
        {
            string journalFilename = Path.Combine(maDir, StorageJournalFilename);
            lock (storageLock)
            {
                try
                {
                    if (!File.Exists(journalFilename))
                        return true;

                    string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                    StorageTransactionJournal journal = JsonConvert.DeserializeObject<StorageTransactionJournal>(
                        StoragePathGuard.ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes, canonicalDir),
                        storageJsonSettings);
                    if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                        throw new InvalidDataException("The retained storage journal is invalid.");

                    List<string> pendingRetainedObsolete = (journal.ObsoleteFilenames ?? new List<string>())
                        .Concat(journal.RetainedObsoleteFilenames ?? new List<string>())
                        .Where(filename => !String.IsNullOrWhiteSpace(filename))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (string filename in pendingRetainedObsolete)
                        ValidateStorageFilename(filename);

                    string backupFilename = GetValidatedStorageBackupPath(maDir, journal.BackupFilename);
                    if (backupFilename != null &&
                        !DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed while rebasing retained storage cleanup."))
                    {
                        return false;
                    }

                    journal.ManifestHash = GetContentHash(manifestContents);
                    journal.CreatedFilenames = new List<string>();
                    journal.ObsoleteFilenames = new List<string>();
                    journal.RetainedObsoleteFilenames = pendingRetainedObsolete.Count > 0 ? pendingRetainedObsolete : null;
                    journal.BackupFilename = null;
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                    return true;
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "The retained storage cleanup could not be associated with the saved manifest.");
                    return false;
                }
            }
        }

        private static bool RestoreManifestBackupIfNeeded(
            string manifestFilename,
            string backupFilename,
            bool overwriteManifest,
            string expectedCanonicalDir = null)
        {
            if (String.IsNullOrWhiteSpace(backupFilename) || !File.Exists(backupFilename))
                return !overwriteManifest || File.Exists(manifestFilename);
            if (!overwriteManifest && File.Exists(manifestFilename))
                return true;

            try
            {
                string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(Path.GetDirectoryName(manifestFilename));
                WriteAllTextAtomically(
                    manifestFilename,
                    StoragePathGuard.ReadTextWithLimit(backupFilename, MaximumManifestFileSizeBytes, canonicalDir),
                    expectedCanonicalDirectory: canonicalDir);
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

            if (!String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) ||
                !filename.StartsWith(StorageBackupFilenamePrefix, StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(StorageBackupFilenameSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A storage transaction contained an invalid manifest backup filename.");

            int guidStart = StorageBackupFilenamePrefix.Length;
            int guidLength = filename.Length - guidStart - StorageBackupFilenameSuffix.Length;
            if (guidLength != 32 || !Guid.TryParseExact(filename.Substring(guidStart, guidLength), "N", out _))
                throw new InvalidDataException("A storage transaction contained an invalid manifest backup filename.");

            return Path.Combine(maDir, filename);
        }

        private static HashSet<string> GetManifestFilenamesForRecovery(
            string manifestFilename,
            string expectedCanonicalDir,
            string validatedRoot)
        {
            HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(manifestFilename))
                return filenames;

            Manifest currentManifest = JsonConvert.DeserializeObject<Manifest>(
                StoragePathGuard.ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes, expectedCanonicalDir),
                storageJsonSettings);
            if (currentManifest == null || currentManifest.Entries == null)
                throw new InvalidDataException("The account manifest contains an invalid entries list.");

            ValidateManifestState(
                currentManifest.Entries,
                currentManifest.Encrypted,
                validatedRoot,
                currentManifest.CredentialsStoreFilename);

            foreach (ManifestEntry entry in currentManifest.Entries)
            {
                if (!filenames.Add(entry.Filename))
                    throw new InvalidDataException("The current account manifest contains duplicate account filenames.");
            }

            if (!String.IsNullOrWhiteSpace(currentManifest.CredentialsStoreFilename))
            {
                filenames.Add(currentManifest.CredentialsStoreFilename);
            }

            return filenames;
        }

        private static void ValidateStorageTransactionFileOwnership(
            IEnumerable<string> created,
            IEnumerable<string> obsolete,
            IEnumerable<string> retainedObsolete,
            string backupFilename,
            HashSet<string> manifestFilenames,
            bool manifestCommitted)
        {
            HashSet<string> transactionFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string filename in created.Concat(obsolete).Concat(retainedObsolete))
            {
                if (!transactionFilenames.Add(filename))
                    throw new InvalidDataException("A storage transaction reused a filename across its file lists.");
            }

            if (backupFilename != null && transactionFilenames.Contains(Path.GetFileName(backupFilename)))
                throw new InvalidDataException("A storage transaction reused its manifest backup filename.");

            IEnumerable<string> filesThatWouldBeDeleted = (manifestCommitted ? obsolete : created).Concat(retainedObsolete);
            foreach (string filename in filesThatWouldBeDeleted)
            {
                if (manifestFilenames.Contains(filename))
                    throw new InvalidDataException("A storage transaction attempted to delete a live authenticator file.");
            }
        }

        private static void WriteAllTextAtomically(
            string filename,
            string contents,
            string backupFilename = null,
            string expectedCanonicalDirectory = null)
        {
            string temporaryFilename = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool tempFileVerified = String.IsNullOrWhiteSpace(expectedCanonicalDirectory);
            try
            {
                using (FileStream stream = new FileStream(temporaryFilename, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (!tempFileVerified)
                    {
                        StoragePathGuard.VerifyOpenedFileWithinCanonicalDirectory(
                            stream.SafeFileHandle,
                            Path.GetFileName(temporaryFilename),
                            expectedCanonicalDirectory);
                        tempFileVerified = true;
                    }

                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(contents);
                        writer.Flush();
                        stream.Flush(true);
                    }
                }

                if (File.Exists(filename))
                {
                    try
                    {
                        File.Replace(temporaryFilename, filename, backupFilename);
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
                else
                    File.Move(temporaryFilename, filename);
            }
            finally
            {
                if (tempFileVerified && File.Exists(temporaryFilename))
                    File.Delete(temporaryFilename);
            }
        }

        private static string CreateBackupFilename(string maDir)
        {
            return Path.Combine(maDir, ".manifest." + Guid.NewGuid().ToString("N") + ".bak");
        }

        private static bool IsAtomicReplacementUnsupported(IOException exception)
        {
            const int ErrorInvalidFunction = unchecked((int)0x80070001);
            const int ErrorNotSupported = unchecked((int)0x80070032);
            return exception.HResult == ErrorInvalidFunction || exception.HResult == ErrorNotSupported;
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

        private static string ResolveValidatedManifestFilePath(string validatedRoot, ManifestEntry entry)
        {
            if (entry == null)
                throw new InvalidDataException("The account manifest contains an empty account entry.");

            return ResolveValidatedManifestFilePath(validatedRoot, entry.Filename);
        }

        private static string ResolveValidatedManifestFilePath(string validatedRoot, string filename)
        {
            ValidateManifestFilename(filename);
            string candidate = Path.GetFullPath(Path.Combine(validatedRoot, filename));
            if (!candidate.StartsWith(validatedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An account manifest entry points outside the account-data directory.");

            return candidate;
        }

        private static string ResolveValidatedCredentialStoreFilePath(string validatedRoot, string credentialsStoreFilename)
        {
            if (!EncryptedCredentialStore.IsValidCredentialStoreFilename(credentialsStoreFilename))
                throw new InvalidDataException("The account manifest contains an invalid credential store filename.");

            string candidate = Path.GetFullPath(Path.Combine(validatedRoot, credentialsStoreFilename));
            if (!candidate.StartsWith(validatedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The account manifest credential store path escapes the data directory.");

            return candidate;
        }

        private static void ValidateManifestState(
            IReadOnlyList<ManifestEntry> entries,
            bool encrypted,
            string validatedRoot,
            string credentialsStoreFilename)
        {
            ValidateManifestStateCore(entries, encrypted, validatedRoot, credentialsStoreFilename, resolvedPaths: null);
        }

        private static string[] ValidateAndResolveManifestEntries(
            IReadOnlyList<ManifestEntry> entries,
            bool encrypted,
            string validatedRoot,
            string credentialsStoreFilename)
        {
            if (entries == null || entries.Count > MaximumManifestEntries)
                throw new InvalidDataException("The account manifest contains an invalid entries list.");

            string[] resolvedPaths = new string[entries.Count];
            ValidateManifestStateCore(entries, encrypted, validatedRoot, credentialsStoreFilename, resolvedPaths);
            return resolvedPaths;
        }

        private static void ValidateManifestStateCore(
            IReadOnlyList<ManifestEntry> entries,
            bool encrypted,
            string validatedRoot,
            string credentialsStoreFilename,
            string[] resolvedPaths)
        {
            if (entries == null || entries.Count > MaximumManifestEntries)
                throw new InvalidDataException("The account manifest contains an invalid entries list.");

            HashSet<ulong> steamIds = new HashSet<ulong>();
            HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                ManifestEntry entry = entries[i];
                if (entry == null || entry.SteamID == 0 || !steamIds.Add(entry.SteamID))
                    throw new InvalidDataException("The account manifest contains an invalid or duplicate Steam identity.");

                string resolvedPath = ResolveValidatedManifestFilePath(validatedRoot, entry);
                if (!filenames.Add(entry.Filename))
                    throw new InvalidDataException("The account manifest contains duplicate account filenames.");

                bool hasSalt = !String.IsNullOrWhiteSpace(entry.Salt);
                bool hasIv = !String.IsNullOrWhiteSpace(entry.IV);
                if (encrypted)
                {
                    if (!hasSalt || !hasIv || !IsBase64WithLength(entry.Salt, 8) || !IsBase64WithLength(entry.IV, 16))
                        throw new InvalidDataException("An encrypted account entry is missing valid encryption metadata.");
                }
                else if (hasSalt || hasIv)
                {
                    throw new InvalidDataException("An unencrypted account entry contains encryption metadata.");
                }

                if (resolvedPaths != null)
                    resolvedPaths[i] = resolvedPath;
            }

            if (!String.IsNullOrWhiteSpace(credentialsStoreFilename))
            {
                ResolveValidatedCredentialStoreFilePath(validatedRoot, credentialsStoreFilename);
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
            if (EncryptedCredentialStore.IsValidCredentialStoreFilename(filename))
                return;

            try
            {
                ValidateManifestFilename(filename);
            }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("A storage transaction contained an invalid filename.");
            }
        }

        private static void SweepOrphanedCredentialStoresLocked(string maDir, string activeCredentialsFilename)
        {
            if (String.IsNullOrWhiteSpace(maDir) || !Directory.Exists(maDir))
                return;

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(maDir, "credentials.*.asda-cred*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (!EncryptedCredentialStore.IsCredentialStoreOrTempFilename(fileName))
                        continue;

                    if (!String.IsNullOrWhiteSpace(activeCredentialsFilename) &&
                        String.Equals(fileName, activeCredentialsFilename, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DeleteFileBestEffort(filePath, "An orphaned encrypted credential file could not be removed.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The orphaned credential store sweep could not enumerate the data directory.");
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
                    File.Delete(filename);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, context);
                return false;
            }
        }

        private sealed class StorageTransactionJournal
        {
            [JsonProperty("manifest_hash")]
            public string ManifestHash { get; set; }

            [JsonProperty("created_filenames")]
            public List<string> CreatedFilenames { get; set; }

            [JsonProperty("obsolete_filenames")]
            public List<string> ObsoleteFilenames { get; set; }

            [JsonProperty("retained_obsolete_filenames", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> RetainedObsoleteFilenames { get; set; }

            [JsonProperty("backup_filename")]
            public string BackupFilename { get; set; }
        }

        private void RecomputeExistingEntries(string validatedRoot = null)
        {
            List<ManifestEntry> newEntries = new List<ManifestEntry>();
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string root = validatedRoot ?? StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);

            foreach (var entry in this.Entries)
            {
                string filename = ResolveValidatedManifestFilePath(root, entry);
                if (File.Exists(filename))
                {
                    newEntries.Add(entry);
                }
            }

            this.Entries = newEntries;

            if (this.Entries.Count == 0)
            {
                this.Encrypted = false;
                this.AllowSavedSteamPasswords = false;
                this.CredentialsStoreFilename = null;
            }
        }

        public StorageResult SetAllowSavedSteamPasswords(bool allow)
        {
            lock (storageLock)
            {
                if (allow && !this.Encrypted)
                    return StorageResult.Failure(StorageFailureKind.Validation, "Enable manifest encryption before saving Steam passwords.");
                if (allow && !EncryptedCredentialStore.IsSupported)
                    return StorageResult.Failure(StorageFailureKind.Encryption, "AES-GCM encryption is not supported on this system.");

                try
                {
                    Manifest stagedManifest = CloneForStorage();
                    stagedManifest.AllowSavedSteamPasswords = allow;
                    List<string> obsoleteFiles = new List<string>();
                    if (!allow && !String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    {
                        obsoleteFiles.Add(this.CredentialsStoreFilename);
                        stagedManifest.CredentialsStoreFilename = null;
                    }

                    StorageResult result = CommitStorageTransaction(
                        stagedManifest,
                        new Dictionary<string, string>(),
                        obsoleteFiles);
                    if (result.Succeeded)
                        CopyStorageStateFrom(stagedManifest);
                    return result;
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The saved-password preference could not be updated.", ex);
                }
            }
        }

        public StorageResult SaveAccountPassword(ulong steamId, string password, string passKey)
        {
            if (steamId == 0)
                return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is invalid.");
            if (String.IsNullOrEmpty(password) || password.Length > EncryptedCredentialStore.MaximumPasswordLength)
                return StorageResult.Failure(StorageFailureKind.Validation, "Enter a valid Steam password.");
            if (!EncryptedCredentialStore.IsSupported)
                return StorageResult.Failure(StorageFailureKind.Encryption, "AES-GCM encryption is not supported on this system.");

            lock (storageLock)
            {
                if (!this.Encrypted || !this.AllowSavedSteamPasswords)
                    return StorageResult.Failure(StorageFailureKind.Validation, "Enable encryption and opt into saved Steam passwords first.");
                if (String.IsNullOrEmpty(passKey) || !IsPasskeyValidForEncryptedWriteLocked(passKey, steamId))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid.");
                if (Entries == null || !Entries.Any(entry => entry != null && entry.SteamID == steamId))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is no longer managed by this authenticator.");

                try
                {
                    Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: true);
                    records[steamId] = new SavedCredentialRecord
                    {
                        SteamId = steamId,
                        Password = password,
                        CredentialGenerationId = EncryptedCredentialStore.CreateNewGenerationId(),
                        PasswordRejected = false,
                        PasswordRejectedUtc = null,
                        UpdatedUtc = DateTime.UtcNow
                    };

                    return CommitCredentialRecordsLocked(records, passKey);
                }
                catch (CryptographicException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Encryption, "The saved credential store could not be decrypted with the current passkey.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The saved Steam password could not be stored securely.", ex);
                }
            }
        }

        public StorageResult RemoveAccountPassword(ulong steamId, string passKey)
        {
            if (steamId == 0)
                return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is invalid.");

            lock (storageLock)
            {
                if (String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    return StorageResult.Success();
                if (!this.Encrypted || String.IsNullOrEmpty(passKey) || !IsPasskeyValidForEncryptedWriteLocked(passKey, steamId))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid.");

                try
                {
                    Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: false);
                    if (records == null)
                    {
                        Manifest stagedManifest = CloneForStorage();
                        stagedManifest.CredentialsStoreFilename = null;
                        StorageResult clearResult = CommitStorageTransaction(stagedManifest, new Dictionary<string, string>(), Enumerable.Empty<string>());
                        if (clearResult.Succeeded)
                            CopyStorageStateFrom(stagedManifest);
                        return clearResult;
                    }

                    if (!records.Remove(steamId))
                        return StorageResult.Success();

                    return CommitCredentialRecordsLocked(records, passKey);
                }
                catch (CryptographicException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Encryption, "The saved credential store could not be decrypted with the current passkey.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The saved Steam password could not be removed.", ex);
                }
            }
        }

        public StorageResult MarkSavedPasswordRejected(
            string passKey,
            ulong steamId,
            string expectedCredentialGenerationId,
            DateTime? rejectedUtc = null)
        {
            if (steamId == 0 || String.IsNullOrWhiteSpace(expectedCredentialGenerationId))
                return StorageResult.Failure(StorageFailureKind.Validation, "Invalid credential rejection parameters.");

            lock (storageLock)
            {
                if (!this.Encrypted || !this.AllowSavedSteamPasswords || String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    return StorageResult.Success();
                if (String.IsNullOrEmpty(passKey) || !IsPasskeyValidForEncryptedWriteLocked(passKey, steamId))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid.");

                try
                {
                    Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: false);
                    if (records == null || !records.TryGetValue(steamId, out SavedCredentialRecord record))
                        return StorageResult.Success();

                    // Only mark rejected when the non-reusable GUID generation matches exactly.
                    if (!String.Equals(record.CredentialGenerationId, expectedCredentialGenerationId, StringComparison.Ordinal))
                        return StorageResult.Success();

                    if (record.PasswordRejected)
                        return StorageResult.Success();

                    record.PasswordRejected = true;
                    record.PasswordRejectedUtc = rejectedUtc ?? DateTime.UtcNow;
                    return CommitCredentialRecordsLocked(records, passKey);
                }
                catch (CryptographicException ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Encryption, "The saved credential store could not be decrypted.", ex);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "The saved-password rejection status could not be saved.", ex);
                }
            }
        }

        internal SavedCredentialRecord GetSavedCredentialRecord(ulong steamId, string passKey)
        {
            if (steamId == 0 || String.IsNullOrEmpty(passKey))
                return null;

            lock (storageLock)
            {
                if (!this.Encrypted || !this.AllowSavedSteamPasswords || String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    return null;

                return TryLoadSavedCredentialRecordLocked(steamId, passKey);
            }
        }

        private void InvalidateCredentialMetadataCacheLocked()
        {
            cachedCredentialMetadataFilename = null;
            cachedCredentialMetadataPassKeyHash = null;
            cachedCredentialMetadata = null;
            cachedCredentialMetadataFailed = false;
        }

        private Dictionary<ulong, SavedCredentialStatus> GetOrLoadCredentialMetadataCacheLocked(string passKey)
        {
            if (String.IsNullOrEmpty(passKey) ||
                !this.Encrypted ||
                !this.AllowSavedSteamPasswords ||
                String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
            {
                InvalidateCredentialMetadataCacheLocked();
                return null;
            }

            string passKeyHash = GetContentHash(passKey);
            if (String.Equals(cachedCredentialMetadataFilename, this.CredentialsStoreFilename, StringComparison.Ordinal) &&
                String.Equals(cachedCredentialMetadataPassKeyHash, passKeyHash, StringComparison.Ordinal))
            {
                return cachedCredentialMetadataFailed ? null : cachedCredentialMetadata;
            }

            try
            {
                Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: false);
                if (records == null)
                {
                    cachedCredentialMetadataFilename = this.CredentialsStoreFilename;
                    cachedCredentialMetadataPassKeyHash = passKeyHash;
                    cachedCredentialMetadata = new Dictionary<ulong, SavedCredentialStatus>();
                    cachedCredentialMetadataFailed = false;
                    return cachedCredentialMetadata;
                }

                Dictionary<ulong, SavedCredentialStatus> loaded = new Dictionary<ulong, SavedCredentialStatus>();
                foreach (KeyValuePair<ulong, SavedCredentialRecord> kvp in records)
                {
                    loaded[kvp.Key] = new SavedCredentialStatus(
                        hasSavedPassword: !String.IsNullOrEmpty(kvp.Value.Password),
                        passwordRejected: kvp.Value.PasswordRejected,
                        passwordRejectedUtc: kvp.Value.PasswordRejectedUtc,
                        credentialGenerationId: kvp.Value.CredentialGenerationId,
                        updatedUtc: kvp.Value.UpdatedUtc);
                }

                cachedCredentialMetadataFilename = this.CredentialsStoreFilename;
                cachedCredentialMetadataPassKeyHash = passKeyHash;
                cachedCredentialMetadata = loaded;
                cachedCredentialMetadataFailed = false;
                return loaded;
            }
            catch (Exception ex)
            {
                // Cache the failed state for this store generation + passkey so 1-second ticks
                // do not repeatedly run 600k PBKDF2 on the UI thread against a corrupt/unreadable store.
                cachedCredentialMetadataFilename = this.CredentialsStoreFilename;
                cachedCredentialMetadataPassKeyHash = passKeyHash;
                cachedCredentialMetadata = null;
                cachedCredentialMetadataFailed = true;
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "Saved credential statuses could not be loaded.");
                return null;
            }
        }

        public StorageResult PruneOrphanedSavedCredentials(string passKey)
        {
            if (String.IsNullOrEmpty(passKey))
                return StorageResult.Success();

            lock (storageLock)
            {
                if (!this.Encrypted || !this.AllowSavedSteamPasswords || String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                    return StorageResult.Success();

                try
                {
                    Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: false);
                    if (records == null || records.Count == 0)
                        return StorageResult.Success();

                    HashSet<ulong> activeSteamIds = new HashSet<ulong>(
                        (Entries ?? Enumerable.Empty<ManifestEntry>())
                            .Where(entry => entry != null && entry.SteamID != 0)
                            .Select(entry => entry.SteamID));
                    ulong[] orphanedIds = records.Keys.Where(id => !activeSteamIds.Contains(id)).ToArray();
                    if (orphanedIds.Length == 0)
                        return StorageResult.Success();

                    foreach (ulong orphanId in orphanedIds)
                        records.Remove(orphanId);

                    return CommitCredentialRecordsLocked(records, passKey);
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Io, "Orphaned saved credentials could not be pruned.", ex);
                }
            }
        }

        public SavedCredentialStatus GetSavedCredentialStatus(ulong steamId, string passKey)
        {
            if (steamId == 0 || String.IsNullOrEmpty(passKey))
                return SavedCredentialStatus.None;

            lock (storageLock)
            {
                Dictionary<ulong, SavedCredentialStatus> cache = GetOrLoadCredentialMetadataCacheLocked(passKey);
                if (cache != null && cache.TryGetValue(steamId, out SavedCredentialStatus status))
                    return status;
                return SavedCredentialStatus.None;
            }
        }

        public Dictionary<ulong, SavedCredentialStatus> GetSavedCredentialStatuses(string passKey)
        {
            if (String.IsNullOrEmpty(passKey))
                return new Dictionary<ulong, SavedCredentialStatus>();

            lock (storageLock)
            {
                Dictionary<ulong, SavedCredentialStatus> cache = GetOrLoadCredentialMetadataCacheLocked(passKey);
                return cache != null
                    ? new Dictionary<ulong, SavedCredentialStatus>(cache)
                    : new Dictionary<ulong, SavedCredentialStatus>();
            }
        }

        private SavedCredentialRecord TryLoadSavedCredentialRecordLocked(ulong steamId, string passKey)
        {
            try
            {
                Dictionary<ulong, SavedCredentialRecord> records = LoadCredentialRecordsLocked(passKey, createIfMissing: false);
                if (records != null && records.TryGetValue(steamId, out SavedCredentialRecord record))
                    return record.Clone();
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "A saved credential record could not be loaded.");
            }
            return null;
        }

        private Dictionary<ulong, SavedCredentialRecord> LoadCredentialRecordsLocked(string passKey, bool createIfMissing)
        {
            if (String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                return createIfMissing ? new Dictionary<ulong, SavedCredentialRecord>() : null;

            string maDir = Path.Combine(GetExecutableDir(), "maFiles");
            string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
            string fullPath = ResolveValidatedCredentialStoreFilePath(validatedRoot, this.CredentialsStoreFilename);
            if (!File.Exists(fullPath))
            {
                CredentialsStoreMissingWarning = true;
                return createIfMissing ? new Dictionary<ulong, SavedCredentialRecord>() : null;
            }

            string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
            string envelopeJson = StoragePathGuard.ReadTextWithLimit(fullPath, EncryptedCredentialStore.MaximumEnvelopeBytes, expectedCanonicalDir);
            return EncryptedCredentialStore.Decrypt(passKey, envelopeJson);
        }

        private StorageResult CommitCredentialRecordsLocked(Dictionary<ulong, SavedCredentialRecord> records, string passKey)
        {
            Manifest stagedManifest = CloneForStorage();
            Dictionary<string, string> stagedFiles = new Dictionary<string, string>();
            List<string> obsoleteFiles = new List<string>();
            if (!String.IsNullOrWhiteSpace(this.CredentialsStoreFilename))
                obsoleteFiles.Add(this.CredentialsStoreFilename);

            if (records == null || records.Count == 0)
            {
                stagedManifest.CredentialsStoreFilename = null;
            }
            else
            {
                string newCredFilename = EncryptedCredentialStore.CreateNewFilename();
                string envelope = EncryptedCredentialStore.Encrypt(passKey, records);
                stagedFiles.Add(newCredFilename, envelope);
                stagedManifest.CredentialsStoreFilename = newCredFilename;
            }

            StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
            if (result.Succeeded)
            {
                CopyStorageStateFrom(stagedManifest);
                CredentialsStoreMissingWarning = false;
            }
            return result;
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
            if (Entries != null)
            {
                foreach (ManifestEntry entry in Entries)
                {
                    if (entry?.TradeConfirmationCheckInterval.HasValue == true)
                    {
                        entry.TradeConfirmationCheckInterval = Math.Clamp(entry.TradeConfirmationCheckInterval.Value, 3, 3600);
                    }
                }
            }
        }

        public AccountConfirmationSettings GetAccountConfirmationSettings(ulong steamId)
        {
            if (steamId == 0)
                return new AccountConfirmationSettings();

            lock (storageLock)
            {
                ManifestEntry entry = Entries?.FirstOrDefault(item => item != null && item.SteamID == steamId);
                if (entry == null)
                    return new AccountConfirmationSettings();

                return new AccountConfirmationSettings
                {
                    AutoConfirmOverrideEnabled = entry.AutoConfirmOverrideEnabled,
                    AutoConfirmTrades = entry.AutoConfirmTrades,
                    AutoConfirmMarketTransactions = entry.AutoConfirmMarketTransactions,
                    TradeConfirmationCheckInterval = entry.TradeConfirmationCheckInterval.HasValue
                        ? Math.Clamp(entry.TradeConfirmationCheckInterval.Value, 3, 3600)
                        : (int?)null
                };
            }
        }

        public ResolvedAccountConfirmationPolicy ResolveConfirmationPolicy(ulong steamId, int eligibleInheritedCount = 1)
        {
            lock (storageLock)
            {
                int globalInterval = TradeConfirmationCustomIntervalEnabled
                    ? Math.Clamp(TradeConfirmationCheckInterval, 3, 3600)
                    : 15;

                ManifestEntry entry = steamId == 0
                    ? null
                    : Entries?.FirstOrDefault(item => item != null && item.SteamID == steamId);

                if (entry != null && entry.AutoConfirmOverrideEnabled)
                {
                    int customInterval = entry.TradeConfirmationCheckInterval.HasValue
                        ? Math.Clamp(entry.TradeConfirmationCheckInterval.Value, 3, 3600)
                        : globalInterval;

                    return new ResolvedAccountConfirmationPolicy(
                        overrideEnabled: true,
                        intervalSeconds: customInterval,
                        autoConfirmTrades: entry.AutoConfirmTrades,
                        autoConfirmMarketTransactions: entry.AutoConfirmMarketTransactions);
                }

                long inheritedIntervalLong = (long)globalInterval * Math.Max(1, eligibleInheritedCount);
                int inheritedInterval = inheritedIntervalLong > int.MaxValue ? int.MaxValue : (int)inheritedIntervalLong;

                return new ResolvedAccountConfirmationPolicy(
                    overrideEnabled: false,
                    intervalSeconds: inheritedInterval,
                    autoConfirmTrades: AutoConfirmTrades,
                    autoConfirmMarketTransactions: AutoConfirmMarketTransactions);
            }
        }

        public StorageResult SaveAccountConfirmationSettings(ulong steamId, AccountConfirmationSettings settings)
        {
            if (steamId == 0 || settings == null)
                return StorageResult.Failure(StorageFailureKind.Validation, "The account confirmation settings are invalid.");

            if (settings.TradeConfirmationCheckInterval.HasValue &&
                (settings.TradeConfirmationCheckInterval.Value < 3 || settings.TradeConfirmationCheckInterval.Value > 3600))
            {
                return StorageResult.Failure(StorageFailureKind.Validation, "The custom check interval must be between 3 and 3600 seconds.");
            }

            lock (storageLock)
            {
                ManifestEntry liveEntry = Entries?.FirstOrDefault(item => item != null && item.SteamID == steamId);
                if (liveEntry == null)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is no longer managed by this authenticator.");

                Manifest staged;
                try
                {
                    staged = CloneForStorage();
                    ManifestEntry stagedEntry = staged.Entries.FirstOrDefault(item => item != null && item.SteamID == steamId);
                    if (stagedEntry == null)
                        return StorageResult.Failure(StorageFailureKind.Validation, "The selected account is no longer managed by this authenticator.");

                    stagedEntry.AutoConfirmOverrideEnabled = settings.AutoConfirmOverrideEnabled;
                    stagedEntry.AutoConfirmTrades = settings.AutoConfirmTrades;
                    stagedEntry.AutoConfirmMarketTransactions = settings.AutoConfirmMarketTransactions;
                    stagedEntry.TradeConfirmationCheckInterval = settings.TradeConfirmationCheckInterval;
                }
                catch (Exception ex)
                {
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account confirmation settings could not be prepared.", ex);
                }

                StorageResult result = staged.SaveWithResult();
                if (result.Succeeded)
                {
                    liveEntry.AutoConfirmOverrideEnabled = settings.AutoConfirmOverrideEnabled;
                    liveEntry.AutoConfirmTrades = settings.AutoConfirmTrades;
                    liveEntry.AutoConfirmMarketTransactions = settings.AutoConfirmMarketTransactions;
                    liveEntry.TradeConfirmationCheckInterval = settings.TradeConfirmationCheckInterval;
                }

                return result;
            }
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

            [JsonProperty("auto_confirm_override_enabled", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AutoConfirmOverrideEnabled { get; set; }

            [JsonProperty("auto_confirm_trades", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AutoConfirmTrades { get; set; }

            [JsonProperty("auto_confirm_market_transactions", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool AutoConfirmMarketTransactions { get; set; }

            [JsonProperty("trade_confirmation_check_interval", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int? TradeConfirmationCheckInterval { get; set; }

            [JsonProperty("last_transfer_attempt_id", NullValueHandling = NullValueHandling.Ignore)]
            public string LastTransferAttemptId { get; set; }
        }
    }
}
