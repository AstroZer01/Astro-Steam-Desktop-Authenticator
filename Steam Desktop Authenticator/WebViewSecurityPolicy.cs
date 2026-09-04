using System;
using System.IO;

namespace Steam_Desktop_Authenticator
{
    internal static class WebViewSecurityPolicy
    {
        public static bool IsTrustedLocalDocument(string source, string expectedPath)
        {
            if (String.IsNullOrWhiteSpace(source) || String.IsNullOrWhiteSpace(expectedPath))
                return false;

            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri sourceUri) ||
                !sourceUri.IsFile ||
                !String.IsNullOrEmpty(sourceUri.Host) ||
                !String.IsNullOrEmpty(sourceUri.Query) ||
                !String.IsNullOrEmpty(sourceUri.Fragment))
                return false;

            try
            {
                string actualPath = Path.GetFullPath(sourceUri.LocalPath);
                string trustedPath = Path.GetFullPath(expectedPath);
                return String.Equals(actualPath, trustedPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is NotSupportedException)
            {
                return false;
            }
        }
    }
}
