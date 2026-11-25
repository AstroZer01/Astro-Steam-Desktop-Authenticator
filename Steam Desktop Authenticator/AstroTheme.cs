using System;
using System.Drawing;
using System.Windows.Forms;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Centralized dark theme for Astro Steam Desktop Authenticator.
    /// Implements the Refined v2 calm slate-navy & ocean-teal design system.
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

        // ── Core Surface Colors (Refined v2 Calm Slate-Navy) ──
        public static readonly Color Canvas = Color.FromArgb(7, 11, 19);                   // #070B13
        public static readonly Color Background = Color.FromArgb(13, 19, 32);              // #0D1320
        public static readonly Color Surface = Color.FromArgb(13, 19, 32);                 // #0D1320
        public static readonly Color SurfaceContainerLowest = Color.FromArgb(9, 14, 25);   // #090E19
        public static readonly Color SurfaceContainerLow = Color.FromArgb(18, 26, 43);     // #121A2B
        public static readonly Color SurfaceContainer = Color.FromArgb(22, 31, 50);        // #161F32
        public static readonly Color SurfaceContainerHigh = Color.FromArgb(30, 40, 62);    // #1E283E
        public static readonly Color SurfaceContainerHighest = Color.FromArgb(39, 50, 74); // #27324A
        public static readonly Color SurfaceBright = Color.FromArgb(45, 58, 84);           // #2D3A54
        public static readonly Color SurfaceVariant = Color.FromArgb(39, 50, 74);          // #27324A

        // ── Interactive & Accent Colors (Deep Glacier Teal + Soft Glacier Cyan) ──
        public static readonly Color Primary = Color.FromArgb(35, 117, 140);               // #23758C (High-contrast CTA fill)
        public static readonly Color PrimaryHover = Color.FromArgb(42, 134, 160);          // #2A86A0
        public static readonly Color PrimaryPressed = Color.FromArgb(29, 100, 121);        // #1D6479
        public static readonly Color PrimaryBorder = Color.FromArgb(58, 150, 176);         // #3A96B0
        public static readonly Color PrimaryAccent = Color.FromArgb(76, 195, 217);         // #4CC3D9 (Borders, icons, highlights)
        public static readonly Color PrimaryLight = Color.FromArgb(133, 214, 230);         // #85D6E6 (Soft accent text)
        public static readonly Color PrimaryDim = Color.FromArgb(42, 120, 145);            // #2A7891
        public static readonly Color OnPrimary = Color.FromArgb(248, 250, 252);            // #F8FAFC (Crisp white on teal >=5.4:1)
        public static readonly Color PrimaryContainer = Color.FromArgb(35, 117, 140);      // #23758C

        // ── Healthy / Approve Colors (Muted Sage Emerald) ──
        public static readonly Color Secondary = Color.FromArgb(106, 208, 162);            // #6AD0A2
        public static readonly Color SecondaryContainer = Color.FromArgb(56, 178, 124);    // #38B27C
        public static readonly Color OnSecondary = Color.FromArgb(5, 35, 24);              // #052318

        // ── Caution / Warning Colors (Muted Gold) ──
        public static readonly Color Warning = Color.FromArgb(212, 158, 61);               // #D49E3D
        public static readonly Color WarningSoft = Color.FromArgb(233, 210, 163);          // #E9D2A3
        public static readonly Color WarningSurface = Color.FromArgb(30, 29, 28);          // Subtle muted gold tint on navy

        // ── Error / Destructive Colors (Muted Rose-Crimson — zero neon glare) ──
        public static readonly Color Error = Color.FromArgb(223, 138, 140);                // #DF8A8C (Rose text)
        public static readonly Color ErrorAccent = Color.FromArgb(179, 77, 80);            // #B34D50 (Left accent bars/borders)
        public static readonly Color ErrorContainer = Color.FromArgb(155, 61, 64);         // #9B3D40 (Solid destructive CTA fill)
        public static readonly Color ErrorHover = Color.FromArgb(175, 72, 75);             // #AF484B
        public static readonly Color ErrorSurface = Color.FromArgb(37, 24, 34);            // Soft rose wash on dark navy
        public static readonly Color OnError = Color.FromArgb(251, 246, 246);              // #FBF6F6

        public static readonly Color TertiaryContainer = Color.FromArgb(223, 138, 140);

        // ── Text & Border Colors ──
        public static readonly Color OnSurface = Color.FromArgb(228, 234, 244);            // #E4EAF4
        public static readonly Color OnSurfaceVariant = Color.FromArgb(150, 164, 184);     // #96A4B8
        public static readonly Color Outline = Color.FromArgb(100, 116, 139);              // #64748B
        public static readonly Color OutlineVariant = Color.FromArgb(51, 65, 85);          // #334155
        public static readonly Color Divider = Color.FromArgb(30, 41, 59);                 // #1E293B

        public static readonly Color Border = Color.FromArgb(20, 255, 255, 255);
        public static readonly Color BorderSubtle = Color.FromArgb(36, 255, 255, 255);

        // ── Monochromatic Glacier Progress Gradient ──
        public static readonly Color GradientStart = Color.FromArgb(39, 105, 132);         // #276984
        public static readonly Color GradientEnd = Color.FromArgb(138, 224, 239);          // #8AE0EF

        // ── Fonts ──
        public static readonly Font FontBody = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        public static readonly Font FontBodyMedium = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        public static readonly Font FontHeadline = new Font("Segoe UI", 12F, FontStyle.Bold);
        public static readonly Font FontHeadlineLarge = new Font("Segoe UI", 16F, FontStyle.Bold);
        public static readonly Font FontLabel = new Font("Consolas", 8.5F, FontStyle.Regular);
        public static readonly Font FontLoginToken = new Font("Consolas", 16F, FontStyle.Bold);
        public static readonly Font FontSmall = new Font("Segoe UI", 8F, FontStyle.Regular);
        public static readonly Font FontInput = new Font("Segoe UI", 10F, FontStyle.Regular);
        public static readonly Font FontInputLarge = new Font("Segoe UI", 13.5F, FontStyle.Regular);
        public static readonly Font FontButton = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font FontButtonLarge = new Font("Segoe UI", 11F, FontStyle.Bold);

        /// <summary>
        /// Applies the Astro Refined v2 dark theme to a form and all its child controls recursively.
        /// Call this after InitializeComponent() in each form.
        /// </summary>
        public static void ApplyTheme(Form form)
        {
            form.BackColor = Background;
            form.ForeColor = OnSurface;

            ApplyDarkTitleBar(form);

            ApplyToControls(form.Controls);
        }

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
                        if (panel.BackColor == SystemColors.Control || panel.BackColor == Color.Empty)
                            panel.BackColor = Color.Transparent;
                        break;

                    case SplitContainer splitContainer:
                        splitContainer.BackColor = Background;
                        splitContainer.ForeColor = OnSurface;
                        ApplyToControls(splitContainer.Panel1.Controls);
                        ApplyToControls(splitContainer.Panel2.Controls);
                        break;

                    case PictureBox pictureBox:
                        if (pictureBox.BackColor != Color.White)
                            pictureBox.BackColor = SurfaceContainer;
                        break;
                }

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

            string name = button.Name?.ToLower() ?? "";
            string text = button.Text?.ToLower() ?? "";

            if (name.Contains("accept") || text.Contains("accept") || text.Contains("submit") || text.Contains("save") || text.Contains("login") || text.Contains("import"))
            {
                StylePrimaryButton(button);
            }
            else if (name.Contains("deny") || text.Contains("deny") || text.Contains("cancel"))
            {
                StyleSecondaryButton(button);
            }
            else
            {
                StyleSurfaceButton(button);
            }
        }

        public static void StylePrimaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Primary;
            button.ForeColor = OnPrimary;
            button.FlatAppearance.BorderColor = PrimaryBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = PrimaryHover;
            button.FlatAppearance.MouseDownBackColor = PrimaryPressed;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleOutlineAccentButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(19, 41, 56);
            button.ForeColor = PrimaryLight;
            button.FlatAppearance.BorderColor = Color.FromArgb(53, 121, 141);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(26, 56, 75);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(16, 35, 48);
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleSecondaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = SurfaceContainerHigh;
            button.ForeColor = OnSurface;
            button.FlatAppearance.BorderColor = OutlineVariant;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SurfaceBright;
            button.FlatAppearance.MouseDownBackColor = SurfaceContainer;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleSurfaceButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(27, 36, 56);
            button.ForeColor = OnSurface;
            button.FlatAppearance.BorderColor = OutlineVariant;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SurfaceBright;
            button.FlatAppearance.MouseDownBackColor = SurfaceContainer;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleDisabledGlassButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = SurfaceContainerLow;
            button.ForeColor = Outline;
            button.FlatAppearance.BorderColor = Divider;
            button.FlatAppearance.BorderSize = 1;
            button.Cursor = Cursors.Default;
        }

        public static void StyleAcceptButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(22, 50, 44);
            button.ForeColor = Secondary;
            button.FlatAppearance.BorderColor = Color.FromArgb(48, 121, 91);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 68, 58);
            button.FlatAppearance.MouseDownBackColor = SecondaryContainer;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleDenyButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = ErrorSurface;
            button.ForeColor = Error;
            button.FlatAppearance.BorderColor = Color.FromArgb(124, 58, 63);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 30, 40);
            button.FlatAppearance.MouseDownBackColor = ErrorContainer;
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static void StyleDangerSolidButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = ErrorContainer;
            button.ForeColor = OnError;
            button.FlatAppearance.BorderColor = Color.FromArgb(184, 83, 86);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ErrorHover;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(132, 48, 51);
            button.Font = FontButton;
            button.Cursor = Cursors.Hand;
        }

        public static Panel CreateCalloutPanel(string text, Color accentBarColor, Color backgroundColor, Color textColor, int width, bool boldFirstLine = false)
        {
            Panel container = new Panel
            {
                BackColor = backgroundColor,
                Width = width,
                Padding = new Padding(14, 10, 12, 10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(width, 44),
                MaximumSize = new Size(width, 0)
            };

            Panel accentBar = new Panel
            {
                BackColor = accentBarColor,
                Dock = DockStyle.Left,
                Width = 4
            };

            Label body = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(Math.Max(120, width - 30), 0),
                Font = boldFirstLine ? FontBodyMedium : new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = textColor,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 0, 0)
            };

            container.Controls.Add(body);
            container.Controls.Add(accentBar);
            container.Paint += (sender, e) =>
            {
                using (Pen borderPen = new Pen(Divider, 1f))
                {
                    e.Graphics.DrawRectangle(borderPen, 0, 0, container.Width - 1, container.Height - 1);
                }
            };
            return container;
        }

        public static Panel CreateDivider(int width)
        {
            return new Panel
            {
                BackColor = Divider,
                Height = 1,
                Width = width,
                Margin = new Padding(0, 6, 0, 10)
            };
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

            if (label.Font.Size <= 7.5f)
            {
                label.ForeColor = Outline;
            }
            else if (label.ForeColor == SystemColors.ControlText || label.ForeColor == Color.Black)
            {
                label.ForeColor = OnSurface;
            }
        }

        public static void StyleLinkLabel(LinkLabel linkLabel)
        {
            linkLabel.BackColor = Color.Transparent;
            linkLabel.LinkColor = PrimaryLight;
            linkLabel.ActiveLinkColor = PrimaryAccent;
            linkLabel.VisitedLinkColor = PrimaryLight;
            linkLabel.ForeColor = OnSurfaceVariant;
        }

        public static void StyleProgressBar(ProgressBar progressBar)
        {
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

        public class AstroMenuRenderer : ToolStripProfessionalRenderer
        {
            public AstroMenuRenderer() : base(new AstroMenuColorTable()) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(3, 1, Math.Max(0, e.Item.Width - 6), Math.Max(0, e.Item.Height - 2));

                if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
                {
                    using (SolidBrush brush = new SolidBrush(SurfaceContainerHigh))
                    {
                        e.Graphics.FillRectangle(brush, rect);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(SurfaceContainer))
                    {
                        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                    }
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled)
                {
                    e.TextColor = Outline;
                }
                else if (e.Item.ForeColor != SystemColors.ControlText && e.Item.ForeColor != Color.Empty && e.Item.ForeColor != OnSurface)
                {
                    e.TextColor = e.Item.ForeColor;
                }
                else if (e.Item.Selected)
                {
                    e.TextColor = PrimaryLight;
                }
                else
                {
                    e.TextColor = OnSurface;
                }
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
                using (Pen pen = new Pen(Divider))
                {
                    e.Graphics.DrawLine(pen, 8, y, rect.Width - 8, y);
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

        public class AstroMenuColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => SurfaceContainerHigh;
            public override Color MenuItemBorder => OutlineVariant;
            public override Color MenuBorder => OutlineVariant;
            public override Color MenuItemSelectedGradientBegin => SurfaceContainerHigh;
            public override Color MenuItemSelectedGradientEnd => SurfaceContainerHigh;
            public override Color MenuItemPressedGradientBegin => SurfaceBright;
            public override Color MenuItemPressedGradientEnd => SurfaceBright;
            public override Color MenuStripGradientBegin => SurfaceContainer;
            public override Color MenuStripGradientEnd => SurfaceContainer;
            public override Color ToolStripDropDownBackground => SurfaceContainer;
            public override Color ImageMarginGradientBegin => SurfaceContainer;
            public override Color ImageMarginGradientMiddle => SurfaceContainer;
            public override Color ImageMarginGradientEnd => SurfaceContainer;
            public override Color SeparatorDark => Divider;
            public override Color SeparatorLight => SurfaceContainer;
            public override Color CheckBackground => Primary;
            public override Color CheckPressedBackground => PrimaryPressed;
            public override Color CheckSelectedBackground => PrimaryHover;
        }
    }
}
