using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public partial class WelcomeForm : Form
    {
        private Manifest man;

        public WelcomeForm()
        {
            InitializeComponent();
            AstroTheme.ApplyTheme(this);

            // Style buttons as primary
            AstroTheme.StylePrimaryButton(btnImportConfig);
            AstroTheme.StylePrimaryButton(btnJustStart);

            man = Manifest.GetManifest();
        }

        private void btnJustStart_Click(object sender, EventArgs e)
        {
            // Mark as not first run anymore
            man.FirstRun = false;
            man.Save();

            showMainForm();
        }

        private void btnImportConfig_Click(object sender, EventArgs e)
        {
            // Let the user select the config dir
            FolderBrowserDialog folderBrowser = new FolderBrowserDialog();
            folderBrowser.Description = "Select the folder of your old Astro Steam Desktop Assistant install";
            DialogResult userClickedOK = folderBrowser.ShowDialog();

            if (userClickedOK == DialogResult.OK)
            {
                string path = folderBrowser.SelectedPath;
                string pathToCopy = null;

                if (Directory.Exists(path + "/maFiles"))
                {
                    // User selected the root install dir
                    pathToCopy = path + "/maFiles";
                }
                else if (File.Exists(path + "/manifest.json"))
                {
                    // User selected the maFiles dir
                    pathToCopy = path;
                }
                else
                {
                    // Could not find either.
                    AstroMessageBox.Show("This folder does not contain either a manifest.json or an maFiles folder.\nPlease select the location where you had Astro Steam Desktop Assistant installed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Copy the contents of the config dir to the new config dir
                string currentPath = Manifest.GetExecutableDir();

                // Create config dir if we don't have it
                if (!Directory.Exists(currentPath + "/maFiles"))
                {
                    Directory.CreateDirectory(currentPath + "/maFiles");
                }

                // Copy all files from the old dir to the new one
                foreach (string newPath in Directory.GetFiles(pathToCopy, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(newPath, newPath.Replace(pathToCopy, currentPath + "/maFiles"), true);
                }

                // Set first run in manifest
                try
                {
                    man = Manifest.GetManifest(true);
                    man.FirstRun = false;
                    man.Save();
                }
                catch (ManifestParseException)
                {
                    // Manifest file was corrupted, generate a new one.
                    try
                    {
                        AstroMessageBox.Show("Your settings were unexpectedly corrupted and were reset to defaults.", "Astro Steam Desktop Assistant", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        man = Manifest.GenerateNewManifest(true);
                    }
                    catch (MaFileEncryptedException)
                    {
                        // An maFile was encrypted, we're fucked.
                        AstroMessageBox.Show("Sorry, but Astro was unable to recover your accounts since you used encryption.\nYou'll need to recover your Steam accounts by removing the authenticator.\nClick OK to view instructions.", "Astro Steam Desktop Assistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/wiki/Help!-I'm-locked-out-of-my-account") { UseShellExecute = true });
                        this.Close();
                        return;
                    }
                }

                // All done!
                AstroMessageBox.Show("All accounts and settings have been imported! Click OK to continue.", "Import accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
                showMainForm();
            }

        }

        private void showMainForm()
        {
            this.Hide();
            new MainForm().Show();
        }
    }
}
