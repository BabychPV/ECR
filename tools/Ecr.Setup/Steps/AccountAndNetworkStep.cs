using System.Security;

namespace Ecr.Setup.Steps;

/// <summary>Крок 2 — обліковий запис служби, порт і шлях до файлу Ecr.msi.</summary>
internal sealed class AccountAndNetworkStep : IWizardStep
{
    private RadioButton? _localSystemOption;
    private RadioButton? _gmsaOption;
    private RadioButton? _domainUserOption;
    private TextBox? _accountNameBox;
    private TextBox? _passwordBox;
    private NumericUpDown? _portUpDown;
    private TextBox? _msiPathBox;

    public string Title => "Обліковий запис і мережа";

    public Control BuildView()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        root.Controls.Add(BuildAccountGroup());
        root.Controls.Add(BuildNetworkGroup());

        return root;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        if ((_gmsaOption!.Checked || _domainUserOption!.Checked) && string.IsNullOrWhiteSpace(_accountNameBox!.Text))
        {
            error = "Вкажіть ім'я облікового запису служби.";
            return false;
        }

        if (_domainUserOption!.Checked && string.IsNullOrWhiteSpace(_passwordBox!.Text))
        {
            error = "Вкажіть пароль облікового запису служби.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_msiPathBox!.Text))
        {
            error = "Вкажіть шлях до файлу Ecr.msi.";
            return false;
        }

        if (!File.Exists(_msiPathBox!.Text))
        {
            error = "Файл MSI за вказаним шляхом не знайдено.";
            return false;
        }

        return true;
    }

    public void Apply(WizardState state)
    {
        state.ServiceAccountMode = _localSystemOption!.Checked
            ? ServiceAccountMode.LocalSystem
            : _gmsaOption!.Checked
                ? ServiceAccountMode.Gmsa
                : ServiceAccountMode.DomainUser;

        state.ServiceAccountName = state.ServiceAccountMode == ServiceAccountMode.LocalSystem
            ? null
            : _accountNameBox!.Text.Trim();

        state.ServicePassword = state.ServiceAccountMode == ServiceAccountMode.DomainUser
            ? ToSecure(_passwordBox!.Text)
            : null;

        state.Port = (int)_portUpDown!.Value;
        state.MsiPath = _msiPathBox!.Text.Trim();
    }

    private GroupBox BuildAccountGroup()
    {
        var group = new GroupBox { Text = "Обліковий запис служби", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _localSystemOption = new RadioButton { Text = "Local System (не рекомендовано)", AutoSize = true, Checked = true };
        _gmsaOption = new RadioButton { Text = "gMSA", AutoSize = true };
        _domainUserOption = new RadioButton { Text = "Обліковий запис і пароль", AutoSize = true };

        _accountNameBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _passwordBox = new TextBox { Dock = DockStyle.Fill, Enabled = false, UseSystemPasswordChar = true };

        _localSystemOption.CheckedChanged += (_, _) => UpdateEnabled();
        _gmsaOption.CheckedChanged += (_, _) => UpdateEnabled();
        _domainUserOption.CheckedChanged += (_, _) => UpdateEnabled();

        layout.Controls.Add(_localSystemOption, 0, 0);
        layout.SetColumnSpan(_localSystemOption, 2);
        layout.Controls.Add(_gmsaOption, 0, 1);
        layout.SetColumnSpan(_gmsaOption, 2);
        layout.Controls.Add(_domainUserOption, 0, 2);
        layout.SetColumnSpan(_domainUserOption, 2);

        layout.Controls.Add(new Label { Text = "Ім'я облікового запису:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 3);
        layout.Controls.Add(_accountNameBox, 1, 3);

        layout.Controls.Add(new Label { Text = "Пароль:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 4);
        layout.Controls.Add(_passwordBox, 1, 4);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildNetworkGroup()
    {
        var group = new GroupBox { Text = "Мережа й пакет", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _portUpDown = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 5000, Width = 100 };
        layout.Controls.Add(new Label { Text = "Порт:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        layout.Controls.Add(_portUpDown, 1, 0);

        _msiPathBox = new TextBox { Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "Огляд..." };
        browseButton.Click += (_, _) => BrowseForMsi();

        var detected = TryDetectMsi();
        if (detected is not null)
        {
            _msiPathBox.Text = detected;
        }

        layout.Controls.Add(new Label { Text = "Файл Ecr.msi:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        layout.Controls.Add(_msiPathBox, 1, 1);
        layout.Controls.Add(browseButton, 2, 1);

        group.Controls.Add(layout);
        return group;
    }

    private void UpdateEnabled()
    {
        var nameEnabled = _gmsaOption!.Checked || _domainUserOption!.Checked;
        _accountNameBox!.Enabled = nameEnabled;
        _passwordBox!.Enabled = _domainUserOption!.Checked;
    }

    private void BrowseForMsi()
    {
        using var dialog = new OpenFileDialog { Filter = "Пакет установки (*.msi)|*.msi", CheckFileExists = true };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _msiPathBox!.Text = dialog.FileName;
        }
    }

    private static string? TryDetectMsi()
    {
        try
        {
            return Directory.GetFiles(AppContext.BaseDirectory, "*.msi").FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SecureString ToSecure(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }
}
