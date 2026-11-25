using System;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    public static class AstroMessageBox
    {
        public static DialogResult Show(string text)
        {
            return Show(text, "Astro SDA", MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        public static DialogResult Show(string text, string caption)
        {
            return Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        {
            return Show(text, caption, buttons, MessageBoxIcon.None);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon))
            {
                return ShowWithActiveOwner(form);
            }
        }

        public static DialogResult ShowWithCustomButtons(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string primaryButtonText, string secondaryButtonText)
        {
            if (buttons != MessageBoxButtons.OKCancel)
                throw new ArgumentOutOfRangeException(nameof(buttons), "Custom button labels are supported only for OK/Cancel dialogs.");

            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon, null, primaryButtonText, secondaryButtonText))
            {
                return ShowWithActiveOwner(form);
            }
        }

        public static DialogResult ShowWithCustomButtons(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string primaryButtonText, string secondaryButtonText, string tertiaryButtonText, bool centerContent = false)
        {
            if (buttons != MessageBoxButtons.YesNoCancel)
                throw new ArgumentOutOfRangeException(nameof(buttons), "Three custom button labels are supported only for Yes/No/Cancel dialogs.");

            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon, null, primaryButtonText, secondaryButtonText, tertiaryButtonText, centerContent))
            {
                return ShowWithActiveOwner(form);
            }
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string checkboxText, out bool isChecked)
        {
            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon, checkboxText))
            {
                DialogResult result = ShowWithActiveOwner(form);
                isChecked = form.IsChecked;
                return result;
            }
        }

        private static DialogResult ShowWithActiveOwner(AstroMessageBoxForm form)
        {
            Form owner = ResolveDialogOwner(form);
            if (owner != null)
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterParent;
                return form.ShowDialog(owner);
            }

            form.ShowInTaskbar = true;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
            return form.ShowDialog();
        }

        private static Form ResolveDialogOwner(Form excludedForm)
        {
            if (IsValidDialogOwner(Form.ActiveForm, excludedForm))
                return Form.ActiveForm;

            // When WebView2's child Chromium HWND has focus, Form.ActiveForm can be null.
            // Walk open WinForms windows from topmost/most-recent back so error dialogs
            // attach to the active modal (e.g. LoginForm) instead of opening behind it.
            FormCollection openForms = Application.OpenForms;
            for (int i = openForms.Count - 1; i >= 0; i--)
            {
                Form candidate;
                try
                {
                    candidate = openForms[i];
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (IsValidDialogOwner(candidate, excludedForm))
                    return candidate;
            }

            return null;
        }

        private static bool IsValidDialogOwner(Form candidate, Form excludedForm)
        {
            return candidate != null &&
                candidate != excludedForm &&
                candidate.Visible &&
                !candidate.Disposing &&
                !candidate.IsDisposed &&
                candidate.WindowState != FormWindowState.Minimized &&
                !(candidate is LoginNotificationPopup) &&
                !(candidate is QrScanOverlayForm);
        }
    }
}
