using Steam_Desktop_Authenticator;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class UpdateSettingsConcurrencyTests
    {
        [Fact]
        public void MergeCheckForUpdatesPreference_PreservesUpdaterDisableAfterActivationRevisionChanges()
        {
            bool mergedValue = MainForm.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false,
                saveStartRevision: 4,
                currentRevision: 5);

            Assert.False(mergedValue);
        }

        [Fact]
        public void MergeCheckForUpdatesPreference_AllowsLaterSettingsSaveWhenRevisionIsUnchanged()
        {
            bool mergedValue = MainForm.MergeCheckForUpdatesPreference(
                requestedValue: true,
                currentValue: false,
                saveStartRevision: 5,
                currentRevision: 5);

            Assert.True(mergedValue);
        }

        [Fact]
        public async Task SettingsSavePausedDuringProxyValidation_PreservesUpdaterDisable()
        {
            bool persistedCheckForUpdates = true;
            long updatePreferenceRevision = 4;
            TaskCompletionSource<bool> proxyValidationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseSettingsSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long saveStartRevision = Volatile.Read(ref updatePreferenceRevision);

            Task<bool> settingsSave = CompleteSettingsSaveAfterProxyValidationAsync();
            await proxyValidationStarted.Task;

            Volatile.Write(ref persistedCheckForUpdates, false);
            Interlocked.Increment(ref updatePreferenceRevision);
            releaseSettingsSave.SetResult(true);

            Assert.False(await settingsSave);
            Assert.False(Volatile.Read(ref persistedCheckForUpdates));

            async Task<bool> CompleteSettingsSaveAfterProxyValidationAsync()
            {
                proxyValidationStarted.SetResult(true);
                await releaseSettingsSave.Task;

                bool mergedValue = MainForm.MergeCheckForUpdatesPreference(
                    requestedValue: true,
                    currentValue: Volatile.Read(ref persistedCheckForUpdates),
                    saveStartRevision,
                    currentRevision: Volatile.Read(ref updatePreferenceRevision));
                Volatile.Write(ref persistedCheckForUpdates, mergedValue);
                return mergedValue;
            }
        }
    }
}
