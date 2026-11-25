using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class CanonicalAccountSaveTests : IDisposable
    {
        private const string TestBackupFilename = ".manifest.00000000000000000000000000000000.bak";
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-canonical-save-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public CanonicalAccountSaveTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void CanonicalAccountFileSaves_CommitInOneTransactionAcrossCreateUpdateImportAndEncryption()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong firstId = 76561198000001000UL;
            ulong secondId = 76561198000001001UL;
            ulong thirdId = 76561198000001002UL;
            ulong fourthId = 76561198000001003UL;
            SteamGuardAccount first = CreateAccount(firstId);
            first.Session.AccessToken = "token-v1";

            // 1. Creating a brand-new account writes directly to its canonical filename in 1 transaction.
            long commitsBeforeCreate = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.SaveAccount(first, false).Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeCreate);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(firstId), Assert.Single(manifest.Entries).Filename);
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));

            // 2. Updating an existing unencrypted canonical account commits in 1 transaction and increments account revision.
            long revBeforeUpdate = manifest.GetAccountStorageRevision(firstId);
            long commitsBeforeUpdate = Manifest.StorageTransactionCommitCount;
            first.Session.AccessToken = "token-v2";
            StorageResult updateResult = manifest.SaveAccount(first, false);
            Assert.True(updateResult.Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeUpdate);
            Assert.Equal(revBeforeUpdate + 1, manifest.GetAccountStorageRevision(firstId));
            Assert.Equal(revBeforeUpdate + 1, updateResult.CommittedStorageRevision);
            Assert.Equal("token-v2", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);

            // Stale refresh using the pre-update revision must still be rejected.
            first.Session.AccessToken = "stale-token";
            StorageResult staleResult = manifest.SaveAccount(first, false, null, revBeforeUpdate, allowCreate: false);
            Assert.False(staleResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, staleResult.FailureKind);

            // 3. Enabling encryption, updating encrypted canonical account, rotating passkey, and disabling encryption each take 1 transaction.
            long commitsBeforeEncrypt = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ChangeEncryptionKey(null, "passkey-1").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeEncrypt);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(firstId), Assert.Single(manifest.Entries).Filename);

            long commitsBeforeEncryptedUpdate = Manifest.StorageTransactionCommitCount;
            first.Session.AccessToken = "token-v3-enc";
            Assert.True(manifest.SaveAccount(first, true, "passkey-1").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeEncryptedUpdate);
            Assert.Equal("token-v3-enc", Assert.Single(Manifest.GetManifest(true).GetAllAccounts("passkey-1")).Session.AccessToken);

            long commitsBeforeRotate = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ChangeEncryptionKey("passkey-1", "passkey-2").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeRotate);
            Assert.Equal("token-v3-enc", Assert.Single(Manifest.GetManifest(true).GetAllAccounts("passkey-2")).Session.AccessToken);

            // 4. Importing a non-canonical source file commits in 1 transaction directly to its canonical filename.
            string nonCanonicalSource = "imported-legacy.maFile";
            File.WriteAllText(Path.Combine(maDirectory, nonCanonicalSource), JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            long commitsBeforeImport = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ImportUnmanagedMaFiles(candidates, "passkey-2").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeImport);
            Assert.False(File.Exists(Path.Combine(maDirectory, nonCanonicalSource)));
            Assert.Equal(
                new[]
                {
                    Manifest.GetCanonicalMaFileFilename(firstId),
                    Manifest.GetCanonicalMaFileFilename(secondId)
                },
                manifest.Entries.Select(e => e.Filename).ToArray());

            // 5. Importing an already-canonical unmanaged source file intentionally uses 2 transactions
            //    (GUID fallback + normalization) so the unmanaged source is never mutated before commit.
            string canonicalSource = Manifest.GetCanonicalMaFileFilename(thirdId);
            File.WriteAllText(Path.Combine(maDirectory, canonicalSource), JsonConvert.SerializeObject(CreateAccount(thirdId)));
            var canonicalCandidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(canonicalCandidates);
            long commitsBeforeEncryptedCanonicalImport = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ImportUnmanagedMaFiles(canonicalCandidates, "passkey-2").Succeeded);
            Assert.Equal(2, Manifest.StorageTransactionCommitCount - commitsBeforeEncryptedCanonicalImport);
            Assert.Equal(
                new[]
                {
                    Manifest.GetCanonicalMaFileFilename(firstId),
                    Manifest.GetCanonicalMaFileFilename(secondId),
                    Manifest.GetCanonicalMaFileFilename(thirdId)
                },
                manifest.Entries.Select(e => e.Filename).ToArray());
            Assert.Equal(3, Manifest.GetManifest(true).GetAllAccounts("passkey-2").Length);

            long commitsBeforeDecrypt = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ChangeEncryptionKey("passkey-2", null).Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeDecrypt);
            Assert.Equal(3, Manifest.GetManifest(true).GetAllAccounts().Length);

            // Already-canonical plaintext import also preserves the 2-transaction non-mutating contract.
            string plaintextCanonicalSource = Manifest.GetCanonicalMaFileFilename(fourthId);
            File.WriteAllText(Path.Combine(maDirectory, plaintextCanonicalSource), JsonConvert.SerializeObject(CreateAccount(fourthId)));
            var plaintextCanonicalCandidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(plaintextCanonicalCandidates);
            long commitsBeforePlaintextCanonicalImport = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ImportUnmanagedMaFiles(plaintextCanonicalCandidates).Succeeded);
            Assert.Equal(2, Manifest.StorageTransactionCommitCount - commitsBeforePlaintextCanonicalImport);
            Assert.Equal(4, Manifest.GetManifest(true).GetAllAccounts().Length);
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void SaveAccount_InPlaceCanonicalUpdateRollbackRestoresPreviousPlaintextAndEncryptedFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000002000UL;
            ulong secondId = 76561198000002001UL;
            SteamGuardAccount first = CreateAccount(firstId);
            first.Session.AccessToken = "plaintext-v1";
            SteamGuardAccount second = CreateAccount(secondId);
            second.Session.AccessToken = "second-v1";
            Assert.True(manifest.SaveAccount(first, false).Succeeded);
            Assert.True(manifest.SaveAccount(second, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            long revisionBefore = manifest.GetAccountStorageRevision(firstId);

            // 1. Plaintext in-place update rollback when manifest.json replacement is blocked.
            first.Session.AccessToken = "plaintext-v2-blocked";
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult result = manifest.SaveAccount(first, false);
                Assert.False(result.Succeeded);
            }

            Assert.Equal(revisionBefore, manifest.GetAccountStorageRevision(firstId));
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            Assert.Equal(
                "plaintext-v1",
                Assert.Single(Manifest.GetManifest(true).GetAllAccounts(), a => a.Session.SteamID == firstId).Session.AccessToken);

            // 2. Multi-account ChangeEncryptionKey rollback restores every replaced canonical file.
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult encryptResult = manifest.ChangeEncryptionKey(null, "blocked-key");
                Assert.False(encryptResult.Succeeded);
            }

            Assert.False(manifest.Encrypted);
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            SteamGuardAccount[] restoredPlaintext = Manifest.GetManifest(true).GetAllAccounts();
            Assert.Equal("plaintext-v1", Assert.Single(restoredPlaintext, a => a.Session.SteamID == firstId).Session.AccessToken);
            Assert.Equal("second-v1", Assert.Single(restoredPlaintext, a => a.Session.SteamID == secondId).Session.AccessToken);

            // 3. Encrypted in-place update rollback restores prior ciphertext matching prior Salt/IV.
            Assert.True(manifest.ChangeEncryptionKey(null, "active-key").Succeeded);
            first.Session.AccessToken = "encrypted-v2-blocked";
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult encryptedUpdateResult = manifest.SaveAccount(first, true, "active-key");
                Assert.False(encryptedUpdateResult.Succeeded);
            }

            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            SteamGuardAccount[] restoredEncrypted = Manifest.GetManifest(true).GetAllAccounts("active-key");
            Assert.Equal("plaintext-v1", Assert.Single(restoredEncrypted, a => a.Session.SteamID == firstId).Session.AccessToken);
            Assert.Equal("second-v1", Assert.Single(restoredEncrypted, a => a.Session.SteamID == secondId).Session.AccessToken);
        }

        [Fact]
        public void SaveAccount_CanonicalCollisionPreservesForeignFileAndFallsBackToGuid()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong accountId = 76561198000003000UL;
            ulong foreignId = 76561198000003999UL;
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string foreignContents = JsonConvert.SerializeObject(CreateAccount(foreignId));
            File.WriteAllText(canonicalPath, foreignContents);

            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "owned-v1";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string assignedFilename = Assert.Single(manifest.Entries).Filename;
            Assert.NotEqual(canonicalFilename, assignedFilename);
            Assert.Equal(foreignContents, File.ReadAllText(canonicalPath));
            Assert.Equal("owned-v1", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);

            // Subsequent update while the foreign canonical file still occupies the slot also keeps it untouched.
            account.Session.AccessToken = "owned-v2";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.NotEqual(canonicalFilename, Assert.Single(manifest.Entries).Filename);
            Assert.Equal(foreignContents, File.ReadAllText(canonicalPath));
            Assert.Equal("owned-v2", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);
        }

        [Fact]
        public void ImportUnmanagedMaFiles_SwappedCanonicalFilenamesPreserveBothAccountsAndNormalize()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong firstId = 76561198000004000UL;
            ulong secondId = 76561198000004001UL;
            SteamGuardAccount first = CreateAccount(firstId);
            first.Session.AccessToken = "first-token";
            SteamGuardAccount second = CreateAccount(secondId);
            second.Session.AccessToken = "second-token";

            // Deliberately swap canonical filenames on disk.
            File.WriteAllText(Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(firstId)), JsonConvert.SerializeObject(second));
            File.WriteAllText(Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(secondId)), JsonConvert.SerializeObject(first));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Equal(2, candidates.Count);
            Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);

            Assert.Equal(2, manifest.Entries.Count);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(firstId), Assert.Single(manifest.Entries, e => e.SteamID == firstId).Filename);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(secondId), Assert.Single(manifest.Entries, e => e.SteamID == secondId).Filename);
            JObject persistedBeforeReload = JObject.Parse(File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
            string[] persistedFilenamesBeforeReload = persistedBeforeReload["entries"].Select(entry => entry["filename"].Value<string>()).ToArray();
            Assert.Contains(Manifest.GetCanonicalMaFileFilename(firstId), persistedFilenamesBeforeReload);
            Assert.Contains(Manifest.GetCanonicalMaFileFilename(secondId), persistedFilenamesBeforeReload);

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(2, reloaded.Entries.Count);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(firstId), Assert.Single(reloaded.Entries, e => e.SteamID == firstId).Filename);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(secondId), Assert.Single(reloaded.Entries, e => e.SteamID == secondId).Filename);
            SteamGuardAccount[] loadedAccounts = reloaded.GetAllAccounts();
            Assert.Equal("first-token", Assert.Single(loadedAccounts, a => a.Session.SteamID == firstId).Session.AccessToken);
            Assert.Equal("second-token", Assert.Single(loadedAccounts, a => a.Session.SteamID == secondId).Session.AccessToken);
        }

        [Fact]
        public void ImportUnmanagedMaFiles_AlreadyCanonicalSourceRollbackRestoresOriginalPlaintext()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000005000UL), true, "import-key").Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            ulong importId = 76561198000005001UL;
            string sourcePath = Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(importId));
            string originalPlaintext = JsonConvert.SerializeObject(CreateAccount(importId));
            File.WriteAllText(sourcePath, originalPlaintext);

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult result = manifest.ImportUnmanagedMaFiles(candidates, "import-key");
                Assert.False(result.Succeeded);
            }

            Assert.Single(manifest.Entries);
            Assert.Equal(originalPlaintext, File.ReadAllText(sourcePath));
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_UncommittedAndCommittedReplacedFilesRecoverSafely()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000006000UL;
            string testAccountBackupFilename = $".mafile.{accountId}.11111111111111111111111111111111.bak";
            SteamGuardAccount original = CreateAccount(accountId);
            original.Session.AccessToken = "original-token";
            Assert.True(manifest.SaveAccount(original, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string accountBackupPath = Path.Combine(maDirectory, testAccountBackupFilename);

            // 1. Uncommitted crash: manifest still has old storage_transaction_id, account file has new contents, backup has old contents.
            string originalManifestContents = File.ReadAllText(manifestPath);
            string originalFileContents = File.ReadAllText(canonicalPath);
            SteamGuardAccount replacementAccount = CreateAccount(accountId);
            replacementAccount.Session.AccessToken = "uncommitted-replacement-token";
            string replacementFileContents = JsonConvert.SerializeObject(replacementAccount);
            File.WriteAllText(accountBackupPath, originalFileContents);
            File.WriteAllText(canonicalPath, replacementFileContents);
            string targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            JObject nextManifestJson = JObject.Parse(originalManifestContents);
            nextManifestJson["storage_transaction_id"] = Guid.NewGuid().ToString("N");
            string uncommittedManifestContents = nextManifestJson.ToString(Formatting.None);

            JObject uncommittedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(uncommittedManifestContents),
                ["previous_manifest_hash"] = ComputeContentHash(originalManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash(originalFileContents),
                    ["replacement_hash"] = ComputeContentHash(replacementFileContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            Manifest recoveredUncommitted = Manifest.GetManifest(true);
            Assert.Equal("original-token", Assert.Single(recoveredUncommitted.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(accountBackupPath));
            Assert.False(File.Exists(journalPath));

            // 2. Committed crash with locked account backup fails closed without touching obsolete files, then completes once unlocked.
            string obsoletePath = Path.Combine(maDirectory, "obsolete-after-replace.maFile");
            File.WriteAllText(obsoletePath, "obsolete");
            File.WriteAllText(accountBackupPath, originalFileContents);
            File.WriteAllText(canonicalPath, replacementFileContents);
            File.WriteAllText(manifestPath, uncommittedManifestContents);
            targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            JObject committedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(uncommittedManifestContents),
                ["previous_manifest_hash"] = ComputeContentHash(originalManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash(originalFileContents),
                    ["replacement_hash"] = ComputeContentHash(replacementFileContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray("obsolete-after-replace.maFile"),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, committedJournal.ToString(Formatting.None));

            using (new FileStream(accountBackupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(journalPath));
            }

            Manifest recoveredCommitted = Manifest.GetManifest(true);
            Assert.Equal("uncommitted-replacement-token", Assert.Single(recoveredCommitted.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(accountBackupPath));
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));

            // 3. Tampered replaced_files backup targeting a live account file is rejected.
            JObject tamperedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = canonicalFilename,
                    ["original_hash"] = ComputeContentHash(originalFileContents),
                    ["replacement_hash"] = ComputeContentHash(replacementFileContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, tamperedJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(canonicalPath));
        }

        [Fact]
        public void StartupRecovery_UncommittedCreatedFileHashAndIdentityChecksPreserveOtherWritersFiles()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong existingId = 76561198000007000UL;
            ulong racedId = 76561198000007001UL;
            ulong createdId = 76561198000007002UL;
            Assert.True(manifest.SaveAccount(CreateAccount(existingId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string racedFilename = Manifest.GetCanonicalMaFileFilename(racedId);
            string createdFilename = Manifest.GetCanonicalMaFileFilename(createdId);
            string racedPath = Path.Combine(maDirectory, racedFilename);
            string createdPath = Path.Combine(maDirectory, createdFilename);

            string otherWriterContents = JsonConvert.SerializeObject(CreateAccount(racedId));
            string ourPlannedRacedContents = "{\"planned\":\"never-moved\"}";
            string ourCreatedContents = JsonConvert.SerializeObject(CreateAccount(createdId));
            File.WriteAllText(racedPath, otherWriterContents);
            File.WriteAllText(createdPath, ourCreatedContents);
            string racedIdentity = StoragePathGuard.GetFileIdentityToken(racedPath, canonicalDir);
            string createdIdentity = StoragePathGuard.GetFileIdentityToken(createdPath, canonicalDir);

            JObject uncommittedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(racedFilename, createdFilename),
                ["created_file_hashes"] = new JObject
                {
                    [racedFilename] = ComputeContentHash(ourPlannedRacedContents),
                    [createdFilename] = ComputeContentHash(ourCreatedContents)
                },
                ["created_file_identities"] = new JObject
                {
                    [racedFilename] = racedIdentity,
                    [createdFilename] = createdIdentity
                },
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            Manifest recovered = Manifest.GetManifest(true);
            Assert.Single(recovered.Entries);
            Assert.True(File.Exists(racedPath));
            Assert.Equal(otherWriterContents, File.ReadAllText(racedPath));
            Assert.False(File.Exists(createdPath));
            Assert.False(File.Exists(journalPath));

            // Byte-identical file created by another writer (different FileIndex) must ALSO be preserved.
            string tempIdentityFile = Path.Combine(maDirectory, "other-writer-origin.tmp");
            File.WriteAllText(tempIdentityFile, otherWriterContents);
            string differentIdentity = StoragePathGuard.GetFileIdentityToken(tempIdentityFile, canonicalDir);
            File.Delete(tempIdentityFile);
            Assert.NotEqual(racedIdentity, differentIdentity);

            JObject identityMismatchJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "-identity-mismatch"),
                ["previous_manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(racedFilename),
                ["created_file_hashes"] = new JObject
                {
                    [racedFilename] = ComputeContentHash(otherWriterContents)
                },
                ["created_file_identities"] = new JObject
                {
                    [racedFilename] = differentIdentity
                },
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, identityMismatchJournal.ToString(Formatting.None));
            Manifest.GetManifest(true);
            Assert.True(File.Exists(racedPath));
            Assert.Equal(otherWriterContents, File.ReadAllText(racedPath));
            Assert.False(File.Exists(journalPath));

            // A v2 journal omitting created_file_hashes or created_file_identities for non-empty created_filenames must fail closed.
            JObject missingHashesJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "-missing-hashes"),
                ["previous_manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(racedFilename),
                ["created_file_identities"] = new JObject { [racedFilename] = racedIdentity },
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, missingHashesJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            JObject missingIdentitiesJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "-missing-identities"),
                ["previous_manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(racedFilename),
                ["created_file_hashes"] = new JObject { [racedFilename] = ComputeContentHash(otherWriterContents) },
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, missingIdentitiesJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // A first-save v2 journal (no previous_manifest_hash, no replaced_files) omitting created_file_hashes must fail closed.
            File.Delete(manifestPath);
            JObject firstSaveMissingHashesJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash("first-save-uncommitted"),
                ["created_filenames"] = new JArray(racedFilename),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, firstSaveMissingHashesJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // Explicit null journal_version must not fall back to legacy recovery.
            firstSaveMissingHashesJournal["journal_version"] = JValue.CreateNull();
            File.WriteAllText(journalPath, firstSaveMissingHashesJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // Duplicate exact or differently-cased journal_version fields in otherwise valid journals must fail closed.
            string exactDupVersionJson = $"{{\"journal_version\":null,\"journal_version\":2,\"manifest_hash\":\"{ComputeContentHash("dup-exact")}\",\"created_filenames\":[\"{racedFilename}\"],\"created_file_hashes\":{{\"{racedFilename}\":\"{ComputeContentHash(otherWriterContents)}\"}},\"created_file_identities\":{{\"{racedFilename}\":\"{racedIdentity}\"}},\"obsolete_filenames\":[],\"backup_filename\":null}}";
            File.WriteAllText(journalPath, exactDupVersionJson);
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            string casedDupVersionJson = $"{{\"journal_version\":2,\"JOURNAL_VERSION\":null,\"manifest_hash\":\"{ComputeContentHash("dup-cased")}\",\"created_filenames\":[\"{racedFilename}\"],\"created_file_hashes\":{{\"{racedFilename}\":\"{ComputeContentHash(otherWriterContents)}\"}},\"created_file_identities\":{{\"{racedFilename}\":\"{racedIdentity}\"}},\"obsolete_filenames\":[],\"backup_filename\":null}}";
            File.WriteAllText(journalPath, casedDupVersionJson);
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // Trailing JSON content after a valid journal object must fail closed.
            string trailingJson = $"{{\"journal_version\":2,\"manifest_hash\":\"{ComputeContentHash("trailing")}\",\"created_filenames\":[\"{racedFilename}\"],\"created_file_hashes\":{{\"{racedFilename}\":\"{ComputeContentHash(otherWriterContents)}\"}},\"created_file_identities\":{{\"{racedFilename}\":\"{racedIdentity}\"}},\"obsolete_filenames\":[],\"backup_filename\":null}} {{\"extra\":true}}";
            File.WriteAllText(journalPath, trailingJson);
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // Unsupported future journal versions must fail closed.
            firstSaveMissingHashesJournal["journal_version"] = 99;
            File.WriteAllText(journalPath, firstSaveMissingHashesJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(racedPath));

            // Handle-bound exclusive deletion fails closed even if another reader holds FileShare.Read | FileShare.Delete.
            File.WriteAllText(createdPath, ourCreatedContents);
            createdIdentity = StoragePathGuard.GetFileIdentityToken(createdPath, canonicalDir);
            JObject exclusiveLockJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash("first-save-exclusive-check"),
                ["created_filenames"] = new JArray(createdFilename),
                ["created_file_hashes"] = new JObject { [createdFilename] = ComputeContentHash(ourCreatedContents) },
                ["created_file_identities"] = new JObject { [createdFilename] = createdIdentity },
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, exclusiveLockJournal.ToString(Formatting.None));
            using (new FileStream(createdPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(createdPath));
            }

            // Once unlocked, handle-bound verified deletion removes the created file cleanly.
            File.Delete(racedPath);
            File.Delete(Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(existingId)));
            Manifest.GetManifest(true);
            Assert.False(File.Exists(createdPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void StartupRecovery_UncommittedReplacedFilesRejectUnverifiedOwnershipAndHashMismatches()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong liveId = 76561198000008000UL;
            string testAccountBackupFilename = $".mafile.{liveId}.22222222222222222222222222222222.bak";
            SteamGuardAccount liveAccount = CreateAccount(liveId);
            liveAccount.Session.AccessToken = "live-untouched";
            Assert.True(manifest.SaveAccount(liveAccount, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string liveFilename = Manifest.GetCanonicalMaFileFilename(liveId);
            string livePath = Path.Combine(maDirectory, liveFilename);
            string liveContents = File.ReadAllText(livePath);
            string liveIdentity = StoragePathGuard.GetFileIdentityToken(livePath, canonicalDir);
            string unmanagedFilename = "unmanaged-target.maFile";
            string unmanagedPath = Path.Combine(maDirectory, unmanagedFilename);
            File.WriteAllText(unmanagedPath, "unmanaged-original");
            string unmanagedIdentity = StoragePathGuard.GetFileIdentityToken(unmanagedPath, canonicalDir);
            string backupPath = Path.Combine(maDirectory, testAccountBackupFilename);
            File.WriteAllText(backupPath, "tampered-backup-payload");

            string currentManifestContents = File.ReadAllText(manifestPath);

            // 1. Uncommitted replaced_files targeting a non-manifest file must fail closed.
            JObject unmanagedTargetJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(currentManifestContents + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(currentManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = unmanagedFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash("tampered-backup-payload"),
                    ["replacement_hash"] = ComputeContentHash("unmanaged-original"),
                    ["target_file_identity"] = unmanagedIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, unmanagedTargetJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal("unmanaged-original", File.ReadAllText(unmanagedPath));

            // 2. Uncommitted replaced_files targeting a live manifest file without hashes must fail closed.
            JObject missingHashJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(currentManifestContents + "-uncommitted"),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = liveFilename,
                    ["backup_filename"] = testAccountBackupFilename
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, missingHashJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal(liveContents, File.ReadAllText(livePath));

            // 3. Uncommitted replaced_files targeting a live manifest file whose on-disk bytes do not match replacement_hash must fail closed.
            JObject mismatchedTargetJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(currentManifestContents + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(currentManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = liveFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash("tampered-backup-payload"),
                    ["replacement_hash"] = ComputeContentHash("some-other-replacement"),
                    ["target_file_identity"] = liveIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, mismatchedTargetJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal(liveContents, File.ReadAllText(livePath));

            // 4. Replaced file whose FileIndex changed (file substituted after crash) fails closed even if hash matches.
            string differentIdentity = StoragePathGuard.GetFileIdentityToken(unmanagedPath, canonicalDir);
            Assert.NotEqual(liveIdentity, differentIdentity);
            File.WriteAllText(backupPath, "original-before-swap");
            JObject mismatchedIdentityJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(currentManifestContents + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(currentManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = liveFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash("original-before-swap"),
                    ["replacement_hash"] = ComputeContentHash(liveContents),
                    ["target_file_identity"] = differentIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, mismatchedIdentityJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal(liveContents, File.ReadAllText(livePath));
        }

        [Fact]
        public void SaveAccount_InPlaceRollbackAndStartupRecoveryRestoreOversizedAndInvalidUtf8Backups()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000009000UL;
            string testAccountBackupFilename = $".mafile.{accountId}.33333333333333333333333333333333.bak";
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "initial-token";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);

            // Replace on-disk canonical file with bytes > 4 MiB containing invalid UTF-8 sequences.
            byte[] rawOriginalBytes = new byte[(4 * 1024 * 1024) + 1024];
            rawOriginalBytes[0] = 0xFF;
            rawOriginalBytes[1] = 0xFE;
            rawOriginalBytes[2] = 0x80;
            rawOriginalBytes[rawOriginalBytes.Length - 1] = 0xC0;
            File.WriteAllBytes(canonicalPath, rawOriginalBytes);

            // 1. Synchronous rollback restores raw bytes without failing size or UTF-8 validation.
            account.Session.AccessToken = "blocked-update";
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult result = manifest.SaveAccount(account, false);
                Assert.False(result.Succeeded);
            }

            Assert.False(File.Exists(journalPath));
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.Equal(rawOriginalBytes, File.ReadAllBytes(canonicalPath));

            // 2. Startup recovery also restores oversized invalid-UTF-8 backups raw-byte-for-byte.
            string backupPath = Path.Combine(maDirectory, testAccountBackupFilename);
            string replacementContents = JsonConvert.SerializeObject(account);
            File.WriteAllBytes(backupPath, rawOriginalBytes);
            File.WriteAllText(canonicalPath, replacementContents);
            string targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            string manifestContents = File.ReadAllText(manifestPath);
            string rawOriginalHash;
            using (SHA256 sha256 = SHA256.Create())
                rawOriginalHash = Convert.ToBase64String(sha256.ComputeHash(rawOriginalBytes));

            JObject uncommittedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(manifestContents + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(manifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = rawOriginalHash,
                    ["replacement_hash"] = ComputeContentHash(replacementContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            Manifest.GetManifest(true);
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(backupPath));
            Assert.Equal(rawOriginalBytes, File.ReadAllBytes(canonicalPath));
        }

        [Fact]
        public void ChangeEncryptionKey_RecoversPendingUncommittedReplacementBeforeReadingAccountFiles()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000010000UL;
            string testAccountBackupFilename = $".mafile.{accountId}.44444444444444444444444444444444.bak";
            SteamGuardAccount committed = CreateAccount(accountId);
            committed.Session.AccessToken = "committed-v1";
            Assert.True(manifest.SaveAccount(committed, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string backupPath = Path.Combine(maDirectory, testAccountBackupFilename);

            string committedManifestContents = File.ReadAllText(manifestPath);
            string committedAccountContents = File.ReadAllText(canonicalPath);
            SteamGuardAccount uncommitted = CreateAccount(accountId);
            uncommitted.Session.AccessToken = "uncommitted-v2";
            string uncommittedAccountContents = JsonConvert.SerializeObject(uncommitted);

            File.WriteAllText(backupPath, committedAccountContents);
            File.WriteAllText(canonicalPath, uncommittedAccountContents);
            string targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            JObject uncommittedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(committedManifestContents + "-failed-save"),
                ["previous_manifest_hash"] = ComputeContentHash(committedManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename,
                    ["original_hash"] = ComputeContentHash(committedAccountContents),
                    ["replacement_hash"] = ComputeContentHash(uncommittedAccountContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            Assert.True(manifest.ChangeEncryptionKey(null, "new-encryption-key").Succeeded);
            SteamGuardAccount loaded = Assert.Single(Manifest.GetManifest(true).GetAllAccounts("new-encryption-key"));
            Assert.Equal("committed-v1", loaded.Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void CanonicalDirectoryCollision_FallsBackToGuidAcrossSaveImportAndEncryptionChange()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong saveId = 76561198000011000UL;
            ulong importId = 76561198000011001UL;

            string saveCanonicalFilename = Manifest.GetCanonicalMaFileFilename(saveId);
            string saveCanonicalDir = Path.Combine(maDirectory, saveCanonicalFilename);
            Directory.CreateDirectory(saveCanonicalDir);

            SteamGuardAccount account = CreateAccount(saveId);
            account.Session.AccessToken = "dir-save-v1";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.NotEqual(saveCanonicalFilename, Assert.Single(manifest.Entries).Filename);
            Assert.True(Directory.Exists(saveCanonicalDir));

            account.Session.AccessToken = "dir-save-v2";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.NotEqual(saveCanonicalFilename, Assert.Single(manifest.Entries).Filename);
            Assert.True(Directory.Exists(saveCanonicalDir));

            Assert.True(manifest.ChangeEncryptionKey(null, "dir-key").Succeeded);
            Assert.NotEqual(saveCanonicalFilename, Assert.Single(manifest.Entries).Filename);
            Assert.True(Directory.Exists(saveCanonicalDir));
            Assert.Equal("dir-save-v2", Assert.Single(Manifest.GetManifest(true).GetAllAccounts("dir-key")).Session.AccessToken);

            string importCanonicalFilename = Manifest.GetCanonicalMaFileFilename(importId);
            string importCanonicalDir = Path.Combine(maDirectory, importCanonicalFilename);
            Directory.CreateDirectory(importCanonicalDir);
            string legacyImportFile = Path.Combine(maDirectory, "dir-collision-import.maFile");
            File.WriteAllText(legacyImportFile, JsonConvert.SerializeObject(CreateAccount(importId)));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);
            Assert.True(manifest.ImportUnmanagedMaFiles(candidates, "dir-key").Succeeded);
            Assert.True(Directory.Exists(importCanonicalDir));
            Assert.False(File.Exists(legacyImportFile));
            Assert.NotEqual(importCanonicalFilename, Assert.Single(manifest.Entries, e => e.SteamID == importId).Filename);
            Assert.Equal(2, Manifest.GetManifest(true).GetAllAccounts("dir-key").Length);
        }

        [Fact]
        public void CommitStorageTransaction_BackupDeletionFailuresSurfaceObsoleteCleanupPendingAndRetainBackupsForRecovery()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000012000UL;
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "encrypted-v1";
            Assert.True(manifest.SaveAccount(account, true, "key-v1").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");

            // 1. Simulate .manifest.<guid>.bak remaining locked during commit cleanup while rotating encryption keys.
            Manifest.BeforeDeleteFileForTesting = path => !Path.GetFileName(path).StartsWith(".manifest.", StringComparison.OrdinalIgnoreCase);
            try
            {
                StorageResult rotateResult = manifest.ChangeEncryptionKey("key-v1", "key-v2");
                Assert.True(rotateResult.Succeeded);
                Assert.True(rotateResult.ObsoleteCleanupPending);
            }
            finally
            {
                Manifest.BeforeDeleteFileForTesting = null;
            }

            Assert.True(File.Exists(journalPath));
            Assert.Single(Directory.EnumerateFiles(maDirectory, ".manifest.*.bak"));
            Assert.Single(Directory.EnumerateFiles(maDirectory, ".mafile.*.bak"));

            // If manifest.json now goes missing, startup restores the old manifest backup AND restores the matching account backup.
            File.Delete(manifestPath);
            Manifest recovered = Manifest.GetManifest(true);
            SteamGuardAccount recoveredAccount = Assert.Single(recovered.GetAllAccounts("key-v1"));
            Assert.Equal("encrypted-v1", recoveredAccount.Session.AccessToken);
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(journalPath));

            // 2. Simulate .mafile.*.bak remaining locked during TryPromptSetupPassKey enabling encryption.
            Assert.True(recovered.ChangeEncryptionKey("key-v1", null).Succeeded);
            List<string> shownMessages = new List<string>();
            int promptStep = 0;
            Manifest.BeforeDeleteFileForTesting = path => !Path.GetFileName(path).StartsWith(".mafile.", StringComparison.OrdinalIgnoreCase);
            try
            {
                Manifest.SetupPassKeyStatus status = recovered.TryPromptSetupPassKey(
                    "Enter passkey",
                    commitImmediately: true,
                    (label, isPassword) =>
                    {
                        promptStep++;
                        return new Manifest.PassKeyPromptResult(false, "setup-key-1", uiReady: true, loadFailed: false, accepted: true);
                    },
                    message => shownMessages.Add(message),
                    out string chosenKey);
                Assert.Equal(Manifest.SetupPassKeyStatus.Set, status);
                Assert.Equal("setup-key-1", chosenKey);
                Assert.Equal(2, promptStep);
            }
            finally
            {
                Manifest.BeforeDeleteFileForTesting = null;
            }

            Assert.Contains(shownMessages, message => message.Contains("could not be deleted immediately", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(journalPath));
            Assert.Single(Directory.EnumerateFiles(maDirectory, ".mafile.*.bak"));

            // Next startup cleans up the lingering plaintext account backup and journal.
            Manifest postSetupReloaded = Manifest.GetManifest(true);
            Assert.Equal("encrypted-v1", Assert.Single(postSetupReloaded.GetAllAccounts("setup-key-1")).Session.AccessToken);
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public async Task GetAllAccounts_AndVerifyPasskey_RecoverPendingUncommittedReplacementAndStaySafeUnderConcurrentSaves()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000013000UL;
            SteamGuardAccount committed = CreateAccount(accountId);
            committed.Session.AccessToken = "committed-enc-v1";
            Assert.True(manifest.SaveAccount(committed, true, "active-key").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string backupFilename = $".mafile.{accountId}.55555555555555555555555555555555.bak";
            string backupPath = Path.Combine(maDirectory, backupFilename);

            string committedManifestContents = File.ReadAllText(manifestPath);
            string committedCiphertext = File.ReadAllText(canonicalPath);
            string uncommittedSalt = FileEncryptor.GetRandomSalt();
            string uncommittedIv = FileEncryptor.GetInitializationVector();
            SteamGuardAccount uncommitted = CreateAccount(accountId);
            uncommitted.Session.AccessToken = "uncommitted-enc-v2";
            string uncommittedCiphertext = FileEncryptor.EncryptData(
                "active-key",
                uncommittedSalt,
                uncommittedIv,
                JsonConvert.SerializeObject(uncommitted));

            File.WriteAllText(backupPath, committedCiphertext);
            File.WriteAllText(canonicalPath, uncommittedCiphertext);
            string targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);
            JObject uncommittedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(committedManifestContents + "-failed-enc-save"),
                ["previous_manifest_hash"] = ComputeContentHash(committedManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(committedCiphertext),
                    ["replacement_hash"] = ComputeContentHash(uncommittedCiphertext),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(manifest.VerifyPasskey("active-key"));
                Assert.Empty(manifest.GetAllAccounts("active-key"));
            }

            Assert.True(manifest.VerifyPasskey("active-key"));
            SteamGuardAccount recovered = Assert.Single(manifest.GetAllAccounts("active-key"));
            Assert.Equal("committed-enc-v1", recovered.Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));

            Task writerTask = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    SteamGuardAccount next = CreateAccount(accountId);
                    next.Session.AccessToken = "concurrent-enc-" + i;
                    StorageResult saveResult = manifest.SaveAccount(next, true, "active-key");
                    Assert.True(saveResult.Succeeded);
                }
            });

            Task readerTask = Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    Assert.True(manifest.VerifyPasskey("active-key"));
                    SteamGuardAccount[] accounts = manifest.GetAllAccounts("active-key");
                    Assert.Single(accounts);
                }
            });

            await Task.WhenAll(writerTask, readerTask);

            Assert.True(manifest.ChangeEncryptionKey("active-key", null).Succeeded);
            string plaintextManifestContents = File.ReadAllText(manifestPath);
            string plaintextCommitted = File.ReadAllText(canonicalPath);
            string expectedToken = Assert.Single(manifest.GetAllAccounts()).Session.AccessToken;
            SteamGuardAccount plaintextUncommitted = CreateAccount(accountId);
            plaintextUncommitted.Session.AccessToken = "uncommitted-plaintext-leak";
            string plaintextUncommittedContents = JsonConvert.SerializeObject(plaintextUncommitted);

            File.WriteAllText(backupPath, plaintextCommitted);
            File.WriteAllText(canonicalPath, plaintextUncommittedContents);
            targetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);
            JObject plaintextJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(plaintextManifestContents + "-failed-plain-save"),
                ["previous_manifest_hash"] = ComputeContentHash(plaintextManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(plaintextCommitted),
                    ["replacement_hash"] = ComputeContentHash(plaintextUncommittedContents),
                    ["target_file_identity"] = targetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, plaintextJournal.ToString(Formatting.None));

            SteamGuardAccount plaintextRecovered = Assert.Single(manifest.GetAllAccounts());
            Assert.Equal(expectedToken, plaintextRecovered.Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public async Task GetAllAccountsWithRevisions_DetectsRolledBackInPlaceWriteAndNeverReturnsUncommittedData()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000013500UL;
            SteamGuardAccount committed = CreateAccount(accountId);
            committed.Session.AccessToken = "committed-before-rollback";
            Assert.True(manifest.SaveAccount(committed, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            long revisionBefore = manifest.StorageRevision;

            using ManualResetEventSlim readerEnteredFileRead = new ManualResetEventSlim(false);
            using ManualResetEventSlim writerReplacedFile = new ManualResetEventSlim(false);
            using ManualResetEventSlim readerFinishedFirstReadAttempt = new ManualResetEventSlim(false);
            using ManualResetEventSlim allowWriterToFailAndRollback = new ManualResetEventSlim(false);
            int readerHookCount = 0;

            manifest.OnBeforeGetAllAccountsFileReadForTest = () =>
            {
                if (Interlocked.Increment(ref readerHookCount) == 1)
                {
                    readerEnteredFileRead.Set();
                    Assert.True(writerReplacedFile.Wait(TimeSpan.FromSeconds(10)));
                }
            };

            manifest.OnAfterReadAccountsFromSnapshotForTest = () =>
            {
                if (Volatile.Read(ref readerHookCount) == 1)
                {
                    readerFinishedFirstReadAttempt.Set();
                }
            };

            Manifest.OnAfterReplaceFilesBeforeManifestCommitForTest = () =>
            {
                writerReplacedFile.Set();
                Assert.True(allowWriterToFailAndRollback.Wait(TimeSpan.FromSeconds(10)));
            };

            try
            {
                using FileStream manifestLock = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                Task<(SteamGuardAccount Account, long StorageRevision)[]> readerTask = Task.Run(() =>
                    manifest.GetAllAccountsWithRevisions());

                Assert.True(readerEnteredFileRead.Wait(TimeSpan.FromSeconds(10)));

                Task<StorageResult> writerTask = Task.Run(() =>
                {
                    SteamGuardAccount uncommitted = CreateAccount(accountId);
                    uncommitted.Session.AccessToken = "uncommitted-rolled-back-token";
                    return manifest.SaveAccount(uncommitted, false);
                });

                Assert.True(writerReplacedFile.Wait(TimeSpan.FromSeconds(10)));
                // Deterministically wait until the reader's first file-read attempt finishes
                // while the writer is paused after replacing the file, then release the writer
                // to fail manifest commit and roll back.
                Assert.True(readerFinishedFirstReadAttempt.Wait(TimeSpan.FromSeconds(10)));
                allowWriterToFailAndRollback.Set();

                StorageResult writeResult = await writerTask;
                Assert.False(writeResult.Succeeded);
                Assert.Equal(revisionBefore, manifest.StorageRevision);

                (SteamGuardAccount Account, long StorageRevision)[] readAccounts = await readerTask;
                (SteamGuardAccount loaded, _) = Assert.Single(readAccounts);
                Assert.Equal("committed-before-rollback", loaded.Session.AccessToken);
                Assert.True(readerHookCount >= 2, "Expected optimistic reader to retry after detecting the rolled-back in-place write.");
            }
            finally
            {
                manifest.OnBeforeGetAllAccountsFileReadForTest = null;
                manifest.OnAfterReadAccountsFromSnapshotForTest = null;
                Manifest.OnAfterReplaceFilesBeforeManifestCommitForTest = null;
            }
        }

        [Fact]
        public void ImportUnmanagedMaFiles_OneLockedCanonicalSourceDoesNotBlockOtherAccountsNormalizationOrSaves()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong unlockedId = 76561198000014000UL;
            ulong lockedId = 76561198000014001UL;
            string unlockedCanonical = Manifest.GetCanonicalMaFileFilename(unlockedId);
            string lockedCanonical = Manifest.GetCanonicalMaFileFilename(lockedId);
            string unlockedPath = Path.Combine(maDirectory, unlockedCanonical);
            string lockedPath = Path.Combine(maDirectory, lockedCanonical);

            SteamGuardAccount unlockedAccount = CreateAccount(unlockedId);
            unlockedAccount.Session.AccessToken = "unlocked-v1";
            SteamGuardAccount lockedAccount = CreateAccount(lockedId);
            lockedAccount.Session.AccessToken = "locked-v1";
            File.WriteAllText(unlockedPath, JsonConvert.SerializeObject(unlockedAccount));
            File.WriteAllText(lockedPath, JsonConvert.SerializeObject(lockedAccount));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Equal(2, candidates.Count);

            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);

                Assert.Equal(unlockedCanonical, Assert.Single(manifest.Entries, e => e.SteamID == unlockedId).Filename);
                unlockedAccount.Session.AccessToken = "unlocked-v2";
                Assert.True(manifest.SaveAccount(unlockedAccount, false).Succeeded);
                Assert.Equal(unlockedCanonical, Assert.Single(manifest.Entries, e => e.SteamID == unlockedId).Filename);
            }

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(unlockedCanonical, Assert.Single(reloaded.Entries, e => e.SteamID == unlockedId).Filename);
            Assert.Equal(lockedCanonical, Assert.Single(reloaded.Entries, e => e.SteamID == lockedId).Filename);
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_RejectsMismatchedManifestBackupAndInvalidOwnershipBeforePublishingManifest()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000015000UL;
            Assert.True(manifest.SaveAccount(CreateAccount(accountId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string backupPath = Path.Combine(maDirectory, TestBackupFilename);
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);

            string originalManifestContents = File.ReadAllText(manifestPath);
            JObject mismatchedManifest = JObject.Parse(originalManifestContents);
            mismatchedManifest["storage_transaction_id"] = Guid.NewGuid().ToString("N");
            File.WriteAllText(backupPath, mismatchedManifest.ToString(Formatting.None));
            File.Delete(manifestPath);

            // 1. Mismatched manifest backup hash fails closed without publishing manifest.json.
            JObject journal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(originalManifestContents + "-committed"),
                ["previous_manifest_hash"] = ComputeContentHash(originalManifestContents),
                ["created_filenames"] = new JArray(),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, journal.ToString(Formatting.None));

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.False(File.Exists(manifestPath));
            Assert.True(File.Exists(backupPath));
            Assert.True(File.Exists(journalPath));

            // 2. Valid manifest backup with tampered ownership (attempting to delete a live file)
            //    must ALSO fail closed BEFORE publishing manifest.json.
            File.WriteAllText(backupPath, originalManifestContents);
            JObject badOwnershipJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(originalManifestContents + "-committed"),
                ["previous_manifest_hash"] = ComputeContentHash(originalManifestContents),
                ["created_filenames"] = new JArray(),
                ["obsolete_filenames"] = new JArray(),
                ["retained_obsolete_filenames"] = new JArray(canonicalFilename),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, badOwnershipJournal.ToString(Formatting.None));

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.False(File.Exists(manifestPath));
            Assert.True(File.Exists(backupPath));
        }

        [Fact]
        public void StartupRecovery_RejectsCrossAccountReplacedBackupSwap()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000016000UL;
            ulong secondId = 76561198000016001UL;
            SteamGuardAccount first = CreateAccount(firstId);
            first.Session.AccessToken = "first-original";
            SteamGuardAccount second = CreateAccount(secondId);
            second.Session.AccessToken = "second-original";
            Assert.True(manifest.SaveAccount(first, false).Succeeded);
            Assert.True(manifest.SaveAccount(second, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string firstFilename = Manifest.GetCanonicalMaFileFilename(firstId);
            string secondFilename = Manifest.GetCanonicalMaFileFilename(secondId);
            string firstPath = Path.Combine(maDirectory, firstFilename);
            string secondPath = Path.Combine(maDirectory, secondFilename);

            string originalManifestContents = File.ReadAllText(manifestPath);
            string firstOriginalContents = File.ReadAllText(firstPath);
            string secondOriginalContents = File.ReadAllText(secondPath);
            string firstIdentity = StoragePathGuard.GetFileIdentityToken(firstPath, canonicalDir);

            string secondBackupFilename = $".mafile.{secondId}.66666666666666666666666666666666.bak";
            File.WriteAllText(Path.Combine(maDirectory, secondBackupFilename), secondOriginalContents);

            JObject swappedJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(originalManifestContents + "-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(originalManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = firstFilename,
                    ["backup_filename"] = secondBackupFilename,
                    ["original_hash"] = ComputeContentHash(secondOriginalContents),
                    ["replacement_hash"] = ComputeContentHash(firstOriginalContents),
                    ["target_file_identity"] = firstIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, swappedJournal.ToString(Formatting.None));

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal(firstOriginalContents, File.ReadAllText(firstPath));
            Assert.Equal(secondOriginalContents, File.ReadAllText(secondPath));
        }

        [Fact]
        public void StartupRecovery_HandleBoundCreatedFileDeletionWorksInLongDataDirectoryPaths()
        {
            string nestedSegment = new string('a', 60);
            string longDataDir = Path.Combine(dataDirectory, nestedSegment, nestedSegment, nestedSegment, nestedSegment);
            string longMaDir = Path.Combine(longDataDir, "maFiles");
            Directory.CreateDirectory(longMaDir);
            Assert.True(longMaDir.Length > 260);

            string previousDir = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", longDataDir);
            try
            {
                ulong accountId = 76561198000017000UL;
                string createdFilename = Manifest.GetCanonicalMaFileFilename(accountId);
                string createdPath = Path.Combine(longMaDir, createdFilename);
                string createdContents = JsonConvert.SerializeObject(CreateAccount(accountId));
                File.WriteAllText(createdPath, createdContents);
                string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(longMaDir);
                string createdIdentity = StoragePathGuard.GetFileIdentityToken(createdPath, canonicalDir);

                JObject journal = new JObject
                {
                    ["journal_version"] = 2,
                    ["manifest_hash"] = ComputeContentHash("long-path-uncommitted"),
                    ["created_filenames"] = new JArray(createdFilename),
                    ["created_file_hashes"] = new JObject { [createdFilename] = ComputeContentHash(createdContents) },
                    ["created_file_identities"] = new JObject { [createdFilename] = createdIdentity },
                    ["obsolete_filenames"] = new JArray(),
                    ["backup_filename"] = null
                };
                string journalPath = Path.Combine(longMaDir, ".asda-storage-transaction.json");
                File.WriteAllText(journalPath, journal.ToString(Formatting.None));

                Manifest.GetManifest(true);
                Assert.False(File.Exists(createdPath));
                Assert.False(File.Exists(journalPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDir);
            }
        }

        [Fact]
        public void SaveAccount_AndRecovery_HandleHardLinksWithExplicitSafePolicy()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000018000UL;
            SteamGuardAccount original = CreateAccount(accountId);
            original.Session.AccessToken = "hardlink-v1";
            Assert.True(manifest.SaveAccount(original, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string externalHardLink = Path.Combine(dataDirectory, "external-hardlink.maFile");
            string originalContents = File.ReadAllText(canonicalPath);

            CreateHardLink(externalHardLink, canonicalPath);

            // 1. In-place canonical replacement must refuse multiply-linked files and fall back to GUID,
            //    leaving the external hard link's bytes completely untouched.
            original.Session.AccessToken = "hardlink-v2";
            Assert.True(manifest.SaveAccount(original, false).Succeeded);
            Assert.Equal(originalContents, File.ReadAllText(externalHardLink));
            Assert.Equal("hardlink-v2", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);

            // 2. Handle-bound exclusive deletion must preserve multiply-linked files even when hash & identity match.
            string linkedCreatedPath = Path.Combine(maDirectory, "hardlinked-created.maFile");
            string linkedCopyPath = Path.Combine(dataDirectory, "hardlinked-created-peer.maFile");
            File.WriteAllText(linkedCreatedPath, "linked-payload");
            CreateHardLink(linkedCopyPath, linkedCreatedPath);
            string linkedHash = ComputeContentHash("linked-payload");
            StoragePathGuard.StorageFileIdentity.TryParseToken(
                StoragePathGuard.GetFileIdentityToken(linkedCreatedPath, canonicalDir),
                out StoragePathGuard.StorageFileIdentity linkedIdentity);

            bool completed = StoragePathGuard.DeleteFileIfVerifiedExclusive(
                linkedCreatedPath,
                linkedHash,
                linkedIdentity,
                canonicalDir,
                out string preservedReason);
            Assert.True(completed);
            Assert.Equal("multiple_links", preservedReason);
            Assert.True(File.Exists(linkedCreatedPath));
            Assert.Equal("linked-payload", File.ReadAllText(linkedCopyPath));

            // 3. RestoreReplacedFileFromVerifiedBackup rejects restoring over a multiply-linked target.
            string backupPath = Path.Combine(maDirectory, $".mafile.{accountId}.77777777777777777777777777777777.bak");
            File.WriteAllText(backupPath, "backup-original");
            Assert.Throws<InvalidDataException>(() => StoragePathGuard.RestoreReplacedFileFromVerifiedBackup(
                backupPath,
                linkedCreatedPath,
                ComputeContentHash("backup-original"),
                linkedHash,
                linkedIdentity,
                canonicalDir));
            Assert.Equal("linked-payload", File.ReadAllText(linkedCopyPath));
        }

        [Fact]
        public void StoragePathGuard_HandleOperationsRejectJunctionsAndPathsOutsideCanonicalDirectory()
        {
            string junctionRoot = Path.Combine(dataDirectory, "guard-junction-test");
            string realMaDir = Path.Combine(junctionRoot, "real-maFiles");
            string outsideDir = Path.Combine(junctionRoot, "outside");
            string junctionMaDir = Path.Combine(junctionRoot, "junction-maFiles");
            Directory.CreateDirectory(realMaDir);
            Directory.CreateDirectory(outsideDir);
            CreateDirectoryJunction(junctionMaDir, outsideDir);

            try
            {
                string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(realMaDir);
                string outsideFile = Path.Combine(outsideDir, "secret.maFile");
                string outsideBackup = Path.Combine(outsideDir, ".mafile.76561198000019000.88888888888888888888888888888888.bak");
                File.WriteAllText(outsideFile, "outside-secret");
                File.WriteAllText(outsideBackup, "outside-backup");

                string viaJunctionFile = Path.Combine(junctionMaDir, "secret.maFile");
                string viaJunctionBackup = Path.Combine(junctionMaDir, ".mafile.76561198000019000.88888888888888888888888888888888.bak");
                string hash = ComputeContentHash("outside-secret");

                Assert.Throws<InvalidDataException>(() => StoragePathGuard.ComputeFileSha256(viaJunctionFile, expectedCanonicalDir, out _));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.TryInspectCanonicalTargetForInPlaceReplacement(viaJunctionFile, expectedCanonicalDir, 64 * 1024 * 1024));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.OpenAndBackUpForInPlaceReplacement(viaJunctionFile, viaJunctionBackup + ".new", expectedCanonicalDir, 64 * 1024 * 1024));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.DeleteFileIfVerifiedExclusive(viaJunctionFile, hash, null, expectedCanonicalDir, out _));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.DeleteRegularFileWithinCanonicalDirectory(viaJunctionFile, expectedCanonicalDir));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.RestoreReplacedFileFromVerifiedBackup(
                    viaJunctionBackup,
                    viaJunctionFile,
                    ComputeContentHash("outside-backup"),
                    hash,
                    null,
                    expectedCanonicalDir));

                // Outside file must remain completely untouched.
                Assert.True(File.Exists(outsideFile));
                Assert.Equal("outside-secret", File.ReadAllText(outsideFile));
            }
            finally
            {
                try { Directory.Delete(junctionMaDir, false); } catch { }
            }
        }

        [Fact]
        public void StartupRecovery_AndRebase_RejectCaseInsensitiveDuplicateKeysAndProtectCredentialsStoreAndSupportV1Upgrade()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000020000UL;
            Assert.True(manifest.SaveAccount(CreateAccount(accountId), true, "cred-key").Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(accountId, "MySteamPass!123", "cred-key").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string credFilename = manifest.CredentialsStoreFilename;
            Assert.False(String.IsNullOrWhiteSpace(credFilename));
            string credPath = Path.Combine(maDirectory, credFilename);
            Assert.True(File.Exists(credPath));
            string liveManifestContents = File.ReadAllText(manifestPath);
            string liveManifestHash = ComputeContentHash(liveManifestContents);

            // 1. Tampered journal trying to delete live CredentialsStoreFilename must fail closed.
            foreach (string listField in new[] { "obsolete_filenames", "retained_obsolete_filenames" })
            {
                JObject credDeleteJournal = new JObject
                {
                    ["journal_version"] = 2,
                    ["manifest_hash"] = liveManifestHash,
                    ["created_filenames"] = new JArray(),
                    ["obsolete_filenames"] = new JArray(),
                    ["backup_filename"] = null
                };
                credDeleteJournal[listField] = new JArray(credFilename);
                File.WriteAllText(journalPath, credDeleteJournal.ToString(Formatting.None));
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(credPath));
            }

            // 2. Case-insensitive duplicate non-version keys (manifest_hash / MANIFEST_HASH and nested replaced_files fields) fail closed.
            string dupManifestHashJson = $"{{\"journal_version\":2,\"manifest_hash\":\"{liveManifestHash}\",\"MANIFEST_HASH\":\"{ComputeContentHash("other")}\",\"created_filenames\":[],\"obsolete_filenames\":[],\"backup_filename\":null}}";
            File.WriteAllText(journalPath, dupManifestHashJson);
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));

            string dupNestedReplacedJson = $"{{\"journal_version\":2,\"manifest_hash\":\"{liveManifestHash}\",\"created_filenames\":[],\"replaced_files\":[{{\"filename\":\"{Manifest.GetCanonicalMaFileFilename(accountId)}\",\"backup_filename\":\".mafile.{accountId}.99999999999999999999999999999999.bak\",\"original_hash\":\"{liveManifestHash}\",\"ORIGINAL_HASH\":\"{liveManifestHash}\",\"replacement_hash\":\"{liveManifestHash}\",\"target_file_identity\":\"00000001:0000000000000001\"}}],\"obsolete_filenames\":[],\"backup_filename\":null}}";
            File.WriteAllText(journalPath, dupNestedReplacedJson);
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));

            // 3. RebaseRetainedStorageTransaction (via SaveWithResult) also rejects case-insensitive duplicate keys.
            File.WriteAllText(journalPath, dupManifestHashJson);
            Assert.False(manifest.SaveWithResult().Succeeded);
            File.Delete(journalPath);

            // 4. Legacy unversioned v1 journal from main containing an obsolete credential sidecar recovers cleanly after upgrade.
            string oldCredFilename = EncryptedCredentialStore.CreateNewFilename();
            string oldCredPath = Path.Combine(maDirectory, oldCredFilename);
            File.WriteAllText(oldCredPath, "{\"legacy\":\"old-cred\"}");
            JObject legacyV1CredJournal = new JObject
            {
                ["manifest_hash"] = liveManifestHash,
                ["created_filenames"] = new JArray(credFilename),
                ["obsolete_filenames"] = new JArray(oldCredFilename),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, legacyV1CredJournal.ToString(Formatting.None));

            Manifest upgraded = Manifest.GetManifest(true);
            Assert.False(File.Exists(oldCredPath));
            Assert.False(File.Exists(journalPath));
            Assert.True(File.Exists(credPath));
            Assert.Equal("MySteamPass!123", upgraded.GetSavedCredentialRecord(accountId, "cred-key")?.Password);
        }

        [Fact]
        public void SaveAccount_AndChangeEncryptionKey_PreserveMonotonicRevisionsCredentialStoreAndIdenticalBytesRollback()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000021000UL;
            ulong secondId = 76561198000021001UL;
            SteamGuardAccount first = CreateAccount(firstId);
            first.Session.AccessToken = "first-v1";
            SteamGuardAccount second = CreateAccount(secondId);
            second.Session.AccessToken = "second-v1";

            // 1. Monotonic per-account storage revision across canonical update -> remove -> re-save.
            StorageResult createRes = manifest.SaveAccount(first, false);
            Assert.True(createRes.Succeeded);
            long revAfterCreate = manifest.GetAccountStorageRevision(firstId);
            Assert.Equal(revAfterCreate, createRes.CommittedStorageRevision);

            first.Session.AccessToken = "first-v2";
            StorageResult updateRes = manifest.SaveAccount(first, false);
            Assert.True(updateRes.Succeeded);
            long revAfterUpdate = manifest.GetAccountStorageRevision(firstId);
            Assert.True(revAfterUpdate > revAfterCreate);
            Assert.Equal(revAfterUpdate, updateRes.CommittedStorageRevision);

            Assert.True(manifest.RemoveAccount(first));
            long revAfterRemove = manifest.GetAccountStorageRevision(firstId);
            Assert.True(revAfterRemove > revAfterUpdate);

            first.Session.AccessToken = "first-v3-reimported";
            StorageResult resaveRes = manifest.SaveAccount(first, false);
            Assert.True(resaveRes.Succeeded);
            long revAfterResave = manifest.GetAccountStorageRevision(firstId);
            Assert.True(revAfterResave > revAfterRemove);
            Assert.Equal(revAfterResave, resaveRes.CommittedStorageRevision);

            // 2. Identical-bytes unencrypted in-place rollback (e.g., only LastTransferAttemptId changes while manifest.json is locked).
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult metaOnlyFail = manifest.SaveAccount(first, false, transferAttemptId: "attempt-blocked");
                Assert.False(metaOnlyFail.Succeeded);
            }
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            Assert.Equal("first-v3-reimported", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);

            // 3. Multi-account ChangeEncryptionKey + saved credential store commits in 1 transaction,
            //    and locking the second canonical .maFile leaves account 1 untouched with zero leftover backups.
            Assert.True(manifest.SaveAccount(second, false).Succeeded);
            Assert.True(manifest.ChangeEncryptionKey(null, "vault-key-1").Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(firstId, "PassOne!123", "vault-key-1").Succeeded);

            long commitsBeforeRotateWithCreds = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ChangeEncryptionKey("vault-key-1", "vault-key-2").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeRotateWithCreds);
            Assert.Equal("PassOne!123", Manifest.GetManifest(true).GetSavedCredentialRecord(firstId, "vault-key-2")?.Password);

            string secondCanonicalPath = Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(secondId));
            using (new FileStream(secondCanonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult midBatchFail = manifest.ChangeEncryptionKey("vault-key-2", "vault-key-3");
                Assert.False(midBatchFail.Succeeded);
            }

            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            Manifest afterMidBatchFail = Manifest.GetManifest(true);
            Assert.Equal(2, afterMidBatchFail.GetAllAccounts("vault-key-2").Length);
            Assert.Equal("PassOne!123", afterMidBatchFail.GetSavedCredentialRecord(firstId, "vault-key-2")?.Password);
        }

        [Fact]
        public void SaveAccount_MidWriteFlushFailureRestoresOriginalBytesAndNeverDeletesBackupWithoutRestoring()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000022000UL;
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "original-before-midwrite-fail";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalPath = Path.Combine(maDirectory, Manifest.GetCanonicalMaFileFilename(accountId));
            string originalContents = File.ReadAllText(canonicalPath);

            // Simulate an I/O error inside OverwriteAndFlush AFTER bytes have been written and
            // truncated on the target stream, before Flush(true) completes.
            StoragePathGuard.OnBeforeFlushReplacementForTest = path =>
            {
                throw new IOException("Simulated disk flush failure after partial in-place write.");
            };
            try
            {
                account.Session.AccessToken = "corrupted-midwrite-update";
                StorageResult result = manifest.SaveAccount(account, false);
                Assert.False(result.Succeeded);
            }
            finally
            {
                StoragePathGuard.OnBeforeFlushReplacementForTest = null;
            }

            Assert.Equal(originalContents, File.ReadAllText(canonicalPath));
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.bak"));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            Assert.Equal("original-before-midwrite-fail", Assert.Single(Manifest.GetManifest(true).GetAllAccounts()).Session.AccessToken);
        }

        [Fact]
        public void StartupRecovery_RecoversPartialInterruptedWriteAndRecreatedMissingTargetAcrossCrash()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000023000UL;
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "intact-original-token";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string backupFilename = $".mafile.{accountId}.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bak";
            string backupPath = Path.Combine(maDirectory, backupFilename);

            string manifestContents = File.ReadAllText(manifestPath);
            string originalContents = File.ReadAllText(canonicalPath);

            // 1. Simulate a power-loss partial write: exact same FileIndex (in-place overwrite),
            //    truncated corrupt JSON on disk, valid backup matching original_hash.
            File.WriteAllText(backupPath, originalContents);
            using (FileStream fs = new FileStream(canonicalPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] partialBytes = Encoding.UTF8.GetBytes("{\"shared_secret\":\"truncated-mid-write");
                fs.Write(partialBytes, 0, partialBytes.Length);
                fs.SetLength(partialBytes.Length);
                fs.Flush(true);
            }
            string sameTargetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            JObject partialWriteJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(manifestContents + "-uncommitted-partial"),
                ["previous_manifest_hash"] = ComputeContentHash(manifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(originalContents),
                    ["replacement_hash"] = ComputeContentHash("{\"full\":\"never-finished\"}"),
                    ["target_file_identity"] = sameTargetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, partialWriteJournal.ToString(Formatting.None));

            Manifest recoveredPartial = Manifest.GetManifest(true);
            Assert.Equal("intact-original-token", Assert.Single(recoveredPartial.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));

            // 2. Simulate crash after missing-target recreation (new FileIndex) and backup deletion,
            //    before journal removal: next startup must accept the restored file matching original_hash.
            string oldIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);
            File.Delete(canonicalPath);
            string tempSwap = Path.Combine(maDirectory, "recreate-new-index.tmp");
            File.WriteAllText(tempSwap, originalContents);
            File.Move(tempSwap, canonicalPath);
            string newIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);
            // Write journal referencing oldIdentity while backup is already gone and canonicalPath has originalContents.
            JObject postRecreateJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(manifestContents + "-uncommitted-recreate"),
                ["previous_manifest_hash"] = ComputeContentHash(manifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(originalContents),
                    ["replacement_hash"] = ComputeContentHash("{\"full\":\"replacement\"}"),
                    ["target_file_identity"] = oldIdentity == newIdentity ? "00000001:0000000000000001" : oldIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, postRecreateJournal.ToString(Formatting.None));

            Manifest recoveredPostRecreate = Manifest.GetManifest(true);
            Assert.Equal("intact-original-token", Assert.Single(recoveredPostRecreate.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void VerifyPasskey_AndEncryptedOperationsSucceedWhileRetainedObsoleteJournalIsOnDisk()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000024000UL;
            ulong secondId = 76561198000024001UL;
            ulong thirdId = 76561198000024002UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), true, "retained-key-1").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");

            // Import an unmanaged file while locking the source file so a retained-obsolete-only
            // journal remains on disk after commit.
            string lockedSourceFilename = "locked-obsolete-source.maFile";
            string lockedSourcePath = Path.Combine(maDirectory, lockedSourceFilename);
            File.WriteAllText(lockedSourcePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            using (new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult importResult = manifest.ImportUnmanagedMaFiles(candidates, "retained-key-1");
                Assert.True(importResult.Succeeded);
                Assert.True(importResult.ObsoleteCleanupPending);
                Assert.True(File.Exists(journalPath));

                // VerifyPasskey, GetAllAccounts, ChangeEncryptionKey, and another ImportUnmanagedMaFiles
                // must ALL succeed while the retained-obsolete cleanup journal stays on disk.
                Manifest freshInstance = Manifest.GetManifest(true);
                Assert.True(File.Exists(journalPath));
                Assert.True(freshInstance.VerifyPasskey("retained-key-1"));
                Assert.Equal(2, freshInstance.GetAllAccounts("retained-key-1").Length);

                StorageResult rotateResult = freshInstance.ChangeEncryptionKey("retained-key-1", "retained-key-2");
                Assert.True(rotateResult.Succeeded);
                Assert.True(rotateResult.ObsoleteCleanupPending);
                Assert.True(freshInstance.VerifyPasskey("retained-key-2"));

                string thirdSourcePath = Path.Combine(maDirectory, "third-unlocked.maFile");
                File.WriteAllText(thirdSourcePath, JsonConvert.SerializeObject(CreateAccount(thirdId)));
                var thirdCandidates = freshInstance.FindUnmanagedMaFiles();
                Assert.Single(thirdCandidates);
                StorageResult thirdImport = freshInstance.ImportUnmanagedMaFiles(thirdCandidates, "retained-key-2");
                Assert.True(thirdImport.Succeeded);
                Assert.Equal(3, freshInstance.GetAllAccounts("retained-key-2").Length);
            }

            // Once unlocked, startup cleans up the retained obsolete file and removes the journal.
            Manifest finalReload = Manifest.GetManifest(true);
            Assert.False(File.Exists(lockedSourcePath));
            Assert.False(File.Exists(journalPath));
            Assert.Equal(3, finalReload.GetAllAccounts("retained-key-2").Length);
        }

        [Fact]
        public void CommitStorageTransaction_PreservesPreviousCredentialStoreWhenManifestBackupLockedAndSweepsOrphanedAccountBackups()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000025000UL;
            Assert.True(manifest.SaveAccount(CreateAccount(accountId), true, "cred-keep-v1").Succeeded);
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(accountId, "KeepMeSafe!99", "cred-keep-v1").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string oldCredFilename = manifest.CredentialsStoreFilename;
            string oldCredPath = Path.Combine(maDirectory, oldCredFilename);
            Assert.True(File.Exists(oldCredPath));

            // 1. Block deletion of .manifest.<guid>.bak during key rotation (which creates a new credential store).
            //    SweepOrphanedCredentialStoresLocked must NOT delete oldCredPath while .manifest.<guid>.bak remains.
            Manifest.BeforeDeleteFileForTesting = path => !Path.GetFileName(path).StartsWith(".manifest.", StringComparison.OrdinalIgnoreCase);
            try
            {
                StorageResult rotateResult = manifest.ChangeEncryptionKey("cred-keep-v1", "cred-keep-v2");
                Assert.True(rotateResult.Succeeded);
                Assert.True(rotateResult.ObsoleteCleanupPending);
            }
            finally
            {
                Manifest.BeforeDeleteFileForTesting = null;
            }

            Assert.True(File.Exists(oldCredPath));
            Assert.Single(Directory.EnumerateFiles(maDirectory, ".manifest.*.bak"));

            // If manifest.json now goes missing, startup restores the old manifest AND the old credential store still works.
            File.Delete(manifestPath);
            Manifest restored = Manifest.GetManifest(true);
            Assert.Equal(oldCredFilename, restored.CredentialsStoreFilename);
            Assert.Equal("KeepMeSafe!99", restored.GetSavedCredentialRecord(accountId, "cred-keep-v1")?.Password);

            // 2. Orphaned pre-journal .mafile.<steamId>.<guid>.bak (from a crash before journal write)
            //    is swept cleanly on subsequent encryption setup, and surfaces ObsoleteCleanupPending if locked.
            Assert.True(restored.ChangeEncryptionKey("cred-keep-v1", null).Succeeded);
            string orphanedBackupFilename = $".mafile.{accountId}.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.bak";
            string orphanedBackupPath = Path.Combine(maDirectory, orphanedBackupFilename);
            File.WriteAllText(orphanedBackupPath, "{\"plaintext\":\"orphaned-pre-journal-backup\"}");

            using (new FileStream(orphanedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult encryptWithLockedOrphan = restored.ChangeEncryptionKey(null, "orphan-key-1");
                Assert.True(encryptWithLockedOrphan.Succeeded);
                Assert.True(encryptWithLockedOrphan.ObsoleteCleanupPending);
                Assert.True(File.Exists(orphanedBackupPath));
            }

            // Once unlocked, next startup or save sweeps the orphaned plaintext backup cleanly.
            Manifest.GetManifest(true);
            Assert.False(File.Exists(orphanedBackupPath));
        }

        [Fact]
        public void SaveWithResult_RebaseRetainedStorageTransactionDirectlyRejectsDuplicateKeysAndInjectedEntries()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000026000UL;
            ulong secondId = 76561198000026001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string lockedSourceFilename = "rebase-locked-obsolete.maFile";
            string lockedSourcePath = Path.Combine(maDirectory, lockedSourceFilename);
            File.WriteAllText(lockedSourcePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            using (new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);
                Assert.True(File.Exists(journalPath));
                string validRetainedJournal = File.ReadAllText(journalPath);

                // 1. Tamper with the journal right BEFORE RebaseRetainedStorageTransaction (after
                //    RecoverPendingStorageTransaction has already succeeded) to inject a duplicate key.
                bool rebaseHookHit = false;
                Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = () =>
                {
                    rebaseHookHit = true;
                    string hash = ComputeContentHash("rebase-dup");
                    File.WriteAllText(
                        journalPath,
                        $"{{\"journal_version\":2,\"manifest_hash\":\"{hash}\",\"MANIFEST_HASH\":\"{hash}\",\"created_filenames\":[],\"obsolete_filenames\":[],\"retained_obsolete_filenames\":[\"{lockedSourceFilename}\"],\"backup_filename\":null}}");
                };
                try
                {
                    manifest.MinimizeToTray = !manifest.MinimizeToTray;
                    StorageResult dupResult = manifest.SaveWithResult();
                    Assert.True(rebaseHookHit);
                    Assert.False(dupResult.Succeeded);
                }
                finally
                {
                    Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = null;
                }

                // Restore valid journal matching current manifest so recovery passes again, then inject
                // an extra unvalidated filename right before RebaseRetainedStorageTransaction.
                JObject validObj = JObject.Parse(validRetainedJournal);
                validObj["manifest_hash"] = ComputeContentHash(File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
                File.WriteAllText(journalPath, validObj.ToString(Formatting.None));

                rebaseHookHit = false;
                string injectedExtraFilename = "injected-extra.maFile";
                string injectedExtraPath = Path.Combine(maDirectory, injectedExtraFilename);
                File.WriteAllText(injectedExtraPath, "{\"untracked\":\"must-not-be-deleted\"}");
                Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = () =>
                {
                    rebaseHookHit = true;
                    JObject injected = JObject.Parse(File.ReadAllText(journalPath));
                    ((JArray)injected["retained_obsolete_filenames"]).Add(injectedExtraFilename);
                    File.WriteAllText(journalPath, injected.ToString(Formatting.None));
                };
                try
                {
                    manifest.CheckForUpdates = !manifest.CheckForUpdates;
                    StorageResult injectedResult = manifest.SaveWithResult();
                    Assert.True(rebaseHookHit);
                    Assert.False(injectedResult.Succeeded);
                }
                finally
                {
                    Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = null;
                }

                // Restarting after the rejected rebase MUST NOT delete the injected untracked file,
                // and must retain only the recovery-validated obsolete entry.
                JObject journalAfterRejectedRebase = JObject.Parse(File.ReadAllText(journalPath));
                Assert.Equal(new[] { lockedSourceFilename }, journalAfterRejectedRebase["retained_obsolete_filenames"].Values<string>().ToArray());
                Manifest restartedWhileLocked = Manifest.GetManifest(true);
                Assert.Equal(2, restartedWhileLocked.Entries.Count);
                Assert.True(File.Exists(injectedExtraPath));
                Assert.Equal("{\"untracked\":\"must-not-be-deleted\"}", File.ReadAllText(injectedExtraPath));
            }

            // Once unlocked, restart cleans up ONLY the legitimate obsolete file and preserves injected-extra.maFile.
            Manifest restartedAfterUnlock = Manifest.GetManifest(true);
            Assert.Equal(2, restartedAfterUnlock.Entries.Count);
            Assert.False(File.Exists(lockedSourcePath));
            Assert.False(File.Exists(journalPath));
            Assert.True(File.Exists(Path.Combine(maDirectory, "injected-extra.maFile")));
            Assert.Equal("{\"untracked\":\"must-not-be-deleted\"}", File.ReadAllText(Path.Combine(maDirectory, "injected-extra.maFile")));
        }

        [Fact]
        public void StartupRecovery_RecoversInterruptedEncryptedInPlaceWriteWhilePreservingExternallyModifiedCiphertext()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000027000UL;
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "encrypted-original-token";
            Assert.True(manifest.SaveAccount(account, true, "enc-recover-key").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);

            string manifestContents = File.ReadAllText(manifestPath);
            string originalCiphertext = File.ReadAllText(canonicalPath);

            // Simulate an I/O failure inside OverwriteAndFlush after writing the sentinel-prefixed
            // encrypted body and before finalizing byte 0, verifying clean in-memory rollback.
            StoragePathGuard.OnBeforeFlushReplacementForTest = path =>
            {
                throw new IOException("Simulated crash before byte-0 commit on encrypted replacement.");
            };
            try
            {
                account.Session.AccessToken = "encrypted-updated-token-that-crashed";
                StorageResult failedSave = manifest.SaveAccount(account, true, "enc-recover-key");
                Assert.False(failedSave.Succeeded);
            }
            finally
            {
                StoragePathGuard.OnBeforeFlushReplacementForTest = null;
            }
            Assert.Equal(originalCiphertext, File.ReadAllText(canonicalPath));

            // 1. Simulate hard power loss leaving the leading 0xFF sentinel + block-aligned Base64
            //    body on the same target FileIndex with a verified backup on disk.
            string backupFilename = $".mafile.{accountId}.cccccccccccccccccccccccccccccccc.bak";
            string backupPath = Path.Combine(maDirectory, backupFilename);
            File.WriteAllText(backupPath, originalCiphertext);

            byte[] midWriteSentinelBytes = (byte[])Encoding.UTF8.GetBytes(originalCiphertext).Clone();
            midWriteSentinelBytes[0] = 0xFF;
            using (FileStream fs = new FileStream(canonicalPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Write(midWriteSentinelBytes, 0, midWriteSentinelBytes.Length);
                fs.SetLength(midWriteSentinelBytes.Length);
                fs.Flush(true);
            }
            string sameTargetIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            JObject encryptedPartialJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(manifestContents + "-enc-uncommitted"),
                ["previous_manifest_hash"] = ComputeContentHash(manifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(originalCiphertext),
                    ["replacement_hash"] = ComputeContentHash("different-replacement-ciphertext"),
                    ["target_file_identity"] = sameTargetIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, encryptedPartialJournal.ToString(Formatting.None));

            Manifest recovered = Manifest.GetManifest(true);
            Assert.Equal("encrypted-original-token", Assert.Single(recovered.GetAllAccounts("enc-recover-key")).Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));

            // 2. Externally modified valid Base64 AES-block-aligned ciphertext (no 0xFF sentinel,
            //    different hash from both OriginalHash and ReplacementHash) must STILL fail closed.
            string externalCiphertext = Convert.ToBase64String(new byte[32]);
            File.WriteAllText(backupPath, originalCiphertext);
            using (FileStream fs = new FileStream(canonicalPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] externalBytes = Encoding.UTF8.GetBytes(externalCiphertext);
                fs.Write(externalBytes, 0, externalBytes.Length);
                fs.SetLength(externalBytes.Length);
                fs.Flush(true);
            }
            encryptedPartialJournal["replaced_files"][0]["target_file_identity"] = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);
            File.WriteAllText(journalPath, encryptedPartialJournal.ToString(Formatting.None));

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.Equal(externalCiphertext, File.ReadAllText(canonicalPath));
        }

        [Fact]
        public void StartupRecovery_MissingTargetRestoreInterruptedBeforePublishDoesNotStrandNextRecovery()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000028000UL;
            SteamGuardAccount account = CreateAccount(accountId);
            account.Session.AccessToken = "missing-target-original";
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string backupFilename = $".mafile.{accountId}.dddddddddddddddddddddddddddddddd.bak";
            string backupPath = Path.Combine(maDirectory, backupFilename);

            string manifestContents = File.ReadAllText(manifestPath);
            string originalContents = File.ReadAllText(canonicalPath);
            string originalIdentity = StoragePathGuard.GetFileIdentityToken(canonicalPath, canonicalDir);

            File.WriteAllText(backupPath, originalContents);
            File.Delete(canonicalPath);

            JObject missingTargetJournal = new JObject
            {
                ["journal_version"] = 2,
                ["manifest_hash"] = ComputeContentHash(manifestContents + "-missing-target"),
                ["previous_manifest_hash"] = ComputeContentHash(manifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = backupFilename,
                    ["original_hash"] = ComputeContentHash(originalContents),
                    ["replacement_hash"] = ComputeContentHash("{\"full\":\"replacement\"}"),
                    ["target_file_identity"] = originalIdentity
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, missingTargetJournal.ToString(Formatting.None));

            // Interrupt missing-target recreation AFTER temp staging and BEFORE atomic FileRenameInfo publish.
            StoragePathGuard.OnBeforePublishRestoredMissingTargetForTest = _ =>
                throw new IOException("Simulated crash during missing-target restore before publish.");
            try
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            }
            finally
            {
                StoragePathGuard.OnBeforePublishRestoredMissingTargetForTest = null;
            }

            // Target must NOT exist half-written, no temp file leaked, and backup + journal stay intact.
            Assert.False(File.Exists(canonicalPath));
            Assert.Empty(Directory.EnumerateFiles(maDirectory, "*.tmp"));
            Assert.True(File.Exists(backupPath));
            Assert.True(File.Exists(journalPath));

            // Next startup cleanly stages and publishes the verified backup to canonicalPath.
            Manifest recovered = Manifest.GetManifest(true);
            Assert.Equal("missing-target-original", Assert.Single(recovered.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void CleanupOnlyJournal_LockedJournalAllowsVerifyPasskeyAndPreventsSettingsCredentialSweep()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000029000UL;
            ulong secondId = 76561198000029001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), true, "cleanup-key-1").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string lockedSourceFilename = "cleanup-locked-obsolete.maFile";
            string lockedSourcePath = Path.Combine(maDirectory, lockedSourceFilename);
            File.WriteAllText(lockedSourcePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            string orphanedCredPath = Path.Combine(maDirectory, "credentials.eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee.asda-cred");
            File.WriteAllText(orphanedCredPath, "orphaned-cred-store-placeholder");

            using (new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates, "cleanup-key-1").Succeeded);
                Assert.True(File.Exists(journalPath));

                // 1. Settings save while retained-cleanup-only journal remains on disk must NOT sweep
                //    credential store files until the journal is cleared.
                manifest.MinimizeToTray = !manifest.MinimizeToTray;
                Assert.True(manifest.SaveWithResult().Succeeded);
                Assert.True(File.Exists(journalPath));
                Assert.True(File.Exists(orphanedCredPath));
            }

            // 2. Now the obsolete file is unlocked and can be deleted, but lock the journal file itself
            //    so deleting the completed cleanup-only journal fails. VerifyPasskey and GetAllAccounts
            //    must STILL succeed!
            using (new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.VerifyPasskey("cleanup-key-1"));
                Assert.Equal(2, manifest.GetAllAccounts("cleanup-key-1").Length);
                Assert.False(File.Exists(lockedSourcePath));
                Assert.True(File.Exists(journalPath));
            }

            // Once journal lock is released, recovery removes the journal and sweeps the orphaned credential store.
            Manifest finalReload = Manifest.GetManifest(true);
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(orphanedCredPath));
            Assert.Equal(2, finalReload.GetAllAccounts("cleanup-key-1").Length);
        }

        [Fact]
        public void SaveWithResult_RejectedRebaseWithReadLockedJournalQuarantinesAndNeverDeletesInjectedFileOnRestart()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000030000UL;
            ulong secondId = 76561198000030001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string quarantinePath = Path.Combine(maDirectory, ".asda-storage-transaction.quarantined");
            string lockedSourceFilename = "quarantine-locked-obsolete.maFile";
            string lockedSourcePath = Path.Combine(maDirectory, lockedSourceFilename);
            File.WriteAllText(lockedSourcePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            string injectedFilename = "injected-must-survive-locked-rebase.maFile";
            string injectedPath = Path.Combine(maDirectory, injectedFilename);
            File.WriteAllText(injectedPath, "{\"untracked\":\"must-survive-even-when-journal-read-locked\"}");

            FileStream journalReadLock = null;
            string manifestBeforeRejectedSave = null;
            using (new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);
                Assert.True(File.Exists(journalPath));
                manifestBeforeRejectedSave = File.ReadAllText(Path.Combine(maDirectory, "manifest.json"));

                // Inject an unvalidated filename into the journal AND hold a read-only handle on the
                // journal so scrubbing fails inside RebaseRetainedStorageTransaction.
                Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = () =>
                {
                    JObject injected = JObject.Parse(File.ReadAllText(journalPath));
                    ((JArray)injected["retained_obsolete_filenames"]).Add(injectedFilename);
                    File.WriteAllText(journalPath, injected.ToString(Formatting.None));
                    journalReadLock = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                };
                try
                {
                    manifest.MinimizeToTray = !manifest.MinimizeToTray;
                    StorageResult result = manifest.SaveWithResult();
                    Assert.False(result.Succeeded);
                    Assert.True(File.Exists(quarantinePath));
                    // Pre-write rebase rejects the save BEFORE mutating manifest.json on disk.
                    Assert.Equal(manifestBeforeRejectedSave, File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
                }
                finally
                {
                    Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = null;
                    journalReadLock?.Dispose();
                    journalReadLock = null;
                }
            }

            // Once all handles close, restart MUST fail closed on the quarantine marker and MUST NOT
            // delete the rejected injected file (or even consume the tampered journal).
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(injectedPath));
            Assert.Equal("{\"untracked\":\"must-survive-even-when-journal-read-locked\"}", File.ReadAllText(injectedPath));

            // Even if the journal file itself is removed while the quarantine marker remains, direct
            // CommitStorageTransaction callers (such as MoveEntryWithResult) must still fail closed.
            // Calling CreateExclusiveMarkerFileIfMissing again on the existing marker must also be a no-op (ERROR_FILE_EXISTS = 80).
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(maDirectory);
            StoragePathGuard.CreateExclusiveMarkerFileIfMissing(quarantinePath, canonicalDir, "duplicate-marker-noop");
            File.Delete(journalPath);
            StorageResult reorderWhileQuarantined = manifest.MoveEntryWithResult(1, 0);
            Assert.False(reorderWhileQuarantined.Succeeded);
            Assert.Equal(manifestBeforeRejectedSave, File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
        }

        [Fact]
        public void StartupRecovery_RestoresSettingsBackupMatchingPreviousManifestHashAfterPreWriteRebaseCrash()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000033000UL;
            ulong secondId = 76561198000033001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string settingsBackupPath = Path.Combine(maDirectory, ".manifest.settings.bak");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string obsoleteFilename = "settings-crash-retained-obsolete.maFile";
            string obsoletePath = Path.Combine(maDirectory, obsoleteFilename);
            File.WriteAllText(obsoletePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);
                Assert.True(File.Exists(journalPath));

                // Simulate SaveWithResult completing pre-write rebase (PreviousManifestHash = old manifest,
                // BackupFilename = null) and crashing mid-ReplaceFileW after manifest.json was moved to
                // .manifest.settings.bak and before the replacement temp file was renamed into place.
                string oldManifestContents = File.ReadAllText(manifestPath);
                JObject rebasedJournal = new JObject
                {
                    ["journal_version"] = 2,
                    ["manifest_hash"] = ComputeContentHash(oldManifestContents + "-new-settings"),
                    ["previous_manifest_hash"] = ComputeContentHash(oldManifestContents),
                    ["created_filenames"] = new JArray(),
                    ["obsolete_filenames"] = new JArray(),
                    ["retained_obsolete_filenames"] = new JArray(obsoleteFilename),
                    ["backup_filename"] = null
                };
                File.WriteAllText(journalPath, rebasedJournal.ToString(Formatting.None));
                File.Move(manifestPath, settingsBackupPath);

                // First recovery attempt while .manifest.settings.bak is read-locked: manifest.json
                // is restored, deleting .manifest.settings.bak fails, so recovery fails closed without
                // dropping the journal.
                using (new FileStream(settingsBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                    Assert.True(File.Exists(manifestPath));
                    Assert.True(File.Exists(settingsBackupPath));
                    Assert.True(File.Exists(journalPath));
                }

                // Next recovery attempt after .manifest.settings.bak unlocks sees manifest.json already
                // present, deletes .manifest.settings.bak before compacting/clearing the journal, and
                // preserves the locked obsolete entry.
                Manifest recoveredWhileLocked = Manifest.GetManifest(true);
                Assert.Equal(2, recoveredWhileLocked.Entries.Count);
                Assert.True(File.Exists(manifestPath));
                Assert.False(File.Exists(settingsBackupPath));
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(journalPath));
            }

            // Once unlocked, next startup finishes deleting the retained obsolete file and removes the journal.
            Manifest finalReload = Manifest.GetManifest(true);
            Assert.Equal(2, finalReload.Entries.Count);
            Assert.False(File.Exists(settingsBackupPath));
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void SaveWithResult_RejectedRebaseWithFailedQuarantineMarkerStillQuarantinesInProcess()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000034000UL;
            ulong secondId = 76561198000034001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string quarantinePath = Path.Combine(maDirectory, ".asda-storage-transaction.quarantined");
            string lockedSourceFilename = "inproc-quarantine-obsolete.maFile";
            string lockedSourcePath = Path.Combine(maDirectory, lockedSourceFilename);
            File.WriteAllText(lockedSourcePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);

            string injectedFilename = "injected-must-survive-inproc-quarantine.maFile";
            string injectedPath = Path.Combine(maDirectory, injectedFilename);
            File.WriteAllText(injectedPath, "{\"untracked\":\"must-survive-in-process\"}");

            FileStream journalReadLock = null;
            using (new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);
                Assert.True(File.Exists(journalPath));

                // Inject an unvalidated entry, hold a read lock on the journal so scrubbing fails,
                // AND fail CREATE_NEW on the quarantine marker file.
                Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = () =>
                {
                    JObject injected = JObject.Parse(File.ReadAllText(journalPath));
                    ((JArray)injected["retained_obsolete_filenames"]).Add(injectedFilename);
                    File.WriteAllText(journalPath, injected.ToString(Formatting.None));
                    journalReadLock = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                };
                StoragePathGuard.OnBeforeCreateQuarantineMarkerForTest = _ =>
                    throw new IOException("Simulated disk full while creating quarantine marker.");
                try
                {
                    manifest.MinimizeToTray = !manifest.MinimizeToTray;
                    StorageResult result = manifest.SaveWithResult();
                    Assert.False(result.Succeeded);
                    Assert.False(File.Exists(quarantinePath));
                }
                finally
                {
                    Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = null;
                    StoragePathGuard.OnBeforeCreateQuarantineMarkerForTest = null;
                    journalReadLock?.Dispose();
                    journalReadLock = null;
                }
            }

            // Even after all locks release and without an on-disk marker, later recovery and writes
            // in the same process must STILL fail closed and never delete the injected file.
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.False(manifest.MoveEntryWithResult(1, 0).Succeeded);
            Assert.True(File.Exists(injectedPath));
            Assert.Equal("{\"untracked\":\"must-survive-in-process\"}", File.ReadAllText(injectedPath));
        }

        [Fact]
        public void StartupAndEncryptionSetup_SweepOrphanedRestoreAndStageTempFilesFromHardCrash()
        {
            // 0. First-save hard crash before any manifest.json or *.maFile exists: startup must sweep
            //    the orphaned plaintext .mafile-stage.<guid>.tmp file instead of returning early.
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            Directory.CreateDirectory(maDirectory);
            string firstSaveStageTmp = Path.Combine(maDirectory, ".mafile-stage.00000000000000000000000000000001.tmp");
            File.WriteAllText(firstSaveStageTmp, "{\"plaintext\":\"first-save-hard-crash-stage\"}");

            Manifest freshAfterFirstCrash = Manifest.GetManifest(true);
            Assert.Empty(freshAfterFirstCrash.Entries);
            Assert.False(File.Exists(firstSaveStageTmp));

            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000031000UL;
            Assert.True(manifest.SaveAccount(CreateAccount(accountId), false).Succeeded);

            string orphanedRestoreTmp = Path.Combine(maDirectory, ".mafile-restore.11111111111111111111111111111111.tmp");
            string orphanedStageTmp = Path.Combine(maDirectory, ".mafile-stage.22222222222222222222222222222222.tmp");
            string unrelatedUserTmp = Path.Combine(maDirectory, "user-note.tmp");
            File.WriteAllText(orphanedRestoreTmp, "{\"plaintext\":\"orphaned-restore-copy\"}");
            File.WriteAllText(orphanedStageTmp, "{\"plaintext\":\"orphaned-stage-copy\"}");
            File.WriteAllText(unrelatedUserTmp, "keep-user-file");

            // 1. If a hard-crash restore temp copy is locked while enabling encryption, ObsoleteCleanupPending surfaces.
            using (new FileStream(orphanedRestoreTmp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult encryptResult = manifest.ChangeEncryptionKey(null, "sweep-tmp-key");
                Assert.True(encryptResult.Succeeded);
                Assert.True(encryptResult.ObsoleteCleanupPending);
                Assert.True(File.Exists(orphanedRestoreTmp));
                Assert.False(File.Exists(orphanedStageTmp));
                Assert.True(File.Exists(unrelatedUserTmp));
            }

            // 2. Once unlocked, startup sweeps the remaining orphaned restore temp file while leaving unrelated files alone.
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.False(File.Exists(orphanedRestoreTmp));
            Assert.True(File.Exists(unrelatedUserTmp));
            Assert.Single(reloaded.GetAllAccounts("sweep-tmp-key"));
        }

        [Fact]
        public void CleanupOnlyJournal_PartialMultiFileCleanupWithReadLockedJournalStillAllowsRepeatedVerifyPasskey()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000032000UL;
            ulong secondId = 76561198000032001UL;
            ulong thirdId = 76561198000032002UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), true, "multi-cleanup-key").Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string obsoleteOnePath = Path.Combine(maDirectory, "multi-obsolete-1.maFile");
            string obsoleteTwoPath = Path.Combine(maDirectory, "multi-obsolete-2.maFile");
            File.WriteAllText(obsoleteOnePath, JsonConvert.SerializeObject(CreateAccount(secondId)));
            File.WriteAllText(obsoleteTwoPath, JsonConvert.SerializeObject(CreateAccount(thirdId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Equal(2, candidates.Count);

            // Lock BOTH obsolete files during import, then transition the journal into cleanup-only
            // retained state (while both stay locked) so VerifyPasskey hits the cleanup-only fast path.
            using (new FileStream(obsoleteTwoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (new FileStream(obsoleteOnePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    StorageResult importResult = manifest.ImportUnmanagedMaFiles(candidates, "multi-cleanup-key");
                    Assert.True(importResult.Succeeded);
                    Assert.True(importResult.ObsoleteCleanupPending);
                    Assert.True(File.Exists(journalPath));

                    // First verify while journal is still pre-compaction and read-locked.
                    using (new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Assert.True(manifest.VerifyPasskey("multi-cleanup-key"));
                    }

                    // Transition journal to retained-cleanup-only state with both obsolete files listed.
                    Manifest transitioned = Manifest.GetManifest(true);
                    Assert.Equal(3, transitioned.Entries.Count);
                    Assert.True(File.Exists(journalPath));
                }

                // Now obsoleteOne is unlocked (will be deleted), obsoleteTwo stays locked, AND the
                // cleanup-only journal itself is read-locked so compacting the shortened list fails.
                // Repeated VerifyPasskey and GetAllAccounts calls must STILL succeed!
                using (new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.True(manifest.VerifyPasskey("multi-cleanup-key"));
                    Assert.False(File.Exists(obsoleteOnePath));
                    Assert.True(File.Exists(obsoleteTwoPath));
                    Assert.True(manifest.VerifyPasskey("multi-cleanup-key"));
                    Assert.Equal(3, manifest.GetAllAccounts("multi-cleanup-key").Length);

                    // Even if manifest.json settings change (causing a hash mismatch against the
                    // read-locked cleanup-only journal), VerifyPasskey succeeds on the FIRST optimistic
                    // attempt without bumping storageMutationEpoch or spinning retries.
                    JObject updatedManifest = JObject.Parse(File.ReadAllText(manifestPath));
                    updatedManifest["minimize_to_tray"] = !(updatedManifest["minimize_to_tray"]?.Value<bool>() ?? true);
                    File.WriteAllText(manifestPath, updatedManifest.ToString(Formatting.None));
                    int optimisticAttempts = 0;
                    manifest.OnBeforeVerifyPasskeyProofPublishForTest = () => optimisticAttempts++;
                    try
                    {
                        Assert.True(manifest.VerifyPasskey("multi-cleanup-key"));
                        Assert.Equal(1, optimisticAttempts);
                    }
                    finally
                    {
                        manifest.OnBeforeVerifyPasskeyProofPublishForTest = null;
                    }
                    Assert.Equal(3, manifest.GetAllAccounts("multi-cleanup-key").Length);
                }
            }

            // Once both locks release, startup finishes deleting obsoleteTwo and removes the journal.
            Manifest finalReload = Manifest.GetManifest(true);
            Assert.False(File.Exists(obsoleteTwoPath));
            Assert.False(File.Exists(journalPath));
            Assert.Equal(3, finalReload.GetAllAccounts("multi-cleanup-key").Length);
        }

        public void Dispose()
        {
            Manifest.BeforeDeleteFileForTesting = null;
            Manifest.OnAfterReplaceFilesBeforeManifestCommitForTest = null;
            Manifest.OnBeforeRebaseRetainedStorageTransactionForTest = null;
            Manifest.ClearInProcessQuarantineForTest();
            StoragePathGuard.OnBeforeFlushReplacementForTest = null;
            StoragePathGuard.OnBeforePublishRestoredMissingTargetForTest = null;
            StoragePathGuard.OnBeforeCreateQuarantineMarkerForTest = null;
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }

        private static SteamGuardAccount CreateAccount(ulong steamId)
        {
            return new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:canonical-save-test",
                Session = new SessionData { SteamID = steamId, AccessToken = "test-access-token" },
                AccountName = "canonical-save-test",
                FullyEnrolled = true
            };
        }

        private static string ComputeContentHash(string contents)
        {
            using (SHA256 sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(contents)));
        }

        private static void CreateHardLink(string linkPath, string existingFilePath)
        {
            RunMklink($"/H \"{linkPath}\" \"{existingFilePath}\"");
        }

        private static void CreateDirectoryJunction(string junctionPath, string targetPath)
        {
            RunMklink($"/J \"{junctionPath}\" \"{targetPath}\"");
        }

        private static void RunMklink(string args)
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink " + args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                Assert.NotNull(process);
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"mklink {args} failed ({process.ExitCode}): {stdout} {stderr}");
            }
        }
    }
}
