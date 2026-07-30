using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Centralized dark theme for Astro Steam Desktop Authenticator.
    /// Based on the "Lumina Trade Core" Stitch design system.
    /// </summary>
    public static class AstroTheme
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyDarkTitleBar(Form form)
        {
            if (Environment.OSVersion.Version.Major >= 10)
            {
                int useImmersiveDarkMode = 1;
                // Try attribute 20 (Windows 10 newer builds, Windows 11)
                int result = DwmSetWindowAttribute(form.Handle, 20, ref useImmersiveDarkMode, sizeof(int));
                if (result != 0)
                {
                    // Try attribute 19 (older Windows 10 builds)
                    DwmSetWindowAttribute(form.Handle, 19, ref useImmersiveDarkMode, sizeof(int));
                }
            }
        }

        // ── Core Surface Colors ──
        public static readonly Color Background = Color.FromArgb(11, 19, 38);           // #0B1326
        public static readonly Color Surface = Color.FromArgb(11, 19, 38);               // #0B1326
        public static readonly Color SurfaceContainerLowest = Color.FromArgb(6, 14, 32); // #060E20
        public static readonly Color SurfaceContainerLow = Color.FromArgb(19, 27, 46);   // #131B2E
        public static readonly Color SurfaceContainer = Color.FromArgb(23, 31, 51);      // #171F33
        public static readonly Color SurfaceContainerHigh = Color.FromArgb(34, 42, 61);  // #222A3D
        public static readonly Color SurfaceContainerHighest = Color.FromArgb(45, 52, 73); // #2D3449
        public static readonly Color SurfaceBright = Color.FromArgb(49, 57, 77);         // #31394D
        public static readonly Color SurfaceVariant = Color.FromArgb(45, 52, 73);        // #2D3449

        // ── Accent Colors ──
        public static readonly Color Primary = Color.FromArgb(0, 229, 255);              // #00E5FF (Cyan)
        public static readonly Color PrimaryLight = Color.FromArgb(195, 245, 255);       // #C3F5FF
        public static readonly Color PrimaryDim = Color.FromArgb(0, 218, 243);           // #00DAF3
        public static readonly Color OnPrimary = Color.FromArgb(0, 54, 61);              // #00363D
        public static readonly Color PrimaryContainer = Color.FromArgb(0, 229, 255);     // #00E5FF

        public static readonly Color Secondary = Color.FromArgb(78, 222, 163);           // #4EDEA3 (Emerald/Green)
        public static readonly Color SecondaryContainer = Color.FromArgb(0, 165, 114);   // #00A572
        public static readonly Color OnSecondary = Color.FromArgb(0, 56, 36);            // #003824

        public static readonly Color Error = Color.FromArgb(255, 180, 171);              // #FFB4AB (Ruby/Red)
        public static readonly Color ErrorContainer = Color.FromArgb(147, 0, 10);        // #93000A
        public static readonly Color OnError = Color.FromArgb(105, 0, 5);                // #690005

        public static readonly Color TertiaryContainer = Color.FromArgb(255, 193, 188);  // #FFC1BC

        // ── Text Colors ──
        public static readonly Color OnSurface = Color.FromArgb(218, 226, 253);          // #DAE2FD
        public static readonly Color OnSurfaceVariant = Color.FromArgb(186, 201, 204);   // #BAC9CC
        public static readonly Color Outline = Color.FromArgb(132, 147, 150);            // #849396
        public static readonly Color OutlineVariant = Color.FromArgb(59, 73, 76);        // #3B494C

        // ── Border Color ──
        public static readonly Color Border = Color.FromArgb(20, 255, 255, 255);         // rgba(255,255,255,0.08)
        public static readonly Color BorderSubtle = Color.FromArgb(40, 255, 255, 255);   // slightly brighter for hover

        // ── Gradient for progress bar ──
        public static readonly Color GradientStart = SecondaryContainer;                 // #00A572
        public static readonly Color GradientEnd = Secondary;                            // #4EDEA3

        // ── Fonts ──
        public static readonly Font FontBody = new Font("Segoe UI", 9F, FontStyle.Regular);
        public static readonly Font FontBodyMedium = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font FontHeadline = new Font("Segoe UI", 12F, FontStyle.Bold);
        public static readonly Font FontHeadlineLarge = new Font("Segoe UI", 18F, FontStyle.Regular);
        public static readonly Font FontLabel = new Font("Consolas", 8.25F, FontStyle.Regular);
        public static readonly Font FontLoginToken = new Font("Consolas", 15.75F, FontStyle.Bold);
        public static readonly Font FontSmall = new Font("Segoe UI", 6.75F, FontStyle.Regular);
        public static readonly Font FontInput = new Font("Segoe UI", 9.75F, FontStyle.Regular);
        public static readonly Font FontInputLarge = new Font("Segoe UI", 14.25F, FontStyle.Regular);
        public static readonly Font FontButton = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font FontButtonLarge = new Font("Segoe UI", 12F, FontStyle.Regular);

        /// <summary>
        /// Applies the Astro dark theme to a form and all its child controls recursively.
        /// Call this after InitializeComponent() in each form.
        /// </summary>
        public static void ApplyTheme(Form form)
        {
            form.BackColor = Background;
            form.ForeColor = OnSurface;

            ApplyDarkTitleBar(form);

            ApplyToControls(form.Controls);
        }

        /// <summary>
        /// Recursively walks all controls and applies theme styling.
        /// </summary>
        private static void ApplyToControls(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                switch (control)
                {
                    case MenuStrip menuStrip:
                        StyleMenuStrip(menuStrip);
                        break;

                    case Button button:
                        StyleButton(button);
                        break;

                    case TextBox textBox:
                        StyleTextBox(textBox);
                        break;

                    case MaskedTextBox maskedTextBox:
                        StyleMaskedTextBox(maskedTextBox);
                        break;

                    case ListBox listBox:
                        StyleListBox(listBox);
                        break;

                    case CheckBox checkBox:
                        StyleCheckBox(checkBox);
                        break;

                    case NumericUpDown numericUpDown:
                        StyleNumericUpDown(numericUpDown);
                        break;

                    case GroupBox groupBox:
                        StyleGroupBox(groupBox);
                        break;

                    case LinkLabel linkLabel:
                        StyleLinkLabel(linkLabel);
                        break;

                    case Label label:
                        StyleLabel(label);
                        break;

                    case ProgressBar progressBar:
                        StyleProgressBar(progressBar);
                        break;

                    case Panel panel:
                        panel.BackColor = Color.Transparent;
                        break;

                    case SplitContainer splitContainer:
                        splitContainer.BackColor = Background;
                        splitContainer.ForeColor = OnSurface;
                        ApplyToControls(splitContainer.Panel1.Controls);
                        ApplyToControls(splitContainer.Panel2.Controls);
                        break;

                    case PictureBox pictureBox:
                        pictureBox.BackColor = SurfaceContainer;
                        break;
                }

                // Recurse into child controls
                if (control.HasChildren && !(control is SplitContainer))
                {
                    ApplyToControls(control.Controls);
                }
            }
        }

        // ── Individual Control Styling ──

        public static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;

            // Check if this is a special button by its name/text
            string name = button.Name?.ToLower() ?? "";
            string text = button.Text?.ToLower() ?? "";

            if (name.Contains("accept") || text.Contains("accept") || text.Contains("submit") || text.Contains("save") || text.Contains("login") || text.Contains("import"))
            {
                // Primary (Cyan) button
                StylePrimaryButton(button);
            }
            else if (name.Contains("deny") || text.Contains("deny") || text.Contains("cancel"))
            {
                // Secondary / Cancel button
                StyleSecondaryButton(button);
            }
            else
            {
                // Default surface button
                StyleSurfaceButton(button);
            }
        }

        public static void StylePrimaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Primary;
            button.ForeColor = OnPrimary;
            button.FlatAppearance.BorderColor = Primary;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = PrimaryLight;
            button.FlatAppearance.MouseDownBackColor = PrimaryDim;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleSecondaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = SurfaceVariant;
            button.ForeColor = OnSurface;
            button.FlatAppearance.BorderColor = Outline;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SurfaceBright;
            button.FlatAppearance.MouseDownBackColor = SurfaceContainerHigh;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleSurfaceButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = SurfaceContainerHigh;
            button.ForeColor = OnSurface;
            button.FlatAppearance.BorderColor = OutlineVariant;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SurfaceBright;
            button.FlatAppearance.MouseDownBackColor = SurfaceVariant;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleDisabledGlassButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.Transparent;
            button.ForeColor = Color.FromArgb(100, OnSurface);
            button.FlatAppearance.BorderColor = Color.FromArgb(50, OutlineVariant);
            button.FlatAppearance.BorderSize = 1;
            button.Cursor = Cursors.Default;
        }

        public static void StyleAcceptButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(25, Secondary);
            button.ForeColor = Secondary;
            button.FlatAppearance.BorderColor = Color.FromArgb(80, Secondary);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SecondaryContainer;
            button.FlatAppearance.MouseDownBackColor = Secondary;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleDenyButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(25, Error);
            button.ForeColor = Error;
            button.FlatAppearance.BorderColor = Color.FromArgb(80, Error);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ErrorContainer;
            button.FlatAppearance.MouseDownBackColor = Error;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleTextBox(TextBox textBox)
        {
            textBox.BackColor = SurfaceContainerLowest;
            textBox.ForeColor = OnSurface;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }

        public static void StyleMaskedTextBox(MaskedTextBox maskedTextBox)
        {
            maskedTextBox.BackColor = SurfaceContainerLowest;
            maskedTextBox.ForeColor = OnSurface;
            maskedTextBox.BorderStyle = BorderStyle.FixedSingle;
        }

        public static void StyleListBox(ListBox listBox)
        {
            listBox.BackColor = SurfaceContainerLowest;
            listBox.ForeColor = OnSurface;
            listBox.BorderStyle = BorderStyle.FixedSingle;
        }

        public static void StyleCheckBox(CheckBox checkBox)
        {
            checkBox.ForeColor = OnSurface;
            checkBox.BackColor = Color.Transparent;
            checkBox.FlatStyle = FlatStyle.Flat;
            checkBox.FlatAppearance.CheckedBackColor = Primary;
        }

        public static void StyleNumericUpDown(NumericUpDown numericUpDown)
        {
            numericUpDown.BackColor = SurfaceContainerLowest;
            numericUpDown.ForeColor = OnSurface;
        }

        public static void StyleGroupBox(GroupBox groupBox)
        {
            groupBox.ForeColor = PrimaryLight;
            groupBox.BackColor = Color.Transparent;
        }

        public static void StyleLabel(Label label)
        {
            label.BackColor = Color.Transparent;

            // If the label has a very small font, it's likely a version/meta label
            if (label.Font.Size <= 7f)
            {
                label.ForeColor = Outline;
            }
            else
            {
                label.ForeColor = OnSurface;
            }
        }

        public static void StyleLinkLabel(LinkLabel linkLabel)
        {
            linkLabel.BackColor = Color.Transparent;
            linkLabel.LinkColor = Primary;
            linkLabel.ActiveLinkColor = PrimaryDim;
            linkLabel.VisitedLinkColor = PrimaryLight;
            linkLabel.ForeColor = Outline;
        }

        public static void StyleProgressBar(ProgressBar progressBar)
        {
            // Standard ProgressBar styling is limited in WinForms.
            // We hide it and replace it with AstroProgressBar at runtime.
            // This method is a no-op; the replacement happens in form-specific code.
            progressBar.BackColor = SurfaceVariant;
        }

        public static void StyleMenuStrip(MenuStrip menuStrip)
        {
            menuStrip.BackColor = SurfaceContainer;
            menuStrip.ForeColor = OnSurface;
            menuStrip.Renderer = new AstroMenuRenderer();
        }

        public static void StyleContextMenuStrip(ContextMenuStrip contextMenuStrip)
        {
            contextMenuStrip.BackColor = SurfaceContainer;
            contextMenuStrip.ForeColor = OnSurface;
            contextMenuStrip.Renderer = new AstroMenuRenderer();
        }

        /// <summary>
        /// Custom renderer for dark-themed menu strips and context menus.
        /// </summary>
        public class AstroMenuRenderer : ToolStripProfessionalRenderer
        {
            public AstroMenuRenderer() : base(new AstroMenuColorTable()) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(Point.Empty, e.Item.Size);

                if (e.Item.Selected || e.Item.Pressed)
                {
                    using (SolidBrush brush = new SolidBrush(SurfaceBright))
                    {
                        e.Graphics.FillRectangle(brush, rect);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(SurfaceContainer))
                    {
                        e.Graphics.FillRectangle(brush, rect);
                    }
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = OnSurface;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(Point.Empty, e.Item.Size);
                using (SolidBrush brush = new SolidBrush(SurfaceContainer))
                {
                    e.Graphics.FillRectangle(brush, rect);
                }
                int y = rect.Height / 2;
                using (Pen pen = new Pen(OutlineVariant))
                {
                    e.Graphics.DrawLine(pen, 4, y, rect.Width - 4, y);
                }
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(SurfaceContainer))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (Pen pen = new Pen(OutlineVariant))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = OnSurfaceVariant;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(SurfaceContainer))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }
        }

        /// <summary>
        /// Custom color table for dark-themed professional renderer.
        /// </summary>
        public class AstroMenuColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => SurfaceBright;
            public override Color MenuItemBorder => OutlineVariant;
            public override Color MenuBorder => OutlineVariant;
            public override Color MenuItemSelectedGradientBegin => SurfaceBright;
            public override Color MenuItemSelectedGradientEnd => SurfaceBright;
            public override Color MenuItemPressedGradientBegin => SurfaceContainerHigh;
            public override Color MenuItemPressedGradientEnd => SurfaceContainerHigh;
            public override Color MenuStripGradientBegin => SurfaceContainer;
            public override Color MenuStripGradientEnd => SurfaceContainer;
            public override Color ToolStripDropDownBackground => SurfaceContainer;
            public override Color ImageMarginGradientBegin => SurfaceContainer;
            public override Color ImageMarginGradientMiddle => SurfaceContainer;
            public override Color ImageMarginGradientEnd => SurfaceContainer;
            public override Color SeparatorDark => OutlineVariant;
            public override Color SeparatorLight => SurfaceContainer;
            public override Color CheckBackground => Primary;
            public override Color CheckPressedBackground => PrimaryDim;
            public override Color CheckSelectedBackground => PrimaryLight;
        }
    }
}
