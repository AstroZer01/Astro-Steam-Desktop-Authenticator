using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class AstroMessageBoxTests
    {
        private static void RunOnStaThread(Action action)
        {
            Exception caught = null;
            Thread thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (caught != null)
                throw new InvalidOperationException("STA test failed.", caught);
        }

        private static AstroMessageBoxForm CreateOffscreenHiddenForm(
            string text,
            string caption,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            var form = new AstroMessageBoxForm(text, caption, buttons, icon)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000),
                Opacity = 0
            };
            form.Show();
            Application.DoEvents();
            return form;
        }

        [Theory]
        [InlineData(MessageBoxIcon.Warning, "Remove Account from App")]
        [InlineData(MessageBoxIcon.Error, "Final Confirmation")]
        public void DestructiveYesNoDialog_DefaultsEnterAndEscapeToNo(MessageBoxIcon icon, string caption)
        {
            RunOnStaThread(() =>
            {
                using (AstroMessageBoxForm enterForm = CreateOffscreenHiddenForm(
                    "This will permanently delete local data. Continue?",
                    caption,
                    MessageBoxButtons.YesNo,
                    icon))
                {
                    Button acceptButton = Assert.IsType<Button>(enterForm.AcceptButton);
                    Button cancelButton = Assert.IsType<Button>(enterForm.CancelButton);
                    Button initialFocus = Assert.IsType<Button>(enterForm.InitialFocusButton);

                    Assert.Equal("No", acceptButton.Text);
                    Assert.Equal("No", cancelButton.Text);
                    Assert.Same(acceptButton, initialFocus);
                    Assert.Same(acceptButton, enterForm.ActiveControl);

                    // Real WinForms Enter key dispatch must safely resolve to DialogResult.No.
                    bool handled = enterForm.ProcessDialogKeyForTests(Keys.Enter);
                    Assert.True(handled);
                    Assert.Equal(DialogResult.No, enterForm.Result);
                }

                using (AstroMessageBoxForm escapeForm = CreateOffscreenHiddenForm(
                    "You have NOT saved this transferred authenticator. Close without saving?",
                    caption,
                    MessageBoxButtons.YesNo,
                    icon))
                {
                    // Real WinForms Escape key dispatch must also safely resolve to DialogResult.No.
                    bool handled = escapeForm.ProcessDialogKeyForTests(Keys.Escape);
                    Assert.True(handled);
                    Assert.Equal(DialogResult.No, escapeForm.Result);
                }
            });
        }

        [Fact]
        public void UnsavedSettingsYesNoCancelWarning_KeepsSaveChangesAsPrimaryNonDestructiveAction()
        {
            RunOnStaThread(() =>
            {
                using (AstroMessageBoxForm form = new AstroMessageBoxForm(
                    "You have unsaved settings changes. Save them before exiting?",
                    "Unsaved Settings",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    null,
                    "Save Changes",
                    "Leave Without Saving",
                    "Cancel",
                    true))
                {
                    Button acceptButton = Assert.IsType<Button>(form.AcceptButton);
                    Button cancelButton = Assert.IsType<Button>(form.CancelButton);

                    Assert.Equal("Save Changes", acceptButton.Text);
                    Assert.Equal(AstroTheme.Primary, acceptButton.BackColor);
                    Assert.Equal("Cancel", cancelButton.Text);
                }
            });
        }

        [Fact]
        public void LongMessageDialog_KeepsActionButtonsInsideVisibleClientArea()
        {
            RunOnStaThread(() =>
            {
                string longText = string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 90).Select(i => $"Line {i}: Detailed diagnostic recovery information that exceeds the maximum dialog height."));

                using (AstroMessageBoxForm form = new AstroMessageBoxForm(
                    longText,
                    "Long Diagnostic Message",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information))
                {
                    Assert.True(form.ButtonPanelForTests.Bottom <= form.ClientSize.Height);
                    Assert.True(form.ButtonPanelForTests.Top >= 0);
                }
            });
        }

        [Fact]
        public void QrScanOverlayForm_CaptureSizeMatches350Viewfinder()
        {
            Assert.Equal(350, QrScanOverlayForm.CaptureSize);
        }
    }
}
