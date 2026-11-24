using Microsoft.Web.WebView2.Core;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class LoginPasskeyPromptTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-login-passkey-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public LoginPasskeyPromptTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void TryResolveManifestPassKey_ReusesValidSuppliedPassKeyWithoutPrompting()
        {
            const string expectedPassKey = "active-session-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(expectedPassKey);
            int promptCalls = 0;
            int errorCalls = 0;

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                expectedPassKey,
                _ => throw new InvalidOperationException("Setup prompt should not run when entries exist."),
                (_, _) =>
                {
                    promptCalls++;
                    return (true, null);
                },
                _ => errorCalls++,
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal(expectedPassKey, resolvedPassKey);
            Assert.Equal(0, promptCalls);
            Assert.Equal(0, errorCalls);
        }

        [Fact]
        public void TryResolveManifestPassKey_MasksPromptWhenSuppliedPassKeyIsMissing()
        {
            const string expectedPassKey = "masked-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(expectedPassKey);
            List<(string Label, bool IsPassword)> prompts = new List<(string, bool)>();
            int errorCalls = 0;

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                null,
                _ => throw new InvalidOperationException("Setup prompt should not run when entries exist."),
                (label, isPassword) =>
                {
                    prompts.Add((label, isPassword));
                    return (false, expectedPassKey);
                },
                _ => errorCalls++,
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal(expectedPassKey, resolvedPassKey);
            (string Label, bool IsPassword) prompt = Assert.Single(prompts);
            Assert.Equal(LoginForm.CurrentPassKeyPromptLabel, prompt.Label);
            Assert.True(prompt.IsPassword);
            Assert.Equal(0, errorCalls);
        }

        [Fact]
        public void TryResolveManifestPassKey_MasksRetriesWhenSuppliedPassKeyIsInvalid()
        {
            const string expectedPassKey = "correct-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(expectedPassKey);
            Queue<(bool Canceled, string Value)> responses = new Queue<(bool, string)>(new[]
            {
                (false, "wrong-entry"),
                (false, expectedPassKey)
            });
            List<bool> maskFlags = new List<bool>();
            List<string> errors = new List<string>();

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                "stale-passkey",
                _ => throw new InvalidOperationException("Setup prompt should not run when entries exist."),
                (_, isPassword) =>
                {
                    maskFlags.Add(isPassword);
                    return responses.Dequeue();
                },
                message => errors.Add(message),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal(expectedPassKey, resolvedPassKey);
            Assert.Equal(new[] { true, true }, maskFlags);
            Assert.Single(errors);
        }

        [Fact]
        public void TryResolveManifestPassKey_ReturnsFalseWhenMaskedPromptIsCanceled()
        {
            Manifest manifest = CreateEncryptedManifestWithAccount("correct-passkey");
            List<bool> maskFlags = new List<bool>();

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                null,
                _ => throw new InvalidOperationException("Setup prompt should not run when entries exist."),
                (_, isPassword) =>
                {
                    maskFlags.Add(isPassword);
                    return (true, "partial-input");
                },
                _ => throw new InvalidOperationException("Cancel should not display an invalid passkey error."),
                out string resolvedPassKey);

            Assert.False(resolved);
            Assert.Null(resolvedPassKey);
            Assert.Equal(new[] { true }, maskFlags);
        }

        [Fact]
        public void TryResolveManifestPassKey_IgnoresSuppliedPassKeyWhenManifestIsUnencrypted()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.SaveAccount(CreateAccount(76561198000001001UL, "unencrypted-user"), false).Succeeded);
            int promptCalls = 0;

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                "unused-cli-passkey",
                _ => throw new InvalidOperationException("Setup prompt should not run when entries exist."),
                (_, _) =>
                {
                    promptCalls++;
                    return (true, null);
                },
                _ => throw new InvalidOperationException("Error message should not show for unencrypted manifest."),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Null(resolvedPassKey);
            Assert.Equal(0, promptCalls);
        }

        [Fact]
        public void TryResolveManifestPassKey_UsesSetupPromptWhenManifestHasNoEntries()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            string capturedPrompt = null;

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                "ignored-when-empty",
                prompt =>
                {
                    capturedPrompt = prompt;
                    return (Manifest.SetupPassKeyStatus.Set, "first-account-passkey");
                },
                (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                _ => throw new InvalidOperationException("Error message should not show on an empty manifest."),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal("first-account-passkey", resolvedPassKey);
            Assert.Equal(LoginForm.SetupPassKeyPromptLabel, capturedPrompt);
        }

        [Fact]
        public void TryResolveManifestPassKey_AbortsEmptyVaultEnrollmentWhenSetupFails()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                null,
                _ => (Manifest.SetupPassKeyStatus.Failed, null),
                (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                _ => throw new InvalidOperationException("Invalid passkey error should not run on an empty manifest."),
                out string resolvedPassKey);

            Assert.False(resolved);
            Assert.Null(resolvedPassKey);
        }

        [Fact]
        public async Task PrepareInitialEnrollmentAsync_DoesNotPromptWhenAlreadyCanceled()
        {
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.Cancel();
                int resolveCalls = 0;
                int startCalls = 0;

                await Assert.ThrowsAsync<OperationCanceledException>(() => LoginForm.PrepareInitialEnrollmentAsync(
                    () =>
                    {
                        resolveCalls++;
                        return (true, "unused");
                    },
                    _ =>
                    {
                        startCalls++;
                        return Task.FromResult(new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization));
                    },
                    cts.Token));

                Assert.Equal(0, resolveCalls);
                Assert.Equal(0, startCalls);
            }
        }

        [Fact]
        public async Task PrepareInitialEnrollmentAsync_SkipsSteamEnrollmentWhenPassKeyResolutionFails()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int startEnrollmentCalls = 0;

            LoginForm.InitialEnrollmentPreparation preparation = await LoginForm.PrepareInitialEnrollmentAsync(
                () =>
                {
                    bool resolved = LoginForm.TryResolveManifestPassKey(
                        manifest,
                        null,
                        _ => (Manifest.SetupPassKeyStatus.Failed, null),
                        (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                        _ => throw new InvalidOperationException("Error message should not show on an empty manifest."),
                        out string passKey);
                    return (resolved, passKey);
                },
                _ =>
                {
                    startEnrollmentCalls++;
                    return Task.FromResult(new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization));
                },
                CancellationToken.None);

            Assert.False(preparation.PassKeyResolved);
            Assert.Null(preparation.PassKey);
            Assert.Null(preparation.EnrollmentOutcome);
            Assert.Equal(0, startEnrollmentCalls);
            Assert.False(manifest.Encrypted);
            Assert.Empty(manifest.Entries);
        }

        [Fact]
        public async Task PrepareInitialEnrollmentAsync_ResolvesPassKeyBeforeStartingSteamEnrollmentAndDefersCommit()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            string manifestPath = Path.Combine(Manifest.GetExecutableDir(), "maFiles", "manifest.json");
            List<string> steps = new List<string>();

            LoginForm.InitialEnrollmentPreparation preparation = await LoginForm.PrepareInitialEnrollmentAsync(
                () =>
                {
                    steps.Add("resolve-passkey");
                    Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                        LoginForm.SetupPassKeyPromptLabel,
                        commitImmediately: false,
                        (label, isPassword) =>
                        {
                            using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                            {
                                form.SubmitInput("deferred-before-start");
                                return form.ToPassKeyPromptResult();
                            }
                        },
                        _ => { },
                        out string passKey);
                    Assert.Equal(Manifest.SetupPassKeyStatus.Set, status);
                    return (true, passKey);
                },
                _ =>
                {
                    steps.Add("start-enrollment");
                    Assert.False(manifest.Encrypted);
                    Assert.False(File.Exists(manifestPath));
                    return Task.FromResult(new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.Failed, "Steam rejected enrollment."));
                },
                CancellationToken.None);

            Assert.Equal(new[] { "resolve-passkey", "start-enrollment" }, steps);
            Assert.True(preparation.PassKeyResolved);
            Assert.Equal("deferred-before-start", preparation.PassKey);
            Assert.Equal(AuthenticatorEnrollmentResult.Failed, preparation.EnrollmentOutcome.Result);
            Assert.False(manifest.Encrypted);
            Assert.False(File.Exists(manifestPath));
        }

        [Fact]
        public async Task PrepareInitialEnrollmentAsync_ReturnsPreResolvedPassKeyForImmediateFirstSave()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            List<string> steps = new List<string>();

            LoginForm.InitialEnrollmentPreparation preparation = await LoginForm.PrepareInitialEnrollmentAsync(
                () =>
                {
                    steps.Add("resolve-passkey");
                    return (true, "ready-for-first-save");
                },
                _ =>
                {
                    steps.Add("start-enrollment");
                    return Task.FromResult(new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization));
                },
                CancellationToken.None);

            Assert.Equal(new[] { "resolve-passkey", "start-enrollment" }, steps);
            Assert.True(preparation.PassKeyResolved);
            Assert.Equal("ready-for-first-save", preparation.PassKey);
            Assert.Equal(AuthenticatorEnrollmentResult.AwaitingFinalization, preparation.EnrollmentOutcome.Result);

            SteamGuardAccount account = CreateAccount(76561198000001007UL, "pre-resolved-user");
            StorageResult saveResult = manifest.SaveAccount(account, encrypt: true, passKey: preparation.PassKey);
            Assert.True(saveResult.Succeeded, saveResult.UserMessage);
            Assert.True(manifest.Encrypted);
            Assert.True(manifest.VerifyPasskey("ready-for-first-save"));
        }

        [Fact]
        public void TryResolveManifestPassKey_AllowsPlaintextOnlyWhenUserExplicitlyDeclinesEncryption()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                null,
                _ => (Manifest.SetupPassKeyStatus.DeclinedUnencrypted, null),
                (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                _ => throw new InvalidOperationException("Invalid passkey error should not run on an empty manifest."),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Null(resolvedPassKey);
        }

        [Fact]
        public void MainForm_AddAccountForwardsActivePassKeyToLoginDialog()
        {
            const string activePassKey = "unlocked-main-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(activePassKey);
            string forwardedPassKey = null;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showAddAccountLoginDialog: currentPassKey =>
                {
                    forwardedPassKey = currentPassKey;
                    return currentPassKey;
                }))
            {
                form.SetEncryptionKey(activePassKey);
                InvokePrivateMethod(form, "btnSteamLogin_Click", form, EventArgs.Empty);

                Assert.Equal(activePassKey, forwardedPassKey);
            }
        }

        [Fact]
        public void MainForm_AddAccountAdoptsPassKeyCreatedDuringFirstEncryptedEnrollment()
        {
            const string newPassKey = "newly-created-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount newAccount = CreateAccount(76561198000001002UL, "first-encrypted-user");

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showAddAccountLoginDialog: currentPassKey =>
                {
                    Assert.Null(currentPassKey);
                    Assert.True(manifest.SaveAccount(newAccount, true, newPassKey).Succeeded);
                    return newPassKey;
                }))
            {
                InvokePrivateMethod(form, "btnSteamLogin_Click", form, EventArgs.Empty);

                Assert.Equal(newPassKey, GetPrivateField<string>(form, "passKey"));
                Button manageEncryptionButton = GetPrivateField<Button>(form, "btnManageEncryption");
                Assert.Equal("Remove Encryption", manageEncryptionButton.Text);
                Assert.True(manageEncryptionButton.Enabled);
                SteamGuardAccount[] loaded = GetPrivateField<SteamGuardAccount[]>(form, "allAccounts");
                SteamGuardAccount single = Assert.Single(loaded);
                Assert.Equal(newAccount.Session.SteamID, single.Session.SteamID);
            }
        }

        [Fact]
        public void MainForm_AddAccountDoesNotAdoptPassKeyWhenFirstSaveFailsOrLeavesEmptyVault()
        {
            const string attemptedPassKey = "failed-first-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount invalidAccount = new SteamGuardAccount
            {
                AccountName = "incomplete-account",
                Session = new SessionData { SteamID = 0 }
            };

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showAddAccountLoginDialog: _ =>
                {
                    StorageResult failedSave = manifest.SaveAccount(invalidAccount, true, attemptedPassKey);
                    Assert.False(failedSave.Succeeded);
                    return null;
                }))
            {
                InvokePrivateMethod(form, "btnSteamLogin_Click", form, EventArgs.Empty);

                Assert.False(manifest.Encrypted);
                Assert.Empty(manifest.Entries);
                Assert.Null(GetPrivateField<string>(form, "passKey"));
                Button manageEncryptionButton = GetPrivateField<Button>(form, "btnManageEncryption");
                Assert.NotEqual("Remove Encryption", manageEncryptionButton.Text);

                // Retrying in the same process without encryption must succeed cleanly.
                SteamGuardAccount retryAccount = CreateAccount(76561198000001003UL, "unencrypted-retry");
                Assert.True(manifest.SaveAccount(retryAccount, false, null).Succeeded);
                Assert.False(manifest.Encrypted);
            }
        }

        [Fact]
        public void MainForm_AddAccountDoesNotAdoptPassKeyWhenFirstEnrollmentCancelsAndRemovesEntry()
        {
            const string tempPassKey = "canceled-first-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001004UL, "canceled-enrollment-user");

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showAddAccountLoginDialog: _ =>
                {
                    Assert.True(manifest.SaveAccount(pendingAccount, true, tempPassKey).Succeeded);
                    Assert.True(manifest.Encrypted);
                    Assert.True(manifest.RemoveAccount(pendingAccount));
                    Assert.False(manifest.Encrypted);
                    return tempPassKey;
                }))
            {
                InvokePrivateMethod(form, "btnSteamLogin_Click", form, EventArgs.Empty);

                Assert.False(manifest.Encrypted);
                Assert.Empty(manifest.Entries);
                Assert.Null(GetPrivateField<string>(form, "passKey"));
                Button manageEncryptionButton = GetPrivateField<Button>(form, "btnManageEncryption");
                Assert.NotEqual("Remove Encryption", manageEncryptionButton.Text);
            }
        }

        [Fact]
        public async Task MainForm_PromptRefreshLoginAsyncForwardsPassKeyAndAdoptsResolvedKeyOnSuccess()
        {
            const string stalePassKey = "stale-passkey";
            const string correctedPassKey = "corrected-refresh-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(correctedPassKey);
            SteamGuardAccount account = manifest.GetAllAccounts(correctedPassKey)[0];
            string forwardedPassKey = null;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showRefreshLoginDialog: (targetAccount, currentPassKey) =>
                {
                    Assert.Same(account, targetAccount);
                    forwardedPassKey = currentPassKey;
                    return (true, correctedPassKey);
                }))
            {
                form.SetEncryptionKey(stalePassKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { account });
                bool succeeded = await InvokePrivateAsyncMethod<bool>(form, "PromptRefreshLoginAsync", account);

                Assert.True(succeeded);
                Assert.Equal(stalePassKey, forwardedPassKey);
                Assert.Equal(correctedPassKey, GetPrivateField<string>(form, "passKey"));
                InvokePrivateMethod(form, "loadAccountsList", new object[] { null });
                SteamGuardAccount[] loaded = GetPrivateField<SteamGuardAccount[]>(form, "allAccounts");
                Assert.Single(loaded);
            }
        }

        [Fact]
        public async Task MainForm_PromptRefreshLoginAsyncDoesNotOverwritePassKeyOnFailure()
        {
            const string activePassKey = "active-refresh-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(activePassKey);
            SteamGuardAccount account = manifest.GetAllAccounts(activePassKey)[0];
            string forwardedPassKey = null;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showRefreshLoginDialog: (targetAccount, currentPassKey) =>
                {
                    Assert.Same(account, targetAccount);
                    forwardedPassKey = currentPassKey;
                    return (false, "unexpected-passkey");
                }))
            {
                form.SetEncryptionKey(activePassKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { account });
                bool succeeded = await InvokePrivateAsyncMethod<bool>(form, "PromptRefreshLoginAsync", account);

                Assert.False(succeeded);
                Assert.Equal(activePassKey, forwardedPassKey);
                Assert.Equal(activePassKey, GetPrivateField<string>(form, "passKey"));
            }
        }

        [Fact]
        public void CreateCurrentPassKeyPrompt_CreatesMaskedInputForm()
        {
            using (InputForm form = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                Assert.True(form.IsPasswordMasked);
                Assert.Equal('*', form.txtBox.PasswordChar);
                Assert.Equal(LoginForm.CurrentPassKeyPromptLabel, GetPrivateField<Label>(form, "labelText").Text);

                form.DisableNativeFallbackControls();
                Assert.Null(form.AcceptButton);
                Assert.Null(form.CancelButton);
                Assert.False(form.txtBox.Enabled);
                Assert.False(GetPrivateField<Button>(form, "btnAccept").Enabled);
                Assert.False(GetPrivateField<Button>(form, "btnCancel").Enabled);
            }
        }

        [Fact]
        public void InputHtml_DefaultsToDisabledPasswordInputUntilSetupRuns()
        {
            string inputHtmlPath = Path.Combine(AppContext.BaseDirectory, "app", "ui", "input.html");
            Assert.True(File.Exists(inputHtmlPath), "Expected packaged input.html at " + inputHtmlPath);
            string html = File.ReadAllText(inputHtmlPath);

            Assert.Contains("<input type=\"password\" id=\"input-box\"", html);
            Assert.Contains("id=\"input-box\" aria-labelledby=\"label-text\" placeholder=\"...\" disabled", html);
            Assert.Contains("id=\"btn-cancel\" class=\"btn-secondary flex-1 shadow-lg\" disabled", html);
            Assert.Contains("id=\"btn-accept\" class=\"btn-primary flex-1 shadow-lg shadow-[#00E5FF]/20\" disabled", html);

            int observeIndex = html.IndexOf("resizeObserver.observe(panel);", StringComparison.Ordinal);
            int inputEnableIndex = html.IndexOf("input.disabled = false;", StringComparison.Ordinal);
            int cancelEnableIndex = html.IndexOf("cancelButton.disabled = false;", StringComparison.Ordinal);
            int buttonEnableIndex = html.IndexOf("acceptButton.disabled = false;", StringComparison.Ordinal);
            int returnTrueIndex = html.IndexOf("return true;", StringComparison.Ordinal);

            Assert.True(observeIndex >= 0, "Expected resizeObserver.observe(panel) in setupInput.");
            Assert.True(inputEnableIndex > observeIndex, "Expected input to stay disabled until setup finishes.");
            Assert.True(cancelEnableIndex > inputEnableIndex, "Expected cancel button to enable after input.");
            Assert.True(buttonEnableIndex > cancelEnableIndex, "Expected accept button to enable after cancel button.");
            Assert.True(returnTrueIndex > buttonEnableIndex, "Expected setupInput to return true after enabling controls.");
        }

        [Fact]
        public void InputForm_TracksUiReadinessAcrossNavigationReloadAndFailure()
        {
            using (InputForm form = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);

                form.BeginModernUiNavigation(navigationId: 10);
                Assert.False(form.UiReady);
                Assert.False(form.LoadFailed);
                Assert.False(form.CanAcceptWebMessage(isWebViewVisible: true));

                Assert.True(form.ShouldHandleNavigationCompleted(10, out int gen10));
                Assert.True(form.TryHandleNavigationCompletedResult(10, gen10, succeeded: true));
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);
                Assert.True(form.CanAcceptWebMessage(isWebViewVisible: true));
                Assert.False(form.CanAcceptWebMessage(isWebViewVisible: false));

                // Starting a reload clears readiness until the new navigation completes.
                form.BeginModernUiNavigation(navigationId: 11);
                Assert.False(form.UiReady);
                Assert.False(form.LoadFailed);
                Assert.False(form.CanAcceptWebMessage(isWebViewVisible: true));

                Assert.True(form.ShouldHandleNavigationCompleted(11, out int gen11));
                Assert.True(form.TryHandleNavigationCompletedResult(11, gen11, succeeded: false));
                Assert.False(form.UiReady);
                Assert.True(form.LoadFailed);
                Assert.False(form.CanAcceptWebMessage(isWebViewVisible: true));
            }
        }

        [Fact]
        public void InputForm_IgnoresStaleNavigationCompletionsDeliveredAfterReloadStarts()
        {
            using (InputForm form = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                // Navigation 1 starts, then navigation 2 (reload) starts before navigation 1 completes.
                form.BeginModernUiNavigation(navigationId: 1);
                form.BeginModernUiNavigation(navigationId: 2);

                // Stale completion for navigation 1 (whether failure or success) must be ignored.
                Assert.False(form.ShouldHandleNavigationCompleted(1, out _));
                Assert.False(form.UiReady);
                Assert.False(form.LoadFailed);

                // Active navigation 2 enters completion and begins awaiting setupInput.
                Assert.True(form.ShouldHandleNavigationCompleted(2, out int gen2));

                // Before setupInput resolves, navigation 3 starts.
                form.BeginModernUiNavigation(navigationId: 3);

                // Stale setupInput completion (failure or success) for navigation 2 must not affect navigation 3.
                Assert.False(form.TryHandleNavigationCompletedResult(2, gen2, succeeded: false));
                Assert.False(form.TryHandleNavigationCompletedResult(2, gen2, succeeded: true));
                Assert.False(form.UiReady);
                Assert.False(form.LoadFailed);

                // Active navigation 3 completes successfully.
                Assert.True(form.ShouldHandleNavigationCompleted(3, out int gen3));
                Assert.True(form.TryHandleNavigationCompletedResult(3, gen3, succeeded: true));
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);

                // Canceled untrusted navigation completion (unrecorded NavigationId 99) must not disrupt ready state.
                Assert.False(form.ShouldHandleNavigationCompleted(99, out _));
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);
            }
        }

        [Theory]
        [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited)]
        [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited)]
        [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)]
        public void InputForm_TerminalProcessFailureClearsReadinessAndFailsClosed(CoreWebView2ProcessFailedKind failureKind)
        {
            using (InputForm form = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                form.BeginModernUiNavigation(navigationId: 5);
                Assert.True(form.ShouldHandleNavigationCompleted(5, out int gen5));
                Assert.True(form.TryHandleNavigationCompletedResult(5, gen5, succeeded: true));
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);

                Assert.True(form.HandleModernUiProcessFailed(failureKind));
                Assert.False(form.UiReady);
                Assert.True(form.LoadFailed);
                Assert.False(form.CanAcceptWebMessage(isWebViewVisible: true));

                // Late setup completion for the crashed navigation must not restore readiness.
                Assert.False(form.TryHandleNavigationCompletedResult(5, gen5, succeeded: true));
                Assert.False(form.UiReady);
                Assert.True(form.LoadFailed);
            }
        }

        [Theory]
        [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
        [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
        public void InputForm_NonTerminalProcessFailureKeepsReadyPromptUsable(CoreWebView2ProcessFailedKind failureKind)
        {
            using (InputForm form = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                form.BeginModernUiNavigation(navigationId: 6);
                Assert.True(form.ShouldHandleNavigationCompleted(6, out int gen6));
                Assert.True(form.TryHandleNavigationCompletedResult(6, gen6, succeeded: true));

                Assert.False(form.HandleModernUiProcessFailed(failureKind));
                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);
                Assert.True(form.CanAcceptWebMessage(isWebViewVisible: true));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TryPromptSetupPassKey_AbortsInsteadOfPlaintextWhenReadyPromptCrashesOrWindowCloses(bool failureReportedBeforeClose)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            List<string> messages = new List<string>();

            bool resolved = LoginForm.TryResolveManifestPassKey(
                manifest,
                null,
                prompt =>
                {
                    Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                        prompt,
                        commitImmediately: false,
                        (label, isPassword) =>
                        {
                            using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                            {
                                form.BeginModernUiNavigation(navigationId: 1);
                                Assert.True(form.ShouldHandleNavigationCompleted(1, out int gen1));
                                Assert.True(form.TryHandleNavigationCompletedResult(1, gen1, succeeded: true));
                                Assert.True(form.UiReady);

                                if (failureReportedBeforeClose)
                                {
                                    Assert.True(form.HandleModernUiProcessFailed(CoreWebView2ProcessFailedKind.RenderProcessExited));
                                    form.SimulateWindowClose();
                                }
                                else
                                {
                                    // User closes the native window before WebView2 delivers ProcessFailed.
                                    form.SimulateWindowClose();
                                }

                                return form.ToPassKeyPromptResult();
                            }
                        },
                        message => messages.Add(message),
                        out string setupPassKey);
                    return (status, setupPassKey);
                },
                (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                _ => throw new InvalidOperationException("Invalid passkey error should not run on an empty manifest."),
                out string resolvedPassKey);

            Assert.False(resolved);
            Assert.Null(resolvedPassKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.DoesNotContain("WARNING: You chose to not encrypt", message);
        }

        [Fact]
        public void InputForm_DistinguishesBlankAcceptExplicitCancelAndWindowClose()
        {
            using (InputForm blankAcceptForm = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                blankAcceptForm.SubmitInput(string.Empty);
                Manifest.PassKeyPromptResult result = blankAcceptForm.ToPassKeyPromptResult();
                Assert.True(result.Canceled);
                Assert.True(result.Accepted);
                Assert.False(result.ExplicitCancel);
                Assert.True(result.UiReady);
                Assert.False(result.LoadFailed);
                Assert.Equal(string.Empty, result.Value);
            }

            using (InputForm canceledForm = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                canceledForm.CancelInput();
                Manifest.PassKeyPromptResult result = canceledForm.ToPassKeyPromptResult();
                Assert.True(result.Canceled);
                Assert.False(result.Accepted);
                Assert.True(result.ExplicitCancel);
                Assert.True(result.UiReady);
                Assert.False(result.LoadFailed);
            }

            using (InputForm windowClosedForm = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                windowClosedForm.SimulateWindowClose();
                Manifest.PassKeyPromptResult result = windowClosedForm.ToPassKeyPromptResult();
                Assert.True(result.Canceled);
                Assert.False(result.Accepted);
                Assert.False(result.ExplicitCancel);
                Assert.True(result.UiReady);
                Assert.False(result.LoadFailed);
            }

            using (InputForm acceptedForm = LoginForm.CreateCurrentPassKeyPrompt(initializeModernUi: false))
            {
                acceptedForm.SubmitInput("secret-passkey");
                Manifest.PassKeyPromptResult result = acceptedForm.ToPassKeyPromptResult();
                Assert.False(result.Canceled);
                Assert.True(result.Accepted);
                Assert.False(result.ExplicitCancel);
                Assert.True(result.UiReady);
                Assert.False(result.LoadFailed);
                Assert.Equal("secret-passkey", result.Value);
            }
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("null", false)]
        [InlineData("false", false)]
        [InlineData("\"true\"", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void InputForm_IsScriptTrueResultRequiresExactBooleanTrue(string scriptResult, bool expected)
        {
            Assert.Equal(expected, InputForm.IsScriptTrueResult(scriptResult));
        }

        [Fact]
        public void PromptSetupPassKey_DeferredCommitDoesNotEncryptOrPersistManifest()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            string manifestPath = Path.Combine(Manifest.GetExecutableDir(), "maFiles", "manifest.json");
            List<(string Label, bool IsPassword)> prompts = new List<(string, bool)>();
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    prompts.Add((label, isPassword));
                    return new Manifest.PassKeyPromptResult(canceled: false, value: "deferred-passkey", uiReady: true, loadFailed: false, accepted: true);
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Set, status);
            Assert.Equal("deferred-passkey", passKey);
            Assert.False(manifest.Encrypted);
            Assert.Empty(messages);
            Assert.Equal(2, prompts.Count);
            Assert.All(prompts, prompt => Assert.True(prompt.IsPassword));
            Assert.False(File.Exists(manifestPath));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void TryPromptSetupPassKey_ReturnsFailedWhenPromptFailsToLoad(bool initialLoadFailed, bool confirmLoadFailed)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (_, isPassword) =>
                {
                    Assert.True(isPassword);
                    promptIndex++;
                    if (promptIndex == 1)
                    {
                        return initialLoadFailed
                            ? new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: false, loadFailed: true, accepted: false)
                            : new Manifest.PassKeyPromptResult(canceled: false, value: "valid-passkey", uiReady: true, loadFailed: false, accepted: true);
                    }

                    return confirmLoadFailed
                        ? new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: false, loadFailed: true, accepted: false)
                        : new Manifest.PassKeyPromptResult(canceled: false, value: "valid-passkey", uiReady: true, loadFailed: false, accepted: true);
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.DoesNotContain("WARNING: You chose to not encrypt", message);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void TryPromptSetupPassKey_ReturnsFailedWhenPromptClosesBeforeUiReady(bool initialClosedEarly, bool confirmClosedEarly)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    Assert.True(isPassword);
                    promptIndex++;
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        if (promptIndex == 1)
                        {
                            if (initialClosedEarly)
                            {
                                form.BeginModernUiNavigation(navigationId: 1);
                                form.Canceled = true;
                            }
                            else
                            {
                                form.SubmitInput("valid-passkey");
                            }
                        }
                        else
                        {
                            if (confirmClosedEarly)
                            {
                                form.BeginModernUiNavigation(navigationId: 2);
                                form.Canceled = true;
                            }
                            else
                            {
                                form.SubmitInput("valid-passkey");
                            }
                        }

                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.DoesNotContain("WARNING: You chose to not encrypt", message);
        }

        [Fact]
        public void TryPromptSetupPassKey_ReturnsFailedWhenConfirmationDoesNotMatch()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (_, _) =>
                {
                    promptIndex++;
                    return promptIndex == 1
                        ? new Manifest.PassKeyPromptResult(canceled: false, value: "passkey-one", uiReady: true, loadFailed: false, accepted: true)
                        : new Manifest.PassKeyPromptResult(canceled: false, value: "passkey-two", uiReady: true, loadFailed: false, accepted: true);
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.Equal("Passkeys do not match.", Assert.Single(messages));
        }

        [Fact]
        public void TryPromptSetupPassKey_ReturnsFailedWithMismatchWhenConfirmationSubmittedBlank()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    promptIndex++;
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        form.SubmitInput(promptIndex == 1 ? "passkey-one" : string.Empty);
                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            Assert.Equal("Passkeys do not match.", Assert.Single(messages));
        }

        [Fact]
        public void TryPromptSetupPassKey_ReturnsFailedWhenConfirmationIsCanceled()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    promptIndex++;
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        if (promptIndex == 1)
                            form.SubmitInput("passkey-one");
                        else
                            form.CancelInput();

                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.DoesNotContain("WARNING: You chose to not encrypt", message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TryPromptSetupPassKey_ReturnsDeclinedUnencryptedOnlyWhenLoadedInitialPromptIsDeclined(bool submitBlank)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        if (submitBlank)
                            form.SubmitInput(string.Empty);
                        else
                            form.CancelInput();

                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.DeclinedUnencrypted, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.Contains("WARNING: You chose to not encrypt", message);
        }

        [Fact]
        public void SaveAccount_AllowsUnencryptedFirstSaveWhenEmptyManifestWasMarkedEncrypted()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.True(manifest.ChangeEncryptionKey(null, "orphan-passkey").Succeeded);
            Assert.True(manifest.Encrypted);
            Assert.Empty(manifest.Entries);

            SteamGuardAccount firstAccount = CreateAccount(76561198000001005UL, "recovered-unencrypted-user");
            StorageResult firstSave = manifest.SaveAccount(firstAccount, false, null);
            Assert.True(firstSave.Succeeded, firstSave.UserMessage);
            Assert.False(manifest.Encrypted);

            // Once an encrypted vault has entries, unencrypted saves remain rejected.
            Assert.True(manifest.ChangeEncryptionKey(null, "active-vault-passkey").Succeeded);
            Assert.True(manifest.Encrypted);
            StorageResult rejectedSave = manifest.SaveAccount(CreateAccount(76561198000001006UL, "must-be-encrypted"), false, null);
            Assert.False(rejectedSave.Succeeded);
        }

        [Fact]
        public void EvaluateConfirmationCodePrompt_RetriesAndPreservesPendingWhenConfirmationUiProcessFails()
        {
            using (InputForm form = new InputForm("Enter confirmation code", password: false, initializeModernUi: false))
            {
                form.BeginModernUiNavigation(1UL);
                Assert.True(form.ShouldHandleNavigationCompleted(1UL, out int generation));
                Assert.True(form.TryHandleNavigationCompletedResult(1UL, generation, succeeded: true));
                Assert.True(form.HandleModernUiProcessFailed(CoreWebView2ProcessFailedKind.RenderProcessExited));
                form.SimulateWindowClose();

                Assert.True(form.Canceled);
                Assert.True(form.LoadFailed);
                Assert.False(form.UiReady);

                int retryPrompts = 0;
                LoginForm.ConfirmationCodePromptDecision decision = LoginForm.EvaluateConfirmationCodePrompt(
                    form.ToPassKeyPromptResult(),
                    () =>
                    {
                        retryPrompts++;
                        return true;
                    });

                Assert.Equal(1, retryPrompts);
                Assert.Equal(LoginForm.ConfirmationCodePromptAction.RetryPrompt, decision.Action);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EvaluateConfirmationCodePrompt_KeepsPendingAndStopsWhenUserDeclinesRetryAfterUiFailure(bool markLoadFailed)
        {
            using (InputForm form = new InputForm("Enter confirmation code", password: false, initializeModernUi: false))
            {
                form.BeginModernUiNavigation(1UL);
                if (markLoadFailed)
                    form.MarkModernUiLoadFailed();
                form.SimulateWindowClose();

                int retryPrompts = 0;
                LoginForm.ConfirmationCodePromptDecision decision = LoginForm.EvaluateConfirmationCodePrompt(
                    form.ToPassKeyPromptResult(),
                    () =>
                    {
                        retryPrompts++;
                        return false;
                    });

                Assert.Equal(1, retryPrompts);
                Assert.Equal(LoginForm.ConfirmationCodePromptAction.KeepPendingAndStop, decision.Action);
            }
        }

        [Fact]
        public void EvaluateConfirmationCodePrompt_CancelsAndRemovesPendingOnlyOnExplicitInPageCancel()
        {
            using (InputForm form = new InputForm("Enter confirmation code", password: false, initializeModernUi: false))
            {
                form.CancelInput();

                int retryPrompts = 0;
                LoginForm.ConfirmationCodePromptDecision decision = LoginForm.EvaluateConfirmationCodePrompt(
                    form.ToPassKeyPromptResult(),
                    () =>
                    {
                        retryPrompts++;
                        return true;
                    });

                Assert.Equal(0, retryPrompts);
                Assert.Equal(LoginForm.ConfirmationCodePromptAction.CancelAndRemovePending, decision.Action);
            }
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(true, false)]
        public void EvaluateConfirmationCodePrompt_PreservesPendingOnReadyNativeWindowCloseOrBlankSubmit(bool submitBlank, bool retry)
        {
            using (InputForm form = new InputForm("Enter confirmation code", password: false, initializeModernUi: false))
            {
                form.BeginModernUiNavigation(1UL);
                Assert.True(form.ShouldHandleNavigationCompleted(1UL, out int generation));
                Assert.True(form.TryHandleNavigationCompletedResult(1UL, generation, succeeded: true));

                if (submitBlank)
                    form.SubmitInput(string.Empty);
                else
                    form.SimulateWindowClose();

                Assert.True(form.UiReady);
                Assert.False(form.LoadFailed);
                Assert.True(form.Canceled);
                Assert.False(form.ExplicitCancel);

                int retryPrompts = 0;
                LoginForm.ConfirmationCodePromptDecision decision = LoginForm.EvaluateConfirmationCodePrompt(
                    form.ToPassKeyPromptResult(),
                    () =>
                    {
                        retryPrompts++;
                        return retry;
                    });

                Assert.Equal(1, retryPrompts);
                Assert.Equal(
                    retry
                        ? LoginForm.ConfirmationCodePromptAction.RetryPrompt
                        : LoginForm.ConfirmationCodePromptAction.KeepPendingAndStop,
                    decision.Action);
            }
        }

        [Fact]
        public void EvaluateConfirmationCodePrompt_SubmitsCodeWhenReadyUiAcceptsInput()
        {
            using (InputForm form = new InputForm("Enter confirmation code", password: false, initializeModernUi: false))
            {
                form.SubmitInput("ABCDE");

                LoginForm.ConfirmationCodePromptDecision decision = LoginForm.EvaluateConfirmationCodePrompt(
                    form.ToPassKeyPromptResult(),
                    () => throw new InvalidOperationException("Retry prompt should not run when input was accepted."));

                Assert.Equal(LoginForm.ConfirmationCodePromptAction.SubmitCode, decision.Action);
                Assert.Equal("ABCDE", decision.ConfirmationCode);
            }
        }

        [Fact]
        public void ShouldPromptStartupPassKey_SkipsEmptyEncryptedVaultAndReachesFirstAccountRecovery()
        {
            // In-memory empty encrypted manifest must not prompt for an unverifiable startup key.
            Manifest emptyEncryptedManifest = Manifest.GenerateNewManifest(false);
            Assert.True(emptyEncryptedManifest.ChangeEncryptionKey(null, "orphan-passkey").Succeeded);
            Assert.True(emptyEncryptedManifest.Encrypted);
            Assert.Empty(emptyEncryptedManifest.Entries);
            Assert.False(MainForm.ShouldPromptStartupPassKey(emptyEncryptedManifest, null));

            // Persisted empty encrypted manifest loaded on startup normalizes on disk and reaches first-account setup.
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            Directory.CreateDirectory(maDir);
            string manifestPath = Path.Combine(maDir, "manifest.json");
            File.WriteAllText(
                manifestPath,
                "{\"encrypted\":true,\"first_run\":false,\"entries\":[]}");

            Manifest loadedEmptyManifest = Manifest.GetManifest(forceLoad: true);
            Assert.False(loadedEmptyManifest.Encrypted);
            Assert.Empty(loadedEmptyManifest.Entries);
            Assert.False(MainForm.ShouldPromptStartupPassKey(loadedEmptyManifest, null));
            Newtonsoft.Json.Linq.JObject persistedEmptyJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath));
            Assert.False(persistedEmptyJson.Value<bool>("encrypted"));
            Assert.Empty((Newtonsoft.Json.Linq.JArray)persistedEmptyJson["entries"]);

            bool resolved = LoginForm.TryResolveManifestPassKey(
                loadedEmptyManifest,
                suppliedPassKey: null,
                promptSetupPassKey: _ => (Manifest.SetupPassKeyStatus.Set, "fresh-passkey"),
                promptCurrentPassKey: (_, _) => throw new InvalidOperationException("Current passkey prompt should not run for empty manifest."),
                showInvalidPassKeyMessage: message => throw new InvalidOperationException("Unexpected error: " + message),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal("fresh-passkey", resolvedPassKey);

            SteamGuardAccount firstAccount = CreateAccount(76561198000001007UL, "startup-recovered-user");
            StorageResult saveResult = loadedEmptyManifest.SaveAccount(firstAccount, encrypt: true, passKey: resolvedPassKey);
            Assert.True(saveResult.Succeeded, saveResult.UserMessage);
            Assert.True(loadedEmptyManifest.Encrypted);
            Assert.True(MainForm.ShouldPromptStartupPassKey(loadedEmptyManifest, null));
            Assert.False(MainForm.ShouldPromptStartupPassKey(loadedEmptyManifest, resolvedPassKey));

            // Persisted encrypted manifest whose only entry's maFile was deleted also recovers cleanly on disk.
            string savedMaFilePath = Path.Combine(maDir, Assert.Single(loadedEmptyManifest.Entries).Filename);
            File.Delete(savedMaFilePath);

            Manifest loadedMissingFileManifest = Manifest.GetManifest(forceLoad: true);
            Assert.False(loadedMissingFileManifest.Encrypted);
            Assert.Empty(loadedMissingFileManifest.Entries);
            Assert.False(MainForm.ShouldPromptStartupPassKey(loadedMissingFileManifest, null));
            Newtonsoft.Json.Linq.JObject persistedMissingFileJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath));
            Assert.False(persistedMissingFileJson.Value<bool>("encrypted"));
            Assert.Empty((Newtonsoft.Json.Linq.JArray)persistedMissingFileJson["entries"]);
        }

        [Theory]
        [InlineData("{\"action\":{}}")]
        [InlineData("{\"action\":[]}")]
        [InlineData("{\"action\":123}")]
        [InlineData("{\"action\":null}")]
        [InlineData("{\"action\":\"012345678901234567890123456789012\"}")]
        [InlineData("{not-json")]
        [InlineData("")]
        public void TryParseWebMessagePayload_RejectsInvalidOrNonStringActionTokensWithoutThrowing(string json)
        {
            bool parsed = InputForm.TryParseWebMessagePayload(json, out Newtonsoft.Json.Linq.JObject payload, out string action);
            Assert.False(parsed);
            Assert.Null(payload);
            Assert.Null(action);
        }

        [Fact]
        public void HandleWebMessageJson_GatesAcceptAndCancelOnReadinessAndVisibility()
        {
            using (InputForm form = new InputForm("Enter passkey", password: true, initializeModernUi: false))
            {
                form.BeginModernUiNavigation(10UL);

                // Non-string action tokens never throw and never alter state.
                form.HandleWebMessageJson("{\"action\":{\"nested\":true}}", isWebViewVisible: true);
                form.HandleWebMessageJson("{\"action\":[\"accept\"]}", isWebViewVisible: true);
                Assert.False(form.Accepted);
                Assert.False(form.ExplicitCancel);

                // Messages before UI readiness are ignored.
                form.HandleWebMessageJson("{\"action\":\"accept\",\"value\":\"early-secret\"}", isWebViewVisible: true);
                form.HandleWebMessageJson("{\"action\":\"cancel\"}", isWebViewVisible: true);
                Assert.False(form.Accepted);
                Assert.False(form.ExplicitCancel);
                Assert.Equal(string.Empty, form.txtBox.Text);

                // Ready UI still ignores messages while WebView is hidden.
                Assert.True(form.ShouldHandleNavigationCompleted(10UL, out int generation));
                Assert.True(form.TryHandleNavigationCompletedResult(10UL, generation, succeeded: true));
                form.HandleWebMessageJson("{\"action\":\"accept\",\"value\":\"hidden-secret\"}", isWebViewVisible: false);
                Assert.False(form.Accepted);

                // After a terminal process failure, messages are ignored even if visible flag is passed.
                Assert.True(form.HandleModernUiProcessFailed(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive));
                form.HandleWebMessageJson("{\"action\":\"cancel\"}", isWebViewVisible: true);
                Assert.False(form.ExplicitCancel);

                // Once reloaded and visible, accept succeeds through the production handler.
                form.BeginModernUiNavigation(11UL);
                Assert.True(form.ShouldHandleNavigationCompleted(11UL, out int reloadedGeneration));
                Assert.True(form.TryHandleNavigationCompletedResult(11UL, reloadedGeneration, succeeded: true));
                form.HandleWebMessageJson("{\"action\":\"accept\",\"value\":\"valid-secret\"}", isWebViewVisible: true);
                Assert.True(form.Accepted);
                Assert.False(form.Canceled);
                Assert.Equal("valid-secret", form.txtBox.Text);
            }
        }

        [Fact]
        public async Task PrepareInitialEnrollmentAsync_DoesNotStartEnrollmentWhenCanceledDuringPassKeyPrompt()
        {
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                bool enrollmentStarted = false;

                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    LoginForm.PrepareInitialEnrollmentAsync(
                        () =>
                        {
                            cts.Cancel();
                            return (true, "resolved-before-cancel");
                        },
                        _ =>
                        {
                            enrollmentStarted = true;
                            return Task.FromResult(new AuthenticatorEnrollmentOutcome(AuthenticatorEnrollmentResult.AwaitingFinalization));
                        },
                        cts.Token));

                Assert.False(enrollmentStarted);
            }
        }

        [Fact]
        public void TryPromptSetupPassKey_CommitsImmediatelyWhenRequested()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount account = CreateAccount(76561198000001009UL, "immediate-commit-user");
            Assert.True(manifest.SaveAccount(account, encrypt: false, passKey: null).Succeeded);
            Assert.False(manifest.Encrypted);

            List<string> messages = new List<string>();
            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: true,
                (label, isPassword) =>
                {
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        form.SubmitInput("immediate-secret");
                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Set, status);
            Assert.Equal("immediate-secret", passKey);
            Assert.True(manifest.Encrypted);
            Assert.True(manifest.VerifyPasskey("immediate-secret"));
            Assert.Equal("Passkey successfully set.", Assert.Single(messages));
        }

        [Fact]
        public void TryPromptSetupPassKey_ReturnsFailedWhenConfirmationWindowClosed()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            int promptIndex = 0;
            List<string> messages = new List<string>();

            Manifest.SetupPassKeyStatus status = manifest.TryPromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    promptIndex++;
                    using (InputForm form = new InputForm(label, isPassword, initializeModernUi: false))
                    {
                        if (promptIndex == 1)
                            form.SubmitInput("passkey-one");
                        else
                            form.SimulateWindowClose();

                        return form.ToPassKeyPromptResult();
                    }
                },
                message => messages.Add(message),
                out string passKey);

            Assert.Equal(Manifest.SetupPassKeyStatus.Failed, status);
            Assert.Null(passKey);
            Assert.False(manifest.Encrypted);
            string message = Assert.Single(messages);
            Assert.DoesNotContain("WARNING: You chose to not encrypt", message);
        }

        [Fact]
        public void ApplyRefreshedSession_DoesNotPromotePendingAuthenticatorToFullyEnrolled()
        {
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001010UL, "pending-refresh-user");
            pendingAccount.FullyEnrolled = false;
            SessionData originalSession = pendingAccount.Session;
            SessionData refreshedSession = new SessionData
            {
                SteamID = 76561198000001010UL,
                AccessToken = "renewed-access-token",
                RefreshToken = "renewed-refresh-token"
            };

            // A failed save restores the original session and keeps FullyEnrolled false.
            bool failedSave = LoginForm.ApplyRefreshedSession(pendingAccount, refreshedSession, () => false);
            Assert.False(failedSave);
            Assert.Same(originalSession, pendingAccount.Session);
            Assert.False(pendingAccount.FullyEnrolled);

            // A successful session refresh updates the session without promoting FullyEnrolled to true.
            bool succeededSave = LoginForm.ApplyRefreshedSession(pendingAccount, refreshedSession, () => true);
            Assert.True(succeededSave);
            Assert.Same(refreshedSession, pendingAccount.Session);
            Assert.False(pendingAccount.FullyEnrolled);
        }

        [Fact]
        public void PendingEnrollmentAccount_SuppressesLoginTokenAndShowsPendingLabel()
        {
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001011UL, "pending-account");
            pendingAccount.FullyEnrolled = false;

            Assert.True(MainForm.IsPendingEnrollment(pendingAccount));
            Assert.Equal(string.Empty, MainForm.GetDisplayLoginToken(pendingAccount, 1700000000L, "PREV1"));
            Assert.Equal("Account: pending-account (pending activation)", MainForm.GetAccountDisplayTitle(pendingAccount));

            pendingAccount.FullyEnrolled = true;
            Assert.False(MainForm.IsPendingEnrollment(pendingAccount));
            string activeCode = MainForm.GetDisplayLoginToken(pendingAccount, 1700000000L);
            Assert.Equal(5, activeCode.Length);
            Assert.Equal("Account: pending-account", MainForm.GetAccountDisplayTitle(pendingAccount));
        }

        [Fact]
        public void IndexHtml_MarksPendingActivationAccountsAndDisablesActiveOperations()
        {
            string indexHtmlPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Steam Desktop Authenticator", "ui", "index.html"));
            string html = File.ReadAllText(indexHtmlPath);

            Assert.Contains("pendingActivation = false", html);
            Assert.Contains("(pending activation)", html);
            Assert.Contains("!account.pendingActivation", html);
            Assert.Contains("Complete Steam activation to use QR login", html);
            Assert.Contains("const canCopy = hasAccounts && !currentAccPending;", html);
            Assert.Contains("const canUnlinkSteamGuard = Boolean(account.steamId) && !Boolean(account.pendingActivation);", html);
            Assert.Contains("Verify Steam activation", html);
            Assert.Contains("remove-account-local-desc", html);
            Assert.Contains("document.getElementById('account-title').innerText = \"Account: ...\";", html);
            Assert.Contains("updateToken(\"\");", html);
        }

        private sealed class FakeStatusTransport : IAuthenticatorProtocolTransport
        {
            private readonly Func<string, string, object, SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>> handler;

            public FakeStatusTransport(Func<string, string, object, SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>> handler)
            {
                this.handler = handler;
            }

            public Task<SteamProtocolResponse<TResponse>> SendAsync<TRequest, TResponse>(
                string service,
                string method,
                TRequest request,
                string accessToken,
                Google.Protobuf.MessageParser<TResponse> responseParser,
                SteamProtocolRequestMethod requestMethod = SteamProtocolRequestMethod.Post,
                CancellationToken cancellationToken = default)
                where TRequest : class, Google.Protobuf.IMessage<TRequest>
                where TResponse : class, Google.Protobuf.IMessage<TResponse>
            {
                SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response> response = handler(service, method, request);
                return Task.FromResult(new SteamProtocolResponse<TResponse>
                {
                    Result = response.Result,
                    Body = (TResponse)(object)response.Body,
                    ErrorMessage = response.ErrorMessage
                });
            }
        }

        [Fact]
        public async Task VerifyAuthenticatorActivationAsync_ValidatesSteamStateAndIdentityMatch()
        {
            SteamGuardAccount account = CreateAccount(76561198000001012UL, "verify-activation-user");
            account.TokenGID = "gid-local-123";
            account.DeviceID = "android:11111111-1111-1111-1111-111111111111";
            account.FullyEnrolled = false;

            // Matching active Steam authenticator confirms this record.
            var matchingTransport = new FakeStatusTransport((_, _, _) =>
                new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
                {
                    Result = 1,
                    Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                    {
                        State = 1,
                        TokenGid = "gid-local-123",
                        DeviceIdentifier = "android:11111111-1111-1111-1111-111111111111"
                    }
                });
            Assert.Equal(
                AuthenticatorActivationStatus.ActiveForThisRecord,
                await account.VerifyAuthenticatorActivationAsync(matchingTransport));

            // Inactive Steam state keeps record genuinely pending.
            var inactiveTransport = new FakeStatusTransport((_, _, _) =>
                new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
                {
                    Result = 1,
                    Body = new SteamAuth.Protocol.CTwoFactor_Status_Response { State = 0 }
                });
            Assert.Equal(
                AuthenticatorActivationStatus.NotActive,
                await account.VerifyAuthenticatorActivationAsync(inactiveTransport));

            // Mismatched token_gid prevents falsely promoting an unfinalized local record.
            var mismatchedGidTransport = new FakeStatusTransport((_, _, _) =>
                new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
                {
                    Result = 1,
                    Body = new SteamAuth.Protocol.CTwoFactor_Status_Response
                    {
                        State = 1,
                        TokenGid = "gid-other-999",
                        DeviceIdentifier = "android:11111111-1111-1111-1111-111111111111"
                    }
                });
            Assert.Equal(
                AuthenticatorActivationStatus.ActiveForDifferentAuthenticator,
                await account.VerifyAuthenticatorActivationAsync(mismatchedGidTransport));

            // Active Steam response with no corroborating token_gid or device_identifier fails closed as Unverified.
            var uncorroboratedTransport = new FakeStatusTransport((_, _, _) =>
                new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
                {
                    Result = 1,
                    Body = new SteamAuth.Protocol.CTwoFactor_Status_Response { State = 1 }
                });
            Assert.Equal(
                AuthenticatorActivationStatus.Unverified,
                await account.VerifyAuthenticatorActivationAsync(uncorroboratedTransport));

            // Missing access token reports SessionInvalid.
            account.Session.AccessToken = string.Empty;
            Assert.Equal(
                AuthenticatorActivationStatus.SessionInvalid,
                await account.VerifyAuthenticatorActivationAsync(matchingTransport));
        }

        [Fact]
        public async Task TryRecoverPendingAccountActivationAsync_PromotesOnlyWhenVerifiedAndDurablySaved()
        {
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001013UL, "startup-pending-user");
            pendingAccount.FullyEnrolled = false;

            // Not active on Steam -> stays pending and does not persist.
            bool persisted = false;
            bool recoveredWhenNotActive = await MainForm.TryRecoverPendingAccountActivationAsync(
                pendingAccount,
                _ => Task.FromResult(AuthenticatorActivationStatus.NotActive),
                _ => { persisted = true; return true; },
                CancellationToken.None);
            Assert.False(recoveredWhenNotActive);
            Assert.False(persisted);
            Assert.False(pendingAccount.FullyEnrolled);

            // Verified active on Steam, but durable save fails -> reverts FullyEnrolled back to false.
            bool recoveredWhenSaveFails = await MainForm.TryRecoverPendingAccountActivationAsync(
                pendingAccount,
                _ => Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord),
                _ => false,
                CancellationToken.None);
            Assert.False(recoveredWhenSaveFails);
            Assert.False(pendingAccount.FullyEnrolled);

            // Verified active on Steam and durable save succeeds -> promoted to FullyEnrolled.
            bool recoveredWhenSaveSucceeds = await MainForm.TryRecoverPendingAccountActivationAsync(
                pendingAccount,
                _ => Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord),
                account => account.FullyEnrolled,
                CancellationToken.None);
            Assert.True(recoveredWhenSaveSucceeds);
            Assert.True(pendingAccount.FullyEnrolled);
        }

        [Fact]
        public async Task TryRecoverExistingPendingAuthenticatorAsync_RecoversVerifiedPendingRecordAndPersistsEnrolledState()
        {
            const string passKey = "recovery-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001014UL, "pending-recovery-user");
            pendingAccount.TokenGID = "gid-recover-1";
            pendingAccount.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: true, passKey: passKey).Succeeded);

            SessionData freshSession = new SessionData
            {
                SteamID = 76561198000001014UL,
                AccessToken = "fresh-access-token",
                RefreshToken = "fresh-refresh-token"
            };

            // When Steam reports a different authenticator, the pending record remains unpromoted with ConfirmedMismatch.
            LoginForm.PendingAuthenticatorRecoveryResult mismatch = await LoginForm.TryRecoverExistingPendingAuthenticatorAsync(
                manifest,
                freshSession,
                passKey,
                (_, _) => Task.FromResult(AuthenticatorActivationStatus.ActiveForDifferentAuthenticator),
                CancellationToken.None);
            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.ConfirmedMismatch, mismatch.Status);
            Assert.Equal(AuthenticatorActivationStatus.ActiveForDifferentAuthenticator, mismatch.ActivationStatus);
            SteamGuardAccount stillPending = Assert.Single(manifest.GetAllAccounts(passKey));
            Assert.False(stillPending.FullyEnrolled);
            Assert.Equal("test-access-token", stillPending.Session.AccessToken);

            // When Steam verification is inconclusive (transient failure or invalid session), report Inconclusive without telling the user to unlink.
            LoginForm.PendingAuthenticatorRecoveryResult inconclusive = await LoginForm.TryRecoverExistingPendingAuthenticatorAsync(
                manifest,
                freshSession,
                passKey,
                (_, _) => Task.FromResult(AuthenticatorActivationStatus.Unverified),
                CancellationToken.None);
            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Inconclusive, inconclusive.Status);
            Assert.False(Assert.Single(manifest.GetAllAccounts(passKey)).FullyEnrolled);

            // When Steam confirms this record is active, it is durably saved with FullyEnrolled = true and the fresh session.
            LoginForm.PendingAuthenticatorRecoveryResult recovered = await LoginForm.TryRecoverExistingPendingAuthenticatorAsync(
                manifest,
                freshSession,
                passKey,
                (candidate, _) =>
                {
                    Assert.Same(freshSession, candidate.Session);
                    return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord);
                },
                CancellationToken.None);
            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Recovered, recovered.Status);

            SteamGuardAccount reloaded = Assert.Single(manifest.GetAllAccounts(passKey));
            Assert.True(reloaded.FullyEnrolled);
            Assert.Equal("fresh-access-token", reloaded.Session.AccessToken);
            Assert.Equal(5, MainForm.GetDisplayLoginToken(reloaded, 1700000000L).Length);
        }

        [Fact]
        public async Task TryRecoverPendingAccountActivationAsync_DoesNotRecreateDeletedEncryptedAccountAsPlaintext()
        {
            const string passKey = "encrypted-pending-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001016UL, "deleted-during-startup-user");
            pendingAccount.TokenGID = "gid-delete-race";
            pendingAccount.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: true, passKey: passKey).Succeeded);
            Assert.True(manifest.Encrypted);

            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            SessionData originalSession = pendingAccount.Session;
            long revisionAtCheck = manifest.GetAccountStorageRevision(originalSession.SteamID);

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { pendingAccount });

                // Simulate user locally deleting the last encrypted pending account while QueryStatus is in flight.
                bool recovered = await MainForm.TryRecoverPendingAccountActivationAsync(
                    pendingAccount,
                    _ =>
                    {
                        Assert.True(manifest.RemoveAccount(pendingAccount, passKey));
                        Assert.False(manifest.Encrypted);
                        Assert.Empty(manifest.Entries);
                        return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord);
                    },
                    activated => form.PersistLoginSession(activated, originalSession, revisionAtCheck),
                    CancellationToken.None);

                Assert.False(recovered);
                Assert.False(pendingAccount.FullyEnrolled);
                Assert.Empty(manifest.Entries);
                Assert.False(manifest.Encrypted);
                Assert.Empty(Directory.GetFiles(maDir, "*.maFile"));
            }
        }

        [Fact]
        public async Task UserFormAuthenticator_GeneratesDeviceCodeForSavedPendingAccountDuringRecovery()
        {
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001017UL, "device-code-recovery-user");
            pendingAccount.FullyEnrolled = false;

            using (Form owner = new Form())
            {
                var authenticator = new UserFormAuthenticator(pendingAccount, owner, CancellationToken.None);
                string deviceCode = await authenticator.GetDeviceCodeAsync(previousCodeWasIncorrect: false);
                Assert.False(string.IsNullOrWhiteSpace(deviceCode));
                Assert.Equal(5, deviceCode.Length);
            }
        }

        [Fact]
        public async Task VerifyAuthenticatorActivationAsync_TreatsMissingProtobufStateAsUnverified()
        {
            SteamGuardAccount account = CreateAccount(76561198000001018UL, "empty-status-user");
            account.TokenGID = "gid-empty-state";
            account.FullyEnrolled = false;

            // Empty protobuf payload (HasState == false, State defaults to 0) must NOT be treated as NotActive.
            var emptyPayload = new SteamAuth.Protocol.CTwoFactor_Status_Response();
            Assert.False(emptyPayload.HasState);
            var emptyStatusTransport = new FakeStatusTransport((_, _, _) =>
                new SteamProtocolResponse<SteamAuth.Protocol.CTwoFactor_Status_Response>
                {
                    Result = 1,
                    Body = emptyPayload
                });

            Assert.Equal(
                AuthenticatorActivationStatus.Unverified,
                await account.VerifyAuthenticatorActivationAsync(emptyStatusTransport));
        }

        [Fact]
        public async Task PersistRecoveredPendingActivation_SucceedsAcrossUiOnlyReloadWhenStorageRevisionUnchanged()
        {
            const string passKey = "ui-reload-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount originalAccount = CreateAccount(76561198000001019UL, "ui-reload-pending-user");
            originalAccount.TokenGID = "gid-ui-reload";
            originalAccount.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(originalAccount, encrypt: true, passKey: passKey).Succeeded);

            SessionData originalSession = originalAccount.Session;
            long revisionAtCheck = manifest.GetAccountStorageRevision(originalSession.SteamID);

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { originalAccount });

                // Simulate WebView2 NavigationCompleted -> loadAccountsList() replacing the in-memory object
                // while the on-disk storage revision remains unchanged.
                bool recovered = await MainForm.TryRecoverPendingAccountActivationAsync(
                    originalAccount,
                    _ =>
                    {
                        SteamGuardAccount[] reloadedInstances = manifest.GetAllAccounts(passKey);
                        Assert.NotSame(originalAccount, reloadedInstances[0]);
                        typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, reloadedInstances);
                        return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord);
                    },
                    activated => form.PersistRecoveredPendingActivation(activated, originalSession, revisionAtCheck),
                    CancellationToken.None);

                Assert.True(recovered);
                SteamGuardAccount currentInMemory = Assert.Single(GetPrivateField<SteamGuardAccount[]>(form, "allAccounts"));
                Assert.True(currentInMemory.FullyEnrolled);
                SteamGuardAccount persisted = Assert.Single(manifest.GetAllAccounts(passKey));
                Assert.True(persisted.FullyEnrolled);
            }
        }

        [Fact]
        public async Task QueuedPendingVerification_RejectsStaleDialogAndSaveWhenReplacementMaFileImported()
        {
            const string passKey = "queued-replacement-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount oldPending = CreateAccount(76561198000001020UL, "queued-user");
            oldPending.SharedSecret = Convert.ToBase64String(new byte[20]);
            oldPending.TokenGID = "gid-old-pending";
            oldPending.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(oldPending, encrypt: true, passKey: passKey).Succeeded);

            long revisionAtQueue = manifest.GetAccountStorageRevision(oldPending.Session.SteamID);
            int dialogCalls = 0;

            // Create a replacement imported record with distinct SharedSecret and TokenGID for the same SteamID.
            SteamGuardAccount importedReplacement = CreateAccount(76561198000001020UL, "queued-user");
            byte[] replacementSecret = new byte[20];
            replacementSecret[0] = 0x5A;
            importedReplacement.SharedSecret = Convert.ToBase64String(replacementSecret);
            importedReplacement.TokenGID = "gid-imported-replacement";
            importedReplacement.FullyEnrolled = true;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showRefreshLoginDialog: (_, _) =>
                {
                    dialogCalls++;
                    return (true, passKey);
                }))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { oldPending });

                // Hold the per-account operation while simulating a concurrent import of a replacement maFile.
                TaskCompletionSource<bool> releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> lockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                MethodInfo runOperation = typeof(MainForm).GetMethod("RunSteamAccountOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(runOperation);
                MethodInfo closedRunOperation = runOperation.MakeGenericMethod(typeof(bool));

                Func<Task<bool>> holdLock = async () =>
                {
                    lockAcquired.SetResult(true);
                    await releaseGate.Task;
                    return true;
                };
                Task<bool> activeStartupTask = (Task<bool>)closedRunOperation.Invoke(form, new object[] { oldPending, holdLock, CancellationToken.None, null });
                await lockAcquired.Task;

                Task<bool> queuedVerificationTask = InvokePrivateAsyncMethod<bool>(form, "PromptRefreshLoginAsync", oldPending);

                // While queued, import the replacement account (bumps storage revision and updates allAccounts).
                Assert.True(manifest.SaveAccount(importedReplacement, encrypt: true, passKey: passKey).Succeeded);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, manifest.GetAllAccounts(passKey));
                releaseGate.SetResult(true);

                await activeStartupTask;
                bool queuedResult = await queuedVerificationTask;

                Assert.False(queuedResult);
                Assert.Equal(0, dialogCalls);

                // Even if LoginForm's recovery helper runs directly with the revision bound before queueing,
                // it refuses to overwrite the imported replacement record.
                SessionData freshSession = new SessionData
                {
                    SteamID = 76561198000001020UL,
                    AccessToken = "fresh-access",
                    RefreshToken = "fresh-refresh"
                };
                LoginForm.PendingAuthenticatorRecoveryResult staleSaveResult = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                    manifest,
                    oldPending,
                    freshSession,
                    passKey,
                    (_, _) => Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord),
                    CancellationToken.None,
                    expectedStorageRevision: revisionAtQueue);

                Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.SaveFailed, staleSaveResult.Status);
                SteamGuardAccount preserved = Assert.Single(manifest.GetAllAccounts(passKey));
                Assert.Equal("gid-imported-replacement", preserved.TokenGID);
                Assert.Equal(importedReplacement.SharedSecret, preserved.SharedSecret);
            }
        }

        [Fact]
        public async Task RecoverPendingAccountOnStartupAsync_HandlesExpiredRefreshWithValidAccessAndSingleSessionInvalidRetry()
        {
            // Case 1: Access token is still valid even though refresh token expired -> verifies without refreshing.
            SteamGuardAccount validAccessAccount = CreateAccount(76561198000001021UL, "valid-access-only");
            validAccessAccount.FullyEnrolled = false;
            int refreshCalls = 0;
            int verifyCalls = 0;
            long persistedRevision = -1;

            bool recoveredValidAccess = await MainForm.RecoverPendingAccountOnStartupAsync(
                validAccessAccount,
                initialStorageRevision: 4L,
                isAccessTokenExpired: () => false,
                isRefreshTokenExpired: () => true,
                refreshAccessTokenAsync: (rev, _) => { refreshCalls++; return Task.FromResult(rev + 1); },
                getCurrentStorageRevision: () => 4L,
                verifyActivationAsync: _ => { verifyCalls++; return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord); },
                persistActivatedAccount: (_, rev) => { persistedRevision = rev; return true; },
                CancellationToken.None);

            Assert.True(recoveredValidAccess);
            Assert.Equal(0, refreshCalls);
            Assert.Equal(1, verifyCalls);
            Assert.Equal(4L, persistedRevision);
            Assert.True(validAccessAccount.FullyEnrolled);

            // Case 2: Initial QueryStatus returns SessionInvalid while refresh token is valid -> refreshes once with bound revision and retries.
            SteamGuardAccount retryAccount = CreateAccount(76561198000001022UL, "retry-session-invalid");
            retryAccount.FullyEnrolled = false;
            long currentRevision = 7L;
            int retryRefreshCalls = 0;
            int retryVerifyCalls = 0;
            long retryPersistedRevision = -1;

            bool recoveredAfterRetry = await MainForm.RecoverPendingAccountOnStartupAsync(
                retryAccount,
                initialStorageRevision: 7L,
                isAccessTokenExpired: () => false,
                isRefreshTokenExpired: () => false,
                refreshAccessTokenAsync: (expectedRev, _) =>
                {
                    Assert.Equal(7L, expectedRev);
                    retryRefreshCalls++;
                    currentRevision++;
                    return Task.FromResult(currentRevision);
                },
                getCurrentStorageRevision: () => currentRevision,
                verifyActivationAsync: _ =>
                {
                    retryVerifyCalls++;
                    return Task.FromResult(retryVerifyCalls == 1
                        ? AuthenticatorActivationStatus.SessionInvalid
                        : AuthenticatorActivationStatus.ActiveForThisRecord);
                },
                persistActivatedAccount: (_, rev) => { retryPersistedRevision = rev; return true; },
                CancellationToken.None);

            Assert.True(recoveredAfterRetry);
            Assert.Equal(1, retryRefreshCalls);
            Assert.Equal(2, retryVerifyCalls);
            Assert.Equal(8L, retryPersistedRevision);
            Assert.True(retryAccount.FullyEnrolled);
        }

        [Fact]
        public void GetRefreshLoginExplanation_TailorsCopyForPendingVsEnrolledAccounts()
        {
            SteamGuardAccount pending = CreateAccount(76561198000001023UL, "pending-explanation-user");
            pending.FullyEnrolled = false;
            Assert.Contains("verify whether this pending authenticator is active", LoginForm.GetRefreshLoginExplanation(pending));

            pending.FullyEnrolled = true;
            Assert.Contains("credentials have expired", LoginForm.GetRefreshLoginExplanation(pending));
        }

        [Fact]
        public async Task PersistRefreshedPendingSession_SucceedsAcrossUiOnlyReloadDuringTokenRefreshAndCompletesStartupRecovery()
        {
            const string passKey = "refresh-reload-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount originalAccount = CreateAccount(76561198000001024UL, "refresh-reload-pending");
            originalAccount.TokenGID = "gid-refresh-reload";
            originalAccount.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(originalAccount, encrypt: true, passKey: passKey).Succeeded);

            SessionData originalSession = originalAccount.Session;
            long snapshotRevision = manifest.GetAccountStorageRevision(originalSession.SteamID);
            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { originalAccount });

                int verifyCalls = 0;
                bool recovered = await MainForm.RecoverPendingAccountOnStartupAsync(
                    originalAccount,
                    snapshotRevision,
                    isAccessTokenExpired: () => false,
                    isRefreshTokenExpired: () => false,
                    refreshAccessTokenAsync: (expectedRevision, ct) => form.RefreshAndPersistPendingAccessTokenAsync(
                        originalAccount,
                        expectedRevision,
                        (session, _) =>
                        {
                            // Simulate WebView2 NavigationCompleted -> loadAccountsList() replacing allAccounts
                            // while RefreshAccessToken is awaiting Steam.
                            SteamGuardAccount[] reloadedInstances = manifest.GetAllAccounts(passKey);
                            Assert.NotSame(originalAccount, reloadedInstances[0]);
                            typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, reloadedInstances);

                            session.AccessToken = "refreshed-access-after-ui-reload";
                            return Task.CompletedTask;
                        },
                        ct),
                    getCurrentStorageRevision: () => manifest.GetAccountStorageRevision(originalSession.SteamID),
                    verifyActivationAsync: _ =>
                    {
                        verifyCalls++;
                        return Task.FromResult(verifyCalls == 1
                            ? AuthenticatorActivationStatus.SessionInvalid
                            : AuthenticatorActivationStatus.ActiveForThisRecord);
                    },
                    persistActivatedAccount: (activated, expectedRevision) =>
                        form.PersistRecoveredPendingActivation(activated, originalSession, expectedRevision),
                    CancellationToken.None);

                Assert.True(recovered);
                Assert.Equal(2, verifyCalls);
                SteamGuardAccount persisted = Assert.Single(manifest.GetAllAccounts(passKey));
                Assert.True(persisted.FullyEnrolled);
                Assert.Equal("refreshed-access-after-ui-reload", persisted.Session.AccessToken);
            }
        }

        [Fact]
        public async Task StartupPendingRecovery_DoesNotOverwriteReplacementBackupImportedDuringFirstQueryStatusOrBeforeStart()
        {
            const string passKey = "import-during-query-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount originalPending = CreateAccount(76561198000001028UL, "import-race-user");
            originalPending.TokenGID = "gid-import-race";
            originalPending.FullyEnrolled = false;
            originalPending.Session.AccessToken = "old-access-token";
            Assert.True(manifest.SaveAccount(originalPending, encrypt: true, passKey: passKey).Succeeded);

            SessionData originalSession = originalPending.Session;
            long snapshotRevision = manifest.GetAccountStorageRevision(originalSession.SteamID);

            SteamGuardAccount importedBackup = CreateAccount(76561198000001028UL, "import-race-user");
            importedBackup.TokenGID = "gid-import-race";
            importedBackup.FullyEnrolled = false;
            importedBackup.Session.AccessToken = "imported-fresh-access";
            importedBackup.Session.RefreshToken = "imported-fresh-refresh";

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { originalPending });

                int refreshAttempts = 0;
                int verifyCalls = 0;

                // Case 1: Backup with identical SteamID & secrets is imported while first QueryStatus is in flight,
                // then first QueryStatus returns SessionInvalid. Bound snapshotRevision rejects the retry save.
                Exception staleException = await Assert.ThrowsAnyAsync<Exception>(() => MainForm.RecoverPendingAccountOnStartupAsync(
                    originalPending,
                    snapshotRevision,
                    isAccessTokenExpired: () => false,
                    isRefreshTokenExpired: () => false,
                    refreshAccessTokenAsync: (expectedRevision, ct) => form.RefreshAndPersistPendingAccessTokenAsync(
                        originalPending,
                        expectedRevision,
                        (session, _) =>
                        {
                            refreshAttempts++;
                            session.AccessToken = "stale-refreshed-access";
                            return Task.CompletedTask;
                        },
                        ct),
                    getCurrentStorageRevision: () => manifest.GetAccountStorageRevision(originalSession.SteamID),
                    verifyActivationAsync: _ =>
                    {
                        verifyCalls++;
                        // Simulate ImportAccountForm saving replacement backup and reloading allAccounts mid-flight.
                        Assert.True(manifest.SaveAccount(importedBackup, encrypt: true, passKey: passKey).Succeeded);
                        typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, manifest.GetAllAccounts(passKey));
                        return Task.FromResult(AuthenticatorActivationStatus.SessionInvalid);
                    },
                    persistActivatedAccount: (activated, expectedRevision) =>
                        form.PersistRecoveredPendingActivation(activated, originalSession, expectedRevision),
                    CancellationToken.None));

                Assert.Equal("StaleAccountGenerationException", staleException.GetType().Name);
                Assert.Equal(1, verifyCalls);
                Assert.Equal(0, refreshAttempts);
                SteamGuardAccount preservedAfterFirstRace = Assert.Single(manifest.GetAllAccounts(passKey));
                Assert.False(preservedAfterFirstRace.FullyEnrolled);
                Assert.Equal("imported-fresh-access", preservedAfterFirstRace.Session.AccessToken);
                Assert.Equal("imported-fresh-refresh", preservedAfterFirstRace.Session.RefreshToken);

                // Case 2: A later entry in the startup snapshot already became stale before its first request -> skips immediately.
                int staleVerifyCalls = 0;
                bool staleResult = await MainForm.RecoverPendingAccountOnStartupAsync(
                    originalPending,
                    snapshotRevision,
                    isAccessTokenExpired: () => false,
                    isRefreshTokenExpired: () => false,
                    refreshAccessTokenAsync: (_, _) => throw new InvalidOperationException("Stale snapshot entry must not refresh."),
                    getCurrentStorageRevision: () => manifest.GetAccountStorageRevision(originalSession.SteamID),
                    verifyActivationAsync: _ => { staleVerifyCalls++; return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord); },
                    persistActivatedAccount: (_, _) => throw new InvalidOperationException("Stale snapshot entry must not persist."),
                    CancellationToken.None);

                Assert.False(staleResult);
                Assert.Equal(0, staleVerifyCalls);
            }
        }

        [Fact]
        public async Task AccountStorageRevision_StaysMonotonicAcrossRemoveAndReimportAndBlocksStaleOverwrite()
        {
            const string passKey = "monotonic-aba-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount originalPending = CreateAccount(76561198000001025UL, "aba-pending-user");
            originalPending.TokenGID = "gid-aba-same";
            originalPending.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(originalPending, encrypt: true, passKey: passKey).Succeeded);

            long revisionBeforeRemove = manifest.GetAccountStorageRevision(originalPending.Session.SteamID);
            Assert.True(revisionBeforeRemove > 0);

            // Remove the account and re-import a backup with the same SteamID and identical secrets.
            Assert.True(manifest.RemoveAccount(originalPending, passKey));
            long revisionAfterRemove = manifest.GetAccountStorageRevision(originalPending.Session.SteamID);
            Assert.True(revisionAfterRemove > revisionBeforeRemove);

            SteamGuardAccount reimportedBackup = CreateAccount(76561198000001025UL, "aba-pending-user");
            reimportedBackup.TokenGID = "gid-aba-same";
            reimportedBackup.FullyEnrolled = false;
            reimportedBackup.Session.AccessToken = "reimported-backup-token";
            Assert.True(manifest.SaveAccount(reimportedBackup, encrypt: true, passKey: passKey).Succeeded);

            long revisionAfterReimport = manifest.GetAccountStorageRevision(originalPending.Session.SteamID);
            Assert.True(revisionAfterReimport > revisionAfterRemove);
            Assert.NotEqual(revisionBeforeRemove, revisionAfterReimport);

            // Both ResolveQueuedAccountForOperation and TryRecoverPendingAccountWithFreshSessionAsync reject the stale revision.
            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, manifest.GetAllAccounts(passKey));
                Assert.Null(form.ResolveQueuedAccountForOperation(originalPending, originalPending.Session, revisionBeforeRemove));
            }

            LoginForm.PendingAuthenticatorRecoveryResult staleRecovery = await LoginForm.TryRecoverPendingAccountWithFreshSessionAsync(
                manifest,
                originalPending,
                new SessionData { SteamID = 76561198000001025UL, AccessToken = "stale-new-token", RefreshToken = "stale-refresh" },
                passKey,
                (_, _) => Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord),
                CancellationToken.None,
                expectedStorageRevision: revisionBeforeRemove);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.SaveFailed, staleRecovery.Status);
            SteamGuardAccount preserved = Assert.Single(manifest.GetAllAccounts(passKey));
            Assert.Equal("reimported-backup-token", preserved.Session.AccessToken);
            Assert.False(preserved.FullyEnrolled);
        }

        [Fact]
        public async Task SetSessionNeedsRenewal_DoesNotInvalidateBoundRefreshRevisionDuringOpenRefreshDialog()
        {
            const string passKey = "renewal-metadata-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount enrolledAccount = CreateAccount(76561198000001026UL, "renewal-metadata-user");
            enrolledAccount.FullyEnrolled = true;
            Assert.True(manifest.SaveAccount(enrolledAccount, encrypt: true, passKey: passKey).Succeeded);

            long revisionAtQueue = manifest.GetAccountStorageRevision(enrolledAccount.Session.SteamID);
            long? forwardedRevision = null;

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { },
                showRefreshLoginDialogWithRevision: (targetAccount, _, expectedRev) =>
                {
                    forwardedRevision = expectedRev;
                    // Simulate background timer calling SetSessionNeedsRenewal while modal LoginForm is open.
                    Assert.True(manifest.SetSessionNeedsRenewal(targetAccount.Session.SteamID, true).Succeeded);
                    targetAccount.Session = new SessionData
                    {
                        SteamID = targetAccount.Session.SteamID,
                        AccessToken = "renewed-after-flag-flip",
                        RefreshToken = "renewed-refresh-token"
                    };
                    StorageResult saveResult = manifest.SaveAccount(
                        targetAccount,
                        encrypt: true,
                        passKey: passKey,
                        expectedStorageRevision: expectedRev,
                        allowCreate: false);
                    Assert.True(saveResult.Succeeded, saveResult.UserMessage);
                    return (true, passKey);
                }))
            {
                form.SetEncryptionKey(passKey);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { enrolledAccount });

                // Also verify a SessionNeedsRenewal flip before dequeue does not break ResolveQueuedAccountForOperation.
                Assert.True(manifest.SetSessionNeedsRenewal(enrolledAccount.Session.SteamID, true).Succeeded);
                Assert.Equal(revisionAtQueue, manifest.GetAccountStorageRevision(enrolledAccount.Session.SteamID));
                Assert.True(manifest.SetSessionNeedsRenewal(enrolledAccount.Session.SteamID, false).Succeeded);
                Assert.Equal(revisionAtQueue, manifest.GetAccountStorageRevision(enrolledAccount.Session.SteamID));

                bool succeeded = await InvokePrivateAsyncMethod<bool>(form, "PromptRefreshLoginAsync", enrolledAccount);
                Assert.True(succeeded);
                Assert.Equal(revisionAtQueue, forwardedRevision);
                Assert.Equal("renewed-after-flag-flip", Assert.Single(manifest.GetAllAccounts(passKey)).Session.AccessToken);
            }
        }

        [Fact]
        public async Task TryRecoverExistingPendingAuthenticatorAsync_ReturnsInconclusiveWhenManifestEntryIsUnreadable()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001027UL, "unreadable-pending-user");
            pendingAccount.FullyEnrolled = false;
            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: false, passKey: null).Succeeded);

            // Corrupt the unencrypted maFile on disk so GetAllAccounts skips it while manifest.Entries still lists this SteamID.
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string maFilePath = Path.Combine(maDir, Assert.Single(manifest.Entries).Filename);
            File.WriteAllText(maFilePath, "{corrupt-json");

            LoginForm.PendingAuthenticatorRecoveryResult result = await LoginForm.TryRecoverExistingPendingAuthenticatorAsync(
                manifest,
                new SessionData { SteamID = 76561198000001027UL, AccessToken = "fresh-token", RefreshToken = "fresh-refresh" },
                passKey: null,
                (_, _) => throw new InvalidOperationException("QueryStatus should not run when local file could not be loaded."),
                CancellationToken.None);

            Assert.Equal(LoginForm.PendingAuthenticatorRecoveryStatus.Inconclusive, result.Status);
        }

        [Fact]
        public async Task ValidateStartupSessionsAsync_SkipsEnrolledAccountReplacedWhileEarlierPendingAccountAwaitsQueryStatus()
        {
            const string passKey = "two-account-startup-passkey";
            Manifest manifest = Manifest.GenerateNewManifest(false);

            // Account 1: pending with valid access token (valid non-expired JWT) and expired refresh token.
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001029UL, "startup-pending-first");
            pendingAccount.TokenGID = "gid-first-pending";
            pendingAccount.FullyEnrolled = false;
            pendingAccount.Session.AccessToken = CreateJwtWithExpiry(DateTimeOffset.UtcNow.AddHours(2));
            pendingAccount.Session.RefreshToken = "expired-refresh-token";

            // Account 2: enrolled with valid refresh token in the same startup snapshot.
            SteamGuardAccount enrolledAccount = CreateAccount(76561198000001030UL, "startup-enrolled-second");
            enrolledAccount.TokenGID = "gid-second-old";
            enrolledAccount.FullyEnrolled = true;
            enrolledAccount.Session.AccessToken = CreateJwtWithExpiry(DateTimeOffset.UtcNow.AddMinutes(-5));
            enrolledAccount.Session.RefreshToken = CreateJwtWithExpiry(DateTimeOffset.UtcNow.AddDays(30));

            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: true, passKey: passKey).Succeeded);
            Assert.True(manifest.SaveAccount(enrolledAccount, encrypt: true, passKey: passKey).Succeeded);

            // Replacement backup imported for Account 2 while Account 1 is awaiting QueryStatus
            // (and before ImportAccountForm's dialog closes to trigger loadAccountsList).
            SteamGuardAccount importedEnrolledBackup = CreateAccount(76561198000001030UL, "startup-enrolled-second");
            importedEnrolledBackup.TokenGID = "gid-second-imported";
            importedEnrolledBackup.FullyEnrolled = true;
            importedEnrolledBackup.Session.AccessToken = CreateJwtWithExpiry(DateTimeOffset.UtcNow.AddHours(4));
            importedEnrolledBackup.Session.RefreshToken = CreateJwtWithExpiry(DateTimeOffset.UtcNow.AddDays(60));
            string expectedImportedAccessToken = importedEnrolledBackup.Session.AccessToken;

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                form.SetEncryptionKey(passKey);
                SteamGuardAccount[] initialLoaded = manifest.GetAllAccounts(passKey);
                Assert.Equal(2, initialLoaded.Length);
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, initialLoaded);

                int enrolledRefreshCalls = 0;
                await form.ValidateStartupSessionsAsync(
                    verifyPendingActivationAsync: (candidate, _) =>
                    {
                        Assert.Equal(76561198000001029UL, candidate.Session.SteamID);
                        // Simulate ImportAccountForm saving replacement backup for Account 2 BEFORE allAccounts reloads.
                        Assert.True(manifest.SaveAccount(importedEnrolledBackup, encrypt: true, passKey: passKey).Succeeded);
                        return Task.FromResult(AuthenticatorActivationStatus.ActiveForThisRecord);
                    },
                    refreshEnrolledAccountAsync: (candidate, expectedRevision, ct) =>
                    {
                        enrolledRefreshCalls++;
                        return form.RefreshAndPersistAccessTokenAsync(
                            candidate,
                            (session, _) =>
                            {
                                session.AccessToken = "stale-overwriting-token";
                                return Task.CompletedTask;
                            },
                            ct,
                            expectedRevision);
                    });

                Assert.Equal(0, enrolledRefreshCalls);
                SteamGuardAccount[] persisted = manifest.GetAllAccounts(passKey);
                SteamGuardAccount persistedPending = Assert.Single(persisted, a => a.Session.SteamID == 76561198000001029UL);
                SteamGuardAccount persistedEnrolled = Assert.Single(persisted, a => a.Session.SteamID == 76561198000001030UL);
                Assert.True(persistedPending.FullyEnrolled);
                Assert.Equal("gid-second-imported", persistedEnrolled.TokenGID);
                Assert.Equal(expectedImportedAccessToken, persistedEnrolled.Session.AccessToken);

                // Even if RefreshAndPersistAccessTokenAsync is called directly with the stale snapshot revision,
                // it throws StaleAccountGenerationException before calling Steam.
                SteamGuardAccount staleEnrolled = Assert.Single(initialLoaded, a => a.Session.SteamID == 76561198000001030UL);
                int directSteamCalls = 0;
                Exception directEx = await Assert.ThrowsAnyAsync<Exception>(() => form.RefreshAndPersistAccessTokenAsync(
                    staleEnrolled,
                    (_, _) => { directSteamCalls++; return Task.CompletedTask; },
                    CancellationToken.None,
                    expectedStorageRevision: manifest.GetAccountStorageRevision(76561198000001030UL) - 1));
                Assert.Equal("StaleAccountGenerationException", directEx.GetType().Name);
                Assert.Equal(0, directSteamCalls);
            }
        }

        private static string CreateJwtWithExpiry(DateTimeOffset expiresAt)
        {
            string header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
            string payload = Base64UrlEncode("{\"exp\":" + expiresAt.ToUnixTimeSeconds() + "}");
            return header + "." + payload + ".sig";
        }

        private static string Base64UrlEncode(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        [Fact]
        public void PendingAccountWithExpiredRefreshToken_DoesNotRequireSessionRenewalOrPersistRenewalFlag()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            SteamGuardAccount pendingAccount = CreateAccount(76561198000001015UL, "expired-pending-user");
            pendingAccount.FullyEnrolled = false;
            pendingAccount.Session.RefreshToken = "not-a-valid-jwt";
            Assert.True(pendingAccount.Session.IsRefreshTokenExpired());
            Assert.True(manifest.SaveAccount(pendingAccount, encrypt: false, passKey: null).Succeeded);

            using (MainForm form = new MainForm(manifest, null, null, null))
            {
                typeof(MainForm).GetField("allAccounts", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, new[] { pendingAccount });

                Assert.False(form.IsSessionRenewalRequired(pendingAccount));
                InvokePrivateMethod(form, "PersistExpiredRefreshTokenStates");
                Assert.False(manifest.IsSessionMarkedForRenewal(pendingAccount.Session.SteamID));
            }
        }

        [Fact]
        public void GetConfirmationCodeRetryPromptMessage_DistinguishesAllDismissalReasons()
        {
            string loadFailedMessage = LoginForm.GetConfirmationCodeRetryPromptMessage(
                new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: false, loadFailed: true, accepted: false));
            Assert.Contains("could not be displayed", loadFailedMessage);

            string notReadyMessage = LoginForm.GetConfirmationCodeRetryPromptMessage(
                new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: false, loadFailed: false, accepted: false));
            Assert.Contains("closed before it finished loading", notReadyMessage);

            string blankAcceptedMessage = LoginForm.GetConfirmationCodeRetryPromptMessage(
                new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: true, loadFailed: false, accepted: true));
            Assert.Contains("No confirmation code was entered", blankAcceptedMessage);

            string nativeCloseMessage = LoginForm.GetConfirmationCodeRetryPromptMessage(
                new Manifest.PassKeyPromptResult(canceled: true, value: string.Empty, uiReady: true, loadFailed: false, accepted: false, explicitCancel: false));
            Assert.Contains("closed without canceling setup", nativeCloseMessage);
        }

        [Fact]
        public void RecomputeExistingEntries_PreservesEncryptedEntriesWhenFileLookupIsNotConfirmedMissing()
        {
            const string passKey = "lookup-error-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(passKey);
            Manifest.ManifestEntry originalEntry = Assert.Single(manifest.Entries);
            string maDir = Path.Combine(Manifest.GetExecutableDir(), "maFiles");
            string existingFilePath = Path.Combine(maDir, originalEntry.Filename);
            string missingFilePath = Path.Combine(maDir, "definitely-missing.maFile");

            Assert.False(Manifest.IsManifestFileConfirmedMissing(existingFilePath));
            Assert.True(Manifest.IsManifestFileConfirmedMissing(missingFilePath));
            Assert.True(Manifest.IsManifestFileConfirmedMissing(maDir));

            // When a file lookup error prevents confirming absence, preserve the entry, salt, IV, and encryption flag.
            manifest.RecomputeExistingEntries(_ => false);
            Manifest.ManifestEntry preservedEntry = Assert.Single(manifest.Entries);
            Assert.Equal(originalEntry.Filename, preservedEntry.Filename);
            Assert.Equal(originalEntry.Salt, preservedEntry.Salt);
            Assert.Equal(originalEntry.IV, preservedEntry.IV);
            Assert.True(manifest.Encrypted);

            // When absence is confirmed, prune the missing entry and clear encryption on an empty vault.
            manifest.RecomputeExistingEntries(_ => true);
            Assert.Empty(manifest.Entries);
            Assert.False(manifest.Encrypted);
        }

        private static Manifest CreateEncryptedManifestWithAccount(string passKey)
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            StorageResult saveResult = manifest.SaveAccount(CreateAccount(76561198000001000UL, "encrypted-user"), true, passKey);
            Assert.True(saveResult.Succeeded, saveResult.UserMessage);
            return manifest;
        }

        private static SteamGuardAccount CreateAccount(ulong steamId, string accountName)
        {
            return new SteamGuardAccount
            {
                AccountName = accountName,
                SharedSecret = Convert.ToBase64String(new byte[20]),
                IdentitySecret = Convert.ToBase64String(new byte[20]),
                RevocationCode = "R12345",
                DeviceID = "android:00000000-0000-0000-0000-000000000001",
                FullyEnrolled = true,
                Session = new SessionData
                {
                    SteamID = steamId,
                    AccessToken = "test-access-token",
                    RefreshToken = "test-refresh-token"
                }
            };
        }

        private static void InvokePrivateMethod(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(target, arguments);
        }

        private static Task<T> InvokePrivateAsyncMethod<T>(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (Task<T>)method.Invoke(target, arguments);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field.GetValue(target);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
            {
                try { Directory.Delete(dataDirectory, true); } catch { }
            }
        }
    }
}
