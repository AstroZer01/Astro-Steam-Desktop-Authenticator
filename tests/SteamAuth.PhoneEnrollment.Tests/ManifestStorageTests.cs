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
        public void GetAllAccounts_RetainsAccountWhenRefreshTokenIsExpiredOrRevoked()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();
            account.Session.RefreshToken = "revoked-refresh-token";

            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            Manifest reloaded = Manifest.GetManifest(true);
            SteamGuardAccount reloadedAccount = Assert.Single(reloaded.GetAllAccounts());

            Assert.Equal(account.Session.SteamID, reloadedAccount.Session.SteamID);
            Assert.Equal("revoked-refresh-token", reloadedAccount.Session.RefreshToken);
            Assert.True(reloadedAccount.Session.IsRefreshTokenExpired());
        }

        [Fact]
        public void SetSessionNeedsRenewal_RetainsManifestEntryAndMaFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();

            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string maFilename = Manifest.GetCanonicalMaFileFilename(account.Session.SteamID);
            string maFilePath = Path.Combine(maDirectory, maFilename);

            StorageResult renewalResult = manifest.SetSessionNeedsRenewal(account.Session.SteamID, true);

            Assert.True(renewalResult.Succeeded);
            Assert.Contains(manifest.Entries, entry => entry.SteamID == account.Session.SteamID);
            Assert.Equal(maFilename, Assert.Single(manifest.Entries, entry => entry.SteamID == account.Session.SteamID).Filename);
            Assert.True(File.Exists(maFilePath));

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.True(reloaded.IsSessionMarkedForRenewal(account.Session.SteamID));
            Assert.Equal(account.Session.SteamID, Assert.Single(reloaded.GetAllAccounts()).Session.SteamID);
            Assert.True(File.Exists(maFilePath));
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
        public void SaveAccount_RejectsStaleRefreshAndDoesNotRecreateRemovedAccount()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount();
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            long refreshRevision = manifest.GetAccountStorageRevision(account.Session.SteamID);

            Assert.True(manifest.RemoveAccount(account));

            StorageResult staleResult = manifest.SaveAccount(account, false, null, refreshRevision, allowCreate: false);
            Assert.False(staleResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, staleResult.FailureKind);
            Assert.Empty(manifest.Entries);

            long currentRevision = manifest.GetAccountStorageRevision(account.Session.SteamID);
            StorageResult recreateResult = manifest.SaveAccount(account, false, null, currentRevision, allowCreate: false);
            Assert.False(recreateResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, recreateResult.FailureKind);
            Assert.Empty(manifest.Entries);
        }

        [Fact]
        public void SaveAccount_AllowsRefreshAfterUnrelatedAccountChange()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount firstAccount = CreateAccount(76561198000000000UL);
            SteamGuardAccount secondAccount = CreateAccount(76561198000000001UL);
            Assert.True(manifest.SaveAccount(firstAccount, false).Succeeded);
            Assert.True(manifest.SaveAccount(secondAccount, false).Succeeded);

            long firstAccountRevision = manifest.GetAccountStorageRevision(firstAccount.Session.SteamID);
            secondAccount.Session.AccessToken = "updated-second-access-token";
            Assert.True(manifest.SaveAccount(secondAccount, false).Succeeded);

            firstAccount.Session.AccessToken = "updated-first-access-token";
            StorageResult refreshResult = manifest.SaveAccount(
                firstAccount,
                false,
                null,
                firstAccountRevision,
                allowCreate: false);

            Assert.True(refreshResult.Succeeded);
            Assert.Equal(2, manifest.Entries.Count);
            Assert.Equal("updated-first-access-token", Assert.Single(manifest.GetAllAccounts(), account => account.Session.SteamID == firstAccount.Session.SteamID).Session.AccessToken);
            Assert.Equal("updated-second-access-token", Assert.Single(manifest.GetAllAccounts(), account => account.Session.SteamID == secondAccount.Session.SteamID).Session.AccessToken);
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
        public void StartupImport_IgnoresAccountsWithIncompleteEnrollment()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "incomplete-enrollment.maFile";
            SteamGuardAccount account = CreateAccount();
            account.FullyEnrolled = false;
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(account));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
            Assert.Empty(manifest.Entries);
        }

        [Theory]
        [InlineData("shared")]
        [InlineData("identity")]
        public void StartupImport_IgnoresAuthenticatorSecretsWithWrongLength(string invalidField)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "short-authenticator-secret.maFile";
            SteamGuardAccount account = CreateAccount();
            if (invalidField == "shared")
                account.SharedSecret = Convert.ToBase64String(new byte[1]);
            else
                account.IdentitySecret = Convert.ToBase64String(new byte[1]);
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(account));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
            Assert.Empty(manifest.Entries);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void StartupImport_IgnoresAccountsWithMissingNames(string accountName)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "missing-account-name.maFile";
            SteamGuardAccount account = CreateAccount();
            account.AccountName = accountName;
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(account));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
            Assert.Empty(manifest.Entries);
        }

        [Fact]
        public void StartupImport_IgnoresOversizedAccountNames()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.Save());
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string sourceFilename = "oversized-account-name.maFile";
            SteamGuardAccount account = CreateAccount();
            account.AccountName = new string('x', 257);
            File.WriteAllText(Path.Combine(maDirectory, sourceFilename), JsonConvert.SerializeObject(account));

            Assert.Empty(manifest.FindUnmanagedMaFiles());
            Assert.True(File.Exists(Path.Combine(maDirectory, sourceFilename)));
            Assert.Empty(manifest.Entries);
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

        [Fact]
        public void MoveEntryWithResult_ReordersAndPersistsWithoutChangingAccountRevisions()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000000000UL;
            ulong secondId = 76561198000000001UL;
            ulong thirdId = 76561198000000002UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(secondId), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(thirdId), false).Succeeded);

            long revisionBefore = manifest.StorageRevision;
            long firstAccountRevBefore = manifest.GetAccountStorageRevision(firstId);
            long secondAccountRevBefore = manifest.GetAccountStorageRevision(secondId);
            long thirdAccountRevBefore = manifest.GetAccountStorageRevision(thirdId);

            StorageResult result = manifest.MoveEntryWithResult(2, 0);

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { thirdId, firstId, secondId }, manifest.Entries.Select(e => e.SteamID).ToArray());
            Assert.Equal(revisionBefore + 1, manifest.StorageRevision);
            Assert.Equal(firstAccountRevBefore, manifest.GetAccountStorageRevision(firstId));
            Assert.Equal(secondAccountRevBefore, manifest.GetAccountStorageRevision(secondId));
            Assert.Equal(thirdAccountRevBefore, manifest.GetAccountStorageRevision(thirdId));

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(new[] { thirdId, firstId, secondId }, reloaded.Entries.Select(e => e.SteamID).ToArray());
        }

        [Fact]
        public void MoveEntryWithResult_ReturnsValidationFailureForInvalidBoundsAndNullEntries()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000001UL), false).Succeeded);
            long revisionBefore = manifest.StorageRevision;

            StorageResult negativeFrom = manifest.MoveEntryWithResult(-1, 0);
            StorageResult negativeTo = manifest.MoveEntryWithResult(0, -1);
            StorageResult outOfRangeFrom = manifest.MoveEntryWithResult(2, 0);
            StorageResult outOfRangeTo = manifest.MoveEntryWithResult(0, 2);

            Assert.All(new[] { negativeFrom, negativeTo, outOfRangeFrom, outOfRangeTo }, result =>
            {
                Assert.False(result.Succeeded);
                Assert.Equal(StorageFailureKind.Validation, result.FailureKind);
            });
            Assert.Equal(new[] { 76561198000000000UL, 76561198000000001UL }, manifest.Entries.Select(e => e.SteamID).ToArray());
            Assert.Equal(revisionBefore, manifest.StorageRevision);

            Manifest nullEntriesManifest = new Manifest { Entries = null };
            StorageResult nullResult = nullEntriesManifest.MoveEntryWithResult(0, 0);
            Assert.False(nullResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, nullResult.FailureKind);
        }

        [Fact]
        public void MoveEntryWithResult_NoOpWhenIndicesMatchDoesNotMutateOrWrite()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);
            long revisionBefore = manifest.StorageRevision;

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            File.WriteAllText(Path.Combine(maDirectory, ".asda-storage-transaction.json"), "invalid json");

            StorageResult result = manifest.MoveEntryWithResult(0, 0);

            Assert.True(result.Succeeded);
            Assert.Equal(revisionBefore, manifest.StorageRevision);
        }

        [Fact]
        public void MoveEntryWithResult_PreservesInMemoryOrderWhenSaveFails()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000000000UL;
            ulong secondId = 76561198000000001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(secondId), false).Succeeded);
            long revisionBefore = manifest.StorageRevision;

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(journalPath, "corrupted journal");

            StorageResult result = manifest.MoveEntryWithResult(0, 1);

            Assert.False(result.Succeeded);
            Assert.Equal(StorageFailureKind.Io, result.FailureKind);
            Assert.Equal(new[] { firstId, secondId }, manifest.Entries.Select(e => e.SteamID).ToArray());
            Assert.Equal(revisionBefore, manifest.StorageRevision);

            File.Delete(journalPath);
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(new[] { firstId, secondId }, reloaded.Entries.Select(e => e.SteamID).ToArray());
        }

        [Fact]
        public void MoveEntryWithResult_PreservesRetainedObsoleteCleanupThroughRecovery()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000000000UL;
            ulong secondId = 76561198000000001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(secondId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string obsoletePath = Path.Combine(maDirectory, "retained-obsolete.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(obsoletePath, "obsolete");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), "unused-created.maFile", "retained-obsolete.maFile", null);

            long revisionBefore = manifest.StorageRevision;
            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                StorageResult result = manifest.MoveEntryWithResult(0, 1);

                Assert.True(result.Succeeded);
                Assert.Equal(new[] { secondId, firstId }, manifest.Entries.Select(e => e.SteamID).ToArray());
                Assert.Equal(revisionBefore + 1, manifest.StorageRevision);
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(journalPath));
                Assert.Empty(Directory.EnumerateFiles(maDirectory, ".manifest.*.bak"));
            }

            // Once unlocked, normal startup recovery must delete the retained obsolete file and preserve the reordered manifest.
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(new[] { secondId, firstId }, reloaded.Entries.Select(e => e.SteamID).ToArray());
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void SaveAccount_DeletesManifestBackupBeforeObsoleteCleanupWhenRetainedObsoleteIsLocked()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount(76561198000000000UL);
            Assert.True(manifest.SaveAccount(account, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string retainedObsoletePath = Path.Combine(maDirectory, "retained-locked.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(retainedObsoletePath, "obsolete");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), "unused-created.maFile", "retained-locked.maFile", null);

            account.Session.AccessToken = "updated-access-token";
            using (new FileStream(retainedObsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.True(manifest.SaveAccount(account, false).Succeeded);
                Assert.Empty(Directory.EnumerateFiles(maDirectory, ".manifest.*.bak"));
                Assert.True(File.Exists(retainedObsoletePath));
                Assert.True(File.Exists(journalPath));
            }

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal("updated-access-token", Assert.Single(reloaded.GetAllAccounts()).Session.AccessToken);
            Assert.False(File.Exists(retainedObsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void MoveEntryWithResult_RollbackOnBlockedManifestReplacementDoesNotOrphanRetainedObsoleteFile()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong firstId = 76561198000000000UL;
            ulong secondId = 76561198000000001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(secondId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string obsoletePath = Path.Combine(maDirectory, "retained-obsolete.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(obsoletePath, "obsolete");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), "unused-created.maFile", "retained-obsolete.maFile", null);

            long revisionBefore = manifest.StorageRevision;
            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult result = manifest.MoveEntryWithResult(0, 1);

                Assert.False(result.Succeeded);
                Assert.Equal(new[] { firstId, secondId }, manifest.Entries.Select(e => e.SteamID).ToArray());
                Assert.Equal(revisionBefore, manifest.StorageRevision);
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(journalPath));
            }

            // Once unlocked, startup recovery must preserve the original order and clean up the retained obsolete file.
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Equal(new[] { firstId, secondId }, reloaded.Entries.Select(e => e.SteamID).ToArray());
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void StartupRecovery_UncommittedTransactionPreservesAndCleansRetainedObsoleteFiles()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string obsoletePath = Path.Combine(maDirectory, "retained-crash-obsolete.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(obsoletePath, "obsolete");

            JObject uncommittedJournal = new JObject
            {
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "uncommitted"),
                ["created_filenames"] = new JArray(),
                ["obsolete_filenames"] = new JArray(),
                ["retained_obsolete_filenames"] = new JArray("retained-crash-obsolete.maFile"),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Manifest whileLocked = Manifest.GetManifest(true);
                Assert.Single(whileLocked.Entries);
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(journalPath));
            }

            Manifest afterUnlock = Manifest.GetManifest(true);
            Assert.Single(afterUnlock.Entries);
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void StartupRecovery_UncommittedRetainedObsoletePreservesLockedManifestBackupUntilUnlocked()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string obsoletePath = Path.Combine(maDirectory, "retained-locked-obsolete.maFile");
            string backupPath = Path.Combine(maDirectory, TestBackupFilename);
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(obsoletePath, "obsolete");
            File.Copy(manifestPath, backupPath);

            JObject uncommittedJournal = new JObject
            {
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath) + "uncommitted"),
                ["created_filenames"] = new JArray(),
                ["obsolete_filenames"] = new JArray(),
                ["retained_obsolete_filenames"] = new JArray("retained-locked-obsolete.maFile"),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, uncommittedJournal.ToString(Formatting.None));

            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(backupPath));
                Assert.True(File.Exists(journalPath));
            }

            Manifest recovered = Manifest.GetManifest(true);
            Assert.Single(recovered.Entries);
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void StartupRecovery_CommittedBlockedBackupDoesNotClearCreatedFilenamesOrTouchObsoleteFiles()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string backupPath = Path.Combine(maDirectory, TestBackupFilename);
            string obsoletePath = Path.Combine(maDirectory, "committed-obsolete.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string createdFilename = manifest.Entries[0].Filename;

            // Pre-commit backup had zero entries; current manifest has the committed account.
            File.WriteAllText(backupPath, JsonConvert.SerializeObject(Manifest.GenerateNewManifest(false)));
            File.WriteAllText(obsoletePath, "obsolete");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), createdFilename, "committed-obsolete.maFile", TestBackupFilename);

            // Keep obsoletePath unlocked while locking only backupPath to prove recovery fails closed
            // before touching obsolete files or clearing created_filenames.
            using (new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
                JObject journalWhileLocked = JObject.Parse(File.ReadAllText(journalPath));
                Assert.Equal(new[] { createdFilename }, journalWhileLocked["created_filenames"].Values<string>().ToArray());
                Assert.True(File.Exists(obsoletePath));
                Assert.True(File.Exists(backupPath));
            }

            // Once unlocked, recovery deletes the backup before obsolete cleanup and preserves the committed account.
            Manifest recovered = Manifest.GetManifest(true);
            Assert.Single(recovered.Entries);
            Assert.Equal(createdFilename, recovered.Entries[0].Filename);
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(backupPath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void StartupRecovery_CleansRetainedObsoleteFileEvenWhenManifestChangesBeforeJournalRebase()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000000000UL), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string obsoletePath = Path.Combine(maDirectory, "retained-before-settings-crash.maFile");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(obsoletePath, "obsolete");
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), "unused-created.maFile", "retained-before-settings-crash.maFile", null);

            // Transition the journal into cleanup-only retained state while obsoletePath is locked.
            using (new FileStream(obsoletePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Manifest reloadedWhileLocked = Manifest.GetManifest(true);
                Assert.Single(reloadedWhileLocked.Entries);
                Assert.True(File.Exists(journalPath));
            }

            // Simulate SaveWithResult updating manifest.json and crashing before RebaseRetainedStorageTransaction runs.
            JObject updatedManifest = JObject.Parse(File.ReadAllText(manifestPath));
            updatedManifest["minimize_to_tray"] = !(updatedManifest["minimize_to_tray"]?.Value<bool>() ?? true);
            File.WriteAllText(manifestPath, updatedManifest.ToString(Formatting.None));

            // Next startup sees a hash mismatch against the cleanup-only journal, yet must still clean up the retained obsolete file.
            Manifest recovered = Manifest.GetManifest(true);
            Assert.Single(recovered.Entries);
            Assert.False(File.Exists(obsoletePath));
            Assert.False(File.Exists(journalPath));
        }

        [Fact]
        public void FailedImportOverlappingRetainedObsoleteFile_PreservesCleanupOnRestart()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong existingId = 76561198000000000UL;
            ulong importedId = 76561198000000001UL;
            Assert.True(manifest.SaveAccount(CreateAccount(existingId), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string overlappingFilename = "overlapping-retained.maFile";
            string overlappingPath = Path.Combine(maDirectory, overlappingFilename);
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            File.WriteAllText(overlappingPath, JsonConvert.SerializeObject(CreateAccount(importedId)));

            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Single(candidates);
            WriteJournal(maDirectory, File.ReadAllText(manifestPath), "unused-created.maFile", overlappingFilename, null);

            // Keep overlappingPath readable so import can read/validate it, while preventing deletion,
            // and hold manifest.json open so File.Replace fails and rolls back.
            using (new FileStream(overlappingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                StorageResult result = manifest.ImportUnmanagedMaFiles(candidates);
                Assert.False(result.Succeeded);
                Assert.Single(manifest.Entries);
                Assert.True(File.Exists(overlappingPath));
                Assert.True(File.Exists(journalPath));
            }

            // Once unlocked, startup recovery must still delete the retained obsolete file rather than orphaning it.
            Manifest reloaded = Manifest.GetManifest(true);
            Assert.Single(reloaded.Entries);
            Assert.False(File.Exists(overlappingPath));
            Assert.False(File.Exists(journalPath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void StartupRecovery_RejectsRetainedObsoleteFilenameThatTargetsALiveAccountFile(bool manifestCommitted)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(), false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string liveFilename = manifest.Entries[0].Filename;
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string manifestHashSource = manifestCommitted
                ? File.ReadAllText(manifestPath)
                : File.ReadAllText(manifestPath) + "uncommitted";

            JObject tamperedJournal = new JObject
            {
                ["manifest_hash"] = ComputeContentHash(manifestHashSource),
                ["created_filenames"] = new JArray(),
                ["obsolete_filenames"] = new JArray(),
                ["retained_obsolete_filenames"] = new JArray(liveFilename),
                ["backup_filename"] = TestBackupFilename
            };
            File.WriteAllText(journalPath, tamperedJournal.ToString(Formatting.None));

            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(Path.Combine(maDirectory, liveFilename)));
            Assert.True(File.Exists(journalPath));
        }

        [Fact]
        public void MoveEntryBySteamIdOffsetWithResult_MovesInConsistentDirectionWithoutReversingOnSortedList()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount accountAlpha = CreateAccount(76561198000000000UL);
            accountAlpha.AccountName = "alpha";
            SteamGuardAccount accountBravo = CreateAccount(76561198000000001UL);
            accountBravo.AccountName = "bravo";
            SteamGuardAccount accountZulu = CreateAccount(76561198000000002UL);
            accountZulu.AccountName = "zulu";

            Assert.True(manifest.SaveAccount(accountAlpha, false).Succeeded);
            Assert.True(manifest.SaveAccount(accountBravo, false).Succeeded);
            Assert.True(manifest.SaveAccount(accountZulu, false).Succeeded);

            string[] sortedDisplay = new[] { "alpha", "bravo", "zulu" };
            SteamGuardAccount[] loadedAccounts = manifest.GetAllAccounts();
            Assert.True(MainForm.TryResolveAccountForReorder(loadedAccounts, sortedDisplay, 1, out ulong bravoId));
            Assert.Equal(accountBravo.Session.SteamID, bravoId);

            // Pressing Ctrl+Up twice on Bravo moves it to index 0 and keeps it at index 0 rather than reversing back.
            Assert.True(manifest.MoveEntryBySteamIdOffsetWithResult(bravoId, -1).Succeeded);
            Assert.Equal(
                new[] { accountBravo.Session.SteamID, accountAlpha.Session.SteamID, accountZulu.Session.SteamID },
                Manifest.GetManifest(true).Entries.Select(e => e.SteamID).ToArray());

            long revisionAtTop = manifest.StorageRevision;
            Assert.True(manifest.MoveEntryBySteamIdOffsetWithResult(bravoId, -1).Succeeded);
            Assert.Equal(revisionAtTop, manifest.StorageRevision);
            Assert.Equal(
                new[] { accountBravo.Session.SteamID, accountAlpha.Session.SteamID, accountZulu.Session.SteamID },
                Manifest.GetManifest(true).Entries.Select(e => e.SteamID).ToArray());

            // Pressing Ctrl+Down moves Bravo back down one slot, and zero/bottom-boundary offsets are no-ops.
            Assert.True(manifest.MoveEntryBySteamIdOffsetWithResult(bravoId, 1).Succeeded);
            Assert.Equal(
                new[] { accountAlpha.Session.SteamID, accountBravo.Session.SteamID, accountZulu.Session.SteamID },
                Manifest.GetManifest(true).Entries.Select(e => e.SteamID).ToArray());

            long revisionBeforeNoOps = manifest.StorageRevision;
            Assert.True(manifest.MoveEntryBySteamIdOffsetWithResult(bravoId, 0).Succeeded);
            Assert.True(manifest.MoveEntryBySteamIdOffsetWithResult(accountZulu.Session.SteamID, 1).Succeeded);
            Assert.Equal(revisionBeforeNoOps, manifest.StorageRevision);

            // Ambiguous duplicate names (both session-backed or mixed with sessionless) must be rejected.
            SteamGuardAccount duplicateSessionlessAlpha = new SteamGuardAccount { AccountName = "alpha", Session = null };
            Assert.False(MainForm.TryResolveAccountForReorder(
                new[] { accountAlpha, duplicateSessionlessAlpha, accountBravo },
                sortedDisplay,
                0,
                out _));

            SteamGuardAccount duplicateSessionBackedAlpha = CreateAccount(76561198000000009UL);
            duplicateSessionBackedAlpha.AccountName = "alpha";
            Assert.False(MainForm.TryResolveAccountForReorder(
                new[] { accountAlpha, duplicateSessionBackedAlpha, accountBravo },
                sortedDisplay,
                0,
                out _));

            // Invalid zero or missing SteamIDs return a validation failure.
            StorageResult zeroIdResult = manifest.MoveEntryBySteamIdOffsetWithResult(0, -1);
            Assert.False(zeroIdResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, zeroIdResult.FailureKind);

            StorageResult missingResult = manifest.MoveEntryBySteamIdOffsetWithResult(76561198000000099UL, -1);
            Assert.False(missingResult.Succeeded);
            Assert.Equal(StorageFailureKind.Validation, missingResult.FailureKind);
        }

        [Fact]
        public async Task ConcurrentMoveEntryAndSaveAccount_PreservesReorderAndSavedAccount()
        {
            for (int round = 0; round < 5; round++)
            {
                Manifest manifest = Manifest.GenerateNewManifest(false);
                ulong firstId = 76561198000000100UL + (ulong)(round * 10);
                ulong secondId = firstId + 1;
                ulong thirdId = firstId + 2;
                Assert.True(manifest.SaveAccount(CreateAccount(firstId), false).Succeeded);
                Assert.True(manifest.SaveAccount(CreateAccount(secondId), false).Succeeded);

                using (System.Threading.Barrier startBarrier = new System.Threading.Barrier(2))
                {
                    Task<StorageResult> reorderTask = Task.Run(() =>
                    {
                        startBarrier.SignalAndWait();
                        StorageResult result = StorageResult.Success();
                        // Odd number of moves leaves [secondId, firstId] regardless of when thirdId is appended.
                        for (int i = 0; i < 5; i++)
                        {
                            result = manifest.MoveEntryBySteamIdOffsetWithResult(
                                i % 2 == 0 ? secondId : firstId,
                                -1);
                            if (!result.Succeeded)
                                return result;
                        }
                        return result;
                    });

                    Task<StorageResult> saveTask = Task.Run(() =>
                    {
                        startBarrier.SignalAndWait();
                        SteamGuardAccount third = CreateAccount(thirdId);
                        third.Session.AccessToken = "concurrent-token-" + round;
                        return manifest.SaveAccount(third, false);
                    });

                    StorageResult[] results = await Task.WhenAll(reorderTask, saveTask);
                    Assert.All(results, result => Assert.True(result.Succeeded));
                }

                Manifest reloaded = Manifest.GetManifest(true);
                ulong[] persistedOrder = reloaded.Entries.Select(e => e.SteamID).ToArray();
                Assert.Equal(new[] { secondId, firstId, thirdId }, persistedOrder);
                Assert.Equal(
                    "concurrent-token-" + round,
                    Assert.Single(reloaded.GetAllAccounts(), account => account.Session.SteamID == thirdId).Session.AccessToken);
            }
        }

        [Fact]
        public void PathValidation_ValidatesStorageDirectoryOncePerOperationRegardlessOfEntryCount()
        {
            (long GetAllDir, long GetAllCanon, long SaveDir, long MoveDir, long LoadDir, long NormalizeDir, long NormalizeCanon, long EncryptDir, long EncryptCanon, long ImportDir, long ImportCanon, long RemoveDir, long RemoveCanon) small =
                MeasureOperationValidationCounts(accountCount: 2, guidRenameCount: 1, importCount: 1);
            (long GetAllDir, long GetAllCanon, long SaveDir, long MoveDir, long LoadDir, long NormalizeDir, long NormalizeCanon, long EncryptDir, long EncryptCanon, long ImportDir, long ImportCanon, long RemoveDir, long RemoveCanon) large =
                MeasureOperationValidationCounts(accountCount: 8, guidRenameCount: 5, importCount: 3);

            // Every operation must do O(1) directory & canonical verifications independent of entry count.
            Assert.Equal(small.GetAllDir, large.GetAllDir);
            Assert.Equal(1, large.GetAllDir);
            Assert.Equal(small.GetAllCanon, large.GetAllCanon);
            Assert.Equal(1, large.GetAllCanon);

            Assert.Equal(small.SaveDir, large.SaveDir);
            Assert.Equal(1, large.SaveDir);

            Assert.Equal(small.MoveDir, large.MoveDir);
            Assert.Equal(1, large.MoveDir);

            Assert.Equal(small.LoadDir, large.LoadDir);
            Assert.True(large.LoadDir <= 2, $"Expected O(1) directory validations on GetManifest, got {large.LoadDir}");

            Assert.Equal(small.NormalizeDir, large.NormalizeDir);
            Assert.Equal(small.NormalizeCanon, large.NormalizeCanon);
            Assert.True(large.NormalizeDir <= 2, $"Expected O(1) directory validations on NormalizeAccountFilenames, got {large.NormalizeDir}");

            Assert.Equal(small.EncryptDir, large.EncryptDir);
            Assert.Equal(small.EncryptCanon, large.EncryptCanon);
            Assert.True(large.EncryptDir <= 4, $"Expected O(1) directory validations on ChangeEncryptionKey, got {large.EncryptDir}");

            Assert.Equal(small.ImportDir, large.ImportDir);
            Assert.Equal(small.ImportCanon, large.ImportCanon);
            Assert.True(large.ImportDir <= 4, $"Expected O(1) directory validations on ImportUnmanagedMaFiles, got {large.ImportDir}");

            Assert.Equal(small.RemoveDir, large.RemoveDir);
            Assert.Equal(small.RemoveCanon, large.RemoveCanon);
            Assert.True(large.RemoveDir <= 2, $"Expected O(1) directory validations on sessionless RemoveAccount, got {large.RemoveDir}");
        }

        private (long GetAllDir, long GetAllCanon, long SaveDir, long MoveDir, long LoadDir, long NormalizeDir, long NormalizeCanon, long EncryptDir, long EncryptCanon, long ImportDir, long ImportCanon, long RemoveDir, long RemoveCanon)
            MeasureOperationValidationCounts(int accountCount, int guidRenameCount, int importCount)
        {
            string runDir = Path.Combine(dataDirectory, "scale-" + accountCount + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDir);
            string previousDir = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            try
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", runDir);
                Manifest manifest = Manifest.GenerateNewManifest(false);
                for (int i = 0; i < accountCount; i++)
                {
                    Assert.True(manifest.SaveAccount(CreateAccount(76561198000001000UL + (ulong)i), false).Succeeded);
                }

                long beforeGetAllDir = StoragePathGuard.DirectoryValidationCount;
                long beforeGetAllCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                SteamGuardAccount[] loaded = manifest.GetAllAccounts();
                long getAllDir = StoragePathGuard.DirectoryValidationCount - beforeGetAllDir;
                long getAllCanon = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeGetAllCanon;
                Assert.Equal(accountCount, loaded.Length);

                long beforeSaveDir = StoragePathGuard.DirectoryValidationCount;
                Assert.True(manifest.SaveWithResult().Succeeded);
                long saveDir = StoragePathGuard.DirectoryValidationCount - beforeSaveDir;

                long beforeMoveDir = StoragePathGuard.DirectoryValidationCount;
                Assert.True(manifest.MoveEntryWithResult(0, 1).Succeeded);
                long moveDir = StoragePathGuard.DirectoryValidationCount - beforeMoveDir;

                long beforeLoadDir = StoragePathGuard.DirectoryValidationCount;
                Manifest reloaded = Manifest.GetManifest(true);
                long loadDir = StoragePathGuard.DirectoryValidationCount - beforeLoadDir;
                Assert.Equal(accountCount, reloaded.Entries.Count);

                string maDirectory = Path.Combine(runDir, "maFiles");
                for (int i = 0; i < guidRenameCount; i++)
                {
                    Manifest.ManifestEntry entry = reloaded.Entries[i];
                    string guidFilename = entry.SteamID + "." + Guid.NewGuid().ToString("N") + ".maFile";
                    File.Move(Path.Combine(maDirectory, entry.Filename), Path.Combine(maDirectory, guidFilename));
                    entry.Filename = guidFilename;
                }
                Assert.True(reloaded.Save());

                long beforeNormalizeDir = StoragePathGuard.DirectoryValidationCount;
                long beforeNormalizeCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                Assert.True(reloaded.NormalizeAccountFilenames().Succeeded);
                long normalizeDir = StoragePathGuard.DirectoryValidationCount - beforeNormalizeDir;
                long normalizeCanon = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeNormalizeCanon;

                long beforeEncryptDir = StoragePathGuard.DirectoryValidationCount;
                long beforeEncryptCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                Assert.True(reloaded.ChangeEncryptionKey(null, "test-passkey").Succeeded);
                long encryptDir = StoragePathGuard.DirectoryValidationCount - beforeEncryptDir;
                long encryptCanon = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeEncryptCanon;
                Assert.Equal(accountCount, reloaded.GetAllAccounts("test-passkey").Length);

                Assert.True(reloaded.ChangeEncryptionKey("test-passkey", null).Succeeded);
                for (int i = 0; i < importCount; i++)
                {
                    ulong importId = 76561198000002000UL + (ulong)i;
                    File.WriteAllText(
                        Path.Combine(maDirectory, $"import-{i}.maFile"),
                        JsonConvert.SerializeObject(CreateAccount(importId)));
                }
                var importCandidates = reloaded.FindUnmanagedMaFiles();
                Assert.Equal(importCount, importCandidates.Count);

                long beforeImportDir = StoragePathGuard.DirectoryValidationCount;
                long beforeImportCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                Assert.True(reloaded.ImportUnmanagedMaFiles(importCandidates).Succeeded);
                long importDir = StoragePathGuard.DirectoryValidationCount - beforeImportDir;
                long importCanon = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeImportCanon;

                SteamGuardAccount uniqueNamed = CreateAccount(76561198000003000UL);
                uniqueNamed.AccountName = "unique-removal-target";
                Assert.True(reloaded.SaveAccount(uniqueNamed, false).Succeeded);
                SteamGuardAccount sessionlessTarget = new SteamGuardAccount { AccountName = "unique-removal-target", Session = null };

                long beforeRemoveDir = StoragePathGuard.DirectoryValidationCount;
                long beforeRemoveCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                Assert.True(reloaded.RemoveAccount(sessionlessTarget, (string)null));
                long removeDir = StoragePathGuard.DirectoryValidationCount - beforeRemoveDir;
                long removeCanon = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeRemoveCanon;

                return (getAllDir, getAllCanon, saveDir, moveDir, loadDir, normalizeDir, normalizeCanon, encryptDir, encryptCanon, importDir, importCanon, removeDir, removeCanon);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDir);
                if (Directory.Exists(runDir))
                    Directory.Delete(runDir, true);
            }
        }

        [Fact]
        public void PathValidation_ColdPasskeyCheckValidatesDirectoryOnceAcrossCorruptEntries()
        {
            (long dirDelta, long canonDelta) fewCorrupt = MeasureColdPasskeySaveValidationCounts(totalAccounts: 3, corruptCount: 1);
            (long dirDelta, long canonDelta) manyCorrupt = MeasureColdPasskeySaveValidationCounts(totalAccounts: 7, corruptCount: 5);

            // Cold passkey check validates maFiles once regardless of how many corrupt entries are skipped.
            Assert.Equal(fewCorrupt.dirDelta, manyCorrupt.dirDelta);
            Assert.Equal(fewCorrupt.canonDelta, manyCorrupt.canonDelta);
            Assert.True(manyCorrupt.dirDelta <= 4, $"Expected O(1) directory validations on cold SaveAccount, got {manyCorrupt.dirDelta}");
            Assert.True(manyCorrupt.canonDelta <= 4, $"Expected O(1) canonical verifications on cold SaveAccount, got {manyCorrupt.canonDelta}");
        }

        private (long dirDelta, long canonDelta) MeasureColdPasskeySaveValidationCounts(int totalAccounts, int corruptCount)
        {
            string runDir = Path.Combine(dataDirectory, "cold-passkey-" + corruptCount + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDir);
            string previousDir = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            try
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", runDir);
                Manifest manifest = Manifest.GenerateNewManifest(false);
                for (int i = 0; i < totalAccounts; i++)
                {
                    Assert.True(manifest.SaveAccount(CreateAccount(76561198000004000UL + (ulong)i), false).Succeeded);
                }
                Assert.True(manifest.ChangeEncryptionKey(null, "cold-passkey").Succeeded);

                // Corrupt the first N account files so cold-start IsPasskeyValidForEncryptedWriteLocked
                // must skip multiple unreadable/corrupt entries before matching the last healthy account.
                string maDirectory = Path.Combine(runDir, "maFiles");
                for (int i = 0; i < corruptCount; i++)
                {
                    File.WriteAllText(Path.Combine(maDirectory, manifest.Entries[i].Filename), "corrupt-ciphertext");
                }

                // Reload fresh Manifest instance so verifiedPasskeyProof is cold (null).
                Manifest coldManifest = Manifest.GetManifest(true);
                SteamGuardAccount newAccount = CreateAccount(76561198000004999UL);

                long beforeDir = StoragePathGuard.DirectoryValidationCount;
                long beforeCanon = StoragePathGuard.CanonicalDirectoryVerificationCount;
                StorageResult saveResult = coldManifest.SaveAccount(newAccount, true, "cold-passkey");
                long dirDelta = StoragePathGuard.DirectoryValidationCount - beforeDir;
                long canonDelta = StoragePathGuard.CanonicalDirectoryVerificationCount - beforeCanon;

                Assert.True(saveResult.Succeeded);
                return (dirDelta, canonDelta);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDir);
                if (Directory.Exists(runDir))
                    Directory.Delete(runDir, true);
            }
        }

        [Fact]
        public void PathValidation_RejectsDirectoryJunctionAtStorageRootAndAncestor()
        {
            string junctionRoot = Path.Combine(Path.GetTempPath(), "asda-junction-tests", Guid.NewGuid().ToString("N"));
            string realTargetDir = Path.Combine(junctionRoot, "target");
            string linkedDataDir = Path.Combine(junctionRoot, "linked-data");
            string directJunctionDataDir = Path.Combine(junctionRoot, "direct-data");
            string directMaFilesJunction = Path.Combine(directJunctionDataDir, "maFiles");
            string danglingTargetDir = Path.Combine(junctionRoot, "dangling-target");
            string danglingJunction = Path.Combine(junctionRoot, "dangling-junction");

            try
            {
                Directory.CreateDirectory(realTargetDir);
                Directory.CreateDirectory(directJunctionDataDir);
                Directory.CreateDirectory(danglingTargetDir);
                CreateDirectoryJunction(linkedDataDir, realTargetDir);
                CreateDirectoryJunction(directMaFilesJunction, realTargetDir);
                CreateDirectoryJunction(danglingJunction, danglingTargetDir);
                Directory.Delete(danglingTargetDir, false);

                // Seed the junction target with a pending transaction journal, backup, and account file
                // to prove CommitStorageTransaction validates maDir BEFORE recovery can restore or delete anything.
                string targetBackupPath = Path.Combine(realTargetDir, TestBackupFilename);
                string targetCreatedPath = Path.Combine(realTargetDir, "uncommitted-target.maFile");
                string targetJournalPath = Path.Combine(realTargetDir, ".asda-storage-transaction.json");
                File.WriteAllText(targetBackupPath, JsonConvert.SerializeObject(Manifest.GenerateNewManifest(false)));
                File.WriteAllText(targetCreatedPath, "should-not-be-deleted");
                WriteJournal(realTargetDir, "uncommitted-manifest", "uncommitted-target.maFile", "obsolete-target.maFile", TestBackupFilename);

                // 1. Ancestor junction (ASDA_DATA_DIRECTORY itself is a junction and maFiles does not exist yet):
                //    GetManifest and saves must fail without creating maFiles inside the junction target.
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", linkedDataDir);
                Assert.Throws<ManifestStorageDirectoryException>(() => Manifest.GetManifest(true));
                Manifest ancestorManifest = Manifest.GenerateNewManifest(false);
                Assert.False(ancestorManifest.SaveWithResult().Succeeded);
                Assert.False(ancestorManifest.SaveAccount(CreateAccount(), false).Succeeded);
                Assert.Empty(ancestorManifest.Entries);
                Assert.False(Directory.Exists(Path.Combine(realTargetDir, "maFiles")));

                // 2. Direct junction at maFiles: every storage boundary must reject it without modifying target files.
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", directJunctionDataDir);
                Manifest directManifest = Manifest.GenerateNewManifest(false);
                directManifest.Entries.Add(new Manifest.ManifestEntry
                {
                    SteamID = 76561198000000000UL,
                    Filename = Manifest.GetCanonicalMaFileFilename(76561198000000000UL)
                });
                directManifest.Entries.Add(new Manifest.ManifestEntry
                {
                    SteamID = 76561198000000001UL,
                    Filename = Manifest.GetCanonicalMaFileFilename(76561198000000001UL)
                });

                Assert.Throws<ManifestStorageDirectoryException>(() => Manifest.GetManifest(true));
                Assert.Throws<InvalidDataException>(() => directManifest.GetAllAccounts());
                Assert.False(directManifest.SaveWithResult().Succeeded);
                Assert.False(directManifest.SaveAccount(CreateAccount(76561198000000002UL), false).Succeeded);
                Assert.False(directManifest.MoveEntryWithResult(0, 1).Succeeded);
                Assert.False(directManifest.NormalizeAccountFilenames().Succeeded);
                Assert.False(directManifest.ChangeEncryptionKey(null, "test-passkey").Succeeded);
                Assert.False(directManifest.RemoveAccount(CreateAccount(76561198000000000UL)));

                SteamGuardAccount sessionless = CreateAccount();
                sessionless.Session = null;
                Assert.False(directManifest.RemoveAccount(sessionless, (string)null));

                var fakeCandidate = new Manifest.UnmanagedMaFileCandidate
                {
                    FileName = "uncommitted-target.maFile",
                    SteamID = 76561198000000099UL,
                    ContentHash = ComputeContentHash("should-not-be-deleted")
                };
                Assert.False(directManifest.ImportUnmanagedMaFiles(new[] { fakeCandidate }).Succeeded);

                // Recovery must not have restored manifest.json or deleted any file inside realTargetDir.
                Assert.False(File.Exists(Path.Combine(realTargetDir, "manifest.json")));
                Assert.True(File.Exists(targetBackupPath));
                Assert.True(File.Exists(targetCreatedPath));
                Assert.True(File.Exists(targetJournalPath));

                // 3. Canonical directory acquisition & opened-file handle verification fail closed:
                //    - Direct junction (with and without trailing separator) is rejected via FILE_FLAG_OPEN_REPARSE_POINT.
                //    - Dangling junction is rejected at both ancestor walk and canonical verification.
                //    - Real subdirectory beneath an ancestor junction is rejected via canonical path comparison.
                //    - Missing directory fails closed (throws instead of returning null).
                //    - ReadTextWithLimit rejects null/empty canonical directory and mismatched opened handles.
                string nestedRealDirInTarget = Path.Combine(realTargetDir, "nested");
                Directory.CreateDirectory(nestedRealDirInTarget);
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.ValidateDirectoryAndGetRoot(directMaFilesJunction + Path.DirectorySeparatorChar));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.GetValidatedCanonicalDirectoryPath(directMaFilesJunction));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.GetValidatedCanonicalDirectoryPath(directMaFilesJunction + Path.DirectorySeparatorChar));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.CreateAndPinDirectory(Path.Combine(linkedDataDir, "nested-create", "maFiles")));
                Assert.False(Directory.Exists(Path.Combine(realTargetDir, "nested-create")));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.ValidateDirectoryAndGetRoot(danglingJunction));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.GetValidatedCanonicalDirectoryPath(danglingJunction));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.GetValidatedCanonicalDirectoryPath(Path.Combine(linkedDataDir, "nested")));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.GetValidatedCanonicalDirectoryPath(Path.Combine(junctionRoot, "does-not-exist")));

                string driveRoot = Path.GetPathRoot(dataDirectory);
                Assert.False(String.IsNullOrEmpty(driveRoot));
                Assert.False(String.IsNullOrEmpty(StoragePathGuard.GetValidatedCanonicalDirectoryPath(driveRoot)));
                Assert.False(String.IsNullOrEmpty(StoragePathGuard.GetValidatedCanonicalDirectoryPath(@"\\?\" + driveRoot)));

                string realMaFilesDir = Path.Combine(dataDirectory, "maFiles");
                Directory.CreateDirectory(realMaFilesDir);
                string realSampleFile = Path.Combine(realMaFilesDir, "sample.maFile");
                File.WriteAllText(realSampleFile, "sample-content");
                string expectedCanonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(realMaFilesDir);
                string expectedCanonicalDirWithTrailingSep = StoragePathGuard.GetValidatedCanonicalDirectoryPath(realMaFilesDir + Path.DirectorySeparatorChar);
                Assert.False(String.IsNullOrEmpty(expectedCanonicalDir));
                Assert.Equal(expectedCanonicalDir, expectedCanonicalDirWithTrailingSep, StringComparer.OrdinalIgnoreCase);
                Assert.Equal("sample-content", StoragePathGuard.ReadTextWithLimit(realSampleFile, 1024 * 1024, expectedCanonicalDir));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.ReadTextWithLimit(realSampleFile, 1024 * 1024, null));
                Assert.Throws<InvalidDataException>(() => StoragePathGuard.ReadTextWithLimit(realSampleFile, 1024 * 1024, String.Empty));
                Assert.Throws<InvalidDataException>(() =>
                    StoragePathGuard.ReadTextWithLimit(
                        Path.Combine(directMaFilesJunction, "uncommitted-target.maFile"),
                        1024 * 1024,
                        expectedCanonicalDir));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
                try
                {
                    if (Directory.Exists(directMaFilesJunction))
                        Directory.Delete(directMaFilesJunction, false);
                }
                catch { }
                try
                {
                    if (Directory.Exists(linkedDataDir))
                        Directory.Delete(linkedDataDir, false);
                }
                catch { }
                try
                {
                    if ((File.GetAttributes(danglingJunction) & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(danglingJunction, false);
                }
                catch { }
                try
                {
                    if (Directory.Exists(junctionRoot))
                        Directory.Delete(junctionRoot, true);
                }
                catch { }
            }
        }

        [Fact]
        public void PathValidation_PinnedDirectoryBlocksDirectoryRenameAndDeleteWhileHeld()
        {
            string pinRoot = Path.Combine(dataDirectory, "pin-test");
            string maFilesDir = Path.Combine(pinRoot, "maFiles");
            string renamedDir = Path.Combine(pinRoot, "maFiles-renamed");
            Directory.CreateDirectory(maFilesDir);

            using (StoragePathGuard.PinnedStorageDirectory pinned = StoragePathGuard.PinExistingDirectory(maFilesDir))
            {
                Assert.False(String.IsNullOrEmpty(pinned.CanonicalDirectoryPath));
                pinned.VerifyStillPinned();

                // While pinned without FILE_SHARE_DELETE, OS rejects renaming or deleting the storage directory.
                Assert.ThrowsAny<IOException>(() => Directory.Move(maFilesDir, renamedDir));
                Assert.ThrowsAny<IOException>(() => Directory.Delete(maFilesDir, false));

                // Child file reads/writes inside the pinned directory continue to succeed.
                string childFile = Path.Combine(maFilesDir, "inside.maFile");
                File.WriteAllText(childFile, "pinned-child-content");
                Assert.Equal("pinned-child-content", StoragePathGuard.ReadTextWithLimit(childFile, 1024, pinned.CanonicalDirectoryPath));
                File.Delete(childFile);
            }

            // Once disposed, directory can be renamed normally.
            Directory.Move(maFilesDir, renamedDir);
            Assert.True(Directory.Exists(renamedDir));
        }

        [Fact]
        public void PathValidation_HandlesLongPathsAndUncNormalization()
        {
            // Verify Win32 normalization helpers preserve UNC and drive roots while stripping non-root trailing separators.
            Assert.Equal(@"C:\", StoragePathGuard.StripNonRootTrailingSeparators(@"C:\"));
            Assert.Equal(@"\\?\C:\", StoragePathGuard.StripNonRootTrailingSeparators(@"\\?\C:\"));
            Assert.Equal(@"\\server\share\", StoragePathGuard.StripNonRootTrailingSeparators(@"\\server\share"));
            Assert.Equal(@"\\server\share\", StoragePathGuard.StripNonRootTrailingSeparators(@"\\server\share\"));
            Assert.Equal(@"\\?\UNC\server\share\", StoragePathGuard.StripNonRootTrailingSeparators(@"\\?\UNC\server\share\"));
            Assert.Equal(@"C:\dir\sub", StoragePathGuard.StripNonRootTrailingSeparators(@"C:\dir\sub\"));
            Assert.Equal(@"\\server\share\maFiles", StoragePathGuard.StripNonRootTrailingSeparators(@"\\server\share\maFiles\"));
            Assert.Equal(@"\\server\share\", StoragePathGuard.ToWin32RootOpenPath(@"\\server\share"));

            string longUnc = @"\\server\share\" + new string('a', 260);
            Assert.StartsWith(@"\\?\UNC\server\share\", StoragePathGuard.ToWin32OpenPath(longUnc), StringComparison.Ordinal);

            // Real filesystem test for an unprefixed path >= 260 chars.
            string longDir = dataDirectory;
            for (int i = 0; i < 6; i++)
            {
                longDir = Path.Combine(longDir, "segment_" + i + "_" + new string('x', 40));
            }
            Assert.True(longDir.Length >= 260, $"Expected path >= 260 chars, got {longDir.Length}");

            Directory.CreateDirectory(longDir);
            string longFile = Path.Combine(longDir, "long.maFile");
            File.WriteAllText(longFile, "long-path-content");

            string validatedRoot = StoragePathGuard.ValidateDirectoryAndGetRoot(longDir);
            Assert.False(String.IsNullOrEmpty(validatedRoot));
            string canonicalDir = StoragePathGuard.GetValidatedCanonicalDirectoryPath(longDir);
            Assert.False(String.IsNullOrEmpty(canonicalDir));
            Assert.Equal("long-path-content", StoragePathGuard.ReadTextWithLimit(longFile, 1024, canonicalDir));

            using (StoragePathGuard.PinnedStorageDirectory pinned = StoragePathGuard.PinExistingDirectory(longDir))
            {
                pinned.VerifyStillPinned();
                Assert.Equal("long-path-content", StoragePathGuard.ReadTextWithLimit(longFile, 1024, pinned.CanonicalDirectoryPath));
            }
        }

        [Fact]
        public void PathValidation_SaveWithResultDoesNotCreateDirectoryWhenValidationFails()
        {
            string freshDir = Path.Combine(dataDirectory, "no-dir-on-invalid-save");
            Directory.CreateDirectory(freshDir);
            string previousDir = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            try
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", freshDir);
                Manifest invalidManifest = Manifest.GenerateNewManifest(false);
                invalidManifest.Entries.Add(new Manifest.ManifestEntry
                {
                    SteamID = 76561198000009999UL,
                    Filename = @"..\escape.maFile"
                });

                StorageResult result = invalidManifest.SaveWithResult();
                Assert.False(result.Succeeded);
                Assert.False(Directory.Exists(Path.Combine(freshDir, "maFiles")));
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDir);
            }
        }

        [Fact]
        public void PathValidation_RejectsEscapingCredentialsStoreFilenameAcrossBoundaries()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount(76561198000008001UL);
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.True(manifest.ChangeEncryptionKey(null, "cred-passkey").Succeeded);

            string outsideFile = Path.Combine(dataDirectory, "outside-secret.txt");
            File.WriteAllText(outsideFile, "outside-data");

            // Point CredentialsStoreFilename outside maFiles and verify every boundary rejects it.
            manifest.CredentialsStoreFilename = @"..\outside-secret.txt";

            Assert.Throws<InvalidDataException>(() => manifest.GetAllAccounts("cred-passkey"));
            Assert.False(manifest.SaveWithResult().Succeeded);
            Assert.False(manifest.NormalizeAccountFilenames().Succeeded);
            Assert.False(manifest.ChangeEncryptionKey("cred-passkey", "new-cred-passkey").Succeeded);
            Assert.False(manifest.RemoveAccount(account, "cred-passkey"));
            Assert.True(File.Exists(outsideFile));
            Assert.Equal("outside-data", File.ReadAllText(outsideFile));
        }

        [Fact]
        public void PathValidation_GetAllAccountsReturnsEmptyWhenMaFilesDirectoryMissing()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.Entries.Add(new Manifest.ManifestEntry
            {
                SteamID = 76561198000007001UL,
                Filename = Manifest.GetCanonicalMaFileFilename(76561198000007001UL)
            });

            Assert.Empty(manifest.GetAllAccounts());
        }

        [Fact]
        public void PathValidation_CreateAndPinDirectoryPinsCreatedHierarchy()
        {
            string nestedTarget = Path.Combine(dataDirectory, "create-pin-parent", "child", "maFiles");
            string renamedTarget = Path.Combine(dataDirectory, "create-pin-parent", "child", "maFiles-moved");
            Assert.False(Directory.Exists(nestedTarget));

            using (StoragePathGuard.PinnedStorageDirectory pinned = StoragePathGuard.CreateAndPinDirectory(nestedTarget))
            {
                Assert.True(Directory.Exists(nestedTarget));
                pinned.VerifyStillPinned();
                Assert.ThrowsAny<IOException>(() => Directory.Move(nestedTarget, renamedTarget));
                Assert.ThrowsAny<IOException>(() => Directory.Delete(nestedTarget, false));
            }

            Directory.Move(nestedTarget, renamedTarget);
            Assert.True(Directory.Exists(renamedTarget));
        }

        [Fact]
        public void GetAllAccounts_RetriesWhenConcurrentSaveRetiresSnapshottedFilename()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount first = CreateAccount(76561198000005001UL);
            SteamGuardAccount second = CreateAccount(76561198000005002UL);
            Assert.True(manifest.SaveAccount(first, false).Succeeded);
            Assert.True(manifest.SaveAccount(second, false).Succeeded);

            // Rename first entry to a GUID filename so a concurrent NormalizeAccountFilenames
            // retires the snapshotted GUID filename and deletes it from disk before the reader opens it.
            string maDir = Path.Combine(dataDirectory, "maFiles");
            string oldFilename = manifest.Entries[0].Filename;
            string guidFilename = first.Session.SteamID + "." + Guid.NewGuid().ToString("N") + ".maFile";
            File.Move(Path.Combine(maDir, oldFilename), Path.Combine(maDir, guidFilename));
            manifest.Entries[0].Filename = guidFilename;
            Assert.True(manifest.Save());

            bool simulatedConcurrentMutation = false;
            manifest.OnBeforeGetAllAccountsFileReadForTest = () =>
            {
                if (simulatedConcurrentMutation)
                    return;
                simulatedConcurrentMutation = true;

                // Retires guidFilename and commits canonical filename on disk while GetAllAccounts holds the old snapshot.
                Assert.True(manifest.NormalizeAccountFilenames().Succeeded);
                Assert.False(File.Exists(Path.Combine(maDir, guidFilename)));
            };

            try
            {
                SteamGuardAccount[] loaded = manifest.GetAllAccounts();
                Assert.True(simulatedConcurrentMutation);
                Assert.Equal(2, loaded.Length);
                Assert.Contains(loaded, account => account.Session.SteamID == first.Session.SteamID);
                Assert.Contains(loaded, account => account.Session.SteamID == second.Session.SteamID);
            }
            finally
            {
                manifest.OnBeforeGetAllAccountsFileReadForTest = null;
            }
        }

        [Fact]
        public void VerifyPasskey_DoesNotOverwriteRotatedPasskeyProofWithStaleKey()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount(76561198000006001UL);
            Assert.True(manifest.SaveAccount(account, false).Succeeded);
            Assert.True(manifest.ChangeEncryptionKey(null, "old-passkey").Succeeded);

            // Deterministically rotate the encryption key AFTER VerifyPasskey("old-passkey") finishes reading
            // with the old key and BEFORE it acquires storageLock to publish verifiedPasskeyProof.
            bool rotatedMidVerification = false;
            manifest.OnBeforeVerifyPasskeyProofPublishForTest = () =>
            {
                if (rotatedMidVerification)
                    return;
                rotatedMidVerification = true;
                Assert.True(manifest.ChangeEncryptionKey("old-passkey", "new-passkey").Succeeded);
            };

            try
            {
                Assert.False(manifest.VerifyPasskey("old-passkey"));
                Assert.True(rotatedMidVerification);
            }
            finally
            {
                manifest.OnBeforeVerifyPasskeyProofPublishForTest = null;
            }

            // Attempting an encrypted write with the retired passkey must fail closed (proof was not overwritten).
            StorageResult staleWrite = manifest.SaveAccount(CreateAccount(76561198000006002UL), true, "old-passkey");
            Assert.False(staleWrite.Succeeded);

            // Writing with the current passkey succeeds.
            StorageResult currentWrite = manifest.SaveAccount(CreateAccount(76561198000006002UL), true, "new-passkey");
            Assert.True(currentWrite.Succeeded);
        }

        private static void CreateDirectoryJunction(string junctionPath, string targetPath)
        {
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
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
                Assert.True(process.ExitCode == 0, $"mklink /J failed ({process.ExitCode}): {stdout} {stderr}");
            }
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
                AccountName = "storage-test",
                FullyEnrolled = true
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
