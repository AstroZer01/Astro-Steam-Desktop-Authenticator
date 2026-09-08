using System;
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

        internal StorageResult DisableStartupUpdateChecks(Manifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            StorageResult result = manifest.SaveSettingsWithResult(staged => staged.CheckForUpdates = false);
            if (result.Succeeded)
                RecordSuccessfulDisable();
            return result;
        }

        internal StorageResult SaveSettingsWithResult(
            Manifest manifest,
            bool requestedValue,
            long saveStartRevision,
            Action<Manifest> updateSettings)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (updateSettings == null)
                throw new ArgumentNullException(nameof(updateSettings));

            return manifest.SaveSettingsWithResult(staged =>
            {
                updateSettings(staged);
                staged.CheckForUpdates = MergeCheckForUpdatesPreference(
                    requestedValue,
                    staged.CheckForUpdates,
                    saveStartRevision);
            });
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
