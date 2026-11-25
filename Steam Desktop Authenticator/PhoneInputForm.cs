using SteamAuth;
using PhoneNumbers;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Steam_Desktop_Authenticator
{
    public partial class PhoneInputForm : Form
    {
        private SteamGuardAccount Account;
        public string PhoneNumber;
        public string CountryCode;
        public bool Canceled;
        public bool ContinueWithoutPhone;
        private ComboBox countrySelector;
        private TextBox phoneNumberInput;
        private string displayedDialingCode;
        private static readonly PhoneNumberUtil PhoneNumbers = PhoneNumberUtil.GetInstance();

        public PhoneInputForm(SteamGuardAccount account)
        {
            this.Account = account;
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            BuildLayout();
            this.ClientSize = new Size(520, 356);
            this.MinimumSize = new Size(520, 356);
            this.MaximumSize = new Size(720, 520);
            this.AutoScroll = true;
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.Text = "Add a Phone Number";
        }

        private void BuildLayout()
        {
            Control[] designerControls = new Control[Controls.Count];
            Controls.CopyTo(designerControls, 0);
            Controls.Clear();
            foreach (Control designerControl in designerControls)
                designerControl.Dispose();
            AutoScroll = true;
            BackColor = AstroTheme.Background;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                Padding = new Padding(22, 20, 22, 18),
                ColumnCount = 1,
                RowCount = 6,
                BackColor = AstroTheme.Background
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 6; i++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label heading = new Label
            {
                Text = "Verify a Phone Number",
                AutoSize = true,
                Font = AstroTheme.FontSectionHeading,
                ForeColor = AstroTheme.OnSurface,
                Margin = new Padding(0, 0, 0, 4)
            };
            Label introduction = new Label
            {
                Text = "Choose your country or region, then enter your phone number in international format. The dialing prefix is filled in automatically and can be edited.",
                AutoSize = true,
                MaximumSize = new Size(476, 0),
                Font = AstroTheme.FontSubtitle,
                ForeColor = AstroTheme.OnSurfaceVariant,
                Margin = new Padding(0, 0, 0, 16)
            };

            Panel countryPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Margin = new Padding(0, 0, 0, 12),
                BackColor = Color.Transparent
            };
            Label countryLabel = new Label
            {
                Text = "Country / Region",
                AutoSize = true,
                Location = new Point(0, 0),
                Font = AstroTheme.FontBodySmallBold,
                ForeColor = AstroTheme.OnSurfaceVariant
            };
            countrySelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(0, 22),
                Width = 476,
                Font = AstroTheme.FontInput,
                BackColor = AstroTheme.SurfaceContainerLowest,
                ForeColor = AstroTheme.OnSurface,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 28,
                DropDownWidth = 476
            };
            countrySelector.DataSource = CountryOptions;
            countrySelector.DisplayMember = nameof(CountryOption.DisplayName);
            countrySelector.SelectedIndexChanged += countrySelector_SelectedIndexChanged;
            countrySelector.DrawItem += countrySelector_DrawItem;
            SelectCurrentRegion();
            countryPanel.Controls.Add(countryLabel);
            countryPanel.Controls.Add(countrySelector);

            Panel numberPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 66,
                Margin = new Padding(0, 0, 0, 14),
                BackColor = Color.Transparent
            };
            Label numberLabel = new Label
            {
                Text = "International Phone Number",
                AutoSize = true,
                Location = new Point(0, 0),
                Font = AstroTheme.FontBodySmallBold,
                ForeColor = AstroTheme.OnSurfaceVariant
            };
            phoneNumberInput = new TextBox
            {
                Location = new Point(0, 22),
                Size = new Size(476, 36),
                Font = AstroTheme.FontMonoInput,
                PlaceholderText = "+55 11 99999 9999",
                BackColor = AstroTheme.SurfaceContainerLowest,
                ForeColor = AstroTheme.OnSurface,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Left
            };
            numberPanel.Controls.Add(numberLabel);
            numberPanel.Controls.Add(phoneNumberInput);

            Panel divider = AstroTheme.CreateDivider(476);
            divider.Dock = DockStyle.Top;
            divider.Margin = new Padding(0, 0, 0, 14);

            Panel footerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Width = 476,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            Button continueWithoutPhone = new Button
            {
                Text = "Skip Phone",
                Size = new Size(106, 34),
                Location = new Point(0, 0)
            };
            Button cancel = new Button
            {
                Text = "Cancel",
                Size = new Size(88, 34),
                Location = new Point(222, 0),
                DialogResult = DialogResult.Cancel
            };
            Button submit = new Button
            {
                Text = "Send Verification",
                Size = new Size(156, 34),
                Location = new Point(320, 0)
            };
            AstroTheme.StyleSurfaceButton(continueWithoutPhone);
            AstroTheme.StyleSurfaceButton(cancel);
            AstroTheme.StylePrimaryButton(submit);
            continueWithoutPhone.Click += btnContinueWithoutPhone_Click;
            cancel.Click += btnCancel_Click;
            submit.Click += btnSubmit_Click;
            footerPanel.Controls.Add(continueWithoutPhone);
            footerPanel.Controls.Add(cancel);
            footerPanel.Controls.Add(submit);

            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(introduction, 0, 1);
            layout.Controls.Add(countryPanel, 0, 2);
            layout.Controls.Add(numberPanel, 0, 3);
            layout.Controls.Add(divider, 0, 4);
            layout.Controls.Add(footerPanel, 0, 5);
            Controls.Add(layout);
            AcceptButton = submit;
            CancelButton = cancel;
            countrySelector_SelectedIndexChanged(this, EventArgs.Empty);
        }

        private void btnSubmit_Click(object sender, EventArgs e)
        {
            CountryOption selectedCountry = countrySelector.SelectedItem as CountryOption;
            string enteredNumber = phoneNumberInput.Text ?? String.Empty;
            string digits = new String(enteredNumber.Where(character => character >= '0' && character <= '9').ToArray());
            string countryDialingDigits = selectedCountry == null
                ? String.Empty
                : new String(selectedCountry.DialingCode.Where(character => character >= '0' && character <= '9').ToArray());
            if (selectedCountry == null ||
                String.IsNullOrWhiteSpace(selectedCountry.RegionCode) ||
                selectedCountry.RegionCode.Length != 2 ||
                !enteredNumber.TrimStart().StartsWith("+", StringComparison.Ordinal) ||
                !digits.StartsWith(countryDialingDigits, StringComparison.Ordinal) ||
                digits.Length <= countryDialingDigits.Length)
            {
                AstroMessageBox.Show("Enter an international phone number that starts with + and the dialing code of the selected country or region, followed by the local number.", "Phone Number", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this.PhoneNumber = "+" + digits;
            this.CountryCode = selectedCountry.RegionCode;
            this.Canceled = false;
            this.Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Canceled = true;
            this.Close();
        }

        private void btnContinueWithoutPhone_Click(object sender, EventArgs e)
        {
            this.ContinueWithoutPhone = true;
            this.Canceled = false;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (this.PhoneNumber == null && !this.ContinueWithoutPhone)
            {
                this.Canceled = true;
            }

            base.OnFormClosing(e);
        }

        private void countrySelector_SelectedIndexChanged(object sender, EventArgs e)
        {
            CountryOption selectedCountry = countrySelector.SelectedItem as CountryOption;
            if (phoneNumberInput != null && selectedCountry != null)
            {
                string currentNumber = phoneNumberInput.Text ?? String.Empty;
                string remainder = GetPhoneNumberRemainder(currentNumber);
                phoneNumberInput.Text = selectedCountry.DialingCode + remainder;
                displayedDialingCode = selectedCountry.DialingCode;
                phoneNumberInput.SelectionStart = phoneNumberInput.Text.Length;
            }
        }

        private string GetPhoneNumberRemainder(string currentNumber)
        {
            if (String.IsNullOrWhiteSpace(currentNumber))
                return String.Empty;

            if (!String.IsNullOrEmpty(displayedDialingCode) && currentNumber.StartsWith(displayedDialingCode, StringComparison.Ordinal))
                return currentNumber.Substring(displayedDialingCode.Length);

            string knownDialingCode = CountryOptions
                .Select(option => option.DialingCode)
                .OrderByDescending(code => code.Length)
                .FirstOrDefault(code => currentNumber.StartsWith(code, StringComparison.Ordinal));
            if (!String.IsNullOrEmpty(knownDialingCode))
                return currentNumber.Substring(knownDialingCode.Length);

            int firstSpace = currentNumber.IndexOf(' ');
            return firstSpace >= 0 ? currentNumber.Substring(firstSpace) : " " + currentNumber;
        }

        private void countrySelector_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            CountryOption country = countrySelector.Items[e.Index] as CountryOption;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool isComboEdit = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
            Color background = isComboEdit
                ? AstroTheme.SurfaceContainerLowest
                : (selected ? AstroTheme.SurfaceContainerHigh : AstroTheme.SurfaceContainerLow);
            Color foreground = selected && !isComboEdit ? AstroTheme.PrimaryLight : AstroTheme.OnSurface;

            using (SolidBrush brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, e.Bounds);

            Rectangle textBounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, country == null ? String.Empty : country.DisplayName,
                e.Font, textBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void SelectCurrentRegion()
        {
            string currentRegion;
            try
            {
                currentRegion = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            }
            catch (ArgumentException)
            {
                currentRegion = "US";
            }

            CountryOption currentCountry = CountryOptions.FirstOrDefault(option => option.RegionCode == currentRegion) ?? CountryOptions.FirstOrDefault(option => option.RegionCode == "US");
            countrySelector.SelectedItem = currentCountry;
        }

        private static IReadOnlyList<CountryOption> CountryOptions { get; } = PhoneNumbers.GetSupportedRegions()
            .Select(regionCode => new CountryOption(GetRegionDisplayName(regionCode), regionCode, "+" + PhoneNumbers.GetCountryCodeForRegion(regionCode)))
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

        private static string GetRegionDisplayName(string regionCode)
        {
            try
            {
                return new RegionInfo(regionCode).EnglishName;
            }
            catch (ArgumentException)
            {
                return regionCode;
            }
        }

        private sealed class CountryOption
        {
            public CountryOption(string name, string regionCode, string dialingCode)
            {
                RegionCode = regionCode;
                DialingCode = dialingCode;
                DisplayName = name + " (" + dialingCode + ")";
            }

            public string RegionCode { get; }
            public string DialingCode { get; }
            public string DisplayName { get; }
        }
    }
}
