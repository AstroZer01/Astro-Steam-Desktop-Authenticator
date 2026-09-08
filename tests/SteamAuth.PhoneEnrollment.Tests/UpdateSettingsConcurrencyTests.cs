using Steam_Desktop_Authenticator;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class UpdateSettingsConcurrencyTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-update-settings-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public UpdateSettingsConcurrencyTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

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
            UpdatePreferenceRevisionTracker.SettingsSaveOperation settingsSave = tracker.BeginSettingsSave();
            tracker.RecordSuccessfulDisable();

            bool mergedValue = settingsSave.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false);

            Assert.False(mergedValue);
        }

        [Fact]
        public void MergeCheckForUpdatesPreference_AllowsLaterSettingsSaveWhenRevisionIsUnchanged()
        {
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();
            UpdatePreferenceRevisionTracker.SettingsSaveOperation settingsSave = tracker.BeginSettingsSave();

            bool mergedValue = settingsSave.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false);

            Assert.True(mergedValue);
        }

        [Fact]
        public async Task SettingsSavePausedDuringProxyValidation_PreservesUpdaterDisable()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();
            UpdatePreferenceRevisionTracker.SettingsSaveOperation settingsSaveOperation = tracker.BeginSettingsSave();
            TaskCompletionSource<bool> proxyValidationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseSettingsSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<StorageResult> settingsSave = CompleteSettingsSaveAfterProxyValidationAsync();
            await proxyValidationStarted.Task;

            StorageResult disableResult = tracker.DisableStartupUpdateChecks(manifest);
            Assert.True(disableResult.Succeeded, disableResult.UserMessage);
            Assert.False(manifest.CheckForUpdates);
            releaseSettingsSave.SetResult(true);

            StorageResult settingsResult = await settingsSave;
            Assert.True(settingsResult.Succeeded, settingsResult.UserMessage);
            Assert.False(manifest.CheckForUpdates);
            Assert.False(Manifest.GetManifest(true).CheckForUpdates);

            async Task<StorageResult> CompleteSettingsSaveAfterProxyValidationAsync()
            {
                proxyValidationStarted.SetResult(true);
                await releaseSettingsSave.Task;

                return settingsSaveOperation.SaveSettingsWithResult(
                    manifest,
                    requestedValue: true,
                    _ => { });
            }
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
