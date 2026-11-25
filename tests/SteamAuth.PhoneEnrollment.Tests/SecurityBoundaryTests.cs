using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class SecurityBoundaryTests
    {
        [Fact]
        public void WebViewSecurityPolicy_RequiresTheExpectedLocalDocument()
        {
            string expectedPath = Path.Combine(Path.GetTempPath(), "asda-ui-boundary", "index.html");
            string expectedUri = new Uri(expectedPath).AbsoluteUri;

            Assert.True(WebViewSecurityPolicy.IsTrustedLocalDocument(expectedUri, expectedPath));
            Assert.False(WebViewSecurityPolicy.IsTrustedLocalDocument("https://example.test/index.html", expectedPath));
            Assert.False(WebViewSecurityPolicy.IsTrustedLocalDocument(expectedUri + "?external=1", expectedPath));
            Assert.False(WebViewSecurityPolicy.IsTrustedLocalDocument(
                new Uri(Path.Combine(Path.GetTempPath(), "asda-ui-boundary", "other.html")).AbsoluteUri,
                expectedPath));
        }

        [Fact]
        public void FileEncryptor_RejectsMalformedAesCbcCiphertext()
        {
            Assert.False(FileEncryptor.IsValidCiphertext(String.Empty));
            Assert.False(FileEncryptor.IsValidCiphertext("not-base64"));
            Assert.False(FileEncryptor.IsValidCiphertext(Convert.ToBase64String(new byte[1])));

            string salt = FileEncryptor.GetRandomSalt();
            string iv = FileEncryptor.GetInitializationVector();
            string validCiphertext = FileEncryptor.EncryptData("test-passkey", salt, iv, "account data");

            Assert.True(FileEncryptor.IsValidCiphertext(validCiphertext));
            Assert.Null(FileEncryptor.DecryptData(
                "test-passkey",
                salt,
                iv,
                Convert.ToBase64String(new byte[1])));
        }

        [Fact]
        public void WelcomeImportValidation_RejectsMisalignedEncryptedCiphertext()
        {
            string stagingDirectory = Path.Combine(Path.GetTempPath(), "asda-import-boundary", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                string filename = "account.maFile";
                string salt = FileEncryptor.GetRandomSalt();
                string iv = FileEncryptor.GetInitializationVector();
                JObject manifest = new JObject
                {
                    ["encrypted"] = true,
                    ["entries"] = new JArray
                    {
                        new JObject
                        {
                            ["filename"] = filename,
                            ["steamid"] = 76561198000000000UL,
                            ["encryption_salt"] = salt,
                            ["encryption_iv"] = iv
                        }
                    }
                };

                string manifestPath = Path.Combine(stagingDirectory, "manifest.json");
                File.WriteAllText(manifestPath, manifest.ToString(Newtonsoft.Json.Formatting.None));
                File.WriteAllText(Path.Combine(stagingDirectory, filename), Convert.ToBase64String(new byte[1]));

                Assert.Throws<InvalidDataException>(() => WelcomeForm.ValidateStagedManifest(manifestPath, stagingDirectory));
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, true);
            }
        }

        [Fact]
        public void WelcomeImportValidation_StripsCredentialPropertiesCaseInsensitively()
        {
            string stagingDirectory = Path.Combine(Path.GetTempPath(), "asda-import-case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                string filename = "76561198000003333.maFile";
                var account = new SteamAuth.SteamGuardAccount
                {
                    AccountName = "case_user",
                    SharedSecret = Convert.ToBase64String(new byte[20]),
                    IdentitySecret = Convert.ToBase64String(new byte[20]),
                    DeviceID = "android:case",
                    FullyEnrolled = true,
                    Session = new SteamAuth.SessionData { SteamID = 76561198000003333UL }
                };
                File.WriteAllText(Path.Combine(stagingDirectory, filename), Newtonsoft.Json.JsonConvert.SerializeObject(account));

                string rawManifest = @"{
                    ""encrypted"": false,
                    ""first_run"": false,
                    ""Allow_Saved_Steam_Passwords"": true,
                    ""CREDENTIALS_STORE_FILENAME"": ""../escape.asda-cred"",
                    ""entries"": [{ ""filename"": ""76561198000003333.maFile"", ""steamid"": 76561198000003333 }]
                }";
                string manifestPath = Path.Combine(stagingDirectory, "manifest.json");
                File.WriteAllText(manifestPath, rawManifest);

                WelcomeForm.ValidateStagedManifest(manifestPath, stagingDirectory);

                string cleaned = File.ReadAllText(manifestPath);
                Assert.DoesNotContain("allow_saved_steam_passwords", cleaned, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("credentials_store_filename", cleaned, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, true);
            }
        }

        [Fact]
        public void AccountExport_NeverIncludesSavedCredentialStoreEntriesOrPasswords()
        {
            string tempData = Path.Combine(Path.GetTempPath(), "asda-sec-export-" + Guid.NewGuid().ToString("N"));
            string tempOut = Path.Combine(Path.GetTempPath(), "asda-sec-out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempData);
            Directory.CreateDirectory(tempOut);
            string prev = Environment.GetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, tempData);

            try
            {
                var account = new SteamAuth.SteamGuardAccount
                {
                    AccountName = "sec_user",
                    SharedSecret = Convert.ToBase64String(new byte[20]),
                    IdentitySecret = Convert.ToBase64String(new byte[20]),
                    RevocationCode = "R11111",
                    DeviceID = "android:sec",
                    FullyEnrolled = true,
                    Session = new SteamAuth.SessionData { SteamID = 76561198000002222UL }
                };

                string zipPath = Path.Combine(tempOut, "export.zip");
                AccountExportService.ExportPlainZip(new[] { account }, zipPath, includeSession: false);

                using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(".asda-cred", StringComparison.OrdinalIgnoreCase));
                    var manifestEntry = archive.GetEntry("manifest.json");
                    Assert.NotNull(manifestEntry);
                    using (var reader = new StreamReader(manifestEntry.Open()))
                    {
                        string manifestText = reader.ReadToEnd();
                        Assert.DoesNotContain("credentials_store_filename", manifestText, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("allow_saved_steam_passwords", manifestText, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, prev);
                try { if (Directory.Exists(tempData)) Directory.Delete(tempData, true); } catch { }
                try { if (Directory.Exists(tempOut)) Directory.Delete(tempOut, true); } catch { }
            }
        }

        [Fact]
        public void EncryptedSaveAccount_SucceedsWhenUnrelatedAccountFileIsCorrupt_AndStillRejectsWrongOrOldPasskey()
        {
            string tempData = Path.Combine(Path.GetTempPath(), "asda-sec-encsave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempData);
            string prev = Environment.GetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, tempData);

            try
            {
                const string passKey = "PrimaryPasskey!2026";
                const string newPassKey = "RotatedPasskey!2026";
                ulong steamIdA = 76561198000009001UL;
                ulong steamIdB = 76561198000009002UL;

                Manifest manifest = Manifest.GenerateNewManifest(false);
                SteamGuardAccount accountA = new SteamGuardAccount
                {
                    AccountName = "healthy_user",
                    SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                    IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                    DeviceID = "android:healthy",
                    FullyEnrolled = true,
                    Session = new SessionData { SteamID = steamIdA, AccessToken = "at-a1", RefreshToken = "rt-a1" }
                };
                SteamGuardAccount accountB = new SteamGuardAccount
                {
                    AccountName = "corrupt_user",
                    SharedSecret = Convert.ToBase64String(Enumerable.Range(41, 20).Select(i => (byte)i).ToArray()),
                    IdentitySecret = Convert.ToBase64String(Enumerable.Range(61, 20).Select(i => (byte)i).ToArray()),
                    DeviceID = "android:corrupt",
                    FullyEnrolled = true,
                    Session = new SessionData { SteamID = steamIdB, AccessToken = "at-b1", RefreshToken = "rt-b1" }
                };

                Assert.True(manifest.SaveAccount(accountA, encrypt: true, passKey: passKey).Succeeded);
                Assert.True(manifest.SaveAccount(accountB, encrypt: true, passKey: passKey).Succeeded);
                Assert.True(manifest.VerifyPasskey(passKey));

                // Corrupt account B's encrypted file on disk AFTER unlock
                string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
                string fileB = Path.Combine(maDir, manifest.Entries.Single(e => e.SteamID == steamIdB).Filename);
                File.WriteAllText(fileB, "corrupted-ciphertext-bytes");

                // Full-store VerifyPasskey still reports false because B is unreadable
                Assert.False(manifest.VerifyPasskey(passKey));

                // Ordinary encrypted SaveAccount for healthy account A MUST STILL SUCCEED with the verified passkey
                accountA.Session.AccessToken = "at-a2";
                Assert.True(manifest.SaveAccount(accountA, encrypt: true, passKey: passKey).Succeeded);

                // Wrong passkey MUST STILL FAIL
                Assert.False(manifest.SaveAccount(accountA, encrypt: true, passKey: "WrongPasskey!").Succeeded);

                // Cold-start Manifest reload #1 (no prior VerifyPasskey): repairing corrupt account B FIRST
                // (target entry is corrupt, falls back to healthy account A) MUST succeed, while wrong passkey fails closed.
                Manifest reloadedForCorruptTarget = Manifest.GetManifest(true);
                Assert.False(reloadedForCorruptTarget.SaveAccount(accountB, encrypt: true, passKey: "WrongPasskey!").Succeeded);
                Assert.True(reloadedForCorruptTarget.SaveAccount(accountB, encrypt: true, passKey: passKey).Succeeded);

                // Re-corrupt account A (the FIRST entry in Entries) with empty/0-byte content and reload fresh:
                // saving a brand-new account C when the first entry in Entries is empty MUST fall back to healthy B and succeed!
                string fileA = Path.Combine(maDir, reloadedForCorruptTarget.Entries.Single(e => e.SteamID == steamIdA).Filename);
                File.WriteAllText(fileA, String.Empty);
                SteamGuardAccount accountC = new SteamGuardAccount
                {
                    AccountName = "new_user_c",
                    SharedSecret = Convert.ToBase64String(Enumerable.Range(81, 20).Select(i => (byte)i).ToArray()),
                    IdentitySecret = Convert.ToBase64String(Enumerable.Range(101, 20).Select(i => (byte)i).ToArray()),
                    DeviceID = "android:newc",
                    FullyEnrolled = true,
                    Session = new SessionData { SteamID = 76561198000009003UL, AccessToken = "at-c1", RefreshToken = "rt-c1" }
                };
                Manifest reloadedForNewAccount = Manifest.GetManifest(true);
                Assert.False(reloadedForNewAccount.SaveAccount(accountC, encrypt: true, passKey: "WrongPasskey!").Succeeded);
                Assert.True(reloadedForNewAccount.SaveAccount(accountC, encrypt: true, passKey: passKey).Succeeded);

                // Restore A, rekey from passKey -> newPassKey, and verify old passKey proof is invalidated immediately
                Assert.True(reloadedForNewAccount.SaveAccount(accountA, encrypt: true, passKey: passKey).Succeeded);
                Assert.True(reloadedForNewAccount.ChangeEncryptionKey(passKey, newPassKey).Succeeded);
                Assert.False(reloadedForNewAccount.SaveAccount(accountA, encrypt: true, passKey: passKey).Succeeded);
                Assert.True(reloadedForNewAccount.SaveAccount(accountA, encrypt: true, passKey: newPassKey).Succeeded);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, prev);
                try { if (Directory.Exists(tempData)) Directory.Delete(tempData, true); } catch { }
            }
        }
    }
}
