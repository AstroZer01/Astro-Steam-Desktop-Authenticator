using System;
using System.Windows.Forms;
using System.Diagnostics;
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
        // a pre-existing process with the same name as the
        // current one, if any; or null if the current process
        // is unique.
        {
            try
            {
                Process curr = Process.GetCurrentProcess();
                Process[] procs = Process.GetProcessesByName(curr.ProcessName);
                foreach (Process p in procs)
                {
                    if ((p.Id != curr.Id) &&
                        (p.MainModule.FileName == curr.MainModule.FileName))
                        return p;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RestoreExistingInstance()
        {
            Process existing = PriorProcess();
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
                RestoreExistingInstance();
                return;
            }

            try
            {
            // Force TLS 1.2 for Steam API compatibility
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

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
                instanceMutex.ReleaseMutex();
                instanceMutex.Dispose();
            }
        }
    }
}
