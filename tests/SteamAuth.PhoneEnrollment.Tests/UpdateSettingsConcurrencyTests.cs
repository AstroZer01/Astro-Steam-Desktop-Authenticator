using Steam_Desktop_Authenticator;
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
    }
}
