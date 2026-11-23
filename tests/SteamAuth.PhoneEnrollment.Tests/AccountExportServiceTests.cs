using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ZXing;
using ZXing.Windows.Compatibility;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class AccountExportServiceTests : IDisposable
    {
        private readonly string tempDir;
        private readonly string exportDir;
        private readonly string previousDataDir;

        public AccountExportServiceTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "asda-export-data-" + Guid.NewGuid().ToString("N"));
            exportDir = Path.Combine(Path.GetTempPath(), "asda-export-out-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(exportDir);
            previousDataDir = Environment.GetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable);
            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, tempDir);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.DataDirectoryEnvironmentVariable, previousDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            try { if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true); } catch { }
        }

        [Fact]
        public void SerializeAllowlistedMaFile_StripsSessionTokensWhenDisabled_AndPreservesSteamIdAndSecrets()
        {
            SteamGuardAccount account = CreateSampleAccount(76561198000001001UL, "alice");

            string strippedJson = AccountExportService.SerializeAllowlistedMaFile(account, includeSession: false);
            JObject stripped = JObject.Parse(strippedJson);
            Assert.Equal(account.SharedSecret, (string)stripped["shared_secret"]);
            Assert.Equal(account.IdentitySecret, (string)stripped["identity_secret"]);
            Assert.Equal(account.RevocationCode, (string)stripped["revocation_code"]);
            Assert.Equal(76561198000001001UL, (ulong)stripped["Session"]["SteamID"]);
            Assert.Null(stripped["Session"]["AccessToken"]);
            Assert.Null(stripped["Session"]["RefreshToken"]);
            Assert.Null(stripped["Session"]["SessionID"]);

            string fullJson = AccountExportService.SerializeAllowlistedMaFile(account, includeSession: true);
            JObject full = JObject.Parse(fullJson);
            Assert.Equal("access-tok", (string)full["Session"]["AccessToken"]);
            Assert.Equal("refresh-tok", (string)full["Session"]["RefreshToken"]);
            Assert.Equal("session-id", (string)full["Session"]["SessionID"]);
        }

        [Fact]
        public void ExportPlainMaFile_AndEncryptedZip_RoundTripThroughFileEncryptorAndWelcomeValidation()
        {
            SteamGuardAccount account1 = CreateSampleAccount(76561198000001002UL, "alice");
            SteamGuardAccount account2 = CreateSampleAccount(76561198000001003UL, "bob");

            // Plain .maFile export
            string plainPath = Path.Combine(exportDir, "alice.maFile");
            AccountExportService.ExportPlainMaFile(account1, plainPath, includeSession: false);
            byte[] rawBytes = File.ReadAllBytes(plainPath);
            // UTF-8 without BOM
            Assert.False(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF);
            SteamGuardAccount reloadedPlain = JsonConvert.DeserializeObject<SteamGuardAccount>(Encoding.UTF8.GetString(rawBytes));
            Assert.Equal(account1.SharedSecret, reloadedPlain.SharedSecret);
            Assert.Equal(account1.Session.SteamID, reloadedPlain.Session.SteamID);
            Assert.Null(reloadedPlain.Session.AccessToken);

            // SDA-Encrypted .zip export
            string zipPath = Path.Combine(exportDir, "backup.zip");
            AccountExportService.ExportEncryptedZip(new[] { account1, account2 }, zipPath, "zip-passkey-123", includeSession: true);

            string extractDir = Path.Combine(exportDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            string manifestPath = Path.Combine(extractDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));
            // Must pass WelcomeForm's strict staged manifest validation!
            WelcomeForm.ValidateStagedManifest(manifestPath, extractDir);

            JObject manObj = JObject.Parse(File.ReadAllText(manifestPath));
            Assert.True((bool)manObj["encrypted"]);
            JArray entries = (JArray)manObj["entries"];
            Assert.Equal(2, entries.Count);

            foreach (JObject entry in entries)
            {
                string fname = (string)entry["filename"];
                string salt = (string)entry["encryption_salt"];
                string iv = (string)entry["encryption_iv"];
                ulong sid = (ulong)entry["steamid"];

                Assert.Equal(8, Convert.FromBase64String(salt).Length);
                Assert.Equal(16, Convert.FromBase64String(iv).Length);

                string ciphertext = File.ReadAllText(Path.Combine(extractDir, fname));
                string decrypted = FileEncryptor.DecryptData("zip-passkey-123", salt, iv, ciphertext);
                Assert.NotNull(decrypted);

                SteamGuardAccount decryptedAccount = JsonConvert.DeserializeObject<SteamGuardAccount>(decrypted);
                Assert.Equal(sid, decryptedAccount.Session.SteamID);
                Assert.Equal("access-tok", decryptedAccount.Session.AccessToken);
            }
        }

        [Fact]
        public void ImportAccountForm_TokenlessMaFile_BindsExpectedSteamIdWithoutManifest_AndRejectsMismatch()
        {
            SteamGuardAccount tokenless = CreateSampleAccount(76561198000001010UL, "charlie");
            tokenless.Session.AccessToken = null;
            tokenless.Session.RefreshToken = null;

            // Without sibling manifest (manifestExpectedSteamId == 0), binds to maFile.Session.SteamID
            ulong boundSteamId = ImportAccountForm.ResolveExpectedSteamId(0, tokenless);
            Assert.Equal(76561198000001010UL, boundSteamId);
            Assert.True(ImportAccountForm.IsImportedSessionSteamIdMatch(boundSteamId, 76561198000001010UL));
            Assert.False(ImportAccountForm.IsImportedSessionSteamIdMatch(boundSteamId, 76561198000009999UL));
            Assert.False(ImportAccountForm.IsImportedSessionSteamIdMatch(boundSteamId, 0));

            // With conflicting manifest SteamID -> throws InvalidDataException
            Assert.Throws<InvalidDataException>(() =>
                ImportAccountForm.ResolveExpectedSteamId(76561198000009999UL, tokenless));
        }

        [Fact]
        public void ValidateExportDestination_BlocksLiveMaFilesDirectory_AndWindowsExtendedPathAliases()
        {
            string liveMaDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            Directory.CreateDirectory(liveMaDir);

            Assert.Throws<InvalidDataException>(() =>
                AccountExportService.ValidateExportDestination(Path.Combine(liveMaDir, "test.maFile")));
            Assert.Throws<InvalidDataException>(() =>
                AccountExportService.ValidateExportDestination(Path.Combine(liveMaDir, "sub", "backup.zip")));
            Assert.Throws<InvalidDataException>(() =>
                AccountExportService.ValidateExportDestination(liveMaDir));
            Assert.Throws<InvalidDataException>(() =>
                AccountExportService.ValidateExportDestination(@"\\?\" + Path.Combine(liveMaDir, "alias.maFile")));
            Assert.Throws<InvalidDataException>(() =>
                AccountExportService.ValidateExportDestination(@"\\.\" + Path.Combine(liveMaDir, "device.maFile")));
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("CON.maFile")]
        [InlineData("PRN")]
        [InlineData("AUX.txt")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("COM\u00B9")]
        [InlineData("COM\u00B9.maFile")]
        [InlineData("COM\u00B2")]
        [InlineData("COM\u00B3")]
        [InlineData("LPT\u00B9")]
        [InlineData("LPT\u00B2")]
        [InlineData("LPT\u00B3.maFile")]
        public void SanitizeExportFilename_NeutralizesAllWindowsReservedDeviceNames(string rawName)
        {
            string sanitized = AccountExportService.SanitizeExportFilename(rawName, 76561198000001050UL, extension: ".maFile");
            Assert.StartsWith("_", sanitized, StringComparison.Ordinal);
            Assert.EndsWith(".maFile", sanitized, StringComparison.Ordinal);
        }

        [Fact]
        public void SanitizeExportFilename_NeutralizesReservedDeviceNamesEvenAfterLongTruncation()
        {
            string paddedReserved = "CON" + new string(' ', 80) + "suffix";
            string sanitized = AccountExportService.SanitizeExportFilename(paddedReserved, 76561198000001051UL, extension: ".maFile");
            Assert.StartsWith("_", sanitized, StringComparison.Ordinal);
            Assert.NotEqual("CON.maFile", sanitized, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void SanitizeExportFilename_EnforcesCaseInsensitiveUniqueness()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string first = AccountExportService.SanitizeExportFilename("Alice", 76561198000001001UL, used, ".maFile");
            string second = AccountExportService.SanitizeExportFilename("alice", 76561198000001002UL, used, ".maFile");
            string third = AccountExportService.SanitizeExportFilename("ALICE", 76561198000001002UL, used, ".maFile");

            Assert.Equal("Alice.maFile", first);
            Assert.Equal("alice_76561198000001002.maFile", second);
            Assert.Equal("ALICE_76561198000001002_2.maFile", third);
        }

        [Fact]
        public void SecretQrBuilder_GeneratesValidKeePassXcAndBitwardenUris_RoundTripsThroughZXing_AndMatchesSteamTotp()
        {
            // Fixed 20-byte RFC test secret vector
            byte[] secretBytes = new byte[]
            {
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30
            };
            string expectedBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
            Assert.Equal(expectedBase32, SecretQrBuilder.EncodeBase32Unpadded(secretBytes));

            SteamGuardAccount account = CreateSampleAccount(76561198000001099UL, "alice/bob?x=1");
            account.SharedSecret = Convert.ToBase64String(secretBytes);

            string keepassUri = SecretQrBuilder.BuildSecretUri(account, SecretQrFormat.KeePassXC);
            Assert.Equal(
                "otpauth://totp/Steam:alice%2Fbob%3Fx%3D1?secret=" + expectedBase32 + "&issuer=Steam&period=30&digits=5&encoder=steam",
                keepassUri);

            string bitwardenUri = SecretQrBuilder.BuildSecretUri(account, SecretQrFormat.Bitwarden);
            Assert.Equal("steam://" + expectedBase32, bitwardenUri);

            // Round-trip both URIs through ZXing QR render + decode
            var reader = new BarcodeReader();
            using (Bitmap kpBitmap = SecretQrBuilder.RenderQrBitmap(keepassUri, 280))
            {
                Result decoded = reader.Decode(kpBitmap);
                Assert.NotNull(decoded);
                Assert.Equal(keepassUri, decoded.Text);
            }

            using (Bitmap bwBitmap = SecretQrBuilder.RenderQrBitmap(bitwardenUri, 280))
            {
                Result decoded = reader.Decode(bwBitmap);
                Assert.NotNull(decoded);
                Assert.Equal(bitwardenUri, decoded.Text);
            }

            // Fixed-vector 5-char Steam TOTP parity: decode Base32 back to raw bytes and compute Steam code
            byte[] decodedFromBase32 = DecodeBase32(expectedBase32);
            Assert.Equal(secretBytes, decodedFromBase32);
            long fixedTime = 1700000000L;
            string expectedSteamCode = ComputeReferenceSteamTotp(decodedFromBase32, fixedTime);
            string actualSteamCode = account.GenerateSteamGuardCodeForTime(fixedTime);
            Assert.Equal(5, actualSteamCode.Length);
            Assert.Equal(expectedSteamCode, actualSteamCode);
        }

        private static SteamGuardAccount CreateSampleAccount(ulong steamId, string name)
        {
            return new SteamGuardAccount
            {
                AccountName = name,
                SharedSecret = Convert.ToBase64String(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray()),
                IdentitySecret = Convert.ToBase64String(Enumerable.Range(21, 20).Select(i => (byte)i).ToArray()),
                Secret1 = Convert.ToBase64String(Enumerable.Range(41, 20).Select(i => (byte)i).ToArray()),
                SerialNumber = "1234567890",
                RevocationCode = "R12345",
                URI = "otpauth://totp/Steam:" + name,
                ServerTime = 1700000000L,
                TokenGID = "gid123",
                Status = 1,
                DeviceID = "android:11111111-2222-3333-4444-555555555555",
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = "access-tok",
                    RefreshToken = "refresh-tok",
                    SessionID = "session-id"
                }
            };
        }

        private static byte[] DecodeBase32(string base32)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            List<byte> result = new List<byte>();
            int buffer = 0;
            int bitsLeft = 0;
            foreach (char c in base32)
            {
                int val = alphabet.IndexOf(c);
                Assert.True(val >= 0);
                buffer = (buffer << 5) | val;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    result.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                    bitsLeft -= 8;
                }
            }
            return result.ToArray();
        }

        private static string ComputeReferenceSteamTotp(byte[] secret, long unixSeconds)
        {
            const string steamChars = "23456789BCDFGHJKMNPQRTVWXY";
            long timeSlice = unixSeconds / 30L;
            byte[] timeBytes = new byte[8];
            for (int i = 7; i >= 0; i--)
            {
                timeBytes[i] = (byte)(timeSlice & 0xFF);
                timeSlice >>= 8;
            }

            using (var hmac = new HMACSHA1(secret))
            {
                byte[] hash = hmac.ComputeHash(timeBytes);
                int offset = hash[19] & 0x0F;
                int fullCode = ((hash[offset] & 0x7F) << 24) |
                    ((hash[offset + 1] & 0xFF) << 16) |
                    ((hash[offset + 2] & 0xFF) << 8) |
                    (hash[offset + 3] & 0xFF);

                char[] code = new char[5];
                for (int i = 0; i < 5; i++)
                {
                    code[i] = steamChars[fullCode % steamChars.Length];
                    fullCode /= steamChars.Length;
                }
                return new string(code);
            }
        }
    }
}
