using System.Security;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 4 — пароль bootstrap-адміністратора. Показується лише в режимі
/// "Перше розгортання" (host — <see cref="MainForm"/> — пропускає цей крок
/// цілком в режимі оновлення, і вперед, і назад).
/// </summary>
internal sealed class CredentialsStep : IWizardStep
{
    // D-104: довжина 12 — розумний дефолт, не повноцінна політика паролів.
    private const int MinimumLength = 12;

    private TextBox? _passwordBox;
    private TextBox? _confirmBox;
    private CheckBox? _showPasswordCheckBox;

    public string Title => "Пароль адміністратора";

    public Control BuildView()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        var explanation = new Label
        {
            Text = "Цей пароль потрібен лише для першого входу — система одразу попросить його змінити.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(4, 4, 4, 12),
        };
        root.Controls.Add(explanation);

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _confirmBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        fields.Controls.Add(new Label { Text = "Пароль:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        fields.Controls.Add(_passwordBox, 1, 0);

        fields.Controls.Add(new Label { Text = "Підтвердження:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        fields.Controls.Add(_confirmBox, 1, 1);

        root.Controls.Add(fields);

        _showPasswordCheckBox = new CheckBox { Text = "Показати паролі", AutoSize = true, Margin = new Padding(4, 8, 4, 0) };
        _showPasswordCheckBox.CheckedChanged += (_, _) =>
        {
            _passwordBox!.UseSystemPasswordChar = !_showPasswordCheckBox!.Checked;
            _confirmBox!.UseSystemPasswordChar = !_showPasswordCheckBox.Checked;
        };
        root.Controls.Add(_showPasswordCheckBox);

        return root;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        var password = _passwordBox!.Text;
        var confirm = _confirmBox!.Text;

        if (password.Length < MinimumLength)
        {
            error = $"Пароль має містити щонайменше {MinimumLength} символів.";
            return false;
        }

        if (password != confirm)
        {
            error = "Паролі не збігаються.";
            return false;
        }

        return true;
    }

    public void Apply(WizardState state)
    {
        state.BootstrapPassword = ToSecure(_passwordBox!.Text);
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
