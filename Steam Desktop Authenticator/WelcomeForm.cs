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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    public partial class WelcomeForm : Form
    {
        private const long MaxImportFileSizeBytes = 4 * 1024 * 1024;
        private const long MaxImportTotalSizeBytes = 32 * 1024 * 1024;
        private const int MaxImportedAccounts = 1000;
        private static readonly JsonSerializerSettings ImportJsonSettings = new JsonSerializerSettings
        {
            MaxDepth = 32,
            DateParseHandling = DateParseHandling.None
        };
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
            StorageResult saveResult = man.SaveWithResult();
            if (!saveResult.Succeeded)
            {
                DiagnosticErrorLogger.Log("Application startup", saveResult.Exception, "The first-run manifest could not be saved.");
                AstroMessageBox.Show(
                    saveResult.UserMessage ?? "Unable to save application settings. Check that the data folder is writable.",
                    "Astro Steam Desktop Assistant",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            showMainForm();
        }

        private void btnImportConfig_Click(object sender, EventArgs e)
        {
            string selectedPath;
            using (FolderBrowserDialog folderBrowser = new FolderBrowserDialog())
            {
                folderBrowser.Description = "Select the folder of your old Astro Steam Desktop Assistant install";
                if (folderBrowser.ShowDialog() != DialogResult.OK)
                    return;

                selectedPath = folderBrowser.SelectedPath;
            }

            string pathToCopy = ResolveImportDirectory(selectedPath);
            if (pathToCopy == null)
            {
                AstroMessageBox.Show("This folder does not contain either a manifest.json or an maFiles folder.\nPlease select the location where you had Astro Steam Desktop Assistant installed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string dataDirectory = Path.GetFullPath(Manifest.GetExecutableDir());
            string destinationDirectory = Path.Combine(dataDirectory, "maFiles");
            string stagingDirectory = Path.Combine(dataDirectory, ".maFiles-import-" + Guid.NewGuid().ToString("N"));
            string backupDirectory = null;
            bool importCommitted = false;
            bool importSucceeded = false;

            try
            {
                StageImport(pathToCopy, stagingDirectory);
                backupDirectory = CommitStagedImport(stagingDirectory, destinationDirectory);
                importCommitted = true;

                man = Manifest.GetManifest(true);
                if (man == null)
                    throw new InvalidDataException("The imported configuration could not be loaded.");

                man.FirstRun = false;
                StorageResult saveResult = man.SaveWithResult();
                if (!saveResult.Succeeded)
                {
                    throw new IOException(saveResult.UserMessage ?? "The imported configuration could not be saved.", saveResult.Exception);
                }

                importSucceeded = true;
                if (backupDirectory != null && Directory.Exists(backupDirectory))
                {
                    try
                    {
                        Directory.Delete(backupDirectory, true);
                        backupDirectory = null;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticErrorLogger.Log("Configuration import", ex, "The previous empty configuration could not be cleaned up after import.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (importCommitted && !importSucceeded)
                    RollbackStagedImport(destinationDirectory, backupDirectory);

                DiagnosticErrorLogger.Log("Configuration import", ex, "The selected Astro configuration could not be imported.");
                AstroMessageBox.Show(
                    "The selected configuration could not be imported. Existing account data should be preserved; the failed import was quarantined when possible.\n\n" + ex.Message,
                    "Import accounts",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    try { Directory.Delete(stagingDirectory, true); } catch { }
                }
            }

            // All done!
            AstroMessageBox.Show("All accounts and settings have been imported! Click OK to continue.", "Import accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
            showMainForm();

        }

        private static string ResolveImportDirectory(string selectedPath)
        {
            if (String.IsNullOrWhiteSpace(selectedPath))
                return null;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(selectedPath);
            }
            catch (Exception)
            {
                return null;
            }

            string rootMaFiles = Path.Combine(fullPath, "maFiles");
            if (Directory.Exists(rootMaFiles) && File.Exists(Path.Combine(rootMaFiles, "manifest.json")))
                return rootMaFiles;

            return File.Exists(Path.Combine(fullPath, "manifest.json")) ? fullPath : null;
        }

        private static void StageImport(string sourceDirectory, string stagingDirectory)
        {
            string sourcePath = Path.GetFullPath(sourceDirectory);
            string stagingPath = Path.GetFullPath(stagingDirectory);
            DirectoryInfo sourceInfo = new DirectoryInfo(sourcePath);
            if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The selected configuration directory is unavailable or uses an unsupported link.");
            EnsureNoReparsePoints(sourcePath);
            if (String.Equals(sourcePath, stagingPath, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(stagingPath) || Directory.Exists(stagingPath))
                throw new InvalidOperationException("The import staging directory is already in use.");

            string stagingParent = Path.GetDirectoryName(stagingPath);
            if (String.IsNullOrWhiteSpace(stagingParent) || !IsContainedPath(stagingParent, stagingPath))
                throw new InvalidDataException("The import staging path is invalid.");
            EnsureNoReparsePoints(stagingParent);

            if (Directory.EnumerateDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidDataException("The selected maFiles directory must contain only its configuration files, not nested directories.");

            Directory.CreateDirectory(stagingPath);
            long totalBytes = 0;
            int importedAccountFileCount = 0;
            foreach (string sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
            {
                FileInfo fileInfo = new FileInfo(sourceFile);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The selected configuration contains an unsupported linked file.");

                string filename = fileInfo.Name;
                bool isManifest = String.Equals(filename, "manifest.json", StringComparison.OrdinalIgnoreCase);
                bool isMaFile = filename.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase);
                if (!isManifest && !isMaFile)
                    continue;
                if (isManifest && !String.Equals(filename, "manifest.json", StringComparison.Ordinal))
                    throw new InvalidDataException("The selected configuration contains more than one manifest filename variant.");
                if (isMaFile && ++importedAccountFileCount > MaxImportedAccounts)
                    throw new InvalidDataException("The selected configuration contains too many account files.");

                if (fileInfo.Length > MaxImportFileSizeBytes ||
                    totalBytes > MaxImportTotalSizeBytes - fileInfo.Length)
                    throw new InvalidDataException("The selected configuration is larger than the supported import limit.");

                string destinationFile = Path.Combine(stagingPath, filename);
                if (!IsContainedPath(stagingPath, destinationFile))
                    throw new InvalidDataException("The selected configuration contains an invalid filename.");

                File.Copy(sourceFile, destinationFile, false);
                long copiedBytes = new FileInfo(destinationFile).Length;
                if (copiedBytes > MaxImportFileSizeBytes || totalBytes > MaxImportTotalSizeBytes - copiedBytes)
                    throw new InvalidDataException("The selected configuration is larger than the supported import limit.");
                totalBytes += copiedBytes;
            }

            string stagedManifestPath = Path.Combine(stagingPath, "manifest.json");
            if (!File.Exists(stagedManifestPath))
                throw new InvalidDataException("The selected configuration does not contain manifest.json.");

            ValidateStagedManifest(stagedManifestPath, stagingPath);
        }

        private static void ValidateStagedManifest(string manifestPath, string stagingDirectory)
        {
            if (new FileInfo(manifestPath).Length > MaxImportFileSizeBytes)
                throw new InvalidDataException("The imported manifest is larger than the supported size limit.");

            JObject manifestJson;
            using (StringReader stringReader = new StringReader(ReadTextWithLimit(manifestPath, MaxImportFileSizeBytes)))
            using (JsonTextReader jsonReader = new JsonTextReader(stringReader) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                manifestJson = JObject.Load(jsonReader);
            }
            JArray entries = manifestJson["entries"] as JArray;
            JToken encryptedToken = manifestJson["encrypted"];
            if (entries == null || entries.Count > MaxImportedAccounts || encryptedToken?.Type != JTokenType.Boolean)
                throw new InvalidDataException("The imported manifest contains an invalid account list.");

            bool encrypted = encryptedToken.Value<bool>();
            HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<ulong> steamIds = new HashSet<ulong>();
            foreach (JToken entryToken in entries)
            {
                JObject entry = entryToken as JObject;
                string filename = entry?.Value<string>("filename");
                JToken steamIdToken = entry?["steamid"];
                ulong steamId = steamIdToken?.Type == JTokenType.Integer ? steamIdToken.Value<ulong>() : 0;
                if (entry == null || entry["filename"]?.Type != JTokenType.String ||
                    steamIdToken?.Type != JTokenType.Integer || steamId == 0 || String.IsNullOrWhiteSpace(filename) ||
                    filename.Length > 255 || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !String.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal) ||
                    !filename.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase) ||
                    !filenames.Add(filename) ||
                    !steamIds.Add(steamId))
                    throw new InvalidDataException("The imported manifest contains an invalid account entry.");

                string accountPath = Path.Combine(stagingDirectory, filename);
                if (!IsContainedPath(stagingDirectory, accountPath) || !File.Exists(accountPath))
                    throw new InvalidDataException("The imported manifest references a missing account file.");

                if (encrypted)
                {
                    if (entry["encryption_salt"]?.Type != JTokenType.String ||
                        entry["encryption_iv"]?.Type != JTokenType.String)
                        throw new InvalidDataException("The imported encrypted manifest is missing encryption metadata.");
                    string salt = entry.Value<string>("encryption_salt");
                    string iv = entry.Value<string>("encryption_iv");
                    if (!IsBase64WithLength(salt, 8) || !IsBase64WithLength(iv, 16))
                        throw new InvalidDataException("The imported encrypted manifest is missing encryption metadata.");

                    string encryptedContents = ReadTextWithLimit(accountPath, MaxImportFileSizeBytes);
                    try
                    {
                        if (Convert.FromBase64String(encryptedContents).Length == 0)
                            throw new InvalidDataException("An imported encrypted account file is empty.");
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidDataException("An imported encrypted account file is not valid base64.", ex);
                    }
                    continue;
                }

                if ((entry["encryption_salt"] != null && entry["encryption_salt"].Type != JTokenType.Null) ||
                    (entry["encryption_iv"] != null && entry["encryption_iv"].Type != JTokenType.Null))
                    throw new InvalidDataException("The imported unencrypted manifest contains encryption metadata.");

                SteamGuardAccount account = JsonConvert.DeserializeObject<SteamGuardAccount>(
                    ReadTextWithLimit(accountPath, MaxImportFileSizeBytes),
                    ImportJsonSettings);
                if (account?.Session == null || account.Session.SteamID == 0 || account.Session.SteamID != steamId)
                    throw new InvalidDataException("An imported account file does not match its manifest entry.");
            }

            foreach (string accountFile in Directory.EnumerateFiles(stagingDirectory, "*.maFile", SearchOption.TopDirectoryOnly))
            {
                if (!filenames.Contains(Path.GetFileName(accountFile)))
                    throw new InvalidDataException("The imported configuration contains an account file that is not listed in its manifest.");
            }
        }

        private static string CommitStagedImport(string stagingDirectory, string destinationDirectory)
        {
            string destinationPath = Path.GetFullPath(destinationDirectory);
            string parentDirectory = Path.GetDirectoryName(destinationPath);
            Directory.CreateDirectory(parentDirectory);
            EnsureNoReparsePoints(parentDirectory);
            DirectoryInfo parentInfo = new DirectoryInfo(parentDirectory);
            if ((parentInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The account-data parent directory uses an unsupported link.");

            string backupDirectory = null;
            if (Directory.Exists(destinationPath))
            {
                DirectoryInfo destinationInfo = new DirectoryInfo(destinationPath);
                if ((destinationInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The current account-data directory uses an unsupported link.");

                if (Directory.EnumerateDirectories(destinationPath, "*", SearchOption.TopDirectoryOnly).Any() ||
                    Directory.EnumerateFiles(destinationPath, "*", SearchOption.TopDirectoryOnly)
                        .Any(file => !String.Equals(Path.GetFileName(file), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                            (new FileInfo(file).Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidOperationException("Existing account data must be moved or backed up before importing another configuration.");

                backupDirectory = Path.Combine(parentDirectory, ".maFiles-before-import-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destinationPath, backupDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, destinationPath);
                return backupDirectory;
            }
            catch
            {
                if (backupDirectory != null && Directory.Exists(backupDirectory) && !Directory.Exists(destinationPath))
                    Directory.Move(backupDirectory, destinationPath);
                throw;
            }
        }

        private static void RollbackStagedImport(string destinationDirectory, string backupDirectory)
        {
            try
            {
                string destinationPath = Path.GetFullPath(destinationDirectory);
                if (Directory.Exists(destinationPath))
                {
                    string quarantineDirectory = Path.Combine(
                        Path.GetDirectoryName(destinationPath),
                        ".maFiles-import-failed-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(destinationPath, quarantineDirectory);
                }

                if (backupDirectory != null && Directory.Exists(backupDirectory) && !Directory.Exists(destinationPath))
                    Directory.Move(backupDirectory, destinationPath);
            }
            catch (Exception ex)
            {
                DiagnosticErrorLogger.Log("Configuration import", ex, "The failed configuration import could not be fully rolled back.");
            }
        }

        private static bool IsContainedPath(string rootDirectory, string candidatePath)
        {
            string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureNoReparsePoints(string directoryPath)
        {
            DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(directoryPath));
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The import path uses an unsupported link.");

                DirectoryInfo parent = current.Parent;
                if (parent == null || String.Equals(parent.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        private static string ReadTextWithLimit(string filename, long maximumBytes)
        {
            FileInfo fileInfo = new FileInfo(filename);
            if (!fileInfo.Exists || (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An imported configuration file is unavailable or uses an unsupported link.");
            if (fileInfo.Length > maximumBytes)
                throw new InvalidDataException("An imported configuration file is larger than the supported size limit.");

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
                        throw new InvalidDataException("An imported configuration file is larger than the supported size limit.");
                    contents.Write(buffer, 0, bytesRead);
                }

                return new UTF8Encoding(false, true).GetString(contents.ToArray());
            }
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

        private void showMainForm()
        {
            if (!ProxyService.ApplySavedConfiguration(man, out string proxyError))
            {
                AstroMessageBox.Show(
                    "Steam networking has been blocked because the saved proxy settings are invalid. Open Settings and correct or disable the proxy.\n\n" + proxyError,
                    "Proxy Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            this.Hide();
            new MainForm().Show();
        }
    }
}
