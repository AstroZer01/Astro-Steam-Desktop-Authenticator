using System.Threading;

namespace Steam_Desktop_Authenticator
{
    internal sealed class UpdatePreferenceRevisionTracker
    {
        private long revision;

        internal long CaptureForSettingsSave()
        {
            return Volatile.Read(ref revision);
        }

        internal void RecordSuccessfulDisable()
        {
            Interlocked.Increment(ref revision);
        }

        internal bool MergeCheckForUpdatesPreference(
            bool requestedValue,
            bool currentValue,
            long saveStartRevision)
        {
            return saveStartRevision == Volatile.Read(ref revision) ? requestedValue : currentValue;
        }
    }
}
