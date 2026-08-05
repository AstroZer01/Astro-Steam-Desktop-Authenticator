using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Opt-in diagnostic logging. Redaction happens here so callers cannot accidentally
    /// write Steam credentials, cookies, IP addresses, or local file details to disk.
    /// </summary>
    internal static class DiagnosticErrorLogger
    {
        private const string LogFolderName = "Astro Steam Desktop Authenticator\\Logs";
        private const int RetentionDays = 14;
        private const long MaximumLogFileSizeBytes = 2 * 1024 * 1024;
        private static readonly object writeLock = new object();
        private static bool enabled;

        private static readonly Regex SensitiveKeyValue = new Regex(
            @"(?ix)([""']?\b(access[_-]?token|refresh[_-]?token|token|cookie|sessionid|steamloginsecure|authorization|auth|shared[_-]?secret|identity[_-]?secret|revocation[_-]?code|password|passkey|mafile|steamid|account[_-]?name|device[_-]?id)[""']?\s*[:=]\s*[""']?)([^\s,;&""']+)",
            RegexOptions.Compiled);
        private static readonly Regex Ipv4Address = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex Ipv6Address = new Regex(@"(?i)\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{0,4}\b", RegexOptions.Compiled);
        private static readonly Regex WindowsPath = new Regex(@"(?i)\b[a-z]:\\(?:[^\r\n""']*)", RegexOptions.Compiled);
        private static readonly Regex BearerCredential = new Regex(@"(?i)\b(Bearer)\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.Compiled);

        public static void Configure(bool isEnabled)
        {
            enabled = isEnabled;
        }

        public static void Log(string component, Exception exception, string context = null)
        {
            if (!enabled || exception == null)
                return;

            try
            {
                string entry = "[" + DateTime.UtcNow.ToString("O") + "] " +
                    "Component=" + Redact(component) + Environment.NewLine +
                    "Context=" + Redact(context ?? "None") + Environment.NewLine +
                    "Exception=" + Redact(exception.ToString()) + Environment.NewLine + Environment.NewLine;

                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LogFolderName);

                lock (writeLock)
                {
                    Directory.CreateDirectory(directory);
                    DeleteExpiredLogs(directory);
                    string filename = GetCurrentLogFilename(directory);
                    if (filename == null)
                        return;
                    File.AppendAllText(filename, entry, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never interfere with the authentication application.
            }
        }

        private static string Redact(string value)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;

            string redacted = BearerCredential.Replace(value, "$1 [REDACTED]");
            redacted = SensitiveKeyValue.Replace(redacted, match => match.Groups[1].Value + "[REDACTED]");
            redacted = Ipv4Address.Replace(redacted, "[REDACTED_IP]");
            redacted = Ipv6Address.Replace(redacted, "[REDACTED_IP]");
            return WindowsPath.Replace(redacted, "[REDACTED_PATH]");
        }

        private static void DeleteExpiredLogs(string directory)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string filename in Directory.GetFiles(directory, "errors-*.log"))
            {
                if (File.GetLastWriteTimeUtc(filename) < cutoff)
                    File.Delete(filename);
            }
        }

        private static string GetCurrentLogFilename(string directory)
        {
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            for (int part = 1; part <= 10; part++)
            {
                string suffix = part == 1 ? String.Empty : "-" + part;
                string filename = Path.Combine(directory, "errors-" + date + suffix + ".log");
                if (!File.Exists(filename) || new FileInfo(filename).Length < MaximumLogFileSizeBytes)
                    return filename;
            }

            return null;
        }
    }
}
