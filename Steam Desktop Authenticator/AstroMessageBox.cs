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
            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon, null, primaryButtonText, secondaryButtonText))
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
            Form owner = Form.ActiveForm;
            if (owner != null && owner.Visible && owner != form)
                return form.ShowDialog(owner);

            form.StartPosition = FormStartPosition.CenterScreen;
            return form.ShowDialog();
        }
    }
}
