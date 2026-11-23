using System;
using System.IO;
using System.Security.Cryptography;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class FileEncryptorTests
    {
        [Fact]
        public void EncryptAndDecrypt_DisposeBothAesAndCryptoTransformsOnSuccessAndFailure()
        {
            string salt = FileEncryptor.GetRandomSalt();
            string iv = FileEncryptor.GetInitializationVector();
            const string plaintext = "{\"account_name\":\"crypto-test\",\"unicode\":\"áéíóú-日本語\"}";

            TrackingAes encryptAes = null;
            string ciphertext = FileEncryptor.EncryptData(
                "correct-passkey",
                salt,
                iv,
                plaintext,
                () => encryptAes = new TrackingAes());

            Assert.NotNull(encryptAes);
            Assert.True(encryptAes.IsDisposed);
            Assert.NotNull(encryptAes.LastEncryptor);
            Assert.True(encryptAes.LastEncryptor.IsDisposed);
            Assert.NotNull(encryptAes.LastKeyPassedToTransform);
            Assert.All(encryptAes.LastKeyPassedToTransform, b => Assert.Equal(0, b));
            Assert.True(FileEncryptor.IsValidCiphertext(ciphertext));

            TrackingAes decryptAes = null;
            string decrypted = FileEncryptor.DecryptData(
                "correct-passkey",
                salt,
                iv,
                ciphertext,
                () => decryptAes = new TrackingAes());

            Assert.Equal(plaintext, decrypted);
            Assert.NotNull(decryptAes);
            Assert.True(decryptAes.IsDisposed);
            Assert.NotNull(decryptAes.LastDecryptor);
            Assert.True(decryptAes.LastDecryptor.IsDisposed);
            Assert.NotNull(decryptAes.LastKeyPassedToTransform);
            Assert.All(decryptAes.LastKeyPassedToTransform, b => Assert.Equal(0, b));

            TrackingAes failedDecryptAes = null;
            string failedDecrypt = FileEncryptor.DecryptData(
                "wrong-passkey",
                salt,
                iv,
                ciphertext,
                () => failedDecryptAes = new TrackingAes());

            Assert.Null(failedDecrypt);
            Assert.NotNull(failedDecryptAes);
            Assert.True(failedDecryptAes.IsDisposed);
            Assert.NotNull(failedDecryptAes.LastDecryptor);
            Assert.True(failedDecryptAes.LastDecryptor.IsDisposed);
            Assert.NotNull(failedDecryptAes.LastKeyPassedToTransform);
            Assert.All(failedDecryptAes.LastKeyPassedToTransform, b => Assert.Equal(0, b));

            TrackingAes failedEncryptAes = null;
            Assert.Throws<CryptographicException>(() => FileEncryptor.EncryptData(
                "correct-passkey",
                salt,
                iv,
                plaintext,
                () => failedEncryptAes = new TrackingAes(throwOnEncryptFinalBlock: true)));

            Assert.NotNull(failedEncryptAes);
            Assert.True(failedEncryptAes.IsDisposed);
            Assert.NotNull(failedEncryptAes.LastEncryptor);
            Assert.True(failedEncryptAes.LastEncryptor.IsDisposed);
            Assert.NotNull(failedEncryptAes.LastKeyPassedToTransform);
            Assert.All(failedEncryptAes.LastKeyPassedToTransform, b => Assert.Equal(0, b));
        }

        [Fact]
        public void DecryptData_RejectsInvalidCiphertextBeforeCreatingAesOrDerivingKey()
        {
            int factoryCalls = 0;
            Func<Aes> trackingFactory = () =>
            {
                factoryCalls++;
                return Aes.Create();
            };

            Assert.Null(FileEncryptor.DecryptData("passkey", "not-base64-salt", "not-base64-iv", "   ", trackingFactory));
            Assert.Null(FileEncryptor.DecryptData("passkey", "not-base64-salt", "not-base64-iv", "not-base64", trackingFactory));
            Assert.Null(FileEncryptor.DecryptData("passkey", "not-base64-salt", "not-base64-iv", Convert.ToBase64String(new byte[15]), trackingFactory));
            Assert.Equal(0, factoryCalls);
        }

        [Fact]
        public void EncryptAndDecrypt_RemainByteForByteCompatibleWithLegacyFormat()
        {
            string standardSalt = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            string longSalt = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            string iv = Convert.ToBase64String(new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 });

            (string Password, string Salt, string Plaintext)[] cases =
            {
                // Standard Unicode payload
                ("legacy-passkey-ñ-🔐", standardSalt, "{\"shared_secret\":\"abc123\",\"account_name\":\"legacy-user-é\"}"),
                // Exact 16-byte and 32-byte block-aligned UTF-8 plaintexts (full 16-byte PKCS#7 padding block)
                ("block-aligned-key", standardSalt, new string('A', 16)),
                ("block-aligned-key", standardSalt, new string('B', 32)),
                // > 64 KiB payload forcing StreamWriter flushes and MemoryStream buffer growth (capacity > length)
                ("large-payload-key", longSalt, new string('X', 70 * 1024) + "-end"),
                // > 8-byte salt and whitespace-only password & plaintext
                ("   ", longSalt, " \t\r\n ")
            };

            foreach ((string password, string salt, string plaintext) in cases)
            {
                string legacyCiphertext = EncryptWithLegacyPipeline(password, salt, iv, plaintext);
                string currentCiphertext = FileEncryptor.EncryptData(password, salt, iv, plaintext);

                Assert.Equal(legacyCiphertext, currentCiphertext);
                Assert.Equal(plaintext, FileEncryptor.DecryptData(password, salt, iv, legacyCiphertext));
            }
        }

        [Fact]
        public void TryDecodeCiphertext_AndValidationEnforceExpectedContracts()
        {
            Assert.False(FileEncryptor.TryDecodeCiphertext(null, out byte[] decodedNull));
            Assert.Null(decodedNull);
            Assert.False(FileEncryptor.TryDecodeCiphertext("   ", out _));
            Assert.False(FileEncryptor.TryDecodeCiphertext("!!!", out _));
            Assert.False(FileEncryptor.TryDecodeCiphertext(Convert.ToBase64String(Array.Empty<byte>()), out _));
            Assert.False(FileEncryptor.TryDecodeCiphertext(Convert.ToBase64String(new byte[17]), out _));

            byte[] aligned = new byte[32];
            for (int i = 0; i < aligned.Length; i++)
                aligned[i] = (byte)(i + 1);
            Assert.True(FileEncryptor.TryDecodeCiphertext(Convert.ToBase64String(aligned), out byte[] decodedAligned));
            Assert.Equal(aligned, decodedAligned);

            Assert.Equal(8, Convert.FromBase64String(FileEncryptor.GetRandomSalt()).Length);
            Assert.Equal(16, Convert.FromBase64String(FileEncryptor.GetInitializationVector()).Length);

            string validSalt = FileEncryptor.GetRandomSalt();
            string validIv = FileEncryptor.GetInitializationVector();
            string validCiphertext = FileEncryptor.EncryptData("pass", validSalt, validIv, "hello");

            int factoryCalls = 0;
            Func<Aes> failIfCalledFactory = () =>
            {
                factoryCalls++;
                return Aes.Create();
            };

            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData(null, validSalt, validIv, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("", validSalt, validIv, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", null, validIv, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", "", validIv, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", "   ", validIv, "hello", failIfCalledFactory));
            Assert.Throws<FormatException>(() => FileEncryptor.EncryptData("pass", "not-base64!", validIv, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", validSalt, null, "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", validSalt, "", "hello", failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.EncryptData("pass", validSalt, "   ", "hello", failIfCalledFactory));
            Assert.Throws<FormatException>(() => FileEncryptor.EncryptData("pass", validSalt, "not-base64!", "hello", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", validSalt, validIv, null, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", validSalt, validIv, "", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.EncryptData("pass", Convert.ToBase64String(new byte[7]), validIv, "hello", failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.EncryptData("pass", validSalt, Convert.ToBase64String(new byte[15]), "hello", failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.EncryptData("pass", validSalt, Convert.ToBase64String(new byte[17]), "hello", failIfCalledFactory));

            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData(null, validSalt, validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("", validSalt, validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", null, validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", "", validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", "   ", validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<FormatException>(() => FileEncryptor.DecryptData("pass", "not-base64!", validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", validSalt, null, validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", validSalt, "", validCiphertext, failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.DecryptData("pass", validSalt, "   ", validCiphertext, failIfCalledFactory));
            Assert.Throws<FormatException>(() => FileEncryptor.DecryptData("pass", validSalt, "not-base64!", validCiphertext, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", validSalt, validIv, null, failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", validSalt, validIv, "", failIfCalledFactory));
            Assert.Throws<ArgumentException>(() => FileEncryptor.DecryptData("pass", Convert.ToBase64String(new byte[7]), validIv, validCiphertext, failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.DecryptData("pass", validSalt, Convert.ToBase64String(new byte[15]), validCiphertext, failIfCalledFactory));
            Assert.Throws<CryptographicException>(() => FileEncryptor.DecryptData("pass", validSalt, Convert.ToBase64String(new byte[17]), validCiphertext, failIfCalledFactory));
            Assert.Equal(0, factoryCalls);
        }

        private static string EncryptWithLegacyPipeline(string password, string salt, string iv, string plaintext)
        {
            byte[] key;
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(password, Convert.FromBase64String(salt), 50000, HashAlgorithmName.SHA1))
            {
                key = pbkdf2.GetBytes(32);
            }

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = Convert.FromBase64String(iv);
                aes.Padding = PaddingMode.PKCS7;
                aes.Mode = CipherMode.CBC;

                using (ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (StreamWriter sw = new StreamWriter(cs))
                    {
                        sw.Write(plaintext);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        private sealed class TrackingAes : Aes
        {
            private readonly Aes inner = Aes.Create();
            private readonly bool throwOnEncryptFinalBlock;

            public TrackingAes(bool throwOnEncryptFinalBlock = false)
            {
                this.throwOnEncryptFinalBlock = throwOnEncryptFinalBlock;
            }

            public bool IsDisposed { get; private set; }
            public TrackingCryptoTransform LastEncryptor { get; private set; }
            public TrackingCryptoTransform LastDecryptor { get; private set; }
            public byte[] LastKeyPassedToTransform { get; private set; }

            public override int KeySize
            {
                get => inner.KeySize;
                set => inner.KeySize = value;
            }

            public override int BlockSize
            {
                get => inner.BlockSize;
                set => inner.BlockSize = value;
            }

            public override int FeedbackSize
            {
                get => inner.FeedbackSize;
                set => inner.FeedbackSize = value;
            }

            public override byte[] IV
            {
                get => inner.IV;
                set => inner.IV = value;
            }

            public override byte[] Key
            {
                get => inner.Key;
                set => inner.Key = value;
            }

            public override CipherMode Mode
            {
                get => inner.Mode;
                set => inner.Mode = value;
            }

            public override PaddingMode Padding
            {
                get => inner.Padding;
                set => inner.Padding = value;
            }

            public override KeySizes[] LegalBlockSizes => inner.LegalBlockSizes;
            public override KeySizes[] LegalKeySizes => inner.LegalKeySizes;

            public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[] rgbIV)
            {
                LastKeyPassedToTransform = rgbKey;
                LastEncryptor = new TrackingCryptoTransform(inner.CreateEncryptor(rgbKey, rgbIV), throwOnEncryptFinalBlock);
                return LastEncryptor;
            }

            public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[] rgbIV)
            {
                LastKeyPassedToTransform = rgbKey;
                LastDecryptor = new TrackingCryptoTransform(inner.CreateDecryptor(rgbKey, rgbIV), throwOnFinalBlock: false);
                return LastDecryptor;
            }

            public override void GenerateIV() => inner.GenerateIV();
            public override void GenerateKey() => inner.GenerateKey();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    IsDisposed = true;
                }
                base.Dispose(disposing);
            }
        }

        private sealed class TrackingCryptoTransform : ICryptoTransform
        {
            private readonly ICryptoTransform inner;
            private readonly bool throwOnFinalBlock;

            public TrackingCryptoTransform(ICryptoTransform inner, bool throwOnFinalBlock)
            {
                this.inner = inner;
                this.throwOnFinalBlock = throwOnFinalBlock;
            }

            public bool IsDisposed { get; private set; }
            public bool CanReuseTransform => inner.CanReuseTransform;
            public bool CanTransformMultipleBlocks => inner.CanTransformMultipleBlocks;
            public int InputBlockSize => inner.InputBlockSize;
            public int OutputBlockSize => inner.OutputBlockSize;

            public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
                => inner.TransformBlock(inputBuffer, inputOffset, inputCount, outputBuffer, outputOffset);

            public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
            {
                if (throwOnFinalBlock)
                    throw new CryptographicException("Simulated transform failure.");
                return inner.TransformFinalBlock(inputBuffer, inputOffset, inputCount);
            }

            public void Dispose()
            {
                inner.Dispose();
                IsDisposed = true;
            }
        }
    }
}
