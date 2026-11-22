using Steam_Desktop_Authenticator;
using SteamAuth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
                    return "first-account-passkey";
                },
                (_, _) => throw new InvalidOperationException("Current passkey prompt should not run on an empty manifest."),
                _ => throw new InvalidOperationException("Error message should not show on an empty manifest."),
                out string resolvedPassKey);

            Assert.True(resolved);
            Assert.Equal("first-account-passkey", resolvedPassKey);
            Assert.Equal(LoginForm.SetupPassKeyPromptLabel, capturedPrompt);
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
        public void MainForm_AdoptResolvedPassKeyUpdatesStaleKeyForSubsequentReloads()
        {
            const string correctedPassKey = "corrected-refresh-passkey";
            Manifest manifest = CreateEncryptedManifestWithAccount(correctedPassKey);

            using (MainForm form = new MainForm(
                manifest,
                new UpdatePreferenceRevisionTracker(),
                null,
                _ => { }))
            {
                form.SetEncryptionKey("stale-passkey");
                form.AdoptResolvedPassKey(correctedPassKey);

                Assert.Equal(correctedPassKey, GetPrivateField<string>(form, "passKey"));
                InvokePrivateMethod(form, "loadAccountsList", new object[] { null });
                SteamGuardAccount[] loaded = GetPrivateField<SteamGuardAccount[]>(form, "allAccounts");
                Assert.Single(loaded);
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
            Assert.Contains("id=\"btn-accept\" class=\"btn-primary flex-1 shadow-lg shadow-[#00E5FF]/20\" disabled", html);

            int observeIndex = html.IndexOf("resizeObserver.observe(panel);", StringComparison.Ordinal);
            int inputEnableIndex = html.IndexOf("input.disabled = false;", StringComparison.Ordinal);
            int buttonEnableIndex = html.IndexOf("acceptButton.disabled = false;", StringComparison.Ordinal);
            int returnTrueIndex = html.IndexOf("return true;", StringComparison.Ordinal);

            Assert.True(observeIndex >= 0, "Expected resizeObserver.observe(panel) in setupInput.");
            Assert.True(inputEnableIndex > observeIndex, "Expected input to stay disabled until setup finishes.");
            Assert.True(buttonEnableIndex > inputEnableIndex, "Expected accept button to enable after input.");
            Assert.True(returnTrueIndex > buttonEnableIndex, "Expected setupInput to return true after enabling controls.");
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

            string passKey = manifest.PromptSetupPassKey(
                LoginForm.SetupPassKeyPromptLabel,
                commitImmediately: false,
                (label, isPassword) =>
                {
                    prompts.Add((label, isPassword));
                    return (false, "deferred-passkey");
                },
                message => messages.Add(message));

            Assert.Equal("deferred-passkey", passKey);
            Assert.False(manifest.Encrypted);
            Assert.Empty(messages);
            Assert.Equal(2, prompts.Count);
            Assert.All(prompts, prompt => Assert.True(prompt.IsPassword));
            Assert.False(File.Exists(manifestPath));
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
