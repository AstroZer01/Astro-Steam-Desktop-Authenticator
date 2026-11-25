using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    public partial class Manifest
    {
        [JsonProperty("storage_transaction_id", NullValueHandling = NullValueHandling.Ignore)]
        internal string StorageTransactionId { get; set; }

        private static long storageTransactionCommitCount;
        private static long storageMutationEpoch;

        private const string StorageJournalFilename = ".asda-storage-transaction.json";
        private const int CurrentStorageJournalVersion = 2;
        private const string SettingsBackupFilename = ".manifest.settings.bak";
        private const string StorageBackupFilenamePrefix = ".manifest.";
        private const string AccountBackupFilenamePrefix = ".mafile.";
        private const string StorageBackupFilenameSuffix = ".bak";
        private const long MaximumAccountBackupSizeBytes = 64L * 1024 * 1024;

        internal static long StorageTransactionCommitCount => Interlocked.Read(ref storageTransactionCommitCount);
        internal static Func<string, bool> BeforeDeleteFileForTesting { get; set; }
        internal static Action OnAfterReplaceFilesBeforeManifestCommitForTest { get; set; }
        internal static Action OnBeforeRebaseRetainedStorageTransactionForTest { get; set; }

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

        private sealed class ManifestRecoveryOwnership
        {
            public ManifestRecoveryOwnership(
                IReadOnlyDictionary<string, ManifestEntry> accountEntries,
                HashSet<string> allLiveFilenames,
                bool encrypted)
            {
                AccountEntries = accountEntries;
                AllLiveFilenames = allLiveFilenames;
                Encrypted = encrypted;
            }

            public IReadOnlyDictionary<string, ManifestEntry> AccountEntries { get; }
            public HashSet<string> AllLiveFilenames { get; }
            public bool Encrypted { get; }
        }

        private static AccountFileTargetPlan PlanAccountFileTarget(
            string maDir,
            ulong steamId,
            string ownedFilename,
            ISet<string> referencedManifestFilenames,
            ISet<string> plannedTargetFilenames,
            ISet<string> batchSourceFilenames,
            string expectedCanonicalDir)
        {
            string canonicalFilename = GetCanonicalMaFileFilename(steamId);
            string canonicalPath = Path.Combine(maDir, canonicalFilename);

            if (!String.IsNullOrWhiteSpace(ownedFilename) &&
                String.Equals(ownedFilename, canonicalFilename, StringComparison.OrdinalIgnoreCase) &&
                !plannedTargetFilenames.Contains(canonicalFilename) &&
                !String.IsNullOrWhiteSpace(expectedCanonicalDir) &&
                File.Exists(Path.Combine(maDir, "manifest.json")) &&
                StoragePathGuard.TryInspectCanonicalTargetForInPlaceReplacement(
                    canonicalPath,
                    expectedCanonicalDir,
                    MaximumAccountBackupSizeBytes))
            {
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

        private static bool TryRecoverPendingJournalBeforeReadLocked(
            string maDir,
            string manifestPath,
            out bool retainedCleanupJournalPresent)
        {
            retainedCleanupJournalPresent = false;
            string journalPath = Path.Combine(maDir, StorageJournalFilename);
            if (!File.Exists(journalPath))
                return true;

            using (StoragePathGuard.PinnedStorageDirectory pinnedDir = StoragePathGuard.TryPinExistingDirectory(maDir, out string validatedRoot))
            {
                if (pinnedDir == null)
                    return true;

                List<string> retainedObsolete = new List<string>();
                bool recovered = RecoverPendingStorageTransaction(
                    maDir,
                    manifestPath,
                    retainedObsolete,
                    pinnedDir.CanonicalDirectoryPath,
                    validatedRoot,
                    allowLockedCompletedJournal: true);
                retainedCleanupJournalPresent = recovered && File.Exists(journalPath);
                return recovered;
            }
        }

        private StorageResult CommitStorageTransaction(
            Manifest stagedManifest,
            IReadOnlyDictionary<string, string> stagedFiles,
            IEnumerable<string> obsoleteFiles,
            IReadOnlyDictionary<string, string> replacedFiles = null,
            bool preserveStagedSettings = false,
            StoragePathGuard.PinnedStorageDirectory existingPinnedDir = null)
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
                Dictionary<string, string> createdFileIdentities = null;
                string previousManifestHash = null;
                List<string> obsoleteFilenames = obsoleteFiles
                    .Where(filename => !String.IsNullOrWhiteSpace(filename))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool manifestCommitted = false;
                bool manifestCommitStarted = false;
                bool journalWritten = false;
                List<string> actuallyCreatedFilenames = new List<string>();
                List<string> trackedRetainedObsoleteFilenames = new List<string>();
                List<StoragePathGuard.StagedTempFile> stagedTempFiles = new List<StoragePathGuard.StagedTempFile>();
                List<StoragePathGuard.LockedReplacementFile> lockedReplacements = new List<StoragePathGuard.LockedReplacementFile>();
                StoragePathGuard.PinnedStorageDirectory pinnedDir = existingPinnedDir;
                bool ownsPinnedDir = false;

                try
                {
                    string validatedRoot;
                    if (pinnedDir != null)
                    {
                        pinnedDir.VerifyStillPinned();
                        validatedRoot = pinnedDir.ValidatedRoot;
                    }
                    else
                    {
                        pinnedDir = StoragePathGuard.TryPinExistingDirectory(maDir, out validatedRoot);
                        ownsPinnedDir = pinnedDir != null;
                    }

                    // A cleanup failure leaves the previous journal intentionally. Complete it
                    // before replacing it so its obsolete files remain recoverable.
                    List<string> retainedObsoleteFilenames = new List<string>();
                    if (pinnedDir != null &&
                        File.Exists(journalFilename) &&
                        !RecoverPendingStorageTransaction(
                            maDir,
                            manifestFilename,
                            retainedObsoleteFilenames,
                            pinnedDir.CanonicalDirectoryPath,
                            pinnedDir.ValidatedRoot))
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

                    stagedManifest.StorageTransactionId = Guid.NewGuid().ToString("N");
                    ValidateManifestState(
                        stagedManifest.Entries,
                        stagedManifest.Encrypted,
                        validatedRoot,
                        stagedManifest.CredentialsStoreFilename);
                    string manifestContents = JsonConvert.SerializeObject(stagedManifest, storageJsonSettings);
                    if (Encoding.UTF8.GetByteCount(manifestContents) > MaximumManifestFileSizeBytes)
                        throw new InvalidDataException("The account manifest is larger than the supported size limit.");

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

                    if (pinnedDir == null)
                    {
                        pinnedDir = StoragePathGuard.CreateAndPinDirectory(maDir);
                        ownsPinnedDir = true;
                        validatedRoot = pinnedDir.ValidatedRoot;
                    }

                    pinnedDir.VerifyStillPinned();
                    string canonicalDir = pinnedDir.CanonicalDirectoryPath;

                    string previousManifestContents = File.Exists(manifestFilename)
                        ? StoragePathGuard.ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes, canonicalDir)
                        : null;
                    previousManifestHash = previousManifestContents != null
                        ? GetContentHash(previousManifestContents)
                        : null;
                    ManifestRecoveryOwnership previousOwnership = replacedTargetFilenames.Count > 0
                        ? ParseManifestOwnershipForRecovery(previousManifestContents, validatedRoot)
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

                    if (Path.Exists(backupFilename))
                        throw new IOException("The manifest backup path is already occupied.");

                    // Pre-stage all created files so their durable file identities are captured before
                    // writing the transaction journal (preventing recovery from ever unlinking a
                    // byte-identical file created by another writer).
                    if (stagedFiles.Count > 0)
                    {
                        createdFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        createdFileIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
                        {
                            StoragePathGuard.StagedTempFile temp = StoragePathGuard.StageNewFileWithIdentity(
                                Path.Combine(maDir, stagedFile.Key),
                                stagedFile.Value,
                                canonicalDir);
                            stagedTempFiles.Add(temp);
                            createdFileHashes[stagedFile.Key] = temp.ContentHash;
                            if (!temp.FileIdentity.IsValid)
                                throw new NotSupportedException("The selected data folder does not provide verifiable file identities required for safe storage transactions.");
                            createdFileIdentities[stagedFile.Key] = temp.FileIdentity.ToToken();
                        }
                    }

                    // Lock every replaced target exclusively and write its CREATE_NEW backup in a single
                    // verified streaming pass before writing the journal, eliminating target-substitution
                    // and backup-overwrite races across commit and synchronous rollback.
                    foreach (string filename in replacedTargetFilenames)
                    {
                        ValidateStorageFilename(filename);
                        if (!previousOwnership.AccountEntries.TryGetValue(filename, out ManifestEntry previousEntry))
                            throw new InvalidDataException("A storage transaction attempted to replace a file not owned by the current manifest.");
                        string accountBackupFilename = CreateAccountBackupFilename(previousEntry.SteamID);
                        string accountBackupPath = GetValidatedAccountBackupPath(maDir, accountBackupFilename, previousEntry.SteamID);
                        if (!transactionFilenames.Add(filename) || !transactionFilenames.Add(accountBackupFilename))
                            throw new InvalidDataException("A storage transaction reused a filename across its file lists.");

                        StoragePathGuard.LockedReplacementFile lockedTarget = StoragePathGuard.OpenAndBackUpForInPlaceReplacement(
                            Path.Combine(maDir, filename),
                            accountBackupPath,
                            canonicalDir,
                            MaximumAccountBackupSizeBytes);
                        lockedReplacements.Add(lockedTarget);

                        replacedFileRecords.Add(new StorageTransactionReplacedFile
                        {
                            Filename = filename,
                            BackupFilename = accountBackupFilename,
                            OriginalHash = lockedTarget.OriginalHash,
                            ReplacementHash = GetContentHash(replacedFiles[filename]),
                            TargetFileIdentity = lockedTarget.TargetIdentity.ToToken()
                        });
                    }

                    // Advance the mutation epoch before writing the journal or mutating any live file
                    // so concurrent optimistic readers detect both committed and rolled-back writes.
                    Interlocked.Increment(ref storageMutationEpoch);

                    StorageTransactionJournal journal = new StorageTransactionJournal()
                    {
                        JournalVersion = CurrentStorageJournalVersion,
                        ManifestHash = GetContentHash(manifestContents),
                        PreviousManifestHash = previousManifestHash,
                        CreatedFilenames = createdFilenames,
                        CreatedFileHashes = createdFileHashes,
                        CreatedFileIdentities = createdFileIdentities,
                        ReplacedFiles = replacedFileRecords.Count > 0 ? replacedFileRecords : null,
                        ObsoleteFilenames = obsoleteFilenames,
                        RetainedObsoleteFilenames = trackedRetainedObsoleteFilenames.Count > 0 ? trackedRetainedObsoleteFilenames : null,
                        BackupFilename = Path.GetFileName(backupFilename)
                    };
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                    journalWritten = true;

                    foreach (StoragePathGuard.StagedTempFile temp in stagedTempFiles)
                    {
                        temp.PublishCreateNew(pinnedDir);
                        actuallyCreatedFilenames.Add(Path.GetFileName(temp.TargetPath));
                    }

                    UTF8Encoding utf8NoBom = new UTF8Encoding(false);
                    for (int i = 0; i < replacedFileRecords.Count; i++)
                    {
                        StorageTransactionReplacedFile record = replacedFileRecords[i];
                        byte[] replacementBytes = utf8NoBom.GetBytes(replacedFiles[record.Filename]);
                        lockedReplacements[i].OverwriteAndFlush(replacementBytes);
                    }

                    OnAfterReplaceFilesBeforeManifestCommitForTest?.Invoke();

                    pinnedDir.VerifyStillPinned();
                    manifestCommitStarted = true;
                    WriteAllTextAtomically(manifestFilename, manifestContents, backupFilename, canonicalDir);
                    manifestCommitted = true;

                    // Release exclusive handles on replaced targets now that manifest.json is committed.
                    DisposeLockedReplacements(lockedReplacements);

                    if (!pinnedDir.TryVerifyStillPinned(out Exception postCommitPinError))
                    {
                        // Manifest is already committed on disk; preserve the journal for safe recovery
                        // and report success with cleanup pending so callers still publish staged state.
                        DiagnosticErrorLogger.Log("Authenticator storage", postCommitPinError, "Skipping post-commit cleanup because the account-data directory changed after the manifest was committed.");
                        return StorageResult.Success(obsoleteCleanupPending: true);
                    }

                    bool backupDeleted = DeleteFileBestEffort(
                        backupFilename,
                        "The completed manifest backup could not be removed.",
                        canonicalDir);
                    bool replacedBackupsDeleted = backupDeleted &&
                        DeleteFilesBestEffort(
                            maDir,
                            replacedFileRecords.Select(record => record.BackupFilename),
                            "A replaced authenticator backup could not be removed after the manifest was committed.",
                            canonicalDir);
                    bool obsoleteDeleted = replacedBackupsDeleted &&
                        DeleteFilesBestEffort(
                            maDir,
                            obsoleteFilenames.Concat(trackedRetainedObsoleteFilenames),
                            "The replaced authenticator file could not be removed after the manifest was committed.",
                            canonicalDir);
                    bool journalDeleted = obsoleteDeleted &&
                        DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed.", canonicalDir);
                    bool orphanedBackupsSwept = true;
                    if (journalDeleted)
                    {
                        // Only sweep orphaned credential stores and pre-journal account backups
                        // once the manifest backup and transaction journal are completely gone.
                        SweepOrphanedCredentialStoresLocked(maDir, stagedManifest.CredentialsStoreFilename, canonicalDir);
                        orphanedBackupsSwept = SweepOrphanedAccountBackupsLocked(maDir, canonicalDir);
                    }
                    return StorageResult.Success(obsoleteCleanupPending: !(journalDeleted && orphanedBackupsSwept));
                }
                catch (JsonException ex)
                {
                    HandleFailedCommit(
                        maDir,
                        manifestFilename,
                        journalFilename,
                        backupFilename,
                        previousManifestHash,
                        actuallyCreatedFilenames,
                        createdFileHashes,
                        createdFileIdentities,
                        replacedFileRecords,
                        lockedReplacements,
                        trackedRetainedObsoleteFilenames,
                        manifestCommitStarted,
                        manifestCommitted,
                        journalWritten,
                        pinnedDir?.CanonicalDirectoryPath,
                        pinnedDir?.ValidatedRoot);
                    return StorageResult.Failure(StorageFailureKind.Serialization, "The account data could not be prepared for saving.", ex);
                }
                catch (NotSupportedException ex)
                {
                    HandleFailedCommit(
                        maDir,
                        manifestFilename,
                        journalFilename,
                        backupFilename,
                        previousManifestHash,
                        actuallyCreatedFilenames,
                        createdFileHashes,
                        createdFileIdentities,
                        replacedFileRecords,
                        lockedReplacements,
                        trackedRetainedObsoleteFilenames,
                        manifestCommitStarted,
                        manifestCommitted,
                        journalWritten,
                        pinnedDir?.CanonicalDirectoryPath,
                        pinnedDir?.ValidatedRoot);
                    return StorageResult.Failure(
                        StorageFailureKind.Io,
                        "The selected data folder does not support the safe atomic saves required for authenticator data. Move the data folder to a local drive and try again.",
                        ex);
                }
                catch (Exception ex)
                {
                    HandleFailedCommit(
                        maDir,
                        manifestFilename,
                        journalFilename,
                        backupFilename,
                        previousManifestHash,
                        actuallyCreatedFilenames,
                        createdFileHashes,
                        createdFileIdentities,
                        replacedFileRecords,
                        lockedReplacements,
                        trackedRetainedObsoleteFilenames,
                        manifestCommitStarted,
                        manifestCommitted,
                        journalWritten,
                        pinnedDir?.CanonicalDirectoryPath,
                        pinnedDir?.ValidatedRoot);
                    return StorageResult.Failure(
                        manifestCommitStarted ? StorageFailureKind.Manifest : StorageFailureKind.Io,
                        manifestCommitStarted
                            ? "The account manifest could not be saved. The existing account data was kept unchanged."
                            : "The account files could not be saved. The existing account data was kept unchanged.",
                        ex);
                }
                finally
                {
                    DisposeLockedReplacements(lockedReplacements);
                    foreach (StoragePathGuard.StagedTempFile temp in stagedTempFiles)
                        temp.Dispose();
                    if (ownsPinnedDir)
                        pinnedDir?.Dispose();
                }
            }
        }

        private static void HandleFailedCommit(
            string maDir,
            string manifestFilename,
            string journalFilename,
            string backupFilename,
            string previousManifestHash,
            IReadOnlyList<string> actuallyCreatedFilenames,
            IReadOnlyDictionary<string, string> createdFileHashes,
            IReadOnlyDictionary<string, string> createdFileIdentities,
            IReadOnlyList<StorageTransactionReplacedFile> replacedFileRecords,
            List<StoragePathGuard.LockedReplacementFile> lockedReplacements,
            IReadOnlyList<string> trackedRetainedObsoleteFilenames,
            bool manifestCommitStarted,
            bool manifestCommitted,
            bool journalWritten,
            string expectedCanonicalDir,
            string validatedRoot)
        {
            if (manifestCommitted)
                return;

            if (journalWritten)
            {
                Interlocked.Increment(ref storageMutationEpoch);
                try
                {
                    RollBackUncommittedTransaction(
                        maDir,
                        manifestFilename,
                        journalFilename,
                        backupFilename,
                        previousManifestHash,
                        actuallyCreatedFilenames,
                        createdFileHashes,
                        createdFileIdentities,
                        replacedFileRecords,
                        lockedReplacements,
                        trackedRetainedObsoleteFilenames,
                        manifestCommitStarted,
                        expectedCanonicalDir,
                        validatedRoot);
                }
                finally
                {
                    Interlocked.Increment(ref storageMutationEpoch);
                }
            }
            else if (lockedReplacements.Count > 0)
            {
                // Failed before writing the journal: targets were never modified; release handles and
                // delete any pre-journal backup copies we reserved.
                List<string> backupPaths = lockedReplacements.Select(item => item.BackupPath).ToList();
                DisposeLockedReplacements(lockedReplacements);
                if (!String.IsNullOrWhiteSpace(expectedCanonicalDir))
                {
                    foreach (string path in backupPaths)
                    {
                        DeleteFileBestEffort(path, "A pre-journal authenticator backup could not be removed after a failed save.", expectedCanonicalDir);
                    }
                }
            }
        }

        private static void DisposeLockedReplacements(List<StoragePathGuard.LockedReplacementFile> lockedReplacements)
        {
            if (lockedReplacements == null || lockedReplacements.Count == 0)
                return;

            foreach (StoragePathGuard.LockedReplacementFile locked in lockedReplacements)
                locked.Dispose();
            lockedReplacements.Clear();
        }

        private static void RollBackUncommittedTransaction(
            string maDir,
            string manifestFilename,
            string journalFilename,
            string backupFilename,
            string previousManifestHash,
            IReadOnlyList<string> createdFilenames,
            IReadOnlyDictionary<string, string> createdFileHashes,
            IReadOnlyDictionary<string, string> createdFileIdentities,
            IReadOnlyList<StorageTransactionReplacedFile> replacedFiles,
            List<StoragePathGuard.LockedReplacementFile> lockedReplacements,
            IReadOnlyList<string> retainedObsoleteFilenames,
            bool manifestCommitStarted,
            string expectedCanonicalDir,
            string validatedRoot)
        {
            if (manifestCommitStarted &&
                !RestoreValidatedManifestBackupIfNeeded(
                    maDir,
                    manifestFilename,
                    backupFilename,
                    previousManifestHash,
                    true,
                    expectedCanonicalDir,
                    validatedRoot))
            {
                DisposeLockedReplacements(lockedReplacements);
                return;
            }

            bool replacedRestored = true;
            if (lockedReplacements != null && lockedReplacements.Count > 0)
            {
                foreach (StoragePathGuard.LockedReplacementFile locked in lockedReplacements)
                {
                    if (!locked.Overwritten)
                        continue;

                    try
                    {
                        locked.RestoreFromVerifiedBackup(expectedCanonicalDir);
                    }
                    catch (Exception ex)
                    {
                        replacedRestored = false;
                        DiagnosticErrorLogger.Log("Authenticator storage", ex, "A replaced authenticator file could not be restored safely through its locked handle.");
                    }
                }

                DisposeLockedReplacements(lockedReplacements);
            }
            else if (replacedFiles != null && replacedFiles.Count > 0)
            {
                replacedRestored = RestoreReplacedFilesIfNeeded(maDir, replacedFiles, expectedCanonicalDir);
            }

            if (!replacedRestored)
                return;

            if (replacedFiles != null &&
                replacedFiles.Count > 0 &&
                !DeleteFilesBestEffort(
                    maDir,
                    replacedFiles.Select(record => record.BackupFilename),
                    "A replaced authenticator backup could not be removed after a failed save.",
                    expectedCanonicalDir))
            {
                return;
            }

            if (DeleteCreatedFilesForRecovery(maDir, createdFilenames, createdFileHashes, createdFileIdentities, expectedCanonicalDir))
            {
                bool backupDeleted = String.IsNullOrWhiteSpace(backupFilename) ||
                    DeleteFileBestEffort(backupFilename, "A manifest backup could not be removed after a failed save.", expectedCanonicalDir);
                if (!backupDeleted)
                    return;

                if (retainedObsoleteFilenames != null &&
                    retainedObsoleteFilenames.Count > 0 &&
                    !DeleteFilesBestEffort(
                        maDir,
                        retainedObsoleteFilenames,
                        "A retained obsolete authenticator file could not be removed after a failed save.",
                        expectedCanonicalDir))
                {
                    List<string> remainingRetainedObsolete = retainedObsoleteFilenames
                        .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                        .ToList();
                    if (remainingRetainedObsolete.Count > 0)
                    {
                        PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, remainingRetainedObsolete, expectedCanonicalDir);
                        return;
                    }
                }

                DeleteFileBestEffort(journalFilename, "A storage journal could not be removed after a failed save.", expectedCanonicalDir);
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
                    JournalVersion = CurrentStorageJournalVersion,
                    ManifestHash = GetContentHash(currentManifestContents),
                    CreatedFilenames = new List<string>(),
                    ReplacedFiles = null,
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

        private static StorageTransactionJournal ParseStorageTransactionJournal(string journalText)
        {
            if (String.IsNullOrWhiteSpace(journalText))
                throw new InvalidDataException("The storage journal is empty.");

            JObject journalJson;
            using (StringReader stringReader = new StringReader(journalText))
            using (JsonTextReader jsonReader = new JsonTextReader(stringReader)
            {
                MaxDepth = storageJsonSettings.MaxDepth,
                DateParseHandling = DateParseHandling.None
            })
            {
                journalJson = JObject.Load(jsonReader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                while (jsonReader.Read())
                {
                    if (jsonReader.TokenType != JsonToken.Comment)
                        throw new InvalidDataException("The storage journal contains unexpected trailing content.");
                }
            }

            ValidateNoCaseInsensitiveDuplicateProperties(journalJson);

            JProperty versionProperty = journalJson.Properties()
                .FirstOrDefault(property => String.Equals(property.Name, "journal_version", StringComparison.OrdinalIgnoreCase));
            if (versionProperty != null && versionProperty.Value.Type != JTokenType.Integer)
            {
                throw new InvalidDataException("The storage journal version is invalid.");
            }

            StorageTransactionJournal journal = journalJson.ToObject<StorageTransactionJournal>(JsonSerializer.Create(storageJsonSettings));
            if (journal == null || String.IsNullOrWhiteSpace(journal.ManifestHash))
                throw new InvalidDataException("The storage journal is invalid.");
            if (journal.JournalVersion.HasValue && journal.JournalVersion.Value != CurrentStorageJournalVersion)
                throw new InvalidDataException("The storage journal version is not supported.");

            return journal;
        }

        private static void ValidateNoCaseInsensitiveDuplicateProperties(JToken token)
        {
            if (token is JObject obj)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in obj.Properties())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("The storage journal contains ambiguous duplicate property names.");
                    ValidateNoCaseInsensitiveDuplicateProperties(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken item in array)
                    ValidateNoCaseInsensitiveDuplicateProperties(item);
            }
        }

        private static bool RecoverPendingStorageTransaction(
            string maDir,
            string manifestFilename,
            ICollection<string> retainedObsoleteFilenames = null,
            string expectedCanonicalDir = null,
            string validatedRoot = null,
            bool allowLockedCompletedJournal = false)
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
                    string journalText = StoragePathGuard.ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes, canonicalDir);
                    StorageTransactionJournal journal = ParseStorageTransactionJournal(journalText);

                    List<string> created = journal.CreatedFilenames ?? new List<string>();
                    List<StorageTransactionReplacedFile> replaced = journal.ReplacedFiles ?? new List<StorageTransactionReplacedFile>();
                    List<string> obsolete = journal.ObsoleteFilenames ?? new List<string>();
                    List<string> retainedObsolete = journal.RetainedObsoleteFilenames ?? new List<string>();
                    foreach (string filename in created.Concat(obsolete).Concat(retainedObsolete))
                        ValidateStorageFilename(filename);

                    bool isVersion2 = journal.JournalVersion == CurrentStorageJournalVersion;
                    bool requiresCreatedFileHashes =
                        isVersion2 ||
                        !String.IsNullOrWhiteSpace(journal.PreviousManifestHash) ||
                        replaced.Count > 0;
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
                    else if (created.Count > 0 && requiresCreatedFileHashes)
                    {
                        throw new InvalidDataException("The pending storage journal is missing created file hashes.");
                    }

                    Dictionary<string, string> createdFileIdentities = null;
                    if (journal.CreatedFileIdentities != null)
                    {
                        createdFileIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (KeyValuePair<string, string> entry in journal.CreatedFileIdentities)
                        {
                            ValidateStorageFilename(entry.Key);
                            if (!StoragePathGuard.StorageFileIdentity.TryParseToken(entry.Value, out _) ||
                                !createdFileIdentities.TryAdd(entry.Key, entry.Value))
                            {
                                throw new InvalidDataException("The pending storage journal contains an invalid created file identity entry.");
                            }
                        }

                        if (createdFileIdentities.Count != created.Count ||
                            created.Any(filename => !createdFileIdentities.ContainsKey(filename)))
                        {
                            throw new InvalidDataException("The pending storage journal created file identities do not match its created file list.");
                        }
                    }
                    else if (isVersion2 && created.Count > 0)
                    {
                        throw new InvalidDataException("The pending storage journal is missing created file identities.");
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
                        if (!String.IsNullOrWhiteSpace(record.TargetFileIdentity) &&
                            !StoragePathGuard.StorageFileIdentity.TryParseToken(record.TargetFileIdentity, out _))
                        {
                            throw new InvalidDataException("The pending storage journal contains an invalid replaced file identity.");
                        }
                        if (isVersion2 && String.IsNullOrWhiteSpace(record.TargetFileIdentity))
                        {
                            throw new InvalidDataException("The pending storage journal is missing replaced file identity metadata.");
                        }
                        ValidateStorageFilename(record.Filename);
                        GetValidatedAccountBackupPath(maDir, record.BackupFilename);
                    }

                    string backupFilename = GetValidatedStorageBackupPath(maDir, journal.BackupFilename);

                    // Inspect the live manifest (or validated manifest backup when manifest.json is missing)
                    // and validate ownership BEFORE publishing any restored manifest to disk.
                    string effectiveManifestContents = null;
                    bool needsManifestRestore = false;
                    if (File.Exists(manifestFilename))
                    {
                        effectiveManifestContents = StoragePathGuard.ReadTextWithLimit(manifestFilename, MaximumManifestFileSizeBytes, canonicalDir);
                    }
                    else if (backupFilename != null && File.Exists(backupFilename))
                    {
                        effectiveManifestContents = ReadAndValidateManifestBackupContents(
                            backupFilename,
                            journal.PreviousManifestHash,
                            canonicalDir,
                            root);
                        needsManifestRestore = true;
                    }

                    string currentManifestHash = effectiveManifestContents != null
                        ? GetContentHash(effectiveManifestContents)
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

                    ManifestRecoveryOwnership manifestOwnership = ParseManifestOwnershipForRecovery(effectiveManifestContents, root);
                    ValidateStorageTransactionFileOwnership(
                        maDir,
                        created,
                        replaced,
                        obsolete,
                        retainedObsolete,
                        backupFilename,
                        manifestOwnership,
                        manifestCommitted);

                    // Fast path for a committed retained-obsolete-only cleanup journal:
                    // do not bump storageMutationEpoch when locked obsolete files remain unchanged
                    // so concurrent readers and VerifyPasskey do not spin or fail.
                    bool isRetainedCleanupOnlyJournal =
                        manifestCommitted &&
                        !needsManifestRestore &&
                        isVersion2 &&
                        backupFilename == null &&
                        created.Count == 0 &&
                        (journal.CreatedFileHashes == null || journal.CreatedFileHashes.Count == 0) &&
                        (journal.CreatedFileIdentities == null || journal.CreatedFileIdentities.Count == 0) &&
                        replaced.Count == 0 &&
                        obsolete.Count == 0 &&
                        String.IsNullOrWhiteSpace(journal.PreviousManifestHash) &&
                        retainedObsolete.Count > 0;

                    if (isRetainedCleanupOnlyJournal)
                    {
                        if (!DeleteFilesBestEffort(maDir, retainedObsolete, "An obsolete authenticator file could not be removed during storage recovery.", canonicalDir))
                        {
                            List<string> remainingObsolete = retainedObsolete
                                .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                                .ToList();
                            if (remainingObsolete.Count > 0)
                            {
                                if (remainingObsolete.Count != retainedObsolete.Count)
                                {
                                    journal.RetainedObsoleteFilenames = remainingObsolete;
                                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                                }
                                if (retainedObsoleteFilenames != null)
                                {
                                    foreach (string filename in remainingObsolete)
                                        retainedObsoleteFilenames.Add(filename);
                                }
                                return true;
                            }
                        }

                        bool retainedJournalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.", canonicalDir);
                        if (retainedJournalDeleted && !File.Exists(journalFilename))
                            return true;
                        return allowLockedCompletedJournal;
                    }

                    Interlocked.Increment(ref storageMutationEpoch);
                    try
                    {
                        if (needsManifestRestore)
                        {
                            WriteAllTextAtomically(manifestFilename, effectiveManifestContents, expectedCanonicalDirectory: canonicalDir);
                            if (!File.Exists(manifestFilename))
                                return false;
                        }

                        if (manifestCommitted)
                        {
                            bool committedBackupDeleted = backupFilename == null ||
                                DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.", canonicalDir);
                            if (!committedBackupDeleted)
                                return false;

                            bool committedReplacedBackupsDeleted = DeleteFilesBestEffort(
                                maDir,
                                replaced.Select(record => record.BackupFilename),
                                "A replaced authenticator backup could not be removed during storage recovery.",
                                canonicalDir);
                            if (!committedReplacedBackupsDeleted)
                                return false;

                            List<string> committedObsolete = obsolete.Concat(retainedObsolete).ToList();
                            if (!DeleteFilesBestEffort(maDir, committedObsolete, "An obsolete authenticator file could not be removed during storage recovery.", canonicalDir))
                            {
                                List<string> remainingObsolete = committedObsolete
                                    .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                                    .ToList();
                                if (remainingObsolete.Count > 0)
                                {
                                    journal.JournalVersion = CurrentStorageJournalVersion;
                                    journal.PreviousManifestHash = null;
                                    journal.BackupFilename = null;
                                    journal.CreatedFilenames = new List<string>();
                                    journal.CreatedFileHashes = null;
                                    journal.CreatedFileIdentities = null;
                                    journal.ReplacedFiles = null;
                                    journal.ObsoleteFilenames = new List<string>();
                                    journal.RetainedObsoleteFilenames = remainingObsolete;
                                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(journal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                                    if (retainedObsoleteFilenames != null)
                                    {
                                        foreach (string filename in remainingObsolete)
                                            retainedObsoleteFilenames.Add(filename);
                                    }
                                    return true;
                                }
                            }

                            bool committedJournalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.", canonicalDir);
                            return committedJournalDeleted && !File.Exists(journalFilename);
                        }

                        if (!RestoreReplacedFilesIfNeeded(maDir, replaced, canonicalDir, manifestOwnership))
                            return false;

                        if (replaced.Count > 0 &&
                            !DeleteFilesBestEffort(
                                maDir,
                                replaced.Select(record => record.BackupFilename),
                                "A replaced authenticator backup could not be removed during storage recovery.",
                                canonicalDir))
                        {
                            return false;
                        }

                        if (!DeleteCreatedFilesForRecovery(maDir, created, createdFileHashes, createdFileIdentities, canonicalDir))
                        {
                            // The manifest still contains the original data, but the
                            // created files are not safe to leave untracked. Keep the
                            // journal and fail closed so recovery can retry them later.
                            return false;
                        }

                        bool uncommittedBackupDeleted = backupFilename == null ||
                            DeleteFileBestEffort(backupFilename, "A completed manifest backup could not be removed during storage recovery.", canonicalDir);
                        if (!uncommittedBackupDeleted)
                            return false;

                        if (retainedObsolete.Count > 0 &&
                            !DeleteFilesBestEffort(maDir, retainedObsolete, "A retained obsolete authenticator file could not be removed during storage recovery.", canonicalDir))
                        {
                            List<string> remainingRetainedObsolete = retainedObsolete
                                .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                                .ToList();
                            if (remainingRetainedObsolete.Count > 0)
                            {
                                if (!PreserveRetainedObsoleteJournal(manifestFilename, journalFilename, remainingRetainedObsolete, canonicalDir))
                                    return false;
                                if (retainedObsoleteFilenames != null)
                                {
                                    foreach (string filename in remainingRetainedObsolete)
                                        retainedObsoleteFilenames.Add(filename);
                                }
                                return true;
                            }
                        }

                        bool journalDeleted = DeleteFileBestEffort(journalFilename, "The completed storage journal could not be removed during storage recovery.", canonicalDir);
                        return journalDeleted && !File.Exists(journalFilename);
                    }
                    finally
                    {
                        Interlocked.Increment(ref storageMutationEpoch);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Authenticator storage", ex, "A pending storage transaction could not be recovered automatically.");
                    return false;
                }
            }
        }

        private static bool RebaseRetainedStorageTransaction(
            string maDir,
            string manifestContents,
            IReadOnlyList<string> expectedRetainedObsoleteFilenames,
            string expectedCanonicalDir = null,
            string validatedRoot = null)
        {
            string journalFilename = Path.Combine(maDir, StorageJournalFilename);
            lock (storageLock)
            {
                try
                {
                    if (!File.Exists(journalFilename))
                        return true;

                    string root = validatedRoot ?? StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                    string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                    string journalText = StoragePathGuard.ReadTextWithLimit(journalFilename, MaximumManifestFileSizeBytes, canonicalDir);
                    StorageTransactionJournal journal = ParseStorageTransactionJournal(journalText);

                    List<string> validatedExpected = (expectedRetainedObsoleteFilenames ?? Enumerable.Empty<string>())
                        .Where(filename => !String.IsNullOrWhiteSpace(filename))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (string filename in validatedExpected)
                        ValidateStorageFilename(filename);

                    ManifestRecoveryOwnership newManifestOwnership = ParseManifestOwnershipForRecovery(manifestContents, root);
                    foreach (string filename in validatedExpected)
                    {
                        if (newManifestOwnership.AllLiveFilenames.Contains(filename))
                            throw new InvalidDataException("A retained storage cleanup entry overlaps with a live file in the saved manifest.");
                    }

                    List<string> pendingRetainedObsolete = validatedExpected
                        .Where(filename => Path.Exists(Path.Combine(maDir, filename)))
                        .ToList();

                    HashSet<string> expectedSet = new HashSet<string>(validatedExpected, StringComparer.OrdinalIgnoreCase);

                    // A rebased journal must still be in retained-cleanup-only state with no injected entries.
                    bool hasUnexpectedState =
                        !String.IsNullOrWhiteSpace(journal.BackupFilename) ||
                        (journal.ReplacedFiles != null && journal.ReplacedFiles.Count > 0) ||
                        (journal.CreatedFilenames != null && journal.CreatedFilenames.Count > 0) ||
                        (journal.ObsoleteFilenames != null && journal.ObsoleteFilenames.Count > 0);
                    bool hasUnexpectedRetainedEntry = (journal.RetainedObsoleteFilenames ?? Enumerable.Empty<string>())
                        .Any(journalEntry => String.IsNullOrWhiteSpace(journalEntry) || !expectedSet.Contains(journalEntry));

                    if (hasUnexpectedState || hasUnexpectedRetainedEntry)
                    {
                        // Scrub any rejected/injected entries from the on-disk journal before failing
                        // so a subsequent startup recovery cannot delete an unvalidated filename.
                        try
                        {
                            if (pendingRetainedObsolete.Count == 0)
                            {
                                DeleteFileBestEffort(
                                    journalFilename,
                                    "The tampered storage journal could not be removed while rejecting settings rebase.",
                                    canonicalDir);
                            }
                            else
                            {
                                StorageTransactionJournal sanitizedJournal = new StorageTransactionJournal
                                {
                                    JournalVersion = CurrentStorageJournalVersion,
                                    ManifestHash = GetContentHash(manifestContents),
                                    PreviousManifestHash = null,
                                    CreatedFilenames = new List<string>(),
                                    CreatedFileHashes = null,
                                    CreatedFileIdentities = null,
                                    ReplacedFiles = null,
                                    ObsoleteFilenames = new List<string>(),
                                    RetainedObsoleteFilenames = pendingRetainedObsolete,
                                    BackupFilename = null
                                };
                                WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(sanitizedJournal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
                            }
                        }
                        catch
                        {
                            // Still fail closed below even if best-effort sanitization fails.
                        }

                        throw new InvalidDataException(hasUnexpectedState
                            ? "The retained storage journal is not in a rebaseable cleanup-only state."
                            : "The retained storage journal changed unexpectedly before rebase.");
                    }

                    if (pendingRetainedObsolete.Count == 0)
                    {
                        return DeleteFileBestEffort(
                            journalFilename,
                            "The completed storage journal could not be removed while rebasing retained storage cleanup.",
                            canonicalDir) && !File.Exists(journalFilename);
                    }

                    StorageTransactionJournal rebasedJournal = new StorageTransactionJournal
                    {
                        JournalVersion = CurrentStorageJournalVersion,
                        ManifestHash = GetContentHash(manifestContents),
                        PreviousManifestHash = null,
                        CreatedFilenames = new List<string>(),
                        CreatedFileHashes = null,
                        CreatedFileIdentities = null,
                        ReplacedFiles = null,
                        ObsoleteFilenames = new List<string>(),
                        RetainedObsoleteFilenames = pendingRetainedObsolete,
                        BackupFilename = null
                    };
                    WriteAllTextAtomically(journalFilename, JsonConvert.SerializeObject(rebasedJournal, storageJsonSettings), expectedCanonicalDirectory: canonicalDir);
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
            IReadOnlyDictionary<string, string> createdFileHashes,
            IReadOnlyDictionary<string, string> createdFileIdentities,
            string expectedCanonicalDir)
        {
            if (createdFilenames == null || createdFilenames.Count == 0)
                return true;

            if (createdFileHashes == null)
            {
                return DeleteFilesBestEffort(
                    maDir,
                    createdFilenames,
                    "A temporary authenticator file could not be removed during storage recovery.",
                    expectedCanonicalDir);
            }

            bool succeeded = true;
            foreach (string filename in createdFilenames)
            {
                string fullPath = Path.Combine(maDir, filename);
                try
                {
                    if (!createdFileHashes.TryGetValue(filename, out string expectedHash))
                        throw new InvalidDataException("A created file entry is missing its expected hash.");

                    StoragePathGuard.StorageFileIdentity? expectedIdentity = null;
                    if (createdFileIdentities != null &&
                        createdFileIdentities.TryGetValue(filename, out string identityToken))
                    {
                        if (!StoragePathGuard.StorageFileIdentity.TryParseToken(identityToken, out StoragePathGuard.StorageFileIdentity parsedIdentity))
                            throw new InvalidDataException("A created file entry contains an invalid identity token.");
                        expectedIdentity = parsedIdentity;
                    }

                    StoragePathGuard.DeleteFileIfVerifiedExclusive(
                        fullPath,
                        expectedHash,
                        expectedIdentity,
                        expectedCanonicalDir,
                        out string preservedReason,
                        BeforeDeleteFileForTesting);

                    if (!String.IsNullOrEmpty(preservedReason))
                    {
                        DiagnosticErrorLogger.Log(
                            "Authenticator storage",
                            new IOException($"An uncommitted created file path was preserved during recovery ({preservedReason})."),
                            "Preserved an existing file that was not verified as created by the uncommitted storage transaction.");
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

        private static bool RestoreReplacedFilesIfNeeded(
            string maDir,
            IReadOnlyList<StorageTransactionReplacedFile> replacedFiles,
            string expectedCanonicalDir,
            ManifestRecoveryOwnership manifestOwnership = null)
        {
            if (replacedFiles == null || replacedFiles.Count == 0)
                return true;

            foreach (StorageTransactionReplacedFile record in replacedFiles)
            {
                string backupPath = Path.Combine(maDir, record.BackupFilename);
                string targetPath = Path.Combine(maDir, record.Filename);
                try
                {
                    StoragePathGuard.StorageFileIdentity? expectedTargetIdentity = null;
                    if (!String.IsNullOrWhiteSpace(record.TargetFileIdentity))
                    {
                        if (!StoragePathGuard.StorageFileIdentity.TryParseToken(record.TargetFileIdentity, out StoragePathGuard.StorageFileIdentity parsedIdentity))
                            throw new InvalidDataException("A replaced file record contains an invalid target identity token.");
                        expectedTargetIdentity = parsedIdentity;
                    }

                    Func<Stream, bool> isIntactAccountFile = null;
                    if (manifestOwnership != null &&
                        manifestOwnership.AccountEntries.TryGetValue(record.Filename, out ManifestEntry entry))
                    {
                        bool isEncrypted = manifestOwnership.Encrypted;
                        isIntactAccountFile = stream => IsIntactAccountFileStream(stream, entry, isEncrypted);
                    }

                    if (File.Exists(backupPath))
                    {
                        StoragePathGuard.RestoreReplacedFileFromVerifiedBackup(
                            backupPath,
                            targetPath,
                            record.OriginalHash,
                            record.ReplacementHash,
                            expectedTargetIdentity,
                            expectedCanonicalDir,
                            isIntactAccountFile);
                    }
                    else
                    {
                        if (Path.Exists(backupPath))
                            throw new InvalidDataException("A replaced authenticator backup path is not a regular file.");
                        if (!File.Exists(targetPath))
                            throw new FileNotFoundException("The expected original authenticator file is missing during rollback.", targetPath);

                        StoragePathGuard.VerifyFileMatchesOriginalHash(
                            targetPath,
                            record.OriginalHash,
                            expectedCanonicalDir);
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

        private static bool IsIntactAccountFileStream(Stream stream, ManifestEntry entry, bool encrypted)
        {
            try
            {
                if (stream == null || stream.Length <= 0 || stream.Length > MaximumAccountFileSizeBytes)
                    return false;

                using (MemoryStream ms = new MemoryStream((int)stream.Length))
                {
                    stream.CopyTo(ms);
                    string text = new UTF8Encoding(false, true).GetString(ms.ToArray());
                    if (encrypted)
                        return FileEncryptor.IsValidCiphertext(text);

                    SteamAuth.SteamGuardAccount parsed = DeserializeAccountJson(text, storageJsonSettings);
                    return parsed?.Session != null && parsed.Session.SteamID != 0 && parsed.Session.SteamID == entry.SteamID;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ReadAndValidateManifestBackupContents(
            string backupFilename,
            string expectedManifestHash,
            string expectedCanonicalDir,
            string validatedRoot)
        {
            string backupContents = StoragePathGuard.ReadTextWithLimit(backupFilename, MaximumManifestFileSizeBytes, expectedCanonicalDir);
            Manifest backupManifest = JsonConvert.DeserializeObject<Manifest>(backupContents, storageJsonSettings);
            if (backupManifest == null || backupManifest.Entries == null)
                throw new InvalidDataException("The manifest backup does not contain a valid entries list.");

            ValidateManifestState(
                backupManifest.Entries,
                backupManifest.Encrypted,
                validatedRoot,
                backupManifest.CredentialsStoreFilename);

            if (!String.IsNullOrWhiteSpace(expectedManifestHash) &&
                !String.Equals(GetContentHash(backupContents), expectedManifestHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The manifest backup does not match the expected pre-transaction manifest hash.");
            }

            return backupContents;
        }

        private static bool RestoreValidatedManifestBackupIfNeeded(
            string maDir,
            string manifestFilename,
            string backupFilename,
            string expectedManifestHash,
            bool overwriteManifest,
            string expectedCanonicalDir = null,
            string validatedRoot = null)
        {
            if (String.IsNullOrWhiteSpace(backupFilename) || !File.Exists(backupFilename))
                return !overwriteManifest || File.Exists(manifestFilename);
            if (!overwriteManifest && File.Exists(manifestFilename))
                return true;

            try
            {
                string root = validatedRoot ?? StoragePathGuard.ValidateDirectoryAndGetRoot(maDir);
                string canonicalDir = expectedCanonicalDir ?? StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDir);
                string backupContents = ReadAndValidateManifestBackupContents(
                    backupFilename,
                    expectedManifestHash,
                    canonicalDir,
                    root);

                WriteAllTextAtomically(manifestFilename, backupContents, expectedCanonicalDirectory: canonicalDir);
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

        private static ManifestRecoveryOwnership ParseManifestOwnershipForRecovery(
            string manifestContents,
            string validatedRoot)
        {
            Dictionary<string, ManifestEntry> accountEntries = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> allLiveFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(manifestContents))
                return new ManifestRecoveryOwnership(accountEntries, allLiveFilenames, encrypted: false);

            Manifest currentManifest = JsonConvert.DeserializeObject<Manifest>(manifestContents, storageJsonSettings);
            if (currentManifest == null || currentManifest.Entries == null)
                throw new InvalidDataException("The account manifest contains an invalid entries list.");

            ValidateManifestState(
                currentManifest.Entries,
                currentManifest.Encrypted,
                validatedRoot,
                currentManifest.CredentialsStoreFilename);

            foreach (ManifestEntry entry in currentManifest.Entries)
            {
                if (!accountEntries.TryAdd(entry.Filename, entry))
                    throw new InvalidDataException("The current account manifest contains duplicate account filenames.");
                allLiveFilenames.Add(entry.Filename);
            }

            if (!String.IsNullOrWhiteSpace(currentManifest.CredentialsStoreFilename))
            {
                allLiveFilenames.Add(currentManifest.CredentialsStoreFilename);
            }

            return new ManifestRecoveryOwnership(accountEntries, allLiveFilenames, currentManifest.Encrypted);
        }

        private static void ValidateStorageTransactionFileOwnership(
            string maDir,
            IEnumerable<string> created,
            IEnumerable<StorageTransactionReplacedFile> replaced,
            IEnumerable<string> obsolete,
            IEnumerable<string> retainedObsolete,
            string backupFilename,
            ManifestRecoveryOwnership manifestOwnership,
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
                if (!manifestOwnership.AccountEntries.TryGetValue(record.Filename, out ManifestEntry entry))
                    throw new InvalidDataException("A storage transaction referenced a replaced file not present in the manifest.");
                GetValidatedAccountBackupPath(maDir, record.BackupFilename, entry.SteamID);
            }

            if (backupFilename != null && !transactionFilenames.Add(Path.GetFileName(backupFilename)))
                throw new InvalidDataException("A storage transaction reused its manifest backup filename.");

            IEnumerable<string> filesThatWouldBeDeleted = (manifestCommitted ? obsolete : created).Concat(retainedObsolete);
            foreach (string filename in filesThatWouldBeDeleted)
            {
                if (manifestOwnership.AllLiveFilenames.Contains(filename))
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
                    ReplaceFileAtomically(temporaryFilename, filename, backupFilename);
                }
                else
                {
                    File.Move(temporaryFilename, filename);
                }
            }
            finally
            {
                if (tempFileVerified && File.Exists(temporaryFilename))
                {
                    if (!String.IsNullOrWhiteSpace(expectedCanonicalDirectory))
                    {
                        try
                        {
                            StoragePathGuard.DeleteRegularFileWithinCanonicalDirectory(temporaryFilename, expectedCanonicalDirectory);
                        }
                        catch
                        {
                            // Best-effort cleanup of temp file.
                        }
                    }
                    else
                    {
                        File.Delete(temporaryFilename);
                    }
                }
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

        private static string GetContentHash(string contents)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(contents)));
            }
        }

        private static void SweepOrphanedCredentialStoresLocked(string maDir, string activeCredentialsFilename, string expectedCanonicalDir = null)
        {
            if (String.IsNullOrWhiteSpace(maDir) || !Directory.Exists(maDir))
                return;

            // Never sweep credential stores while a storage transaction journal exists on disk.
            if (File.Exists(Path.Combine(maDir, StorageJournalFilename)))
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

                    DeleteFileBestEffort(filePath, "An orphaned encrypted credential file could not be removed.", expectedCanonicalDir);
                }
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The orphaned credential store sweep could not enumerate the data directory.");
            }
        }

        private static bool SweepOrphanedAccountBackupsLocked(string maDir, string expectedCanonicalDir = null)
        {
            if (String.IsNullOrWhiteSpace(maDir) || !Directory.Exists(maDir))
                return true;

            // Never sweep account backups while a storage transaction journal exists on disk.
            if (File.Exists(Path.Combine(maDir, StorageJournalFilename)))
                return true;

            bool allDeleted = true;
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(maDir, ".mafile.*.bak", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (!IsValidAccountBackupFilename(fileName))
                        continue;

                    if (!DeleteFileBestEffort(filePath, "An orphaned pre-journal authenticator backup could not be removed.", expectedCanonicalDir))
                        allDeleted = false;
                }
            }
            catch (Exception ex)
            {
                allDeleted = false;
                DiagnosticErrorLogger.Log("Authenticator storage", ex, "The orphaned account backup sweep could not enumerate the data directory.");
            }

            return allDeleted;
        }

        private static bool IsValidAccountBackupFilename(string filename)
        {
            try
            {
                GetValidatedAccountBackupPath(".", filename);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static bool DeleteFilesBestEffort(string directory, IEnumerable<string> filenames, string context, string expectedCanonicalDir = null)
        {
            bool succeeded = true;
            foreach (string filename in filenames)
            {
                if (!DeleteFileBestEffort(Path.Combine(directory, filename), context, expectedCanonicalDir))
                    succeeded = false;
            }
            return succeeded;
        }

        private static bool DeleteFileBestEffort(string filename, string context, string expectedCanonicalDir = null)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(expectedCanonicalDir))
                {
                    StoragePathGuard.DeleteRegularFileWithinCanonicalDirectory(
                        filename,
                        expectedCanonicalDir,
                        BeforeDeleteFileForTesting);
                    return true;
                }

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

            [JsonProperty("target_file_identity", NullValueHandling = NullValueHandling.Ignore)]
            public string TargetFileIdentity { get; set; }
        }

        private sealed class StorageTransactionJournal
        {
            [JsonProperty("journal_version", NullValueHandling = NullValueHandling.Ignore)]
            public int? JournalVersion { get; set; }

            [JsonProperty("manifest_hash")]
            public string ManifestHash { get; set; }

            [JsonProperty("previous_manifest_hash", NullValueHandling = NullValueHandling.Ignore)]
            public string PreviousManifestHash { get; set; }

            [JsonProperty("created_filenames")]
            public List<string> CreatedFilenames { get; set; }

            [JsonProperty("created_file_hashes", NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, string> CreatedFileHashes { get; set; }

            [JsonProperty("created_file_identities", NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, string> CreatedFileIdentities { get; set; }

            [JsonProperty("replaced_files", NullValueHandling = NullValueHandling.Ignore)]
            public List<StorageTransactionReplacedFile> ReplacedFiles { get; set; }

            [JsonProperty("obsolete_filenames")]
            public List<string> ObsoleteFilenames { get; set; }

            [JsonProperty("retained_obsolete_filenames", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> RetainedObsoleteFilenames { get; set; }

            [JsonProperty("backup_filename")]
            public string BackupFilename { get; set; }
        }
    }
}
