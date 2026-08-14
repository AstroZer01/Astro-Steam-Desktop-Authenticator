using System;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    internal static class ApplicationPaths
    {
        private static readonly string[] RequiredWebResourceNames = { "index.html", "login.html", "input.html" };

        public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        public static string UiDirectory => Path.Combine(InstallDirectory, "app", "ui");

        public static string WebViewUserDataDirectory => Path.Combine(InstallDirectory, "app", "webview2");

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
