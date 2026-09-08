using System;
using System.Threading;

namespace Steam_Desktop_Authenticator
{
    internal sealed class UpdatePreferenceRevisionTracker
    {
        private long revision;

        internal SettingsSaveOperation BeginSettingsSave()
        {
            return new SettingsSaveOperation(this, Volatile.Read(ref revision));
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

        private StorageResult SaveSettingsWithResult(
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

        internal sealed class SettingsSaveOperation
        {
            private readonly UpdatePreferenceRevisionTracker tracker;
            private readonly long saveStartRevision;

            internal SettingsSaveOperation(UpdatePreferenceRevisionTracker tracker, long saveStartRevision)
            {
                this.tracker = tracker;
                this.saveStartRevision = saveStartRevision;
            }

            internal StorageResult SaveSettingsWithResult(
                Manifest manifest,
                bool requestedValue,
                Action<Manifest> updateSettings)
            {
                return tracker.SaveSettingsWithResult(manifest, requestedValue, saveStartRevision, updateSettings);
            }

            internal bool MergeCheckForUpdatesPreference(bool requestedValue, bool currentValue)
            {
                return tracker.MergeCheckForUpdatesPreference(requestedValue, currentValue, saveStartRevision);
            }
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
