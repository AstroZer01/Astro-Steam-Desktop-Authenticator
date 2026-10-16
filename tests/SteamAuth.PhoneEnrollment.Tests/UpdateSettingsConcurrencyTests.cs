using Steam_Desktop_Authenticator;
using Newtonsoft.Json.Linq;
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
    public sealed class UpdateSettingsConcurrencyTests : IDisposable
    {
        private const string AvailableReleaseUrl = "https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/download/99.99.99/ASDA-Release-99.99.99.zip";
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

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, true)]
        [InlineData(false, false, true)]
        public void UpdateNotificationPolicyKeepsManualChecksIndependentOfLaunchPreference(
            bool isStartupCheck,
            bool checkForUpdates,
            bool expected)
        {
            Assert.Equal(expected, MainForm.ShouldShowUpdateNotification(isStartupCheck, checkForUpdates));
        }

        [Theory]
        [InlineData("1.3.0", "1.3.0.0", false)]
        [InlineData("1.3.1", "1.3.0.0", true)]
        [InlineData("1.2.9", "1.3.0", false)]
        public void UpdateVersionComparisonTreatsEquivalentReleaseVersionsAsEqual(
            string latestVersion,
            string installedVersion,
            bool expected)
        {
            Assert.Equal(
                expected,
                MainForm.IsUpdateAvailable(new Version(latestVersion), new Version(installedVersion)));
        }

        [Fact]
        public async Task DisabledLaunchNotificationStillChecksAndStoresAvailableRelease()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }, client))
            {
                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Equal(AvailableReleaseUrl, GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
            }
        }

        [Theory]
        [InlineData("http-error")]
        [InlineData("invalid-json")]
        [InlineData("invalid-version")]
        [InlineData("untrusted-url")]
        [InlineData("missing-assets")]
        [InlineData("oversized-response")]
        public async Task FailedRecheckPreservesValidatedReleaseAndAllowsLaterSuccessfulCheck(string failure)
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler((requestNumber, _) =>
            {
                if (requestNumber == 1)
                    return Task.FromResult(CreateHttpResponse(CreateReleaseResponse("99.99.99", AvailableReleaseUrl)));
                if (requestNumber == 3)
                    return Task.FromResult(CreateHttpResponse(CreateReleaseResponse(System.Windows.Forms.Application.ProductVersion, AvailableReleaseUrl)));

                HttpResponseMessage response;
                switch (failure)
                {
                    case "http-error":
                        response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                        break;
                    case "invalid-json":
                        response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ invalid") };
                        break;
                    case "invalid-version":
                        response = CreateHttpResponse(CreateReleaseResponse("not-a-version", AvailableReleaseUrl));
                        break;
                    case "untrusted-url":
                        response = CreateHttpResponse(CreateReleaseResponse("99.99.100", "https://example.test/update.zip"));
                        break;
                    case "missing-assets":
                        response = CreateHttpResponse(new JObject { ["tag_name"] = "99.99.100" });
                        break;
                    case "oversized-response":
                        response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1024 * 1024 + 1)) };
                        break;
                    default:
                        throw new InvalidOperationException(failure);
                }

                return Task.FromResult(response);
            });
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }, client))
            {
                await form.CheckForUpdatesAsync(true);
                await form.CheckForUpdatesAsync(true);

                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Equal(AvailableReleaseUrl, GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.Equal("error", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));

                await form.CheckForUpdatesAsync(true);

                Assert.Equal(3, handler.RequestCount);
                Assert.Null(GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Null(GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));
            }
        }

        [Fact]
        public async Task OverlappingManualAndStartupChecksShareThePendingRequest()
        {
            TaskCompletionSource<HttpResponseMessage> pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler((_, __) => pendingResponse.Task);
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }, client))
            {
                Task startupCheck = form.CheckForUpdatesAsync(true);
                Assert.Equal(1, handler.RequestCount);
                Assert.False(startupCheck.IsCompleted);

                await form.CheckForUpdatesAsync(false);
                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
                Assert.True(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                pendingResponse.SetResult(CreateHttpResponse(CreateReleaseResponse("99.99.99", AvailableReleaseUrl)));
                await startupCheck;

                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
            }
        }

        [Fact]
        public async Task ClosingDuringResponseReadDoesNotPublishTheLateRelease()
        {
            TaskCompletionSource<HttpResponseMessage> pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler((_, __) => pendingResponse.Task);
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }, client))
            {
                CancellationTokenSource lifetimeSource = GetPrivateField<CancellationTokenSource>(form, "lifetimeCancellationSource");
                Task updateCheck = form.CheckForUpdatesAsync(true);
                byte[] responseBytes = Encoding.UTF8.GetBytes(CreateReleaseResponse("99.99.99", AvailableReleaseUrl).ToString());
                pendingResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CancelAtEndStream(responseBytes, lifetimeSource.Cancel))
                });
                await updateCheck;

                Assert.True(lifetimeSource.IsCancellationRequested);
                Assert.Null(GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Null(GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.NotEqual("error", GetPrivateField<string>(form, "updateCheckStatusTone"));
            }
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

        [Theory]
        [InlineData(false, "approve")]
        [InlineData(true, "approve")]
        [InlineData(false, "deny")]
        [InlineData(true, "deny")]
        [InlineData(false, "deny-exception")]
        [InlineData(true, "deny-exception")]
        public async Task SaveSettingsAsync_DecliningAutomaticLoginActionConfirmationPreventsPersistence(
            bool proxyEnabled,
            string actionCase)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            if (actionCase == "deny-exception")
                manifest.LoginActionMode = LoginActionModes.Deny;

            StorageResult baselineSave = manifest.SaveWithResult();
            Assert.True(baselineSave.Succeeded, baselineSave.UserMessage);

            int proxyTests = 0;
            int confirmations = 0;
            int proxyApplications = 0;
            string confirmationMessage = null;
            string requestedMode = actionCase == "approve" ? LoginActionModes.ApprovePersistent : LoginActionModes.Deny;
            bool enableDenyException = actionCase == "deny-exception";

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                async (_, __) =>
                {
                    proxyTests++;
                    await Task.Yield();
                    return new ProxyTestResult { Succeeded = true, Message = "Proxy test succeeded." };
                },
                _ => proxyApplications++,
                confirmAutomaticLoginAction: message =>
                {
                    confirmations++;
                    confirmationMessage = message;
                    return DialogResult.No;
                }))
            {
                await form.SaveSettingsAsync(CreateLoginActionSettingsPayload(
                    proxyEnabled,
                    requestedMode,
                    enableDenyException));
            }

            Assert.Equal(1, confirmations);
            Assert.Contains("every managed account", confirmationMessage);
            Assert.Equal(proxyEnabled ? 1 : 0, proxyTests);
            Assert.Equal(0, proxyApplications);

            Assert.Equal(actionCase == "deny-exception" ? LoginActionModes.Deny : LoginActionModes.Manual, manifest.LoginActionMode);
            Assert.False(manifest.AutoConfirmMarketTransactions);
            Assert.False(manifest.LoginActionAutoAllowIpEnabled);
            Assert.False(manifest.LoginActionAutoAllowCurrentDeviceIp);

            Manifest persisted = Manifest.GetManifest(true);
            Assert.Equal(actionCase == "deny-exception" ? LoginActionModes.Deny : LoginActionModes.Manual, persisted.LoginActionMode);
            Assert.False(persisted.AutoConfirmMarketTransactions);
            Assert.False(persisted.LoginActionAutoAllowIpEnabled);
            Assert.False(persisted.LoginActionAutoAllowCurrentDeviceIp);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SaveSettingsAsync_AcceptsConfirmedAutomaticLoginActionWithOrWithoutProxy(bool proxyEnabled)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            StorageResult baselineSave = manifest.SaveWithResult();
            Assert.True(baselineSave.Succeeded, baselineSave.UserMessage);

            int proxyTests = 0;
            int confirmations = 0;
            int proxyApplications = 0;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                async (_, __) =>
                {
                    proxyTests++;
                    await Task.Yield();
                    return new ProxyTestResult { Succeeded = true, Message = "Proxy test succeeded." };
                },
                _ => proxyApplications++,
                confirmAutomaticLoginAction: _ =>
                {
                    confirmations++;
                    return DialogResult.Yes;
                }))
            {
                await form.SaveSettingsAsync(CreateLoginActionSettingsPayload(
                    proxyEnabled,
                    LoginActionModes.ApprovePersistent,
                    enableDenyException: false));
            }

            Assert.Equal(1, confirmations);
            Assert.Equal(proxyEnabled ? 1 : 0, proxyTests);
            Assert.Equal(1, proxyApplications);
            Assert.Equal(LoginActionModes.ApprovePersistent, manifest.LoginActionMode);
            Assert.True(manifest.AutoConfirmMarketTransactions);
            Assert.Equal(LoginActionModes.ApprovePersistent, Manifest.GetManifest(true).LoginActionMode);
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

        private static JObject CreateLoginActionSettingsPayload(bool proxyEnabled, string loginActionMode, bool enableDenyException)
        {
            JObject payload = CreateSettingsPayload(
                checkForUpdates: false,
                proxyEnabled: proxyEnabled,
                autoConfirmMarket: true);
            payload["loginActionMode"] = loginActionMode;
            payload["loginActionAutoAllowIpEnabled"] = enableDenyException;
            payload["loginActionAutoAllowCurrentDeviceIp"] = enableDenyException;
            return payload;
        }

        private static JObject CreateReleaseResponse(string tagName, string downloadUrl)
        {
            return new JObject
            {
                ["tag_name"] = tagName,
                ["assets"] = new JArray
                {
                    new JObject { ["browser_download_url"] = downloadUrl }
                }
            };
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field.GetValue(instance);
        }

        private static HttpResponseMessage CreateHttpResponse(JObject response)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToString(), Encoding.UTF8, "application/json")
            };
        }

        private sealed class FixedUpdateResponseHandler : HttpMessageHandler
        {
            private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> respond;

            public FixedUpdateResponseHandler(JObject response)
                : this((_, __) => Task.FromResult(CreateHttpResponse(response)))
            {
            }

            public FixedUpdateResponseHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
            {
                this.respond = respond;
            }

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return respond(RequestCount, cancellationToken);
            }
        }

        private sealed class CancelAtEndStream : MemoryStream
        {
            private readonly Action cancel;

            public CancelAtEndStream(byte[] bytes, Action cancel) : base(bytes)
            {
                this.cancel = cancel;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int bytesRead = Read(buffer, offset, count);
                if (bytesRead == 0)
                    cancel();
                return Task.FromResult(bytesRead);
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
