using System;
using System.Windows.Forms;
using System.Diagnostics;
using CommandLine;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    static class Program
    {
        private const string InstanceMutexName = @"Local\AstroSteamDesktopAuthenticator_0F37C513_9AF4_42C8_9CE9_F9B3BFA55E4E";
        private const int SW_RESTORE = 9;
        private static Mutex instanceMutex;

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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
                        ShowWindowAsync(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                        return;
                    }
                }
                catch (Exception)
                {
                    return;
                }

                Thread.Sleep(100);
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
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
