using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
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
    }
}
