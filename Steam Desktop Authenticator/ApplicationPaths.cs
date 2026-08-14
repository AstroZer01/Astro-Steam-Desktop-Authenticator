using System;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    internal static class ApplicationPaths
    {
        public const string DataDirectoryEnvironmentVariable = "ASDA_DATA_DIRECTORY";

        private static readonly string[] RequiredWebResourceNames = { "index.html", "login.html", "input.html" };

        public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        public static string DataDirectory => ResolveDataDirectory();

        public static string UiDirectory => Path.Combine(InstallDirectory, "app", "ui");

        public static string WebViewUserDataDirectory => Path.Combine(InstallDirectory, "app", "webview2");

        private static string ResolveDataDirectory()
        {
            string configuredDataDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (String.IsNullOrWhiteSpace(configuredDataDirectory))
            {
                return InstallDirectory;
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
