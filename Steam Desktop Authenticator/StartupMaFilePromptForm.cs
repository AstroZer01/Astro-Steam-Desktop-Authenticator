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
            int count = filenames.Count;
            Text = "Import Authenticator Files";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(540, multiple ? 420 : 254);
            BackColor = AstroTheme.Background;
            ForeColor = AstroTheme.OnSurface;

            Label heading = new Label
            {
                AutoSize = true,
                Location = new Point(22, 18),
                Text = multiple
                    ? $"Import {count} Authenticator Files?"
                    : "Import Authenticator File?",
                ForeColor = AstroTheme.OnSurface,
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            Controls.Add(heading);

            Label explanation = new Label
            {
                AutoSize = false,
                Location = new Point(22, 46),
                Size = new Size(496, 40),
                Text = multiple
                    ? "New valid authenticator files were detected in the maFiles folder. Import all of them into Astro SDA?"
                    : "A new valid authenticator file was detected in the maFiles folder. Import it into Astro SDA?",
                ForeColor = AstroTheme.OnSurfaceVariant,
                Font = new Font("Segoe UI", 9.25F, FontStyle.Regular),
                BackColor = Color.Transparent
            };
            Controls.Add(explanation);

            int listHeight = multiple ? 236 : 62;
            ListBox fileList = new ListBox
            {
                Location = new Point(22, 92),
                Size = new Size(496, listHeight),
                IntegralHeight = false,
                HorizontalScrollbar = false,
                SelectionMode = SelectionMode.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 32,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = AstroTheme.SurfaceContainerLowest,
                ForeColor = AstroTheme.OnSurface
            };
            foreach (string filename in filenames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                fileList.Items.Add(filename);

            fileList.DrawItem += (sender, e) =>
            {
                if (e.Index < 0)
                    return;

                string filename = fileList.Items[e.Index]?.ToString() ?? string.Empty;
                Color rowBg = e.Index % 2 == 0
                    ? AstroTheme.SurfaceContainerLowest
                    : Color.FromArgb(13, 20, 34);

                using (SolidBrush bgBrush = new SolidBrush(rowBg))
                {
                    e.Graphics.FillRectangle(bgBrush, e.Bounds);
                }

                Rectangle nameRect = new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(40, e.Bounds.Width - 88), e.Bounds.Height);
                using (Font monoFont = new Font("Consolas", 9F, FontStyle.Regular))
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        filename,
                        monoFont,
                        nameRect,
                        AstroTheme.OnSurface,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }

                // Subtle emerald "Valid" pill on the right
                Rectangle badgeRect = new Rectangle(e.Bounds.Right - 64, e.Bounds.Y + 6, 52, e.Bounds.Height - 12);
                using (SolidBrush badgeBg = new SolidBrush(Color.FromArgb(20, 46, 40)))
                using (Pen badgeBorder = new Pen(Color.FromArgb(42, 102, 78), 1f))
                {
                    e.Graphics.FillRectangle(badgeBg, badgeRect);
                    e.Graphics.DrawRectangle(badgeBorder, badgeRect.X, badgeRect.Y, badgeRect.Width - 1, badgeRect.Height - 1);
                }
                using (Font badgeFont = new Font("Segoe UI", 7.75F, FontStyle.Bold))
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        "Valid",
                        badgeFont,
                        badgeRect,
                        AstroTheme.Secondary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                using (Pen sepPen = new Pen(AstroTheme.Divider, 1f))
                {
                    e.Graphics.DrawLine(sepPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
            };
            Controls.Add(fileList);

            int dividerTop = fileList.Bottom + 16;
            Panel divider = AstroTheme.CreateDivider(496);
            divider.Location = new Point(22, dividerTop);
            Controls.Add(divider);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Location = new Point(22, dividerTop + 14),
                Size = new Size(496, 40),
                WrapContents = false,
                BackColor = Color.Transparent
            };

            Button importButton = new Button
            {
                Text = multiple ? $"Import All ({count})" : "Import",
                DialogResult = DialogResult.OK,
                Width = multiple ? 132 : 108,
                Height = 34,
                Margin = new Padding(8, 0, 0, 0)
            };
            Button ignoreButton = new Button
            {
                Text = "Ignore",
                DialogResult = DialogResult.Cancel,
                Width = 96,
                Height = 34,
                Margin = new Padding(0)
            };
            // FlowDirection.RightToLeft places the first added control on the far right (primary CTA on right).
            buttons.Controls.Add(importButton);
            buttons.Controls.Add(ignoreButton);
            Controls.Add(buttons);

            AcceptButton = importButton;
            CancelButton = ignoreButton;
            Load += (sender, args) =>
            {
                AstroTheme.ApplyTheme(this);
                heading.ForeColor = AstroTheme.OnSurface;
                explanation.ForeColor = AstroTheme.OnSurfaceVariant;
                AstroTheme.StylePrimaryButton(importButton);
                AstroTheme.StyleSurfaceButton(ignoreButton);
                fileList.BackColor = AstroTheme.SurfaceContainerLowest;
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
