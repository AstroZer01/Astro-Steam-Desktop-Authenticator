using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class EncryptedCredentialStoreTests
    {
        [Fact]
        public void EncryptAndDecrypt_RoundTripsRecordsWithAesGcmAndUniqueGenerationIds()
        {
            Assert.True(EncryptedCredentialStore.IsSupported);

            string gen1 = EncryptedCredentialStore.CreateNewGenerationId();
            string gen2 = EncryptedCredentialStore.CreateNewGenerationId();
            Assert.NotEqual(gen1, gen2);
            Assert.Equal(32, gen1.Length);

            var input = new Dictionary<ulong, SavedCredentialRecord>
            {
                [76561198000000101UL] = new SavedCredentialRecord
                {
                    SteamId = 76561198000000101UL,
                    Password = "MySuperSecretPassword!123",
                    CredentialGenerationId = gen1,
                    PasswordRejected = false,
                    UpdatedUtc = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc)
                }
            };

            string envelopeJson = EncryptedCredentialStore.Encrypt("Passkey-Strong-42!", input);
            Assert.DoesNotContain("MySuperSecretPassword!123", envelopeJson);

            JObject envelope = JObject.Parse(envelopeJson);
            Assert.Equal(1, envelope.Value<int>("version"));
            Assert.Equal("pbkdf2-sha256", envelope.Value<string>("kdf"));
            Assert.Equal(600_000, envelope.Value<int>("iterations"));

            Dictionary<ulong, SavedCredentialRecord> decrypted = EncryptedCredentialStore.Decrypt("Passkey-Strong-42!", envelopeJson);
            Assert.Single(decrypted);
            Assert.Equal("MySuperSecretPassword!123", decrypted[76561198000000101UL].Password);
            Assert.Equal(gen1, decrypted[76561198000000101UL].CredentialGenerationId);
            Assert.False(decrypted[76561198000000101UL].PasswordRejected);

            Assert.Throws<CryptographicException>(() => EncryptedCredentialStore.Decrypt("Wrong-Passkey!", envelopeJson));
        }

        [Theory]
        [InlineData("{\"version\":2,\"kdf\":\"pbkdf2-sha256\",\"iterations\":600000,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"ciphertext\":\"AA==\"}")]
        [InlineData("{\"version\":1,\"kdf\":\"pbkdf2-sha1\",\"iterations\":600000,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"ciphertext\":\"AA==\"}")]
        [InlineData("{\"version\":1,\"kdf\":\"pbkdf2-sha256\",\"iterations\":999999999,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"ciphertext\":\"AA==\"}")]
        [InlineData("{\"version\":1,\"kdf\":\"pbkdf2-sha256\",\"iterations\":600000,\"salt\":\"short\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"ciphertext\":\"AA==\"}")]
        [InlineData("{\"version\":1,\"kdf\":\"pbkdf2-sha256\",\"iterations\":600000,\"salt\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"nonce\":\"AAAA\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"ciphertext\":\"AA==\"}")]
        public void Decrypt_RejectsTamperedOrUnsupportedEnvelopeParametersBeforeKdf(string maliciousEnvelope)
        {
            // Notice iterations=999999999 must fail instantaneously BEFORE running PBKDF2!
            Assert.Throws<InvalidDataException>(() => EncryptedCredentialStore.Decrypt("any-passkey", maliciousEnvelope));
        }

        [Fact]
        public void Decrypt_RejectsOversizedEnvelopeBeforeParsing()
        {
            string oversized = new string('A', EncryptedCredentialStore.MaximumEnvelopeBytes + 10);
            Assert.Throws<InvalidDataException>(() => EncryptedCredentialStore.Decrypt("any-passkey", oversized));
        }

        [Fact]
        public void FilenameValidators_MatchOnlyGenerationalCredentialFilesAndTemps()
        {
            string valid = EncryptedCredentialStore.CreateNewFilename();
            Assert.True(EncryptedCredentialStore.IsValidCredentialStoreFilename(valid));
            Assert.True(EncryptedCredentialStore.IsCredentialStoreOrTempFilename(valid));
            Assert.True(EncryptedCredentialStore.IsCredentialStoreOrTempFilename(valid + "." + Guid.NewGuid().ToString("N") + ".tmp"));

            Assert.False(EncryptedCredentialStore.IsValidCredentialStoreFilename("76561198000000001.maFile"));
            Assert.False(EncryptedCredentialStore.IsValidCredentialStoreFilename("..\\credentials." + Guid.NewGuid().ToString("N") + ".asda-cred"));
            Assert.False(EncryptedCredentialStore.IsValidCredentialStoreFilename("credentials.nothex.asda-cred"));
        }
    }

    [Collection("Manifest storage")]
    public sealed class CredentialStoreManifestIntegrationTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-cred-manifest-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public CredentialStoreManifestIntegrationTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string name)
        {
            return new SteamGuardAccount
            {
                AccountName = name,
                SharedSecret = Convert.ToBase64String(Enumerable.Repeat((byte)7, 20).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Repeat((byte)9, 20).ToArray()),
                DeviceID = "android:" + Guid.NewGuid().ToString("D"),
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = "access-" + steamId,
                    RefreshToken = "refresh-" + steamId
                }
            };
        }

        [Fact]
        public void CredentialStore_LifecycleRotationRemovalOrphanSweepAndAtomicGenerationChecks()
        {
            const string passKey1 = "Passkey-One-123!";
            const string passKey2 = "Passkey-Two-456!";
            ulong steamId1 = 76561198000001001UL;
            ulong steamId2 = 76561198000001002UL;

            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(steamId1, "alpha"), true, passKey1).Succeeded);
            Assert.True(manifest.SaveAccount(CreateAccount(steamId2, "beta"), true, passKey1).Succeeded);

            // Opt-in requires encryption and persists cleanly
            Assert.True(manifest.SetAllowSavedSteamPasswords(true).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId1, "pw-alpha-1", passKey1).Succeeded);
            Assert.True(manifest.SaveAccountPassword(steamId2, "pw-beta-1", passKey1).Succeeded);

            string maDir = Path.Combine(dataDirectory, "maFiles");
            string firstCredFilename = manifest.CredentialsStoreFilename;
            Assert.NotNull(firstCredFilename);
            Assert.True(File.Exists(Path.Combine(maDir, firstCredFilename)));

            // Plant an orphaned .asda-cred and stranded .tmp file; startup / save sweeps both!
            string orphanCred = EncryptedCredentialStore.CreateNewFilename();
            string orphanTmp = EncryptedCredentialStore.CreateNewFilename() + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(Path.Combine(maDir, orphanCred), "orphan");
            File.WriteAllText(Path.Combine(maDir, orphanTmp), "orphan-tmp");

            Manifest reloaded = Manifest.GetManifest(true);
            Assert.False(File.Exists(Path.Combine(maDir, orphanCred)));
            Assert.False(File.Exists(Path.Combine(maDir, orphanTmp)));
            Assert.True(File.Exists(Path.Combine(maDir, firstCredFilename)));

            // Atomic SaveAccount check: stale CredentialGenerationId MUST be rejected!
            SavedCredentialRecord rec1 = reloaded.GetSavedCredentialRecord(steamId1, passKey1);
            Assert.NotNull(rec1);
            string oldGenId = rec1.CredentialGenerationId;

            // Update password -> new random GUID generation ID
            Assert.True(reloaded.SaveAccountPassword(steamId1, "pw-alpha-2", passKey1).Succeeded);
            SavedCredentialRecord rec1Updated = reloaded.GetSavedCredentialRecord(steamId1, passKey1);
            Assert.NotEqual(oldGenId, rec1Updated.CredentialGenerationId);

            // Stale MarkSavedPasswordRejected with oldGenId must NOT reject the new credential!
            Assert.True(reloaded.MarkSavedPasswordRejected(passKey1, steamId1, oldGenId).Succeeded);
            Assert.False(reloaded.GetSavedCredentialRecord(steamId1, passKey1).PasswordRejected);

            // Stale SaveAccount with oldGenId must fail!
            SteamGuardAccount updatedSessionAccount = CreateAccount(steamId1, "alpha");
            StorageResult staleSave = reloaded.SaveAccount(
                updatedSessionAccount,
                true,
                passKey1,
                allowCreate: false,
                requireAllowSavedPasswords: true,
                expectedCredentialGenerationId: oldGenId,
                credentialPassKey: passKey1);
            Assert.False(staleSave.Succeeded);

            // Matching generation ID succeeds!
            StorageResult freshSave = reloaded.SaveAccount(
                updatedSessionAccount,
                true,
                passKey1,
                allowCreate: false,
                requireAllowSavedPasswords: true,
                expectedCredentialGenerationId: rec1Updated.CredentialGenerationId,
                credentialPassKey: passKey1);
            Assert.True(freshSave.Succeeded);

            // Re-keying rotates the credential store in the same transaction
            Assert.True(reloaded.ChangeEncryptionKey(passKey1, passKey2).Succeeded);
            Assert.Equal("pw-alpha-2", reloaded.GetSavedCredentialRecord(steamId1, passKey2).Password);
            Assert.Equal("pw-beta-1", reloaded.GetSavedCredentialRecord(steamId2, passKey2).Password);

            // Removing one account removes only its credential record and keeps the other
            Assert.True(reloaded.RemoveAccount(CreateAccount(steamId1, "alpha"), passKey2));
            Assert.Null(reloaded.GetSavedCredentialRecord(steamId1, passKey2));
            Assert.Equal("pw-beta-1", reloaded.GetSavedCredentialRecord(steamId2, passKey2).Password);

            // Removing encryption disables AllowSavedSteamPasswords and deletes the credential store
            Assert.True(reloaded.ChangeEncryptionKey(passKey2, null).Succeeded);
            Assert.False(reloaded.Encrypted);
            Assert.False(reloaded.AllowSavedSteamPasswords);
            Assert.Null(reloaded.CredentialsStoreFilename);
            Assert.Empty(Directory.EnumerateFiles(maDir, "*.asda-cred*"));
        }

        [Fact]
        public void WelcomeForm_ValidateStagedManifest_StripsSavedPasswordFlagsAndCredentialStoreReference()
        {
            string stagingDir = Path.Combine(dataDirectory, "staging-import");
            Directory.CreateDirectory(stagingDir);
            ulong steamId = 76561198000002001UL;
            SteamGuardAccount account = CreateAccount(steamId, "imported_user");
            File.WriteAllText(Path.Combine(stagingDir, steamId + ".maFile"), JsonConvert.SerializeObject(account));

            string manifestPath = Path.Combine(stagingDir, "manifest.json");
            JObject rawManifest = new JObject
            {
                ["encrypted"] = false,
                ["allow_saved_steam_passwords"] = true,
                ["credentials_store_filename"] = EncryptedCredentialStore.CreateNewFilename(),
                ["entries"] = new JArray
                {
                    new JObject
                    {
                        ["steamid"] = steamId,
                        ["filename"] = steamId + ".maFile"
                    }
                }
            };
            File.WriteAllText(manifestPath, rawManifest.ToString(Formatting.None));

            WelcomeForm.ValidateStagedManifest(manifestPath, stagingDir);

            JObject cleaned = JObject.Parse(File.ReadAllText(manifestPath));
            Assert.False(cleaned.Value<bool>("allow_saved_steam_passwords"));
            Assert.Null(cleaned["credentials_store_filename"]);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
