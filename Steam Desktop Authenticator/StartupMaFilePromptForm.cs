using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Small native prompt used only during startup. A ListBox is used for the
    /// bulk case so a long candidate list remains scrollable and readable.
    /// </summary>
    internal sealed class StartupMaFilePromptForm : Form
    {
        private StartupMaFilePromptForm(IReadOnlyList<string> filenames)
        {
            bool multiple = filenames.Count > 1;
            Text = "Import authenticator files";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, multiple ? 430 : 250);
            BackColor = AstroTheme.Background;

            Label explanation = new Label
            {
                AutoSize = false,
                Location = new Point(24, 20),
                Size = new Size(512, multiple ? 66 : 92),
                Text = multiple
                    ? "New valid authenticator files were found in the maFiles folder. Import all of them into Astro SDA?"
                    : "A new valid authenticator file was found in the maFiles folder. Import it into Astro SDA?",
                ForeColor = AstroTheme.OnSurface,
                Font = new Font(Font, FontStyle.Regular)
            };
            Controls.Add(explanation);

            ListBox fileList = new ListBox
            {
                Location = new Point(24, multiple ? 94 : 112),
                Size = new Size(512, multiple ? 250 : 66),
                IntegralHeight = false,
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.One,
                BackColor = AstroTheme.SurfaceContainer,
                ForeColor = AstroTheme.OnSurface
            };
            foreach (string filename in filenames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                fileList.Items.Add(filename);
            if (fileList.Items.Count > 0)
                fileList.SelectedIndex = 0;
            Controls.Add(fileList);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Location = new Point(24, multiple ? 360 : 178),
                Size = new Size(512, 48),
                WrapContents = false,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Button ignoreButton = new Button { Text = "Ignore", DialogResult = DialogResult.Cancel, Width = 105, Height = 34, Margin = new Padding(8, 4, 0, 0) };
            Button importButton = new Button
            {
                Text = multiple ? "Import all" : "Import",
                DialogResult = DialogResult.OK,
                Width = 105,
                Height = 34,
                Margin = new Padding(8, 4, 0, 0)
            };
            buttons.Controls.Add(ignoreButton);
            buttons.Controls.Add(importButton);
            Controls.Add(buttons);

            AcceptButton = importButton;
            CancelButton = ignoreButton;
            Load += (sender, args) =>
            {
                AstroTheme.ApplyTheme(this);
                AstroTheme.StylePrimaryButton(importButton);
                AstroTheme.StyleSurfaceButton(ignoreButton);
                fileList.BackColor = AstroTheme.SurfaceContainer;
                fileList.ForeColor = AstroTheme.OnSurface;
            };
        }

        public static bool Show(IWin32Window owner, IReadOnlyList<string> filenames)
        {
            if (filenames == null || filenames.Count == 0)
                return false;

            using (StartupMaFilePromptForm prompt = new StartupMaFilePromptForm(filenames))
                return prompt.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
