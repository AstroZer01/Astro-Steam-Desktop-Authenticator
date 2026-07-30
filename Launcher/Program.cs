using System.Diagnostics;

string binPath = Path.Combine(AppContext.BaseDirectory, "bin", "Steam Desktop Authenticator.exe");

if (File.Exists(binPath))
{
    var processInfo = new ProcessStartInfo
    {
        FileName = binPath,
        WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "bin"),
        UseShellExecute = false
    };

    // Pass along any arguments
    foreach (var arg in args)
    {
        processInfo.ArgumentList.Add(arg);
    }

    try
    {
        Process.Start(processInfo);
    }
    catch (Exception ex)
    {
        // Ignore or log error
    }
}
else
{
    // The bin folder or executable is missing.
    // In a real app we might show a MessageBox, but since it's a console/WinExe without WinForms references, we just exit.
}
