using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using CommandLine;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SteamAuth;

namespace Steam_Desktop_Authenticator
{
    static class Program
    {
        private const string InstanceMutexName = @"Local\AstroSteamDesktopAuthenticator_0F37C513_9AF4_42C8_9CE9_F9B3BFA55E4E";
        private const int SW_RESTORE = 9;
        internal const int RestoreExistingInstanceMessage = 0x8000 + 0x5A;
        private static Mutex instanceMutex;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        public static Process PriorProcess()
        // Returns a System.Diagnostics.Process pointing to
        // a pre-existing process in the current Windows session
        // running the same executable path, or null if none is found.
        {
            try
            {
                using (Process curr = Process.GetCurrentProcess())
                {
                    string currentPath = GetCurrentExecutablePath(curr);
                    if (String.IsNullOrWhiteSpace(currentPath))
                        return null;

                    Process[] procs = Process.GetProcessesByName(curr.ProcessName);
                    return FindPriorProcess(
                        curr.Id,
                        curr.SessionId,
                        currentPath,
                        procs,
                        process => process.Id,
                        process => process.SessionId,
                        process => process.MainModule?.FileName);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string GetCurrentExecutablePath(Process currentProcess)
        {
            if (currentProcess != null)
            {
                try
                {
                    // Prefer MainModule.FileName over Environment.ProcessPath on .NET 8 Windows:
                    // it matches the candidate path reader (Process.MainModule.FileName) and avoids
                    // a .NET 8 Environment.ProcessPath buffer-growth hang on executable paths >= MAX_PATH.
                    string modulePath = currentProcess.MainModule?.FileName;
                    if (!String.IsNullOrWhiteSpace(modulePath))
                        return modulePath;
                }
                catch (Exception)
                {
                }
            }

            return Environment.ProcessPath;
        }

        internal static TProcess FindPriorProcess<TProcess>(
            int currentProcessId,
            string currentExecutablePath,
            IReadOnlyList<TProcess> candidates,
            Func<TProcess, int> getProcessId,
            Func<TProcess, string> getExecutablePath)
            where TProcess : class, IDisposable
        {
            return FindPriorProcess(
                currentProcessId,
                currentSessionId: 0,
                currentExecutablePath,
                candidates,
                getProcessId,
                getSessionId: _ => 0,
                getExecutablePath);
        }

        internal static TProcess FindPriorProcess<TProcess>(
            int currentProcessId,
            int currentSessionId,
            string currentExecutablePath,
            IReadOnlyList<TProcess> candidates,
            Func<TProcess, int> getProcessId,
            Func<TProcess, int> getSessionId,
            Func<TProcess, string> getExecutablePath)
            where TProcess : class, IDisposable
        {
            if (getProcessId == null)
                throw new ArgumentNullException(nameof(getProcessId));
            if (getSessionId == null)
                throw new ArgumentNullException(nameof(getSessionId));
            if (getExecutablePath == null)
                throw new ArgumentNullException(nameof(getExecutablePath));
            if (candidates == null || candidates.Count == 0)
                return null;

            string normalizedCurrentPath = NormalizeProcessPath(currentExecutablePath);
            TProcess match = null;
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    TProcess candidate = candidates[i];
                    if (candidate == null)
                        continue;

                    if (match == null && normalizedCurrentPath != null)
                    {
                        try
                        {
                            if (getProcessId(candidate) != currentProcessId &&
                                getSessionId(candidate) == currentSessionId)
                            {
                                string candidatePath = NormalizeProcessPath(getExecutablePath(candidate));
                                if (candidatePath != null &&
                                    String.Equals(candidatePath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    match = candidate;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Inaccessible (e.g., elevated) or exited processes must not abort lookup.
                        }
                    }

                    if (!ReferenceEquals(candidate, match))
                    {
                        DisposeQuietly(candidate);
                    }
                }

                return match;
            }
            catch (Exception)
            {
                // Clean up an already-retained match if indexing a custom IReadOnlyList throws.
                DisposeQuietly(match);
                throw;
            }

            static void DisposeQuietly(TProcess process)
            {
                if (process == null)
                    return;

                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        internal static string NormalizeProcessPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
                return null;

            string candidate = path;
            bool isExtendedVerbatim = candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase);
            if (candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                string uncRemainder = candidate.Substring(8);
                if (!IsValidUncRemainder(uncRemainder))
                    return null;
                if (isExtendedVerbatim && HasTrailingSpaceOrDotSegment(uncRemainder))
                    return candidate;
                candidate = @"\\" + uncRemainder;
            }
            else if (candidate.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                // .NET's Process.MainModule.FileName strips "\\?\" from "\\?\UNC\..."
                // leaving "UNC\server\share\...".
                string uncRemainder = candidate.Substring(4);
                if (!IsValidUncRemainder(uncRemainder))
                    return null;
                candidate = @"\\" + uncRemainder;
            }
            else if (isExtendedVerbatim ||
                candidate.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            {
                string deviceRemainder = candidate.Substring(4);
                if (IsDriveRootedPath(deviceRemainder))
                {
                    if (isExtendedVerbatim && HasTrailingSpaceOrDotSegment(deviceRemainder))
                        return candidate;
                    candidate = deviceRemainder;
                }
                else if (deviceRemainder.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) ||
                    deviceRemainder.StartsWith(@"GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve non-drive device/volume paths rather than stripping "\\?\"
                    // into an unrooted string that Path.GetFullPath would resolve against CWD.
                    return candidate;
                }
                else
                {
                    return null;
                }
            }

            if (String.IsNullOrWhiteSpace(candidate) ||
                Char.IsWhiteSpace(candidate[0]) ||
                !Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                return null;
            }

            static bool IsDriveRootedPath(string value)
            {
                return value.Length >= 3 &&
                    ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z')) &&
                    value[1] == ':' &&
                    (value[2] == '\\' || value[2] == '/');
            }

            static bool IsValidUncRemainder(string value)
            {
                return !String.IsNullOrWhiteSpace(value) &&
                    !Char.IsWhiteSpace(value[0]) &&
                    value[0] != '\\' &&
                    value[0] != '/';
            }

            static bool HasTrailingSpaceOrDotSegment(string value)
            {
                string[] segments = value.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segments.Length; i++)
                {
                    string segment = segments[i];
                    char last = segment[segment.Length - 1];
                    if (last == ' ' || (last == '.' && segment != "." && segment != ".."))
                        return true;
                }

                return false;
            }
        }

        private static void RestoreExistingInstance()
        {
            using (Process existing = PriorProcess())
            {
                if (existing == null)
                    return;

                // A pinned-taskbar launch can arrive while the first process is still
                // constructing its window, so wait briefly for a usable top-level handle.
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        existing.Refresh();
                        if (existing.HasExited)
                            return;

                        IntPtr handle = existing.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            PostMessage(handle, RestoreExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero);
                            ShowWindowAsync(handle, SW_RESTORE);
                            SetForegroundWindow(handle);
                            return;
                        }

                        if (PostRestoreMessageToHiddenWindow(existing.Id))
                            return;
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    Thread.Sleep(100);
                }
            }
        }

        private static bool PostRestoreMessageToHiddenWindow(int processId)
        {
            bool messagePosted = false;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                if (windowProcessId == processId &&
                    PostMessage(hWnd, RestoreExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero))
                {
                    messagePosted = true;
                }

                return true;
            }, IntPtr.Zero);

            return messagePosted;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            SteamAuthDiagnostics.ErrorLogger = (exception, context) => DiagnosticErrorLogger.Log("SteamAuth", exception, context);
            bool createdNew;
            instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            if (!createdNew)
            {
                // Release our handle to the existing named mutex immediately before waiting
                // on window restoration so we do not keep the mutex alive if the primary exits.
                instanceMutex.Dispose();
                instanceMutex = null;
                RestoreExistingInstance();
                return;
            }

            try
            {
            // Parse command line arguments
            CommandLineOptions options = new();
            Parser.Default.ParseArguments<CommandLineOptions>(args)
                .WithParsed(o => options = o);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (ApplicationPaths.TryGetMissingWebResource(out string missingResource))
            {
                AstroMessageBox.Show(
                    "Astro SDA cannot start because a required application file is missing:\n\n" +
                    missingResource +
                    "\n\nRestore the complete release folder and try again.",
                    "Astro Steam Desktop Assistant",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Manifest man;

            try
            {
                man = Manifest.GetManifest();
            }
            catch (ManifestRecoveryException)
            {
                AstroMessageBox.Show(
                    "Astro SDA could not safely recover its account data. No account data was loaded. Resolve any locked or unavailable account files, then restart the application.",
                    "Astro Steam Desktop Assistant",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
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
                    Process.Start(new ProcessStartInfo(@"https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/wiki/Help!-I'm-locked-out-of-my-account") { UseShellExecute = true });
                    return;
                }
            }

            DiagnosticErrorLogger.Configure(man.DiagnosticErrorLoggingEnabled);
            if (!ProxyService.ApplySavedConfiguration(man, out string proxyError))
            {
                AstroMessageBox.Show(
                    "Steam networking has been blocked because the saved proxy settings are invalid. Open Settings and correct or disable the proxy.\n\n" + proxyError,
                    "Proxy Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            Application.ThreadException += (sender, eventArgs) =>
                DiagnosticErrorLogger.Log("Windows Forms UI", eventArgs.Exception, "Unhandled UI-thread exception.");
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                    DiagnosticErrorLogger.Log("Application", exception, "Unhandled application exception.");
            };
            TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
                DiagnosticErrorLogger.Log("Task scheduler", eventArgs.Exception, "Unobserved task exception.");

            if (man.FirstRun)
            {
                try
                {
                    WindowsStartup.SetEnabled(true);
                }
                catch (Exception ex)
                {
                    DiagnosticErrorLogger.Log("Windows startup", ex, "Could not enable the default Start with Windows setting.");
                }
            }

            if (man.FirstRun && man.Entries.Count == 0)
            {
                // No accounts, run welcome form
                Application.Run(new WelcomeForm());
            }
            else
            {
                // Already has accounts, or not first run
                MainForm mf = new MainForm();
                mf.SetEncryptionKey(options.EncryptionKey);
                mf.StartSilent(options.Silent);
                Application.Run(mf);
            }
            }
            finally
            {
                try
                {
                    instanceMutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                finally
                {
                    instanceMutex?.Dispose();
                    instanceMutex = null;
                }
            }
        }
    }
}
