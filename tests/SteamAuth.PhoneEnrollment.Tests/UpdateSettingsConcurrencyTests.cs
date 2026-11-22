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
        public void UpdateCheckPolicyKeepsManualChecksIndependentOfLaunchPreference(
            bool isStartupCheck,
            bool checkForUpdates,
            bool expected)
        {
            Assert.Equal(expected, MainForm.ShouldRunUpdateCheck(isStartupCheck, checkForUpdates));
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
        public async Task DisabledAutomaticUpdateChecksSkipsStartupNetworkRequest()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;
            int promptCount = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    return (DialogResult.No, false);
                }))
            {
                await form.CheckForUpdatesAsync(true);
                await form.CheckForUpdatesAsync(true);

                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, promptCount);
                Assert.Null(GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Null(GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.Equal(
                    "Automatic update checks are disabled. You can check manually anytime.",
                    GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("info", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
            }
        }

        [Fact]
        public async Task ManualUpdateCheckStillQueriesAndStoresAvailableReleaseWhenAutomaticChecksDisabled()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;
            int promptCount = 0;
            string capturedCheckboxText = "unexpected";

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, checkboxText) =>
                {
                    promptCount++;
                    capturedCheckboxText = checkboxText;
                    return (DialogResult.No, false);
                }))
            {
                await form.CheckForUpdatesAsync(true);
                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(
                    "Automatic update checks are disabled. You can check manually anytime.",
                    GetPrivateField<string>(form, "updateCheckStatusMessage"));

                await form.CheckForUpdatesAsync(false);

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(1, promptCount);
                Assert.Null(capturedCheckboxText);
                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Equal(AvailableReleaseUrl, GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));

                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Equal(AvailableReleaseUrl, GetPrivateField<string>(form, "availableUpdateDownloadUrl"));
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));
            }
        }

        [Fact]
        public async Task OptingOutDuringPreRequestStatusPublishSkipsNetworkRequestAndRestoresIdleState()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            TaskCompletionSource<bool> preRequestPublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releasePreRequestPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Collections.Generic.List<(bool Checking, string Message, string Tone)> publishedStates =
                new System.Collections.Generic.List<(bool Checking, string Message, string Tone)>();
            int promptCount = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    return (DialogResult.No, false);
                },
                publishUpdateCheckStateAsync: async (checking, message, tone) =>
                {
                    publishedStates.Add((checking, message, tone));
                    if (checking && !preRequestPublishStarted.Task.IsCompleted)
                    {
                        preRequestPublishStarted.SetResult(true);
                        await releasePreRequestPublish.Task;
                    }
                }))
            {
                Task startupCheck = form.CheckForUpdatesAsync(true);
                await preRequestPublishStarted.Task;

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: false,
                    proxyEnabled: false,
                    autoConfirmMarket: false));
                Assert.False(manifest.CheckForUpdates);

                releasePreRequestPublish.SetResult(true);
                await startupCheck;

                Assert.Equal(0, handler.RequestCount);
                Assert.Equal(0, promptCount);
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.Equal(
                    "Automatic update checks are disabled. You can check manually anytime.",
                    GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("info", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.Equal(2, publishedStates.Count);
                Assert.True(publishedStates[0].Checking);
                Assert.Equal((false, "Automatic update checks are disabled. You can check manually anytime.", "info"), publishedStates[1]);
            }
        }

        [Fact]
        public async Task ManualCheckDuringAbortedOptOutIdlePublishRunsAndDoesNotStickBusyState()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            TaskCompletionSource<bool> preRequestPublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releasePreRequestPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> abortIdlePublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseAbortIdlePublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Collections.Generic.List<(bool Checking, string Message, string Tone)> publishedStates =
                new System.Collections.Generic.List<(bool Checking, string Message, string Tone)>();
            int promptCount = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    return (DialogResult.No, false);
                },
                publishUpdateCheckStateAsync: async (checking, message, tone) =>
                {
                    publishedStates.Add((checking, message, tone));
                    if (checking && !preRequestPublishStarted.Task.IsCompleted)
                    {
                        preRequestPublishStarted.SetResult(true);
                        await releasePreRequestPublish.Task;
                    }
                    else if (!checking &&
                        String.Equals(message, "Automatic update checks are disabled. You can check manually anytime.", StringComparison.Ordinal) &&
                        !abortIdlePublishStarted.Task.IsCompleted)
                    {
                        abortIdlePublishStarted.SetResult(true);
                        await releaseAbortIdlePublish.Task;
                    }
                }))
            {
                Task startupCheck = form.CheckForUpdatesAsync(true);
                await preRequestPublishStarted.Task;

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: false,
                    proxyEnabled: false,
                    autoConfirmMarket: false));

                releasePreRequestPublish.SetResult(true);
                await abortIdlePublishStarted.Task;

                Task manualCheck = form.CheckForUpdatesAsync(false);
                releaseAbortIdlePublish.SetResult(true);

                await Task.WhenAll(startupCheck, manualCheck);

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(1, promptCount);
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.DoesNotContain(publishedStates, state => state.Message == "A check for updates is already in progress.");
                Assert.Equal((false, "Version 99.99.99 is available.", "success"), publishedStates[publishedStates.Count - 1]);
            }
        }

        [Fact]
        public async Task ReEnablingDuringAbortedOptOutIdlePublishClearsDisabledNotice()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            TaskCompletionSource<bool> preRequestPublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releasePreRequestPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> abortIdlePublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseAbortIdlePublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Collections.Generic.List<(bool Checking, string Message, string Tone)> publishedStates =
                new System.Collections.Generic.List<(bool Checking, string Message, string Tone)>();

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false),
                publishUpdateCheckStateAsync: async (checking, message, tone) =>
                {
                    publishedStates.Add((checking, message, tone));
                    if (checking && !preRequestPublishStarted.Task.IsCompleted)
                    {
                        preRequestPublishStarted.SetResult(true);
                        await releasePreRequestPublish.Task;
                    }
                    else if (!checking &&
                        String.Equals(message, "Automatic update checks are disabled. You can check manually anytime.", StringComparison.Ordinal) &&
                        !abortIdlePublishStarted.Task.IsCompleted)
                    {
                        abortIdlePublishStarted.SetResult(true);
                        await releaseAbortIdlePublish.Task;
                    }
                }))
            {
                Task startupCheck = form.CheckForUpdatesAsync(true);
                await preRequestPublishStarted.Task;

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: false,
                    proxyEnabled: false,
                    autoConfirmMarket: false));

                releasePreRequestPublish.SetResult(true);
                await abortIdlePublishStarted.Task;

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: true,
                    proxyEnabled: false,
                    autoConfirmMarket: false));

                releaseAbortIdlePublish.SetResult(true);
                await startupCheck;

                Assert.Equal(0, handler.RequestCount);
                Assert.True(manifest.CheckForUpdates);
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.Equal(String.Empty, GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("info", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.Equal((false, String.Empty, "info"), publishedStates[publishedStates.Count - 1]);
            }
        }

        [Fact]
        public async Task OptingOutDuringPreRequestStatusPublishRestoresPriorCheckResult()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            TaskCompletionSource<bool> secondPreRequestPublishStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseSecondPreRequestPublish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Collections.Generic.List<(bool Checking, string Message, string Tone)> publishedStates =
                new System.Collections.Generic.List<(bool Checking, string Message, string Tone)>();
            int busyPublishes = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false),
                publishUpdateCheckStateAsync: async (checking, message, tone) =>
                {
                    publishedStates.Add((checking, message, tone));
                    if (checking && ++busyPublishes == 2)
                    {
                        secondPreRequestPublishStarted.SetResult(true);
                        await releaseSecondPreRequestPublish.Task;
                    }
                }))
            {
                await form.CheckForUpdatesAsync(false);
                Assert.Equal(1, handler.RequestCount);
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));

                Task activationCheck = form.CheckForUpdatesAsync(true);
                await secondPreRequestPublishStarted.Task;

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: false,
                    proxyEnabled: false,
                    autoConfirmMarket: false));

                releaseSecondPreRequestPublish.SetResult(true);
                await activationCheck;

                Assert.Equal(1, handler.RequestCount);
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.Equal((false, "Version 99.99.99 is available.", "success"), publishedStates[publishedStates.Count - 1]);
            }
        }

        [Fact]
        public async Task OptingOutWhileStartupRequestIsInFlightSuppressesLaunchPrompt()
        {
            TaskCompletionSource<HttpResponseMessage> pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler((_, __) => pendingResponse.Task);
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            int promptCount = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    return (DialogResult.No, false);
                }))
            {
                Task startupCheck = form.CheckForUpdatesAsync(true);
                Assert.Equal(1, handler.RequestCount);

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: false,
                    proxyEnabled: false,
                    autoConfirmMarket: false));
                Assert.False(manifest.CheckForUpdates);

                pendingResponse.SetResult(CreateHttpResponse(CreateReleaseResponse("99.99.99", AvailableReleaseUrl)));
                await startupCheck;

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal(0, promptCount);
                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
            }
        }

        [Fact]
        public async Task ReEnablingAutomaticUpdateChecksClearsDisabledNoticeButPreservesActualCheckResults()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;
            System.Collections.Generic.List<(bool Checking, string Message, string Tone)> publishedStates =
                new System.Collections.Generic.List<(bool Checking, string Message, string Tone)>();

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false),
                publishUpdateCheckStateAsync: (checking, message, tone) =>
                {
                    publishedStates.Add((checking, message, tone));
                    return Task.CompletedTask;
                }))
            {
                await form.CheckForUpdatesAsync(true);
                Assert.Equal(
                    "Automatic update checks are disabled. You can check manually anytime.",
                    GetPrivateField<string>(form, "updateCheckStatusMessage"));

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: true,
                    proxyEnabled: false,
                    autoConfirmMarket: false));

                Assert.True(manifest.CheckForUpdates);
                Assert.Equal(String.Empty, GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("info", GetPrivateField<string>(form, "updateCheckStatusTone"));
                Assert.Equal((false, String.Empty, "info"), publishedStates[publishedStates.Count - 1]);

                await form.CheckForUpdatesAsync(false);
                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));

                await form.SaveSettingsAsync(CreateSettingsPayload(
                    checkForUpdates: true,
                    proxyEnabled: false,
                    autoConfirmMarket: true));

                Assert.Equal("Version 99.99.99 is available.", GetPrivateField<string>(form, "updateCheckStatusMessage"));
                Assert.Equal("success", GetPrivateField<string>(form, "updateCheckStatusTone"));
            }
        }

        [Fact]
        public async Task StartupUpdateCheckOptOutDisablesSubsequentAutomaticChecks()
        {
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler(CreateReleaseResponse("99.99.99", AvailableReleaseUrl));
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = true;
            string capturedCheckboxText = null;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, checkboxText) =>
                {
                    capturedCheckboxText = checkboxText;
                    return (DialogResult.No, true);
                }))
            {
                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
                Assert.Equal("Don't check for updates on launch", capturedCheckboxText);
                Assert.False(manifest.CheckForUpdates);
                Assert.False(Manifest.GetManifest(true).CheckForUpdates);

                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
            }
        }

        [Fact]
        public async Task DisabledStartupCheckWhileManualCheckInProgressDoesNotOverwriteBusyState()
        {
            TaskCompletionSource<HttpResponseMessage> pendingResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            FixedUpdateResponseHandler handler = new FixedUpdateResponseHandler((_, __) => pendingResponse.Task);
            Manifest manifest = Manifest.GenerateNewManifest(false);
            manifest.CheckForUpdates = false;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false)))
            {
                Task manualCheck = form.CheckForUpdatesAsync(false);
                Assert.Equal(1, handler.RequestCount);
                Assert.True(GetPrivateField<bool>(form, "isCheckingForUpdates"));

                await form.CheckForUpdatesAsync(true);

                Assert.Equal(1, handler.RequestCount);
                Assert.True(GetPrivateField<bool>(form, "isCheckingForUpdates"));
                Assert.Equal("Checking for updates...", GetPrivateField<string>(form, "updateCheckStatusMessage"));

                pendingResponse.SetResult(CreateHttpResponse(CreateReleaseResponse("99.99.99", AvailableReleaseUrl)));
                await manualCheck;

                Assert.Equal(new Version("99.99.99"), GetPrivateField<Version>(form, "availableUpdateVersion"));
                Assert.False(GetPrivateField<bool>(form, "isCheckingForUpdates"));
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
            manifest.CheckForUpdates = true;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false)))
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
            manifest.CheckForUpdates = true;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) => (DialogResult.No, false)))
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
            manifest.CheckForUpdates = true;
            int promptCount = 0;

            using (HttpClient client = new HttpClient(handler))
            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                client,
                promptForUpdate: (_, __) =>
                {
                    promptCount++;
                    return (DialogResult.No, false);
                }))
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
                Assert.Equal(0, promptCount);
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
