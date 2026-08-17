using System;
using System.Diagnostics;

namespace SteamAuth
{
    /// <summary>
    /// Provides the host application with a safe hook for operational SteamAuth errors.
    /// </summary>
    public static class SteamAuthDiagnostics
    {
        public static Action<Exception, string> ErrorLogger { get; set; }

        public static void Log(Exception exception, string context)
        {
            if (exception == null)
                return;

            Action<Exception, string> logger = ErrorLogger;
            if (logger != null)
            {
                try
                {
                    logger(exception, context);
                }
                catch
                {
                    // Diagnostic logging must not affect authenticator operations.
                }
            }
            else
            {
                Trace.TraceError("SteamAuth: " + context + " " + exception.Message);
            }
        }
    }
}
