using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Steam_Desktop_Authenticator
{
    public sealed class SavedCredentialRecord
    {
        [JsonProperty("steamid")]
        public ulong SteamId { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("credential_generation_id")]
        public string CredentialGenerationId { get; set; }

        [JsonProperty("password_rejected")]
        public bool PasswordRejected { get; set; }

        [JsonProperty("password_rejected_utc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTime? PasswordRejectedUtc { get; set; }

        [JsonProperty("updated_utc")]
        public DateTime UpdatedUtc { get; set; }

        public SavedCredentialRecord Clone()
        {
            return new SavedCredentialRecord
            {
                SteamId = SteamId,
                Password = Password,
                CredentialGenerationId = CredentialGenerationId,
                PasswordRejected = PasswordRejected,
                PasswordRejectedUtc = PasswordRejectedUtc,
                UpdatedUtc = UpdatedUtc
            };
        }
    }

    public sealed class SavedCredentialStatus
    {
        public SavedCredentialStatus(
            bool hasSavedPassword,
            bool passwordRejected,
            DateTime? passwordRejectedUtc,
            string credentialGenerationId,
            DateTime? updatedUtc)
        {
            HasSavedPassword = hasSavedPassword;
            PasswordRejected = passwordRejected;
            PasswordRejectedUtc = passwordRejectedUtc;
            CredentialGenerationId = credentialGenerationId;
            UpdatedUtc = updatedUtc;
        }

        public bool HasSavedPassword { get; }
        public bool PasswordRejected { get; }
        public DateTime? PasswordRejectedUtc { get; }
        public string CredentialGenerationId { get; }
        public DateTime? UpdatedUtc { get; }

        public static readonly SavedCredentialStatus None = new SavedCredentialStatus(false, false, null, null, null);
    }

    public static class EncryptedCredentialStore
    {
        public const int FormatVersion = 1;
        public const string KdfAlgorithm = "pbkdf2-sha256";
        public const int KdfIterations = 600_000;
        public const int SaltSizeBytes = 16;
        public const int NonceSizeBytes = 12;
        public const int TagSizeBytes = 16;
        public const int KeySizeBytes = 32;
        public const int MaximumEnvelopeBytes = 1024 * 1024; // 1 MiB
        public const int MaximumPasswordLength = 4096;

        private static readonly Regex ValidFilenameRegex = new Regex(
            @"^credentials\.[0-9a-f]{32}\.asda-cred$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ValidFilenameOrTempRegex = new Regex(
            @"^credentials\.[0-9a-f]{32}\.asda-cred(?:\.[0-9a-f]{32}\.tmp)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly JsonSerializerSettings CredentialJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 16,
            DateParseHandling = DateParseHandling.None
        };

        private sealed class CredentialEnvelope
        {
            [JsonProperty("version")]
            public int Version { get; set; }

            [JsonProperty("kdf")]
            public string Kdf { get; set; }

            [JsonProperty("iterations")]
            public int Iterations { get; set; }

            [JsonProperty("salt")]
            public string Salt { get; set; }

            [JsonProperty("nonce")]
            public string Nonce { get; set; }

            [JsonProperty("tag")]
            public string Tag { get; set; }

            [JsonProperty("ciphertext")]
            public string Ciphertext { get; set; }
        }

        private sealed class CredentialPayload
        {
            [JsonProperty("records")]
            public Dictionary<ulong, SavedCredentialRecord> Records { get; set; } = new Dictionary<ulong, SavedCredentialRecord>();
        }

        public static bool IsSupported => AesGcm.IsSupported;

        public static string CreateNewFilename()
        {
            return "credentials." + Guid.NewGuid().ToString("N") + ".asda-cred";
        }

        public static string CreateNewGenerationId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public static bool IsValidCredentialStoreFilename(string filename)
        {
            return !String.IsNullOrEmpty(filename) && ValidFilenameRegex.IsMatch(filename);
        }

        public static bool IsCredentialStoreOrTempFilename(string filename)
        {
            return !String.IsNullOrEmpty(filename) && ValidFilenameOrTempRegex.IsMatch(filename);
        }

        public static string Encrypt(string passKey, IReadOnlyDictionary<ulong, SavedCredentialRecord> records)
        {
            if (!IsSupported)
                throw new PlatformNotSupportedException("AES-GCM encryption is not supported on this platform.");
            if (String.IsNullOrEmpty(passKey))
                throw new ArgumentException("A passkey is required to encrypt saved credentials.", nameof(passKey));

            var payload = new CredentialPayload();
            if (records != null)
            {
                foreach (KeyValuePair<ulong, SavedCredentialRecord> kvp in records)
                {
                    if (kvp.Key == 0 || kvp.Value == null || String.IsNullOrEmpty(kvp.Value.Password))
                        continue;
                    if (kvp.Value.Password.Length > MaximumPasswordLength)
                        throw new InvalidDataException("A saved password exceeds the supported length limit.");
                    if (String.IsNullOrWhiteSpace(kvp.Value.CredentialGenerationId))
                        throw new InvalidDataException("A saved credential record is missing its generation identifier.");

                    SavedCredentialRecord copy = kvp.Value.Clone();
                    copy.SteamId = kvp.Key;
                    payload.Records[kvp.Key] = copy;
                }
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            byte[] tag = new byte[TagSizeBytes];
            string saltBase64 = Convert.ToBase64String(salt);
            byte[] aad = BuildAssociatedData(FormatVersion, KdfAlgorithm, KdfIterations, saltBase64);

            byte[] plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload, CredentialJsonSettings));
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] key = null;

            try
            {
                key = DeriveKey(passKey, salt, KdfIterations);
                using (AesGcm aesGcm = new AesGcm(key, TagSizeBytes))
                {
                    aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                }

                CredentialEnvelope envelope = new CredentialEnvelope
                {
                    Version = FormatVersion,
                    Kdf = KdfAlgorithm,
                    Iterations = KdfIterations,
                    Salt = saltBase64,
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Ciphertext = Convert.ToBase64String(ciphertext)
                };

                string serialized = JsonConvert.SerializeObject(envelope, CredentialJsonSettings);
                if (Encoding.UTF8.GetByteCount(serialized) > MaximumEnvelopeBytes)
                    throw new InvalidDataException("The encrypted credential store exceeds the maximum supported size.");

                return serialized;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (key != null)
                    CryptographicOperations.ZeroMemory(key);
            }
        }

        public static Dictionary<ulong, SavedCredentialRecord> Decrypt(string passKey, string envelopeJson)
        {
            if (!IsSupported)
                throw new PlatformNotSupportedException("AES-GCM encryption is not supported on this platform.");
            if (String.IsNullOrEmpty(passKey))
                throw new ArgumentException("A passkey is required to decrypt saved credentials.", nameof(passKey));
            if (String.IsNullOrWhiteSpace(envelopeJson))
                throw new InvalidDataException("The credential store file is empty.");
            if (Encoding.UTF8.GetByteCount(envelopeJson) > MaximumEnvelopeBytes)
                throw new InvalidDataException("The credential store file exceeds the maximum supported size.");

            CredentialEnvelope envelope;
            try
            {
                using (StringReader stringReader = new StringReader(envelopeJson))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 8, DateParseHandling = DateParseHandling.None })
                {
                    envelope = JsonSerializer.Create(CredentialJsonSettings).Deserialize<CredentialEnvelope>(jsonReader);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The credential store envelope is malformed.", ex);
            }

            // Strictly validate ALL envelope parameters BEFORE running PBKDF2!
            if (envelope == null ||
                envelope.Version != FormatVersion ||
                !String.Equals(envelope.Kdf, KdfAlgorithm, StringComparison.Ordinal) ||
                envelope.Iterations != KdfIterations ||
                String.IsNullOrWhiteSpace(envelope.Salt) ||
                String.IsNullOrWhiteSpace(envelope.Nonce) ||
                String.IsNullOrWhiteSpace(envelope.Tag) ||
                String.IsNullOrWhiteSpace(envelope.Ciphertext))
            {
                throw new InvalidDataException("The credential store envelope has unsupported or missing cryptographic parameters.");
            }

            byte[] salt = DecodeExactBase64(envelope.Salt, SaltSizeBytes, "salt");
            byte[] nonce = DecodeExactBase64(envelope.Nonce, NonceSizeBytes, "nonce");
            byte[] tag = DecodeExactBase64(envelope.Tag, TagSizeBytes, "tag");
            byte[] ciphertext = DecodeNonEmptyBase64(envelope.Ciphertext, MaximumEnvelopeBytes, "ciphertext");
            byte[] aad = BuildAssociatedData(envelope.Version, envelope.Kdf, envelope.Iterations, envelope.Salt);

            byte[] plaintext = new byte[ciphertext.Length];
            byte[] key = null;
            try
            {
                key = DeriveKey(passKey, salt, envelope.Iterations);
                using (AesGcm aesGcm = new AesGcm(key, TagSizeBytes))
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                }

                string plaintextJson = Encoding.UTF8.GetString(plaintext);
                CredentialPayload payload;
                using (StringReader stringReader = new StringReader(plaintextJson))
                using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                {
                    payload = JsonSerializer.Create(CredentialJsonSettings).Deserialize<CredentialPayload>(jsonReader);
                }

                Dictionary<ulong, SavedCredentialRecord> result = new Dictionary<ulong, SavedCredentialRecord>();
                if (payload?.Records != null)
                {
                    foreach (KeyValuePair<ulong, SavedCredentialRecord> kvp in payload.Records)
                    {
                        if (kvp.Key == 0 || kvp.Value == null || String.IsNullOrEmpty(kvp.Value.Password))
                            continue;
                        if (String.IsNullOrWhiteSpace(kvp.Value.CredentialGenerationId))
                            continue;

                        SavedCredentialRecord copy = kvp.Value.Clone();
                        copy.SteamId = kvp.Key;
                        result[kvp.Key] = copy;
                    }
                }

                return result;
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("The credential store could not be decrypted with the provided passkey.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (key != null)
                    CryptographicOperations.ZeroMemory(key);
            }
        }

        private static byte[] DeriveKey(string passKey, byte[] salt, int iterations)
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(passKey);
            try
            {
                return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, KeySizeBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }

        private static byte[] BuildAssociatedData(int version, string kdf, int iterations, string saltBase64)
        {
            return Encoding.UTF8.GetBytes("ASDA-CREDENTIALS-V1:" + version + ":" + kdf + ":" + iterations + ":" + saltBase64);
        }

        private static byte[] DecodeExactBase64(string value, int expectedLength, string fieldName)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The credential store " + fieldName + " is not valid base64.", ex);
            }

            if (bytes.Length != expectedLength)
                throw new InvalidDataException("The credential store " + fieldName + " has an unexpected length.");

            return bytes;
        }

        private static byte[] DecodeNonEmptyBase64(string value, int maxLength, string fieldName)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The credential store " + fieldName + " is not valid base64.", ex);
            }

            if (bytes.Length == 0 || bytes.Length > maxLength)
                throw new InvalidDataException("The credential store " + fieldName + " length is out of range.");

            return bytes;
        }
    }
}
