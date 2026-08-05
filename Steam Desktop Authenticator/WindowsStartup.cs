using Microsoft.Win32;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    internal static class WindowsStartup
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled()
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(Application.ProductName) != null;
        }

        public static void SetEnabled(bool enabled)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (enabled)
                key.SetValue(Application.ProductName, "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue(Application.ProductName, false);
        }
    }
}
