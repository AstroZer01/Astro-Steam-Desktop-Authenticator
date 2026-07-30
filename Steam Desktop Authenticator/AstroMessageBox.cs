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
                return form.ShowDialog();
            }
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string checkboxText, out bool isChecked)
        {
            using (AstroMessageBoxForm form = new AstroMessageBoxForm(text, caption, buttons, icon, checkboxText))
            {
                DialogResult result = form.ShowDialog();
                isChecked = form.IsChecked;
                return result;
            }
        }
    }
}
