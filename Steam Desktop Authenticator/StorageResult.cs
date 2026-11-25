using System;

namespace Steam_Desktop_Authenticator
{
    public enum StorageFailureKind
    {
        None,
        Validation,
        Encryption,
        Serialization,
        Manifest,
        Io,
        Recovery
    }

    /// <summary>
    /// The outcome of a durable authenticator-storage operation.  User-facing text is
    /// deliberately generic so it never exposes account data, session tokens, or keys.
    /// </summary>
    public sealed class StorageResult
    {
        private StorageResult(
            bool succeeded,
            StorageFailureKind failureKind,
            string userMessage,
            Exception exception,
            bool obsoleteCleanupPending)
        {
            Succeeded = succeeded;
            FailureKind = failureKind;
            UserMessage = userMessage;
            Exception = exception;
            ObsoleteCleanupPending = obsoleteCleanupPending;
        }

        public bool Succeeded { get; }
        public StorageFailureKind FailureKind { get; }
        public string UserMessage { get; }
        public bool ObsoleteCleanupPending { get; }
        internal Exception Exception { get; }

        public static StorageResult Success(bool obsoleteCleanupPending = false)
        {
            return new StorageResult(true, StorageFailureKind.None, null, null, obsoleteCleanupPending);
        }

        public static StorageResult Failure(StorageFailureKind failureKind, string userMessage, Exception exception = null)
        {
            return new StorageResult(false, failureKind, userMessage, exception, false);
        }
    }
}
