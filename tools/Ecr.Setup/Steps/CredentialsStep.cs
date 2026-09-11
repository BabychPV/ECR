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

        // ⛔ Q-233 (реальний прогін людиною, ще раз): фікс Q-227
        // (RowStyle(AutoSize) на кожен рядок) виявився НЕДОСТАТНІМ — текст
        // і далі накладався сам на себе на реальному екрані. Причина
        // глибша, ніж бракуючий RowStyle: `AutoSize=true` + `MaximumSize`
        // просить WinForms порахувати висоту переносу ПІД ЧАС власного
        // проходу компонування `TableLayoutPanel` — а порядок "спершу
        // ширина колонки, чи спершу висота рядка" саме там і плаває
        // (той самий клас пастки, що вже документований для Q-218/Q-220,
        // просто на рівень глибше). Фікс — виміряти висоту переносу
        // САМИМ, тим самим інструментом, яким Label і так малює текст
        // (`TextRenderer`, GDI — Label.UseCompatibleTextRendering за
        // замовчуванням `false`), і задати розмір ЯВНО: жодної залежності
        // від того, коли саме TableLayoutPanel порахує ширину колонки.
        const int explanationWidth = 520;
        const string explanationText =
            "This password is needed only for the first sign-in — the system will immediately ask to change it.";
        var measured = TextRenderer.MeasureText(
            explanationText, Control.DefaultFont, new Size(explanationWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

        var explanation = new Label
        {
            Text = explanationText,
            AutoSize = false,
            // +4px: невеликий запас понад точний вимір — той самий текст,
            // намальований Label, а не TextRenderer напряму, має трохи
            // власного внутрішнього відступу; кілька зайвих пікселів
            // порожнечі внизу нешкідливі, а недобір знову дав би обрізання.
            Size = new Size(explanationWidth, measured.Height + 4),
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
