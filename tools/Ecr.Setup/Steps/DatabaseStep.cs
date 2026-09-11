using System.Security;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 3 — SQL Server, база даних, спосіб автентифікації. У режимі
/// оновлення додатково показує прапорець "-SkipSchema".
/// </summary>
internal sealed class DatabaseStep : IWizardStep
{
    private readonly WizardState _state;

    private TextBox? _instanceBox;
    private TextBox? _databaseBox;
    private RadioButton? _windowsAuthOption;
    private RadioButton? _sqlAuthOption;
    private TextBox? _loginBox;
    private TextBox? _sqlPasswordBox;
    private CheckBox? _skipSchemaCheckBox;

    public DatabaseStep(WizardState state)
    {
        _state = state;
    }

    public string Title => "Database";

    public Control BuildView()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        root.Controls.Add(BuildConnectionGroup());
        root.Controls.Add(BuildAuthGroup());

        _skipSchemaCheckBox = new CheckBox
        {
            Text = "Schema already applied separately (skip)",
            AutoSize = true,
            Margin = new Padding(4, 12, 4, 0),
        };
        root.Controls.Add(_skipSchemaCheckBox);

        return root;
    }

    public void OnShow(WizardState state)
    {
        // Прапорець стосується лише оновлення — у першому розгортанні схему
        // застосовує сам скрипт, пропускати нічого. Крок будується один раз,
        // тож видимість перевіряємо щоразу, коли користувач сюди повертається
        // (могли змінити режим на кроці 1 і прийти назад).
        _skipSchemaCheckBox!.Visible = state.Mode == WizardMode.Update;
        if (state.Mode == WizardMode.FirstDeployment)
        {
            _skipSchemaCheckBox.Checked = false;
        }
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(_instanceBox!.Text))
        {
            error = "Enter the SQL Server instance.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_databaseBox!.Text))
        {
            error = "Enter the database name.";
            return false;
        }

        if (_sqlAuthOption!.Checked)
        {
            if (string.IsNullOrWhiteSpace(_loginBox!.Text))
            {
                error = "Enter the SQL login.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_sqlPasswordBox!.Text))
            {
                error = "Enter the SQL login password.";
                return false;
            }
        }

        // ⛔ Q-228: без цього виклику відсутня база виявлялася лише на кроці
        // 6, глибоко в deploy-ecr.ps1, сирим текстом sqlcmd — після того, як
        // користувач уже пройшов ще три кроки й запустив усе встановлення.
        // Інсталятор базу НЕ створює (це лишається адміністратору БД,
        // `install-guide.md` §0) — тут лише повідомляємо про це раніше й
        // зрозуміліше, а не змінюємо, хто відповідає за створення бази.
        if (!SqlPreflight.TryVerifyDatabaseExists(
                _instanceBox!.Text.Trim(), _databaseBox!.Text.Trim(),
                _windowsAuthOption!.Checked, _loginBox!.Text.Trim(), _sqlPasswordBox!.Text,
                out error))
        {
            return false;
        }

        return true;
    }

    public void Apply(WizardState state)
    {
        state.SqlInstance = _instanceBox!.Text.Trim();
        state.Database = _databaseBox!.Text.Trim();
        state.SqlAuthIsWindows = _windowsAuthOption!.Checked;
        state.SqlLogin = state.SqlAuthIsWindows ? null : _loginBox!.Text.Trim();
        state.SqlLoginPassword = state.SqlAuthIsWindows ? null : ToSecure(_sqlPasswordBox!.Text);
        state.SkipSchema = state.Mode == WizardMode.Update && _skipSchemaCheckBox!.Checked;
    }

    private GroupBox BuildConnectionGroup()
    {
        var group = new GroupBox { Text = "Server", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _instanceBox = new TextBox { Dock = DockStyle.Fill, Text = _state.SqlInstance };
        _databaseBox = new TextBox { Dock = DockStyle.Fill, Text = _state.Database };

        layout.Controls.Add(new Label { Text = "SQL Server instance:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        layout.Controls.Add(_instanceBox, 1, 0);

        layout.Controls.Add(new Label { Text = "Database name:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        layout.Controls.Add(_databaseBox, 1, 1);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildAuthGroup()
    {
        var group = new GroupBox { Text = "Authentication", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _windowsAuthOption = new RadioButton { Text = "Windows Authentication", AutoSize = true, Checked = true };
        _sqlAuthOption = new RadioButton { Text = "SQL login", AutoSize = true };

        _loginBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _sqlPasswordBox = new TextBox { Dock = DockStyle.Fill, Enabled = false, UseSystemPasswordChar = true };

        _windowsAuthOption.CheckedChanged += (_, _) => UpdateEnabled();
        _sqlAuthOption.CheckedChanged += (_, _) => UpdateEnabled();

        layout.Controls.Add(_windowsAuthOption, 0, 0);
        layout.SetColumnSpan(_windowsAuthOption, 2);
        layout.Controls.Add(_sqlAuthOption, 0, 1);
        layout.SetColumnSpan(_sqlAuthOption, 2);

        layout.Controls.Add(new Label { Text = "Login:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 2);
        layout.Controls.Add(_loginBox, 1, 2);

        layout.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 3);
        layout.Controls.Add(_sqlPasswordBox, 1, 3);

        group.Controls.Add(layout);
        return group;
    }

    private void UpdateEnabled()
    {
        _loginBox!.Enabled = _sqlAuthOption!.Checked;
        _sqlPasswordBox!.Enabled = _sqlAuthOption!.Checked;
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
