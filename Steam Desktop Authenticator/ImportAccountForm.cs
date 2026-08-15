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
                string fullPath = openFileDialog1.FileName;
                string fileContents = File.ReadAllText(fullPath);
                SteamGuardAccount maFile = null;
                bool isEncrypted = false;
                string salt = null;
                string iv = null;

                // Check if the source manifest exists to see if it's encrypted
                string path = fullPath.Replace(openFileDialog1.SafeFileName, "");
                string manifestPath = path + "manifest.json";
                
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        string manifestContents = File.ReadAllText(manifestPath);
                        ImportManifest account = JsonConvert.DeserializeObject<ImportManifest>(manifestContents);
                        
                        if (account != null && account.Entries != null)
                        {
                            foreach (var entry in account.Entries)
                            {
                                if (entry.Filename == openFileDialog1.SafeFileName)
                                {
                                    if (!string.IsNullOrEmpty(entry.Salt) && !string.IsNullOrEmpty(entry.IV))
                                    {
                                        isEncrypted = true;
                                        salt = entry.Salt;
                                        iv = entry.IV;
                                    }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Account import", ex, "The source manifest could not be read; the selected maFile will be imported without its source encryption metadata.");
                    }
                }

                if (isEncrypted)
                {
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
                            ShowOwnedDialog(passKeyForm, dialogOwner);
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

                maFile = JsonConvert.DeserializeObject<SteamGuardAccount>(fileContents);
                if (maFile == null) throw new InvalidDataException("The selected file did not contain a Steam Guard account.");

                if (maFile.Session == null || maFile.Session.SteamID == 0 || maFile.Session.IsAccessTokenExpired())
                {
                    using (LoginForm loginForm = new LoginForm(LoginForm.LoginType.Import, maFile))
                    {
                        ShowOwnedDialog(loginForm, dialogOwner);
                        if (loginForm.Session == null || loginForm.Session.SteamID == 0)
                        {
                            MessageBox.Show("Login failed. Try to import this account again.", "Account Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
