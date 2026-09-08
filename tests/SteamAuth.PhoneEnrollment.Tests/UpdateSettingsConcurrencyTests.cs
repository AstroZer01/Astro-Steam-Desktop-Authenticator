using Steam_Desktop_Authenticator;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class UpdateSettingsConcurrencyTests
    {
        [Fact]
        public void ActivationAndStartupShareOneInitialUpdateCheckRegistration()
        {
            bool startupUpdateCheckStarted = false;
            int updateChecksStarted = 0;
            Action startCheck = () => updateChecksStarted++;

            Assert.True(MainForm.TryStartStartupUpdateCheck(ref startupUpdateCheckStarted, startCheck));
            Assert.False(MainForm.TryStartStartupUpdateCheck(ref startupUpdateCheckStarted, startCheck));
            Assert.Equal(1, updateChecksStarted);
        }

        [Fact]
        public void MergeCheckForUpdatesPreference_PreservesUpdaterDisableAfterActivationRevisionChanges()
        {
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();
            long saveStartRevision = tracker.CaptureForSettingsSave();
            tracker.RecordSuccessfulDisable();

            bool mergedValue = tracker.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false,
                saveStartRevision);

            Assert.False(mergedValue);
        }

        [Fact]
        public void MergeCheckForUpdatesPreference_AllowsLaterSettingsSaveWhenRevisionIsUnchanged()
        {
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();

            bool mergedValue = tracker.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false,
                tracker.CaptureForSettingsSave());

            Assert.True(mergedValue);
        }

        [Fact]
        public async Task SettingsSavePausedDuringProxyValidation_PreservesUpdaterDisable()
        {
            bool persistedCheckForUpdates = true;
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();
            TaskCompletionSource<bool> proxyValidationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseSettingsSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long saveStartRevision = tracker.CaptureForSettingsSave();

            Task<bool> settingsSave = CompleteSettingsSaveAfterProxyValidationAsync();
            await proxyValidationStarted.Task;

            Volatile.Write(ref persistedCheckForUpdates, false);
            tracker.RecordSuccessfulDisable();
            releaseSettingsSave.SetResult(true);

            Assert.False(await settingsSave);
            Assert.False(Volatile.Read(ref persistedCheckForUpdates));

            async Task<bool> CompleteSettingsSaveAfterProxyValidationAsync()
            {
                proxyValidationStarted.SetResult(true);
                await releaseSettingsSave.Task;

                bool mergedValue = tracker.MergeCheckForUpdatesPreference(
                    requestedValue: true,
                    currentValue: Volatile.Read(ref persistedCheckForUpdates),
                    saveStartRevision);
                Volatile.Write(ref persistedCheckForUpdates, mergedValue);
                return mergedValue;
            }
        }
    }
}
