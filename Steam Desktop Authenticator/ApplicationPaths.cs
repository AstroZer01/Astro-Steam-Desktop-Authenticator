using System;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    internal static class ApplicationPaths
    {
        public const string DataDirectoryEnvironmentVariable = "ASDA_DATA_DIRECTORY";

        private static readonly string[] RequiredWebResourceNames =
        {
            "index.html",
            "login.html",
            "input.html",
            Path.Combine("assets", "css", "app.css"),
            Path.Combine("assets", "fonts", "inter-latin.woff2"),
            Path.Combine("assets", "fonts", "jetbrains-mono-latin.woff2"),
            Path.Combine("assets", "icons", "check.svg")
        };

        public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        public static string DataDirectory => ResolveDataDirectory();

        public static string UiDirectory => Path.Combine(InstallDirectory, "app", "ui");

        public static string WebViewUserDataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Astro Steam Desktop Authenticator",
            "WebView2");

        private static string ResolveDataDirectory()
        {
            string configuredDataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (String.IsNullOrWhiteSpace(configuredDataDirectory))
            {
                return ResolvePortableDataDirectory() ?? InstallDirectory;
            }

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDataDirectory));
            }
            catch (Exception)
            {
                return InstallDirectory;
            }
        }

        private static string ResolvePortableDataDirectory()
        {
            DirectoryInfo installDirectory = new DirectoryInfo(InstallDirectory);
            DirectoryInfo portableRoot = installDirectory.Parent;
            if (portableRoot == null || !String.Equals(installDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase))
                return null;

            string processPath = Environment.ProcessPath;
            if (String.IsNullOrWhiteSpace(processPath))
                return null;

            string executableName = Path.GetFileName(processPath);
            if (String.IsNullOrWhiteSpace(executableName))
                return null;
            string launcherPath = Path.Combine(portableRoot.FullName, executableName);
            string uiDirectory = Path.Combine(installDirectory.FullName, "app", "ui");
            if (!File.Exists(launcherPath) || !Directory.Exists(uiDirectory))
                return null;

            return portableRoot.FullName;
        }

        public static bool TryGetMissingWebResource(out string missingResource)
        {
            foreach (string resourceName in RequiredWebResourceNames)
            {
                string resourcePath = Path.Combine(UiDirectory, resourceName);
                if (!File.Exists(resourcePath))
                {
                    missingResource = resourcePath;
                    return true;
                }
            }

            missingResource = null;
            return false;
        }
    }
}
