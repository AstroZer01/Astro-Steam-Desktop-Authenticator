using System.Diagnostics;
using System.Windows.Forms;

const string DataDirectoryEnvironmentVariable = "ASDA_DATA_DIRECTORY";
string binPath = Path.Combine(AppContext.BaseDirectory, "bin", "Steam Desktop Authenticator.exe");

if (File.Exists(binPath))
{
    var processInfo = new ProcessStartInfo
    {
        FileName = binPath,
        WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "bin"),
        UseShellExecute = false
    };
    processInfo.Environment[DataDirectoryEnvironmentVariable] = AppContext.BaseDirectory;

    // Pass along any arguments
    foreach (var arg in args)
    {
        processInfo.ArgumentList.Add(arg);
    }

    try
    {
        using Process process = Process.Start(processInfo)
            ?? throw new InvalidOperationException("Windows did not create the application process.");
    }
    catch (Exception ex)
    {
        Environment.ExitCode = 1;
        MessageBox.Show(
            "Astro Steam Desktop Assistant could not be started.\n\n" + ex.Message,
            "Astro Steam Desktop Assistant",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
else
{
    Environment.ExitCode = 1;
    MessageBox.Show(
        "Astro Steam Desktop Assistant is incomplete. The main application executable is missing:\n\n" + binPath,
        "Astro Steam Desktop Assistant",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
