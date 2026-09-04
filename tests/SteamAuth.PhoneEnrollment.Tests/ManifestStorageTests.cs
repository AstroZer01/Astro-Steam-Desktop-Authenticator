using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [CollectionDefinition("Manifest storage", DisableParallelization = true)]
    public sealed class ManifestStorageCollection
    {
    }

    [Collection("Manifest storage")]
    public sealed class ManifestStorageTests : IDisposable
    {
        private const string TestBackupFilename = ".manifest.00000000000000000000000000000000.bak";
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-storage-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public ManifestStorageTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void SaveAccount_CommitsANewAccountThatCanBeReloaded()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();

            StorageResult result = manifest.SaveAccount(account, false);

            Assert.True(result.Succeeded);
            Manifest reloaded = Manifest.GetManifest(true);
            SteamGuardAccount reloadedAccount = Assert.Single(reloaded.GetAllAccounts());
            Assert.Equal(account.Session.SteamID, reloadedAccount.Session.SteamID);
        }

        [Fact]
        public void SaveAccount_LeavesOnlyTheCanonicalFilename()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);

            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(76561198000000000UL), manifest.Entries.Single().Filename);
            Assert.True(File.Exists(Path.Combine(maDirectory, manifest.Entries.Single().Filename)));
            Assert.Single(Directory.EnumerateFiles(maDirectory, "*.maFile"));
        }

        [Fact]
        public void NormalizeAccountFilenames_MigratesExistingGuidReference()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(76561198000000000UL);
            string guidFilename = "76561198000000000.e170cb3692b3434c930def2e89e981ae.maFile";
            File.Move(Path.Combine(maDirectory, canonicalFilename), Path.Combine(maDirectory, guidFilename));
            JObject manifestJson = JObject.Parse(File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
            manifestJson["entries"][0]["filename"] = guidFilename;
            File.WriteAllText(Path.Combine(maDirectory, "manifest.json"), manifestJson.ToString(Formatting.None));

            Manifest reloaded = Manifest.GetManifest(true);

            Assert.Equal(canonicalFilename, Assert.Single(reloaded.Entries).Filename);
            Assert.True(File.Exists(Path.Combine(maDirectory, canonicalFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, guidFilename)));
        }

        [Fact]
        public void NormalizeAccountFilenames_DoesNotOverwriteCanonicalCollision()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(76561198000000000UL);
            string guidFilename = "76561198000000000.collision.maFile";
            string originalContents = File.ReadAllText(Path.Combine(maDirectory, canonicalFilename));
            File.Move(Path.Combine(maDirectory, canonicalFilename), Path.Combine(maDirectory, guidFilename));
            File.WriteAllText(Path.Combine(maDirectory, canonicalFilename), JsonConvert.SerializeObject(CreateAccount(76561198000000001UL)));
            JObject manifestJson = JObject.Parse(File.ReadAllText(Path.Combine(maDirectory, "manifest.json")));
            manifestJson["entries"][0]["filename"] = guidFilename;
            File.WriteAllText(Path.Combine(maDirectory, "manifest.json"), manifestJson.ToString(Formatting.None));

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.True(reloaded.NormalizeAccountFilenames().Succeeded);

            Assert.Equal(guidFilename, Assert.Single(reloaded.Entries).Filename);
            Assert.Equal(76561198000000001UL, JsonConvert.DeserializeObject<SteamGuardAccount>(File.ReadAllText(Path.Combine(maDirectory, canonicalFilename))).Session.SteamID);
            Assert.Equal(originalContents, File.ReadAllText(Path.Combine(maDirectory, guidFilename)));
        }

        [Fact]
        public void StartupImport_AcceptsExpiredSessionAndDeletesSourceAfterCommit()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "new-account.maFile";
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(CreateAccount()));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);
            Assert.True(manifest.ImportUnmanagedMaFiles(candidates).Succeeded);

            Assert.False(File.Exists(Path.Combine(maDirectory, sourceFilename)));
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(76561198000000000UL), Assert.Single(manifest.Entries).Filename);
            Assert.True(manifest.IsSessionMarkedForRenewal(76561198000000000UL));
        }

        [Fact]
        public void StartupImport_BulkValidationFailureLeavesEverySourceUntouched()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string firstFilename = "first.maFile";
            string secondFilename = "second.maFile";
            File.WriteAllText(Path.Combine(maDirectory, firstFilename), JsonConvert.SerializeObject(CreateAccount(76561198000000000UL)));
            File.WriteAllText(Path.Combine(maDirectory, secondFilename), JsonConvert.SerializeObject(CreateAccount(76561198000000001UL)));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Equal(2, candidates.Count);
            File.WriteAllText(Path.Combine(maDirectory, secondFilename), JsonConvert.SerializeObject(CreateAccount(76561198000000002UL)));

            StorageResult result = manifest.ImportUnmanagedMaFiles(candidates);

            Assert.False(result.Succeeded);
            Assert.Empty(manifest.Entries);
            Assert.True(File.Exists(Path.Combine(maDirectory, firstFilename)));
            Assert.True(File.Exists(Path.Combine(maDirectory, secondFilename)));
        }

        [Fact]
        public void StartupImport_IgnoresMissingSessionAndEncryptedCandidates()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            File.WriteAllText(Path.Combine(maDirectory, "missing-session.maFile"), JsonConvert.SerializeObject(new SteamGuardAccount { AccountName = "manual-only" }));
            string salt = FileEncryptor.GetRandomSalt();
            string iv = FileEncryptor.GetInitializationVector();
            File.WriteAllText(Path.Combine(maDirectory, "encrypted.maFile"), FileEncryptor.EncryptData("secret", salt, iv, JsonConvert.SerializeObject(CreateAccount())));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
        }

        [Theory]
        [InlineData("shared")]
        [InlineData("identity")]
        [InlineData("device")]
        public void StartupImport_IgnoresAccountsWithIncompleteAuthenticatorData(string invalidField)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "incomplete-authenticator.maFile";
            SteamGuardAccount account = CreateAccount();
            if (invalidField == "shared")
                account.SharedSecret = "not-base64";
            else if (invalidField == "identity")
                account.IdentitySecret = null;
            else
                account.DeviceID = null;
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(account));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
        }

        [Fact]
        public void StartupImport_RevalidatesAuthenticatorDataBeforeCommit()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "changed-authenticator.maFile";
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(CreateAccount()));

            Manifest.UnmanagedMaFileCandidate candidate = Assert.Single(manifest.FindUnmanagedMaFiles());
            SteamGuardAccount changedAccount = CreateAccount();
            changedAccount.IdentitySecret = "not-base64";
            string changedContents = JsonConvert.SerializeObject(changedAccount);
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), changedContents);
            candidate.ContentHash = ComputeContentHash(changedContents);

            StorageResult result = manifest.ImportUnmanagedMaFiles(new[] { candidate });

            Assert.False(result.Succeeded);
            Assert.Empty(manifest.Entries);
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
        }

        [Fact]
        public void SessionRenewalState_PersistsAndCanBeCleared()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);

            Assert.True(manifest.SetSessionNeedsRenewal(76561198000000000UL, false).Succeeded);
            Assert.True(manifest.SetSessionNeedsRenewal(76561198000000000UL, true).Succeeded);
            Assert.True(Manifest.GetManifest(true).IsSessionMarkedForRenewal(76561198000000000UL));
            Assert.True(manifest.SetSessionNeedsRenewal(76561198000000000UL, false).Succeeded);
            Assert.False(Manifest.GetManifest(true).IsSessionMarkedForRenewal(76561198000000000UL));
        }

        [Fact]
        public void ChangeEncryptionKey_StagesEveryAccountAndPreservesReloadability()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            StorageResult result = manifest.ChangeEncryptionKey(null, "test-passkey");

            Assert.True(result.Succeeded);
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.True(reloaded.Encrypted);
            SteamGuardAccount reloadedAccount = Assert.Single(reloaded.GetAllAccounts("test-passkey"));
            Assert.Equal(account.Session.SteamID, reloadedAccount.Session.SteamID);
            Assert.Equal(Manifest.GetCanonicalMaFileFilename(account.Session.SteamID), Assert.Single(reloaded.Entries).Filename);
        }

        [Fact]
        public void GetAllAccounts_RejectsManifestPathTraversal()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.Entries.Add(new Manifest.ManifestEntry
            {
                Filename = "..\\outside.maFile",
                SteamID = 76561198000000000UL
            });

            Assert.Throws<InvalidDataException>(() => manifest.GetAllAccounts());
        }

        [Fact]
        public void GetAllAccounts_IgnoresAccountWhoseIdentityDoesNotMatchManifest()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string accountFile = Path.Combine(dataDirectory, "maFiles", manifest.Entries.Single().Filename);
            SteamGuardAccount mismatchedAccount = CreateAccount(76561198000000001UL);
            File.WriteAllText(accountFile, JsonConvert.SerializeObject(mismatchedAccount));

            Assert.Empty(manifest.GetAllAccounts());
        }

        [Fact]
        public void SaveAccount_ReturnsIoFailureWithoutChangingTheInMemoryManifest()
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, "maFiles"), "not a directory");
            Manifest manifest = Manifest.GenerateNewManifest(false);

            StorageResult result = manifest.SaveAccount(CreateAccount(), false);

            Assert.False(result.Succeeded);
            Assert.Equal(StorageFailureKind.Io, result.FailureKind);
            Assert.Empty(manifest.Entries);
        }

        [Fact]
        public void SaveSettingsWithResult_PersistsProxySettingsWithoutChangingAccounts()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);

            StorageResult result = manifest.SaveSettingsWithResult(staged =>
            {
                staged.ProxyEnabled = true;
                staged.ProxyScheme = "https";
                staged.ProxyHost = "proxy.example";
                staged.ProxyPort = 3128;
                staged.ProxyUsername = "proxy-user";
                staged.ProxyPassword = "plain-secret";
            });

            Assert.True(result.Succeeded);
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.True(reloaded.ProxyEnabled);
            Assert.Equal("https", reloaded.ProxyScheme);
            Assert.Equal("proxy.example", reloaded.ProxyHost);
            Assert.Equal(3128, reloaded.ProxyPort);
            Assert.Equal("proxy-user", reloaded.ProxyUsername);
            Assert.Equal("plain-secret", reloaded.ProxyPassword);
            Assert.Single(reloaded.Entries);
        }

        [Fact]
        public void SaveSettingsWithResult_RollsBackWhenTheStagedUpdateFails()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.ProxyHost = "original.example";
            Assert.True(manifest.Save());

            StorageResult result = manifest.SaveSettingsWithResult(staged =>
            {
                staged.ProxyHost = "replacement.example";
                throw new InvalidOperationException("simulated validation failure");
            });

            Assert.False(result.Succeeded);
            Assert.Equal("original.example", manifest.ProxyHost);
            Assert.Equal("original.example", Manifest.GetManifest(true).ProxyHost);
        }

        [Fact]
        public void RemoveAccount_RemovesALocalOnlyAccountByItsUniqueAccountName()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            SteamGuardAccount localOnlyAccount = Assert.Single(manifest.GetAllAccounts());
            localOnlyAccount.Session = null;

            Assert.True(manifest.RemoveAccount(localOnlyAccount, (string)null));

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Empty(reloaded.Entries);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dataDirectory, "maFiles"), "*.maFile"));
        }

        [Fact]
        public async Task ConcurrentAccountSaves_PreserveBothAccounts()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);

            StorageResult[] results = await Task.WhenAll(
                Task.Run(() => manifest.SaveAccount(CreateAccount(76561198000000000UL), false)),
                Task.Run(() => manifest.SaveAccount(CreateAccount(76561198000000001UL), false)));

            Assert.All(results, result => Assert.True(result.Succeeded));
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(2, reloaded.GetAllAccounts().Length);
        }

        [Fact]
        public void StartupRecovery_CompletesAnInterruptedCommittedTransaction()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            JObject replacement = JObject.Parse(File.ReadAllText(manifestPath));
            string oldFilename = replacement["entries"][0]["filename"].Value<string>();
            string newFilename = "recovered.maFile";
            File.Copy(Path.Combine(maDirectory, oldFilename), Path.Combine(maDirectory, newFilename));
            replacement["entries"][0]["filename"] = newFilename;
            string replacementContents = replacement.ToString(Formatting.None);
            File.WriteAllText(manifestPath, replacementContents);
            WriteJournal(maDirectory, replacementContents, newFilename, oldFilename);

            Manifest reloaded = Manifest.GetManifest(true);

            Assert.Single(reloaded.GetAllAccounts());
            Assert.True(File.Exists(Path.Combine(maDirectory, newFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, oldFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_RollsBackAnInterruptedUncommittedTransaction()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string oldManifestContents = File.ReadAllText(manifestPath);
            JObject replacement = JObject.Parse(oldManifestContents);
            string oldFilename = replacement["entries"][0]["filename"].Value<string>();
            string newFilename = "uncommitted.maFile";
            File.Copy(Path.Combine(maDirectory, oldFilename), Path.Combine(maDirectory, newFilename));
            replacement["entries"][0]["filename"] = newFilename;
            string replacementContents = replacement.ToString(Formatting.None);
            WriteJournal(maDirectory, replacementContents, newFilename, oldFilename);

            Manifest reloaded = Manifest.GetManifest(true);

            Assert.Single(reloaded.GetAllAccounts());
            Assert.True(File.Exists(Path.Combine(maDirectory, oldFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, newFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_FailsClosedWhenAnUncommittedFileCannotBeRemoved()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            JObject replacement = JObject.Parse(File.ReadAllText(manifestPath));
            string oldFilename = replacement["entries"][0]["filename"].Value<string>();
            string newFilename = "locked-uncommitted.maFile";
            File.Copy(Path.Combine(maDirectory, oldFilename), Path.Combine(maDirectory, newFilename));
            replacement["entries"][0]["filename"] = newFilename;
            string replacementContents = replacement.ToString(Formatting.None);
            WriteJournal(maDirectory, replacementContents, newFilename, oldFilename);

            using (new FileStream(Path.Combine(maDirectory, newFilename), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(Path.Combine(maDirectory, newFilename)));
                Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            }
        }

        [Fact]
        public void SaveWithResult_CompletesAPendingCommittedTransactionBeforeSaving()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string oldFilename = manifest.Entries[0].Filename;
            string newFilename = "pending-settings-save.maFile";
            File.Copy(Path.Combine(maDirectory, oldFilename), Path.Combine(maDirectory, newFilename));
            manifest.Entries[0].Filename = newFilename;
            string committedContents = JsonConvert.SerializeObject(manifest);
            File.WriteAllText(manifestPath, committedContents);
            WriteJournal(maDirectory, committedContents, newFilename, oldFilename);

            Assert.True(manifest.SaveWithResult().Succeeded);

            Assert.True(File.Exists(Path.Combine(maDirectory, newFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, oldFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            Assert.Single(Manifest.GetManifest(true).GetAllAccounts());
        }

        [Fact]
        public void StartupRecovery_CompletesAnInterruptedEncryptionChange()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            JObject replacement = JObject.Parse(File.ReadAllText(manifestPath));
            string oldFilename = replacement["entries"][0]["filename"].Value<string>();
            string newFilename = "encrypted-recovery.maFile";
            string salt = FileEncryptor.GetRandomSalt();
            string iv = FileEncryptor.GetInitializationVector();
            string plaintext = File.ReadAllText(Path.Combine(maDirectory, oldFilename));
            File.WriteAllText(Path.Combine(maDirectory, newFilename), FileEncryptor.EncryptData("test-passkey", salt, iv, plaintext));
            replacement["encrypted"] = true;
            replacement["entries"][0]["filename"] = newFilename;
            replacement["entries"][0]["encryption_salt"] = salt;
            replacement["entries"][0]["encryption_iv"] = iv;
            string replacementContents = replacement.ToString(Formatting.None);
            File.WriteAllText(manifestPath, replacementContents);
            WriteJournal(maDirectory, replacementContents, newFilename, oldFilename);

            Manifest reloaded = Manifest.GetManifest(true);

            Assert.True(reloaded.Encrypted);
            Assert.Single(reloaded.GetAllAccounts("test-passkey"));
            Assert.True(File.Exists(Path.Combine(maDirectory, newFilename)));
            Assert.False(File.Exists(Path.Combine(maDirectory, oldFilename)));
        }

        [Fact]
        public void StartupRecovery_StopsManifestLoadingWhenTheJournalIsInvalid()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            File.WriteAllText(Path.Combine(maDirectory, ".asda-storage-transaction.json"), "not valid JSON");

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_KeepsTheJournalWhenItsBackupCannotBeRemoved()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string backupPath = Path.Combine(maDirectory, TestBackupFilename);
            File.WriteAllText(backupPath, "backup");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), manifest.Entries[0].Filename, "obsolete.maFile");

            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
            }
        }

        [Fact]
        public void StartupRecovery_DoesNotRollBackLiveFilesAfterACleanupFailureAndSettingsSave()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            JObject replacement = JObject.Parse(File.ReadAllText(manifestPath));
            string oldFilename = replacement["entries"][0]["filename"].Value<string>();
            string newFilename = "retained-live-file.maFile";
            File.Copy(Path.Combine(maDirectory, oldFilename), Path.Combine(maDirectory, newFilename));
            replacement["entries"][0]["filename"] = newFilename;
            string replacementContents = replacement.ToString(Formatting.None);
            File.WriteAllText(manifestPath, replacementContents);
            WriteJournal(maDirectory, replacementContents, newFilename, oldFilename);

            using (new FileStream(Path.Combine(maDirectory, oldFilename), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Manifest reloaded = Manifest.GetManifest(true);
                JObject retainedJournal = JObject.Parse(File.ReadAllText(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
                Assert.Empty(retainedJournal["created_filenames"].Values<string>());
                Assert.True(reloaded.SaveWithResult().Succeeded);
            }

            Manifest afterSettingsSave = Manifest.GetManifest(true);
            Assert.True(File.Exists(Path.Combine(maDirectory, newFilename)));
            Assert.Equal(newFilename, Assert.Single(afterSettingsSave.Entries).Filename);
        }

        [Fact]
        public void StartupRecovery_RejectsABackupNameThatTargetsALiveAccountFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string liveFilename = manifest.Entries[0].Filename;
            WriteJournal(
                maDirectory,
                File.ReadAllText(manifestPath),
                "created.maFile",
                "obsolete.maFile",
                liveFilename);

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(Path.Combine(maDirectory, liveFilename)));
            Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_RejectsAFileListThatTargetsALiveAccountFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string liveFilename = manifest.Entries[0].Filename;
            WriteJournal(
                maDirectory,
                File.ReadAllText(manifestPath),
                "created.maFile",
                liveFilename);

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(Path.Combine(maDirectory, liveFilename)));
            Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRecovery_RejectsAnUncommittedCreatedFileThatTargetsALiveAccountFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string liveFilename = manifest.Entries[0].Filename;
            WriteJournal(
                maDirectory,
                File.ReadAllText(manifestPath) + "changed",
                liveFilename,
                "obsolete.maFile");

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(Path.Combine(maDirectory, liveFilename)));
            Assert.True(File.Exists(Path.Combine(maDirectory, ".asda-storage-transaction.json")));
        }

        [Fact]
        public void StartupRemovesAStaleSettingsBackupAfterLoadingAValidManifest()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string backupPath = Path.Combine(maDirectory, ".manifest.settings.bak");
            File.Copy(manifestPath, backupPath);

            Manifest.GetManifest(true);

            Assert.False(File.Exists(backupPath));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }

        private static SteamGuardAccount CreateAccount(ulong steamId = 76561198000000000UL)
        {
            return new SteamGuardAccount
            {
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                DeviceID = "android:storage-test",
                Session = new SessionData { SteamID = steamId, AccessToken = "test-access-token" },
                AccountName = "storage-test"
            };
        }

        private static string ComputeContentHash(string contents)
        {
            using (SHA256 sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(contents)));
        }

        private static void WriteJournal(
            string maDirectory,
            string manifestContents,
            string createdFilename,
            string obsoleteFilename,
            string backupFilename = TestBackupFilename)
        {
            string hash;
            using (SHA256 sha256 = SHA256.Create())
                hash = Convert.ToBase64String(sha256.ComputeHash(new UTF8Encoding(false).GetBytes(manifestContents)));
            JObject journal = new JObject
            {
                ["manifest_hash"] = hash,
                ["created_filenames"] = new JArray(createdFilename),
                ["obsolete_filenames"] = new JArray(obsoleteFilename),
                ["backup_filename"] = backupFilename
            };
            File.WriteAllText(Path.Combine(maDirectory, ".asda-storage-transaction.json"), journal.ToString(Formatting.None));
        }
    }
}
