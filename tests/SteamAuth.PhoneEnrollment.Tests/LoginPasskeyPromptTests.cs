using Microsoft.Web.WebView2.Core;
using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
