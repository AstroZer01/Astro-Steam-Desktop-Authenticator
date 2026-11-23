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
        public void CanonicalAccountFileSaves_CommitInOneTransactionAcrossCreateUpdateImportAndEncryption()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            ulong firstId = 76561198000001000UL;
            ulong secondId = 76561198000001001UL;
            ulong thirdId = 76561198000001002UL;
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
            Assert.True(manifest.SaveAccount(first, false).Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeUpdate);
            Assert.Equal(revBeforeUpdate + 1, manifest.GetAccountStorageRevision(firstId));
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

            // 4. Importing both a non-canonical source file and an already-canonical source file commits in 1 transaction.
            string nonCanonicalSource = "imported-legacy.maFile";
            string canonicalSource = Manifest.GetCanonicalMaFileFilename(thirdId);
            File.WriteAllText(Path.Combine(maDirectory, nonCanonicalSource), JsonConvert.SerializeObject(CreateAccount(secondId)));
            File.WriteAllText(Path.Combine(maDirectory, canonicalSource), JsonConvert.SerializeObject(CreateAccount(thirdId)));
            var candidates = manifest.FindUnmanagedMaFiles();
            Assert.Equal(2, candidates.Count);

            long commitsBeforeImport = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ImportUnmanagedMaFiles(candidates, "passkey-2").Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeImport);
            Assert.False(File.Exists(Path.Combine(maDirectory, nonCanonicalSource)));
            Assert.Equal(
                new[]
                {
                    Manifest.GetCanonicalMaFileFilename(firstId),
                    Manifest.GetCanonicalMaFileFilename(thirdId),
                    Manifest.GetCanonicalMaFileFilename(secondId)
                },
                manifest.Entries.Select(e => e.Filename).ToArray());
            Assert.Equal(3, Manifest.GetManifest(true).GetAllAccounts("passkey-2").Length);

            long commitsBeforeDecrypt = Manifest.StorageTransactionCommitCount;
            Assert.True(manifest.ChangeEncryptionKey("passkey-2", null).Succeeded);
            Assert.Equal(1, Manifest.StorageTransactionCommitCount - commitsBeforeDecrypt);
            Assert.Equal(3, Manifest.GetManifest(true).GetAllAccounts().Length);
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
            const string testAccountBackupFilename = ".mafile.11111111111111111111111111111111.bak";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            ulong accountId = 76561198000006000UL;
            SteamGuardAccount original = CreateAccount(accountId);
            original.Session.AccessToken = "original-token";
            Assert.True(manifest.SaveAccount(original, false).Succeeded);

            string maDirectory = Path.Combine(dataDirectory, "maFiles");
            string manifestPath = Path.Combine(maDirectory, "manifest.json");
            string journalPath = Path.Combine(maDirectory, ".asda-storage-transaction.json");
            string canonicalFilename = Manifest.GetCanonicalMaFileFilename(accountId);
            string canonicalPath = Path.Combine(maDirectory, canonicalFilename);
            string accountBackupPath = Path.Combine(maDirectory, testAccountBackupFilename);

            // 1. Uncommitted crash: manifest still has old storage_transaction_id, account file has new contents, backup has old contents.
            string originalFileContents = File.ReadAllText(canonicalPath);
            SteamGuardAccount replacementAccount = CreateAccount(accountId);
            replacementAccount.Session.AccessToken = "uncommitted-replacement-token";
            File.WriteAllText(accountBackupPath, originalFileContents);
            File.WriteAllText(canonicalPath, JsonConvert.SerializeObject(replacementAccount));

            JObject nextManifestJson = JObject.Parse(File.ReadAllText(manifestPath));
            nextManifestJson["storage_transaction_id"] = Guid.NewGuid().ToString("N");
            string uncommittedManifestContents = nextManifestJson.ToString(Formatting.None);

            JObject uncommittedJournal = new JObject
            {
                ["manifest_hash"] = ComputeContentHash(uncommittedManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename
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
            File.WriteAllText(canonicalPath, JsonConvert.SerializeObject(replacementAccount));
            File.WriteAllText(manifestPath, uncommittedManifestContents);

            JObject committedJournal = new JObject
            {
                ["manifest_hash"] = ComputeContentHash(uncommittedManifestContents),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = testAccountBackupFilename
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
                ["manifest_hash"] = ComputeContentHash(File.ReadAllText(manifestPath)),
                ["created_filenames"] = new JArray(),
                ["replaced_files"] = new JArray(new JObject
                {
                    ["filename"] = canonicalFilename,
                    ["backup_filename"] = canonicalFilename
                }),
                ["obsolete_filenames"] = new JArray(),
                ["backup_filename"] = null
            };
            File.WriteAllText(journalPath, tamperedJournal.ToString(Formatting.None));
            Assert.Throws<ManifestRecoveryException>(() => Manifest.GetManifest(true));
            Assert.True(File.Exists(canonicalPath));
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
