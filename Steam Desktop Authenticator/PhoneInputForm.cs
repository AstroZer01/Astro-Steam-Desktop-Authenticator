using SteamAuth;
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

        public PhoneInputForm(SteamGuardAccount account)
        {
            this.Account = account;
            InitializeComponent();
            AstroTheme.ApplyTheme(this);
            BuildLayout();
            this.ClientSize = new Size(540, 320);
            this.MinimumSize = new Size(540, 320);
            this.MaximumSize = new Size(720, 500);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Text = "Add a phone number";
        }

        private void BuildLayout()
        {
            Control[] designerControls = new Control[Controls.Count];
            Controls.CopyTo(designerControls, 0);
            Controls.Clear();
            foreach (Control designerControl in designerControls)
                designerControl.Dispose();
            AutoScroll = false;
            BackColor = AstroTheme.Background;

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                ColumnCount = 1,
                RowCount = 5,
                BackColor = AstroTheme.Background
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label heading = new Label
            {
                Text = "Verify a phone number",
                AutoSize = true,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = AstroTheme.OnSurface,
                Margin = new Padding(0, 0, 0, 4)
            };
            Label introduction = new Label
            {
                Text = "Choose your country or region, then enter your phone number in international format. The country prefix is filled in for you and can be edited.",
                AutoSize = true,
                MaximumSize = new Size(490, 0),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = AstroTheme.OnSurfaceVariant,
                Margin = new Padding(0, 0, 0, 14)
            };

            countrySelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 10f),
                Margin = new Padding(0, 0, 0, 12),
                Height = 32,
                BackColor = AstroTheme.SurfaceVariant,
                ForeColor = AstroTheme.OnSurface,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 30,
                DropDownWidth = 490
            };
            countrySelector.DataSource = CountryOptions;
            countrySelector.DisplayMember = nameof(CountryOption.DisplayName);
            countrySelector.SelectedIndexChanged += countrySelector_SelectedIndexChanged;
            countrySelector.DrawItem += countrySelector_DrawItem;
            SelectCurrentRegion();

            Panel numberPanel = new Panel { Dock = DockStyle.Top, Height = 66, Margin = new Padding(0) };
            Label numberLabel = new Label
            {
                Text = "International phone number",
                AutoSize = true,
                Location = new Point(0, 0),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = AstroTheme.OnSurfaceVariant
            };
            phoneNumberInput = new TextBox
            {
                Location = new Point(0, 24),
                Size = new Size(490, 34),
                Font = new Font("Segoe UI", 14f),
                PlaceholderText = "+55 11 99999 9999",
                BackColor = AstroTheme.SurfaceVariant,
                ForeColor = AstroTheme.OnSurface,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center
            };
            numberPanel.Controls.Add(numberLabel);
            numberPanel.Controls.Add(phoneNumberInput);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            Button continueWithoutPhone = new Button { Text = "Continue without phone", AutoSize = true, Height = 34, Margin = new Padding(8, 0, 0, 0) };
            Button cancel = new Button { Text = "Cancel", AutoSize = true, Height = 34, DialogResult = DialogResult.Cancel, Margin = new Padding(8, 0, 0, 0) };
            Button submit = new Button { Text = "Send verification", AutoSize = true, Height = 34, Margin = new Padding(0) };
            AstroTheme.StyleSecondaryButton(continueWithoutPhone);
            AstroTheme.StyleSecondaryButton(cancel);
            AstroTheme.StylePrimaryButton(submit);
            continueWithoutPhone.Click += btnContinueWithoutPhone_Click;
            cancel.Click += btnCancel_Click;
            submit.Click += btnSubmit_Click;
            buttons.Controls.Add(continueWithoutPhone);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(submit);

            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(introduction, 0, 1);
            layout.Controls.Add(countrySelector, 0, 2);
            layout.Controls.Add(numberPanel, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
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
            if (selectedCountry == null || String.IsNullOrWhiteSpace(selectedCountry.RegionCode) || selectedCountry.RegionCode.Length != 2 || digits.Length <= countryDialingDigits.Length || !enteredNumber.TrimStart().StartsWith("+"))
            {
                AstroMessageBox.Show("Choose a country or region with a valid two-letter country code and enter the phone number after the country code, starting with +.", "Phone Number", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                string remainder = !String.IsNullOrEmpty(displayedDialingCode) && currentNumber.StartsWith(displayedDialingCode, StringComparison.Ordinal)
                    ? currentNumber.Substring(displayedDialingCode.Length)
                    : String.Empty;
                phoneNumberInput.Text = selectedCountry.DialingCode + remainder;
                displayedDialingCode = selectedCountry.DialingCode;
                phoneNumberInput.SelectionStart = phoneNumberInput.Text.Length;
            }
        }

        private void countrySelector_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            CountryOption country = countrySelector.Items[e.Index] as CountryOption;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? AstroTheme.PrimaryContainer : AstroTheme.SurfaceVariant;
            Color foreground = selected ? AstroTheme.OnPrimary : AstroTheme.OnSurface;

            using (SolidBrush brush = new SolidBrush(background))
                e.Graphics.FillRectangle(brush, e.Bounds);

            TextRenderer.DrawText(e.Graphics, country == null ? String.Empty : country.DisplayName,
                e.Font, e.Bounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private void SelectCurrentRegion()
        {
            string currentRegion;
            try
            {
                currentRegion = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            }
            catch (CultureNotFoundException)
            {
                currentRegion = "US";
            }

            CountryOption currentCountry = CountryOptions.FirstOrDefault(option => option.RegionCode == currentRegion) ?? CountryOptions.First(option => option.RegionCode == "US");
            countrySelector.SelectedItem = currentCountry;
        }

        private static IReadOnlyList<CountryOption> CountryOptions { get; } = new List<CountryOption>
        {
            new CountryOption("Argentina", "AR", "+54"), new CountryOption("Australia", "AU", "+61"), new CountryOption("Austria", "AT", "+43"),
            new CountryOption("Bangladesh", "BD", "+880"), new CountryOption("Belgium", "BE", "+32"), new CountryOption("Brazil", "BR", "+55"),
            new CountryOption("Bulgaria", "BG", "+359"), new CountryOption("Canada", "CA", "+1"), new CountryOption("Chile", "CL", "+56"),
            new CountryOption("China", "CN", "+86"), new CountryOption("Colombia", "CO", "+57"), new CountryOption("Croatia", "HR", "+385"),
            new CountryOption("Czechia", "CZ", "+420"), new CountryOption("Denmark", "DK", "+45"), new CountryOption("Egypt", "EG", "+20"),
            new CountryOption("Finland", "FI", "+358"), new CountryOption("France", "FR", "+33"), new CountryOption("Germany", "DE", "+49"),
            new CountryOption("Greece", "GR", "+30"), new CountryOption("Hong Kong", "HK", "+852"), new CountryOption("Hungary", "HU", "+36"),
            new CountryOption("India", "IN", "+91"), new CountryOption("Indonesia", "ID", "+62"), new CountryOption("Ireland", "IE", "+353"),
            new CountryOption("Israel", "IL", "+972"), new CountryOption("Italy", "IT", "+39"), new CountryOption("Japan", "JP", "+81"),
            new CountryOption("Malaysia", "MY", "+60"), new CountryOption("Mexico", "MX", "+52"), new CountryOption("Netherlands", "NL", "+31"),
            new CountryOption("New Zealand", "NZ", "+64"), new CountryOption("Norway", "NO", "+47"), new CountryOption("Pakistan", "PK", "+92"),
            new CountryOption("Philippines", "PH", "+63"), new CountryOption("Poland", "PL", "+48"), new CountryOption("Portugal", "PT", "+351"),
            new CountryOption("Romania", "RO", "+40"), new CountryOption("Russia", "RU", "+7"), new CountryOption("Saudi Arabia", "SA", "+966"),
            new CountryOption("Singapore", "SG", "+65"), new CountryOption("Slovakia", "SK", "+421"), new CountryOption("South Africa", "ZA", "+27"),
            new CountryOption("South Korea", "KR", "+82"), new CountryOption("Spain", "ES", "+34"), new CountryOption("Sweden", "SE", "+46"),
            new CountryOption("Switzerland", "CH", "+41"), new CountryOption("Taiwan", "TW", "+886"), new CountryOption("Thailand", "TH", "+66"),
            new CountryOption("Turkey", "TR", "+90"), new CountryOption("Ukraine", "UA", "+380"), new CountryOption("United Arab Emirates", "AE", "+971"),
            new CountryOption("United Kingdom", "GB", "+44"), new CountryOption("United States", "US", "+1"), new CountryOption("Vietnam", "VN", "+84")
        };

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
