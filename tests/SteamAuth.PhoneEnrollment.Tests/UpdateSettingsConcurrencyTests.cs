using Steam_Desktop_Authenticator;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading;
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
            TaskCompletionSource<bool> proxyValidationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseSettingsSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (MainForm form = new MainForm(
                manifest,
                tracker,
                async (_, __) =>
                {
                    proxyValidationStarted.SetResult(true);
                    await releaseSettingsSave.Task;
                    return new ProxyTestResult { Succeeded = true, Message = "Proxy test succeeded." };
                },
                _ => { }))
            {
                Task settingsSave = form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: true,
                    proxyEnabled: true,
                    autoConfirmMarket: true));
                await proxyValidationStarted.Task;

                StorageResult disableResult = tracker.DisableStartupUpdateChecks(manifest);
                Assert.True(disableResult.Succeeded, disableResult.UserMessage);
                Assert.False(manifest.CheckForUpdates);
                releaseSettingsSave.SetResult(true);

                await settingsSave;
            }

            Assert.False(manifest.CheckForUpdates);
            Assert.True(manifest.AutoConfirmMarketTransactions);
            Assert.False(Manifest.GetManifest(true).CheckForUpdates);
            Assert.True(Manifest.GetManifest(true).AutoConfirmMarketTransactions);
        }

        [Fact]
        public async Task SaveSettingsAsync_PersistsUpdaterPreferenceFromProductionCaller()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            UpdatePreferenceRevisionTracker tracker = new UpdatePreferenceRevisionTracker();

            using (MainForm form = new MainForm(manifest, tracker, null, _ => { }))
            {
                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: true,
                    proxyEnabled: false,
                    autoConfirmMarket: true));
            }

            Assert.True(manifest.CheckForUpdates);
            Assert.True(manifest.AutoConfirmMarketTransactions);
            Assert.True(Manifest.GetManifest(true).CheckForUpdates);
            Assert.True(Manifest.GetManifest(true).AutoConfirmMarketTransactions);
        }

        private static JObject CreateSettingsPayload(bool checkForUpdates, bool proxyEnabled, bool autoConfirmMarket)
        {
            return new JObject
            {
                ["saveContext"] = String.Empty,
                ["tradeConfirmationCustomIntervalEnabled"] = false,
                ["tradeConfirmationCheckInterval"] = 15,
                ["autoConfirmMarket"] = autoConfirmMarket,
                ["autoConfirmTrades"] = false,
                ["minimizeToTray"] = false,
                ["checkForUpdates"] = checkForUpdates,
                ["diagnosticErrorLoggingEnabled"] = false,
                ["loginActionMonitoringEnabled"] = false,
                ["loginActionMode"] = "manual",
                ["loginActionAutoAllowIpEnabled"] = false,
                ["loginActionAutoAllowCurrentDeviceIp"] = false,
                ["loginActionAutoAllowIp"] = String.Empty,
                ["proxyEnabled"] = proxyEnabled,
                ["proxyScheme"] = "http",
                ["proxyHost"] = proxyEnabled ? "127.0.0.1" : String.Empty,
                ["proxyPort"] = proxyEnabled ? 8080 : 0,
                ["proxyUsername"] = String.Empty,
                ["proxyPasswordAction"] = "keep",
                ["proxyPassword"] = String.Empty
            };
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}
