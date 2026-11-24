using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// This class provides the controls that will encrypt and decrypt the *.maFile files
    /// 
    /// Passwords entered will be passed into PBKDF2 (RFC2898) with a cryptographically random salt.
    /// The generated key will then be passed into AES-256 which will encrypt the data
    /// in cipher block chaining (CBC) mode, and then write both the PBKDF2 salt and encrypted data onto the disk.
    /// </summary>
    public static class FileEncryptor
    {
        private const int PBKDF2_ITERATIONS = 50000; //Set to 50k to make program not unbearably slow. May increase in future.
        private const int SALT_LENGTH = 8;
        private const int KEY_SIZE_BYTES = 32;
        private const int IV_LENGTH = 16;
        private const int AES_BLOCK_SIZE_BYTES = 16;

        /// <summary>
        /// Returns an 8-byte cryptographically random salt in base64 encoding
        /// </summary>
        /// <returns></returns>
        public static string GetRandomSalt()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(SALT_LENGTH));
        }

        /// <summary>
        /// Returns a 16-byte cryptographically random initialization vector (IV) in base64 encoding
        /// </summary>
        /// <returns></returns>
        public static string GetInitializationVector()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(IV_LENGTH));
        }

        private static byte[] DecodeInitializationVector(string iv)
        {
            byte[] ivBytes = Convert.FromBase64String(iv);
            if (ivBytes.Length != IV_LENGTH)
            {
                throw new CryptographicException("Specified initialization vector (IV) does not match the block size for this algorithm.");
            }
            return ivBytes;
        }

        /// <summary>
        /// Generates an encryption key derived using a password, a random salt, and specified number of rounds of PBKDF2
        /// </summary>
        private static byte[] DeriveKey(string password, byte[] saltBytes)
        {
            // Match legacy Rfc2898DeriveBytes(string, ...) Encoding.UTF8 replacement-fallback behavior
            // (including unpaired UTF-16 surrogates) and wipe temporary password bytes immediately.
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, saltBytes, PBKDF2_ITERATIONS, HashAlgorithmName.SHA1, KEY_SIZE_BYTES);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }

        /// <summary>
        /// Checks whether an encoded value has the non-empty, block-aligned shape
        /// required by AES-CBC before it is accepted as encrypted account data.
        /// This does not prove that the passkey or PKCS#7 padding is valid.
        /// </summary>
        public static bool IsValidCiphertext(string encryptedData)
        {
            return TryDecodeCiphertext(encryptedData, out _);
        }

        internal static bool TryDecodeCiphertext(string encryptedData, out byte[] cipherText)
        {
            cipherText = null;
            if (String.IsNullOrWhiteSpace(encryptedData))
                return false;

            try
            {
                byte[] decoded = Convert.FromBase64String(encryptedData);
                if (decoded.Length == 0 || decoded.Length % AES_BLOCK_SIZE_BYTES != 0)
                    return false;

                cipherText = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Tries to decrypt and return data given an encrypted base64 encoded string. Must use the same
        /// password, salt, IV, and ciphertext that was used during the original encryption of the data.
        /// </summary>
        /// <param name="password"></param>
        /// <param name="passwordSalt"></param>
        /// <param name="IV">Initialization Vector</param>
        /// <param name="encryptedData"></param>
        /// <returns></returns>
        public static string DecryptData(string password, string passwordSalt, string IV, string encryptedData)
        {
            return DecryptData(password, passwordSalt, IV, encryptedData, Aes.Create);
        }

        internal static string DecryptData(string password, string passwordSalt, string IV, string encryptedData, Func<Aes> aesFactory)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password is empty");
            }
            if (string.IsNullOrEmpty(passwordSalt))
            {
                throw new ArgumentException("Salt is empty");
            }
            if (string.IsNullOrEmpty(IV))
            {
                throw new ArgumentException("Initialization Vector is empty");
            }
            if (string.IsNullOrEmpty(encryptedData))
            {
                throw new ArgumentException("Encrypted data is empty");
            }

            if (!TryDecodeCiphertext(encryptedData, out byte[] cipherText))
                return null;

            byte[] saltBytes = Convert.FromBase64String(passwordSalt);
            byte[] ivBytes = DecodeInitializationVector(IV);
            byte[] key = DeriveKey(password, saltBytes);
            try
            {
                using (Aes aes256 = (aesFactory ?? Aes.Create)())
                {
                    aes256.Padding = PaddingMode.PKCS7;
                    aes256.Mode = CipherMode.CBC;

                    using (ICryptoTransform decryptor = aes256.CreateDecryptor(key, ivBytes))
                    {
                        // Wrap in a try since a bad password yields a bad key, which throws on padding validation
                        try
                        {
                            using (MemoryStream msDecrypt = new MemoryStream(cipherText, writable: false))
                            using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                            using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                            {
                                return srDecrypt.ReadToEnd();
                            }
                        }
                        catch (CryptographicException)
                        {
                            return null;
                        }
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Encrypts a string given a password, salt, and initialization vector, then returns result in base64 encoded string.
        /// 
        /// To retrieve this data, you must decrypt with the same password, salt, IV, and ciphertext that was used during encryption
        /// </summary>
        /// <param name="password"></param>
        /// <param name="passwordSalt"></param>
        /// <param name="IV"></param>
        /// <param name="plaintext"></param>
        /// <returns></returns>
        public static string EncryptData(string password, string passwordSalt, string IV, string plaintext)
        {
            return EncryptData(password, passwordSalt, IV, plaintext, Aes.Create);
        }

        internal static string EncryptData(string password, string passwordSalt, string IV, string plaintext, Func<Aes> aesFactory)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password is empty");
            }
            if (string.IsNullOrEmpty(passwordSalt))
            {
                throw new ArgumentException("Salt is empty");
            }
            if (string.IsNullOrEmpty(IV))
            {
                throw new ArgumentException("Initialization Vector is empty");
            }
            if (string.IsNullOrEmpty(plaintext))
            {
                throw new ArgumentException("Plaintext data is empty");
            }

            byte[] saltBytes = Convert.FromBase64String(passwordSalt);
            byte[] ivBytes = DecodeInitializationVector(IV);
            byte[] key = DeriveKey(password, saltBytes);
            try
            {
                using (Aes aes256 = (aesFactory ?? Aes.Create)())
                {
                    aes256.Padding = PaddingMode.PKCS7;
                    aes256.Mode = CipherMode.CBC;

                    using (ICryptoTransform encryptor = aes256.CreateEncryptor(key, ivBytes))
                    using (MemoryStream msEncrypt = new MemoryStream())
                    {
                        using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write, leaveOpen: true))
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            swEncrypt.Write(plaintext);
                        }

                        if (msEncrypt.TryGetBuffer(out ArraySegment<byte> ciphertextBuffer))
                            return Convert.ToBase64String(ciphertextBuffer.AsSpan());

                        return Convert.ToBase64String(msEncrypt.ToArray());
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
