using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SteamAuth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    public partial class ImportAccountForm : Form
    {
        private const long MaximumImportFileSizeBytes = 4 * 1024 * 1024;
        private const int MaximumImportManifestEntries = 1000;
        private static readonly JsonSerializerSettings ImportJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 32,
            DateParseHandling = DateParseHandling.None
        };
        private Manifest mManifest;

        [System.Runtime.InteropServices.DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect,
            int nTopRect,
            int nRightRect,
            int nBottomRect,
            int nWidthEllipse,
            int nHeightEllipse
        );

        private string mCurrentPassKey;

        public ImportAccountForm(string currentPassKey)
        {
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            this.mManifest = Manifest.GetManifest();
            this.mCurrentPassKey = currentPassKey;

            // Round borders
            btnImport.FlatAppearance.BorderSize = 0;
            btnCancel.FlatAppearance.BorderSize = 0;
            btnImport.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, btnImport.Width, btnImport.Height, 8, 8));
            btnCancel.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, btnCancel.Width, btnCancel.Height, 8, 8));
        }

        private void btnImport_Click(object sender, EventArgs e)
        {
            Form dialogOwner = Owner;
            this.Close();

            using (OpenFileDialog openFileDialog1 = new OpenFileDialog())
            {
                openFileDialog1.Filter = "maFiles (.maFile)|*.maFile|All Files (*.*)|*.*";
                openFileDialog1.FilterIndex = 1;
                openFileDialog1.Multiselect = false;

                if (openFileDialog1.ShowDialog(dialogOwner) != DialogResult.OK) return;

                try
                {
                    string fullPath = Path.GetFullPath(openFileDialog1.FileName);
                    FileInfo sourceFile = new FileInfo(fullPath);
                    if (!sourceFile.Exists || (sourceFile.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The selected account file is unavailable or uses an unsupported link.");

                    string filename = sourceFile.Name;
                    if (!IsValidImportFilename(filename))
                        throw new InvalidDataException("Select a valid .maFile account file.");

                    EnsureNoReparsePoints(sourceFile.DirectoryName);
                    string fileContents = ReadTextWithLimit(fullPath, MaximumImportFileSizeBytes);
                    SteamGuardAccount maFile = null;
                    bool isEncrypted = false;
                    string salt = null;
                    string iv = null;
                    ulong expectedSteamId = 0;

                    // Check the source manifest beside the selected account file. A
                    // malformed manifest must not silently turn an encrypted file into
                    // an unencrypted import attempt.
                    string sourceDirectory = sourceFile.DirectoryName;
                    string manifestPath = Path.Combine(sourceDirectory, "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        ImportManifest sourceManifest = ReadImportManifest(manifestPath);
                        ImportManifestEntry matchingEntry = ValidateImportManifest(sourceManifest, filename);
                        if (sourceManifest.Encrypted)
                        {
                            if (matchingEntry == null)
                                throw new InvalidDataException("The selected encrypted account is not listed in its source manifest.");

                            isEncrypted = true;
                            salt = matchingEntry.Salt;
                            iv = matchingEntry.IV;
                            expectedSteamId = matchingEntry.SteamID;
                        }
                        else if (matchingEntry != null)
                        {
                            expectedSteamId = matchingEntry.SteamID;
                        }
                    }

                if (isEncrypted)
                {
                    if (!FileEncryptor.IsValidCiphertext(fileContents))
                        throw new InvalidDataException("The selected encrypted account file is not a valid AES-CBC ciphertext.");

                    // Try silent decrypt with RAM passkey
                    string decryptedText = null;
                    if (!string.IsNullOrEmpty(mCurrentPassKey))
                    {
                        decryptedText = FileEncryptor.DecryptData(mCurrentPassKey, salt, iv, fileContents);
                    }

                    if (decryptedText == null)
                    {
                        // Prompt user for import passkey
                        string importedPassKey;
                        using (InputForm passKeyForm = new InputForm("Enter the passkey for the imported account.", true))
                        {
                            passKeyForm.ShowInputDialog(dialogOwner);
                            if (passKeyForm.Canceled) return;
                            importedPassKey = passKeyForm.txtBox.Text;
                        }
                        decryptedText = FileEncryptor.DecryptData(importedPassKey, salt, iv, fileContents);

                        if (decryptedText == null)
                        {
                            MessageBox.Show("Decryption Failed.\nImport Failed.", "Account Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }

                    fileContents = decryptedText;
                }

                if (Encoding.UTF8.GetByteCount(fileContents ?? String.Empty) > MaximumImportFileSizeBytes)
                    throw new InvalidDataException("The decrypted account file is larger than the supported size limit.");

                maFile = JsonConvert.DeserializeObject<SteamGuardAccount>(fileContents, ImportJsonSettings);
                if (maFile == null) throw new InvalidDataException("The selected file did not contain a Steam Guard account.");

                if (expectedSteamId != 0 && maFile.Session != null && maFile.Session.SteamID != 0 &&
                    maFile.Session.SteamID != expectedSteamId)
                    throw new InvalidDataException("The selected account does not match its source manifest entry.");

                if (maFile.Session == null || maFile.Session.SteamID == 0 || maFile.Session.IsAccessTokenExpired())
                {
                    using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Import, maFile))
                    {
                        ShowOwnedDialog(loginForm, dialogOwner);
                        if (loginForm.Session == null || loginForm.Session.SteamID == 0 ||
                            (expectedSteamId != 0 && loginForm.Session.SteamID != expectedSteamId))
                        {
                            MessageBox.Show("The login did not match the selected account. Try to import this account again.", "Account Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        maFile.Session = loginForm.Session;
                    }
                }

                // Save account, applying destination encryption securely.
                StorageResult saveResult = mManifest.SaveAccount(maFile, mManifest.Encrypted, mCurrentPassKey);
                if (!saveResult.Succeeded)
                {
                    DiagnosticErrorLogger.Log("Account import", saveResult.Exception, "The import was canceled because local account persistence failed.");
                    AstroMessageBox.Show(saveResult.UserMessage ?? "The account could not be saved. No import was completed.", "Account Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MessageBox.Show("Account Imported!", "Account Import", MessageBoxButtons.OK);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Account import", ex, "The selected maFile could not be imported.");
                    MessageBox.Show("This file is not a valid SteamAuth maFile or decryption failed.\nImport Failed.", "Account Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static ImportManifest ReadImportManifest(string manifestPath)
        {
            FileInfo manifestFile = new FileInfo(manifestPath);
            if (!manifestFile.Exists || (manifestFile.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The source manifest is unavailable or uses an unsupported link.");

            string contents = ReadTextWithLimit(manifestPath, MaximumImportFileSizeBytes);
            using (StringReader stringReader = new StringReader(contents))
            using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                ImportManifest manifest = JsonSerializer.Create(ImportJsonSettings).Deserialize<ImportManifest>(jsonReader);
                if (manifest == null || manifest.Entries == null || manifest.Entries.Count > MaximumImportManifestEntries)
                    throw new InvalidDataException("The source manifest contains an invalid account list.");
                return manifest;
            }
        }

        private static ImportManifestEntry ValidateImportManifest(ImportManifest manifest, string selectedFilename)
        {
            HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<ulong> steamIds = new HashSet<ulong>();
            ImportManifestEntry matchingEntry = null;
            foreach (ImportManifestEntry entry in manifest.Entries)
            {
                if (entry == null || !IsValidImportFilename(entry.Filename) || entry.SteamID == 0 ||
                    !filenames.Add(entry.Filename) || !steamIds.Add(entry.SteamID))
                    throw new InvalidDataException("The source manifest contains an invalid account entry.");

                bool hasSalt = !String.IsNullOrWhiteSpace(entry.Salt);
                bool hasIv = !String.IsNullOrWhiteSpace(entry.IV);
                if (manifest.Encrypted)
                {
                    if (!hasSalt || !hasIv || !IsBase64WithLength(entry.Salt, 8) || !IsBase64WithLength(entry.IV, 16))
                        throw new InvalidDataException("The encrypted source manifest contains invalid encryption metadata.");
                }
                else if (hasSalt || hasIv)
                {
                    throw new InvalidDataException("The unencrypted source manifest contains encryption metadata.");
                }

                if (String.Equals(entry.Filename, selectedFilename, StringComparison.OrdinalIgnoreCase))
                    matchingEntry = entry;
            }

            return matchingEntry;
        }

        private static string ReadTextWithLimit(string filename, long maximumBytes)
        {
            FileInfo fileInfo = new FileInfo(filename);
            if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The selected import file is unavailable or uses an unsupported link.");
            if (fileInfo.Length > maximumBytes)
                throw new InvalidDataException("The selected import file is larger than the supported size limit.");

            using (FileStream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (MemoryStream contents = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                long totalBytes = 0;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maximumBytes)
                        throw new InvalidDataException("The selected import file is larger than the supported size limit.");
                    contents.Write(buffer, 0, bytesRead);
                }

                return new UTF8Encoding(false, true).GetString(contents.ToArray());
            }
        }

        private static bool IsValidImportFilename(string filename)
        {
            return !String.IsNullOrWhiteSpace(filename) && filename.Length <= 255 &&
                filename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) &&
                filename.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBase64WithLength(string value, int expectedLength)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            try
            {
                return Convert.FromBase64String(value).Length == expectedLength;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void EnsureNoReparsePoints(string directoryPath)
        {
            DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(directoryPath));
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected import path uses an unsupported link.");

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        private static void ShowOwnedDialog(Form dialog, Form dialogOwner)
        {
            if (dialogOwner != null && dialogOwner.Visible && !dialogOwner.IsDisposed)
                dialog.ShowDialog(dialogOwner);
            else
                dialog.ShowDialog();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void Import_maFile_Form_FormClosing(object sender, FormClosingEventArgs e)
        {
        }
    }


    public class AppManifest
    {
        [JsonProperty("encrypted")]
        public bool Encrypted { get; set; }
    }


    public class ImportManifest
    {
        [JsonProperty("encrypted")]
        public bool Encrypted { get; set; }

        [JsonProperty("entries")]
        public List<ImportManifestEntry> Entries { get; set; }
    }

    public class ImportManifestEntry
    {
        [JsonProperty("encryption_iv")]
        public string IV { get; set; }

        [JsonProperty("encryption_salt")]
        public string Salt { get; set; }

        [JsonProperty("filename")]
        public string Filename { get; set; }

        [JsonProperty("steamid")]
        public ulong SteamID { get; set; }
    }
}
