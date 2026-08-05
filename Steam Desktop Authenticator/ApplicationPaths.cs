using System;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    internal static class ApplicationPaths
    {
        private static readonly string[] RequiredWebResourceNames = { "index.html", "login.html", "input.html" };

        public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        public static string WebRootDirectory => Path.Combine(InstallDirectory, "app", "wwwroot");

        public static string WebViewUserDataDirectory => Path.Combine(InstallDirectory, "app", "webview2");

        public static bool TryGetMissingWebResource(out string missingResource)
        {
            foreach (string resourceName in RequiredWebResourceNames)
            {
                string resourcePath = Path.Combine(WebRootDirectory, resourceName);
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
