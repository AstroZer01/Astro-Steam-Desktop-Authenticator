using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class StartupLifecycleAndActivationTests : IDisposable
    {
        private const string AvailableReleaseUrl = "https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/download/99.99.99/ASDA-Release-99.99.99.zip";
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-startup-lifecycle-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public StartupLifecycleAndActivationTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void ShouldMinimizeToTrayOnClose_BlocksMinimizingUninitializedOrLockedVault()
        {
            Manifest encryptedManifest = CreateEncryptedManifestWithAccount("vault-passkey");
            encryptedManifest.MinimizeToTray = true;

            Manifest unencryptedManifest = Manifest.GenerateNewManifest(false);
            unencryptedManifest.MinimizeToTray = true;

            // Aborted or in-progress startup must never be trapped in tray as a zombie process.
            Assert.False(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: false,
                encryptedManifest,
                startupInitialized: false,
                currentPassKey: null));

            Assert.False(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: false,
                unencryptedManifest,
                startupInitialized: false,
                currentPassKey: null));

            // Even if startupInitialized were somehow set, a locked encrypted vault must not minimize.
            Assert.False(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: false,
                encryptedManifest,
                startupInitialized: true,
                currentPassKey: null));

            // Explicit exits always close, even after startup completes.
            Assert.False(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: true,
                encryptedManifest,
                startupInitialized: true,
                currentPassKey: "vault-passkey"));

            // Normal user close after startup unlock minimizes to tray when enabled.
            Assert.True(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: false,
                encryptedManifest,
                startupInitialized: true,
                currentPassKey: "vault-passkey"));

            Assert.True(MainForm.ShouldMinimizeToTrayOnClose(
                CloseReason.UserClosing,
                explicitExitRequested: false,
                unencryptedManifest,
                startupInitialized: true,
                currentPassKey: null));
        }

        [Fact]
        public void MainForm_FormClosing_DoesNotHideToTrayWhenStartupAbortsWithMinimizeToTrayEnabled()
        {
            Manifest encryptedManifest = CreateEncryptedManifestWithAccount("vault-passkey");
            encryptedManifest.MinimizeToTray = true;

            using (MainForm form = new MainForm(encryptedManifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                MethodInfo closingMethod = typeof(MainForm).GetMethod(
                    "MainForm_FormClosing",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(closingMethod);

                // Simulate MainForm_Shown calling Close() when passkey prompt is canceled before startup initializes.
                FormClosingEventArgs args = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
                closingMethod.Invoke(form, new object[] { form, args });

                Assert.False(args.Cancel);
            }
        }

        [Fact]
        public void MainForm_FormClosing_HidesToTrayOnlyAfterStartupInitializesAndUnlocks()
        {
            Manifest encryptedManifest = CreateEncryptedManifestWithAccount("vault-passkey");
            encryptedManifest.MinimizeToTray = true;

            using (MainForm form = new MainForm(encryptedManifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                form.SetEncryptionKey("vault-passkey");
                SetPrivateField(form, "startupInitialized", true);

                MethodInfo closingMethod = typeof(MainForm).GetMethod(
                    "MainForm_FormClosing",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(closingMethod);

                FormClosingEventArgs userCloseArgs = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
                closingMethod.Invoke(form, new object[] { form, userCloseArgs });
                Assert.True(userCloseArgs.Cancel);

                SetPrivateField(form, "explicitExitRequested", true);
                FormClosingEventArgs explicitExitArgs = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
                closingMethod.Invoke(form, new object[] { form, explicitExitArgs });
                Assert.False(explicitExitArgs.Cancel);
            }
        }

        [Fact]
        public async Task StartUpdateCheckFromActivation_SkipsUntilStartupCompletesAndVaultIsUnlocked()
        {
            Manifest encryptedManifest = CreateEncryptedManifestWithAccount("vault-passkey");
            encryptedManifest.CheckForUpdates = true;

            TaskCompletionSource<bool> promptCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int promptCount = 0;
            CountingUpdateHandler handler = new CountingUpdateHandler();

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                encryptedManifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    promptCalled.TrySetResult(true);
                    return (DialogResult.No, false);
                }))
            {
                MethodInfo activationUpdateMethod = typeof(MainForm).GetMethod(
                    "StartUpdateCheckFromActivation",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(activationUpdateMethod);

                // Before startup finishes and while locked: activation must not trigger update checks.
                activationUpdateMethod.Invoke(form, null);
                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, promptCount);
                Assert.False(GetPrivateField<bool>(form, "startupUpdateCheckStarted"));

                // Even if maintenance flag were set, a still-locked encrypted vault must not trigger update checks.
                SetPrivateField(form, "startupAccountMaintenanceFinished", true);
                activationUpdateMethod.Invoke(form, null);
                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, promptCount);
                Assert.False(GetPrivateField<bool>(form, "startupUpdateCheckStarted"));

                // Once unlocked and startup maintenance finished, activation update check runs normally.
                form.SetEncryptionKey("vault-passkey");
                activationUpdateMethod.Invoke(form, null);
                Task completed = await Task.WhenAny(promptCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(promptCalled.Task, completed);
                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(1, promptCount);
                Assert.True(GetPrivateField<bool>(form, "startupUpdateCheckStarted"));
            }
        }

        [Fact]
        public void CanRevealModernUi_RequiresInitializedAndUnlockedVault()
        {
            Manifest encryptedManifest = CreateEncryptedManifestWithAccount("vault-passkey");
            Manifest unencryptedManifest = Manifest.GenerateNewManifest(false);

            Assert.False(MainForm.CanRevealModernUi(null, startupInitialized: false, currentPassKey: null));
            Assert.False(MainForm.CanRevealModernUi(null, startupInitialized: true, currentPassKey: null));
            Assert.False(MainForm.CanRevealModernUi(encryptedManifest, startupInitialized: false, currentPassKey: null));
            Assert.False(MainForm.CanRevealModernUi(encryptedManifest, startupInitialized: false, currentPassKey: "vault-passkey"));
            Assert.False(MainForm.CanRevealModernUi(encryptedManifest, startupInitialized: true, currentPassKey: null));
            Assert.True(MainForm.CanRevealModernUi(encryptedManifest, startupInitialized: true, currentPassKey: "vault-passkey"));

            Assert.False(MainForm.CanRevealModernUi(unencryptedManifest, startupInitialized: false, currentPassKey: null));
            Assert.True(MainForm.CanRevealModernUi(unencryptedManifest, startupInitialized: true, currentPassKey: null));
        }

        private static Manifest CreateEncryptedManifestWithAccount(string passKey)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = new SteamGuardAccount
            {
                AccountName = "startup_user",
                SharedSecret = Convert.ToBase64String(new byte[20] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }),
                IdentitySecret = Convert.ToBase64String(new byte[20] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40 }),
                RevocationCode = "R12345",
                DeviceID = "android:startup-test",
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = 76561198000009991UL
                }
            };

            StorageResult saveResult = manifest.SaveAccount(account, encrypt: true, passKey: passKey);
            Assert.True(saveResult.Succeeded, saveResult.UserMessage);
            return manifest;
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field.GetValue(instance);
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(instance, value);
        }

        private sealed class CountingUpdateHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                JObject release = new JObject
                {
                    ["tag_name"] = "99.99.99",
                    ["assets"] = new JArray
                    {
                        new JObject { ["browser_download_url"] = AvailableReleaseUrl }
                    }
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(release.ToString(), Encoding.UTF8, "application/json")
                });
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
