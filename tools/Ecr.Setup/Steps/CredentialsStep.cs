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

    public string Title => "Administrator Password";

    public Control BuildView()
    {
        // ⛔ Q-227 (аудит/людина): на реальному екрані напис-пояснення
        // (нижче, багаторядковий через MaximumSize) НАКЛАДАВСЯ на поля
        // "Password:"/"Confirm:". Причина — той самий клас крихкості, що
        // й Q-218 (Dock=Fill+AutoSize на TableLayoutPanel — задокументована
        // пастка WinForms), лише тут він проявляється як накладання рядків,
        // а не як групування радіокнопок: без ЯВНОГО RowStyle для кожного
        // рядка (раніше не було жодного RowStyles.Add — рахунок висоти
        // рядків лишався на неявній поведінці TableLayoutPanel), висота під
        // багаторядковий Label резервувалась ненадійно, і наступний рядок
        // (fields) лягав, не дочекавшись реальної висоти попереднього.
        // Фікс — явний RowStyle(SizeType.AutoSize) на кожен рядок і явні
        // (колонка, рядок) в Controls.Add — ТОЧНО той підхід, що вже
        // працює в InstallStep.BuildView (RowCount + RowStyles.Add на
        // кожен рядок), а не нова, неперевірена ідея.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var explanation = new Label
        {
            Text = "This password is needed only for the first sign-in — the system will immediately ask to change it.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(4, 4, 4, 12),
        };
        root.Controls.Add(explanation, 0, 0);

        var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _confirmBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        fields.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        fields.Controls.Add(_passwordBox, 1, 0);

        fields.Controls.Add(new Label { Text = "Confirm:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        fields.Controls.Add(_confirmBox, 1, 1);

        root.Controls.Add(fields, 0, 1);

        _showPasswordCheckBox = new CheckBox { Text = "Show passwords", AutoSize = true, Margin = new Padding(4, 8, 4, 0) };
        _showPasswordCheckBox.CheckedChanged += (_, _) =>
        {
            _passwordBox!.UseSystemPasswordChar = !_showPasswordCheckBox!.Checked;
            _confirmBox!.UseSystemPasswordChar = !_showPasswordCheckBox.Checked;
        };
        root.Controls.Add(_showPasswordCheckBox, 0, 2);

        return root;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        var password = _passwordBox!.Text;
        var confirm = _confirmBox!.Text;

        if (password.Length < MinimumLength)
        {
            error = $"Password must be at least {MinimumLength} characters long.";
            return false;
        }

        if (password != confirm)
        {
            error = "Passwords do not match.";
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
