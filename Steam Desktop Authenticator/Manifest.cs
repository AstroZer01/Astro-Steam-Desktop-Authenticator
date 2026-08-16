using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth;
using System;
using System.Collections.Generic;
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

        private static Manifest _manifest { get; set; }
        private static readonly object storageLock = new object();
        private const string StorageJournalFilename = ".asda-storage-transaction.json";
        private const string SettingsBackupFilename = ".manifest.settings.bak";

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

            if (!RecoverPendingStorageTransaction(maDir, manifestFile))
                throw new ManifestParseException();
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
                string manifestContents = File.ReadAllText(manifestFile);
                JObject manifestJson = JObject.Parse(manifestContents);
                _manifest = manifestJson.ToObject<Manifest>();
                bool migratedLegacyTradeSettings = _manifest.MigrateLegacyTradeConfirmationSettings(manifestJson);

                _manifest.NormalizeTradeConfirmationSettings();
                _manifest.NormalizeLoginActionSettings();

                if (migratedLegacyTradeSettings)
                {
                    _manifest.Save();
                }

                if (_manifest.Encrypted && _manifest.Entries.Count == 0)
                {
                    _manifest.Encrypted = false;
                    _manifest.Save();
                }

                _manifest.RecomputeExistingEntries();

                lock (storageLock)
                {
                    DeleteFileBestEffort(
                        Path.Combine(maDir, SettingsBackupFilename),
                        "A stale manifest backup could not be removed after the settings were loaded.");
                }

                return _manifest;
            }
            catch (Exception)
            {
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
            newManifest.MinimizeToTray = true;
            newManifest.LoginActionMonitoringEnabled = true;
            newManifest.LoginActionMode = LoginActionModes.Manual;
            newManifest.LoginActionAutoAllowIpEnabled = false;
            newManifest.LoginActionAutoAllowCurrentDeviceIp = false;
            newManifest.LoginActionAutoAllowIp = String.Empty;
            newManifest.Entries = new List<ManifestEntry>();
            newManifest.FirstRun = true;

            // Take a pre-manifest version and generate a manifest for it.
            if (!scanDir)
            {
                return newManifest;
            }

            string maDir = Manifest.GetExecutableDir() + "/maFiles/";
            if (!Directory.Exists(maDir))
            {
                return newManifest;
            }

            DirectoryInfo dir = new DirectoryInfo(maDir);
            var files = dir.GetFiles();

            foreach (var file in files)
            {
                if (file.Extension != ".maFile") continue;

                string contents = File.ReadAllText(file.FullName);
                try
                {
                    SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(contents);
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
            if (passKey == null && this.Encrypted) return new SteamGuardAccount[0];
            string maDir = Manifest.GetExecutableDir() + "/maFiles/";

            List<SteamAuth.SteamGuardAccount> accounts = new List<SteamAuth.SteamGuardAccount>();
            foreach (var entry in this.Entries)
            {
                string fileText = File.ReadAllText(maDir + entry.Filename);
                if (this.Encrypted)
                {
                    string decryptedText = FileEncryptor.DecryptData(passKey, entry.Salt, entry.IV, fileText);
                    if (decryptedText == null) return new SteamGuardAccount[0];
                    fileText = decryptedText;
                }

                var account = JsonConvert.DeserializeObject<SteamAuth.SteamGuardAccount>(fileText);
                if (account == null) continue;
                accounts.Add(account);

                if (limit != -1 && limit >= accounts.Count)
                    break;
            }

            return accounts.ToArray();
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

                for (int i = 0; i < this.Entries.Count; i++)
                {
                    ManifestEntry existingEntry = this.Entries[i];
                    string existingFilename = Path.Combine(maDir, existingEntry.Filename);
                    if (!File.Exists(existingFilename))
                    {
                        return StorageResult.Failure(StorageFailureKind.Validation, "One of the local authenticator files is missing. Encryption settings were not changed.");
                    }

                    string fileContents = File.ReadAllText(existingFilename);
                    if (this.Encrypted)
                    {
                        fileContents = FileEncryptor.DecryptData(oldKey, existingEntry.Salt, existingEntry.IV, fileContents);
                        if (fileContents == null)
                        {
                            return StorageResult.Failure(StorageFailureKind.Encryption, "The existing authenticator files could not be decrypted. Encryption settings were not changed.");
                        }
                    }
                    if (JsonConvert.DeserializeObject<SteamGuardAccount>(fileContents)?.Session == null)
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
                StorageResult result = CommitStorageTransaction(stagedManifest, stagedFiles, obsoleteFiles);
                if (result.Succeeded)
                    CopyStorageStateFrom(stagedManifest);
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
            if (!this.Encrypted || this.Entries.Count == 0) return true;

            var accounts = this.GetAllAccounts(passkey, 1);
            return accounts != null && accounts.Length == 1;
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
                    string contents = File.ReadAllText(Path.Combine(maDir, candidate.Filename));
                    if (Encrypted)
                    {
                        contents = FileEncryptor.DecryptData(passKey, candidate.Salt, candidate.IV, contents);
                        if (contents == null)
                            return null;
                    }
                    SteamGuardAccount storedAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(contents);
                    if (storedAccount != null && String.Equals(storedAccount.AccountName, account.AccountName, StringComparison.Ordinal))
                        matchingEntries.Add(candidate);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "Could not inspect a local account while preparing its removal.");
                    return null;
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

        public StorageResult SaveAccount(SteamGuardAccount account, bool encrypt, string passKey = null)
        {
            lock (storageLock)
            {
                if (account == null || account.Session == null || account.Session.SteamID == 0)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account data is incomplete and could not be saved.");
                if (encrypt && String.IsNullOrEmpty(passKey))
                    return StorageResult.Failure(StorageFailureKind.Validation, "An encryption passkey is required to save this account.");
                if (!encrypt && this.Encrypted)
                    return StorageResult.Failure(StorageFailureKind.Validation, "The account must be saved using the existing encryption passkey.");

                try
                {
                if (this.Encrypted && !this.VerifyPasskey(passKey))
                    return StorageResult.Failure(StorageFailureKind.Validation, "The encryption passkey is invalid. The account was not saved.");

                string salt = null;
                string iV = null;
                string jsonAccount = JsonConvert.SerializeObject(account);
                SteamGuardAccount validatedAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(jsonAccount);
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
                string filename = account.Session.SteamID + "." + Guid.NewGuid().ToString("N") + ".maFile";
                ManifestEntry newEntry = new ManifestEntry()
                {
                    SteamID = account.Session.SteamID,
                    IV = iV,
                    Salt = salt,
                    Filename = filename
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
                    CopyStorageStateFrom(stagedManifest);
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
                    Directory.CreateDirectory(maDir);
                    if (!RecoverPendingStorageTransaction(maDir, Path.Combine(maDir, "manifest.json")))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data save could not be recovered safely. No settings were changed.");
                    }
                    string contents = JsonConvert.SerializeObject(this);
                    string backupFilename = Path.Combine(maDir, SettingsBackupFilename);
                    WriteAllTextAtomically(Path.Combine(maDir, "manifest.json"), contents, backupFilename);
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

        private Manifest CloneForStorage()
        {
            Manifest clone = JsonConvert.DeserializeObject<Manifest>(JsonConvert.SerializeObject(this));
            if (clone == null)
                throw new JsonSerializationException("The manifest could not be cloned for a storage transaction.");
            clone.Entries ??= new List<ManifestEntry>();
            return clone;
        }

        private void CopyStorageStateFrom(Manifest source)
        {
            Entries = source.Entries;
            Encrypted = source.Encrypted;
        }

        private void CopySettingsInto(Manifest destination)
        {
            destination.FirstRun = FirstRun;
            destination.FirstQR = FirstQR;
            destination.TradeConfirmationCustomIntervalEnabled = TradeConfirmationCustomIntervalEnabled;
            destination.TradeConfirmationCheckInterval = TradeConfirmationCheckInterval;
            destination.AutoConfirmMarketTransactions = AutoConfirmMarketTransactions;
            destination.AutoConfirmTrades = AutoConfirmTrades;
            destination.MinimizeToTray = MinimizeToTray;
            destination.CheckForUpdates = CheckForUpdates;
            destination.DiagnosticErrorLoggingEnabled = DiagnosticErrorLoggingEnabled;
            destination.LoginActionMonitoringEnabled = LoginActionMonitoringEnabled;
            destination.LoginActionMode = LoginActionMode;
            destination.LoginActionAutoAllowIpEnabled = LoginActionAutoAllowIpEnabled;
            destination.LoginActionAutoAllowCurrentDeviceIp = LoginActionAutoAllowCurrentDeviceIp;
            destination.LoginActionAutoAllowIp = LoginActionAutoAllowIp;
        }

        private StorageResult CommitStorageTransaction(
            Manifest stagedManifest,
            IReadOnlyDictionary<string, string> stagedFiles,
            IEnumerable<string> obsoleteFiles)
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

                try
                {
                    // A cleanup failure leaves the previous journal intentionally. Complete it
                    // before replacing it so its obsolete files remain recoverable.
                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (!RecoverPendingStorageTransaction(maDir, manifestFilename, retainedObsoleteFilenames))
                    {
                        return StorageResult.Failure(StorageFailureKind.Io, "A previous account-data transaction could not be recovered safely. No account data was changed.");
                    }

                    foreach (string filename in retainedObsoleteFilenames)
                    {
                        if (!obsoleteFilenames.Contains(filename, StringComparer.OrdinalIgnoreCase))
                            obsoleteFilenames.Add(filename);
                    }

                    CopySettingsInto(stagedManifest);
                    string manifestContents = JsonConvert.SerializeObject(stagedManifest);
                    Directory.CreateDirectory(maDir);

                    foreach (string filename in obsoleteFilenames)
                        ValidateStorageFilename(filename);

                    foreach (string filename in createdFilenames)
                        ValidateStorageFilename(filename);

                    StorageTransactionJournal journal = new StorageTransactionJournal()
                    {
                        ManifestHash = GetContentHash(manifestContents),
                        CreatedFilenames = createdFilenames,
                        ObsoleteFilenames = obsoleteFilenames,
                        BackupFilename = Path.GetFileName(backupFilename)
                    };
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal));

                    foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
                        WriteAllTextAtomically(Path.Combine(maDir, stagedFile.Key), stagedFile.Value);

                    manifestCommitStarted = true;
                    WriteAllTextAtomically(manifestFilename, manifestContents, backupFilename);
                    manifestCommitted = true;

                    bool cleanupSucceeded = DeleteFilesBestEffort(maDir, obsoleteFilenames, "The replaced authenticator file could not be removed after the manifest was committed.");
                    if (cleanupSucceeded && DeleteFileBestEffort(backupFilename, "The completed manifest backup could not be removed."))
                    {
                        DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed.");
                    }
                    return StorageResult.Success();
                }
                catch (JsonException ex)
                {
                    if (!manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, manifestCommitStarted);
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be prepared for saving.", ex);
                }
                catch (NotSupportedException ex)
                {
                    if (!manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, manifestCommitStarted);
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The selected data folder does not support the safe atomic saves required for authenticator data. Move the data folder to a local drive and try again.",
                        ex);
                }
                catch (Exception ex)
                {
                    if (!manifestCommitted)
                        RollBackUncommittedTransaction(maDir, manifestFilename, journalFilename, backupFilename, createdFilenames, manifestCommitStarted);
                    return StorageResult.Failure(
                        manifestCommitStarted ? StorageFailureKind.Manifest : StorageFailureKind.Io,
                        manifestCommitStarted
                            ? "The account manifest could not be saved. The existing account data was kept unchanged."
                            : "The account files could not be saved. The existing account data was kept unchanged.",
                        ex);
                }
            }
        }

        private static void RollBackUncommittedTransaction(string maDir, string manifestFilename, string journalFilename, string backupFilename, IEnumerable<string> createdFilenames, bool manifestCommitStarted)
        {
            if (manifestCommitStarted && !RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, true))
                return;

            if (DeleteFilesBestEffort(maDir, createdFilenames, "A temporary authenticator file could not be removed after a failed save."))
            {
                DeleteFileBestEffort(journalFilename, "A storage journal could not be removed after a failed save.");
                DeleteFileBestEffort(backupFilename, "A manifest backup could not be removed after a failed save.");
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
                    StorageTransactionJournal journal = JsonConvert.DeserializeObject<StorageTransactionJournal>(File.ReadAllText(journalFilename));
                    if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                        throw new InvalidDataException("The pending storage journal is invalid.");

                    List<string> created = journal.CreatedFilenames ?? new List<string>();
                    List<string> obsolete = journal.ObsoleteFilenames ?? new List<string>();
                    foreach (string filename in created.Concat(obsolete))
                        ValidateStorageFilename(filename);

                    string backupFilename = String.IsNullOrWhiteSpace(journal.BackupFilename)
                        ? null
                        : Path.Combine(maDir, Path.GetFileName(journal.BackupFilename));
                    if (!RestoreManifestBackupIfNeeded(manifestFilename, backupFilename, false))
                        return false;

                    bool manifestCommitted = File.Exists(manifestFilename) &&
                        String.Equals(GetContentHash(File.ReadAllText(manifestFilename)), journal.ManifestHash, StringComparison.Ordinal);
                    bool cleanupSucceeded = manifestCommitted
                        ? DeleteFilesBestEffort(maDir, obsolete, "An obsolete authenticator file could not be removed during storage recovery.")
                        : DeleteFilesBestEffort(maDir, created, "A temporary authenticator file could not be removed during storage recovery.");

                    if (!cleanupSucceeded)
                    {
                        if (!manifestCommitted)
                            return false;

                        // The manifest is committed, so its newly created files are live.
                        // Retain only the obsolete-file cleanup state before allowing a
                        // later settings save to replace the manifest.
                        journal.CreatedFilenames = new List<string>();
                        WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal));
                        if (retainedObsoleteFilenames != null)
                        {
                            foreach (string filename in obsolete)
                                retainedObsoleteFilenames.Add(filename);
                        }
                        return true;
                    }

                    bool backupDeleted = backupFilename == null ||
                        DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.");
                    if (!backupDeleted)
                        return false;

                    bool journalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.");
                    return journalDeleted && !File.Exists(journalFilename);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "A pending storage transaction could not be recovered automatically.");
                    QuarantineStorageJournal(journalFilename);
                    return false;
                }
            }
        }

        private static bool RestoreManifestBackupIfNeeded(string manifestFilename, string backupFilename, bool overwriteManifest)
        {
            if (String.IsNullOrWhiteSpace(backupFilename) || !File.Exists(backupFilename))
                return !overwriteManifest || File.Exists(manifestFilename);
            if (!overwriteManifest && File.Exists(manifestFilename))
                return true;

            try
            {
                WriteAllTextAtomically(manifestFilename, File.ReadAllText(backupFilename));
                return File.Exists(manifestFilename);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The manifest backup could not be restored safely.");
                return false;
            }
        }

        private static void QuarantineStorageJournal(string journalFilename)
        {
            try
            {
                if (!File.Exists(journalFilename))
                    return;

                string directory = Path.GetDirectoryName(journalFilename);
                string filename = Path.GetFileName(journalFilename);
                string quarantineFilename = Path.Combine(directory, filename + "." + Guid.NewGuid().ToString("N") + ".quarantine");
                File.Move(journalFilename, quarantineFilename);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The unrecoverable storage journal could not be quarantined.");
            }
        }

        private static void WriteAllTextAtomically(string filename, string contents, string backupFilename = null)
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
                }
                else
                    File.Move(temporaryFilename, filename);
            }
            finally
            {
                if (File.Exists(temporaryFilename))
                    File.Delete(temporaryFilename);
            }
        }

        private static string CreateBackupFilename(string maDir)
        {
            return Path.Combine(maDir, ".manifest." + Guid.NewGuid().ToString("N") + ".bak");
        }

        private static string GetContentHash(string contents)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(contents)));
            }
        }

        private static void ValidateStorageFilename(string filename)
        {
            if (String.IsNullOrWhiteSpace(filename) || !String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal))
                throw new InvalidDataException("A storage transaction contained an invalid filename.");
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

            [JsonProperty("backup_filename")]
            public string BackupFilename { get; set; }
        }

        private void RecomputeExistingEntries()
        {
            List<ManifestEntry> newEntries = new List<ManifestEntry>();
            string maDir = Manifest.GetExecutableDir() + "/maFiles/";

            foreach (var entry in this.Entries)
            {
                string filename = maDir + entry.Filename;
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

        public void MoveEntry(int from, int to)
        {
            if (from < 0 || to < 0 || from > Entries.Count || to > Entries.Count - 1) return;
            ManifestEntry sel = Entries[from];
            Entries.RemoveAt(from);
            Entries.Insert(to, sel);
            Save();
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
        }
    }
}
