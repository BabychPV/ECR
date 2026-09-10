namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 1 — єдине рішення екрана: перше розгортання чи оновлення наявної
/// установки. Від цього залежить, чи буде показано крок 4 ("Пароль
/// адміністратора") і чи запропонує крок 3 прапорець "-SkipSchema".
/// </summary>
internal sealed class ModeStep : IWizardStep
{
    private RadioButton? _firstDeploymentOption;
    private RadioButton? _updateOption;

    public string Title => "Режим";

    public Control BuildView()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        _firstDeploymentOption = new RadioButton { Checked = true };
        _updateOption = new RadioButton();

        layout.Controls.Add(BuildOption(
            _firstDeploymentOption,
            "Перше розгортання",
            "Порожній сервер, база даних ще не налаштована"));

        layout.Controls.Add(BuildOption(
            _updateOption,
            "Оновлення наявної установки",
            "Служба вже встановлена раніше, потрібно лише оновити версію"));

        return layout;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        return true;
    }

    public void Apply(WizardState state)
    {
        state.Mode = _updateOption is { Checked: true } ? WizardMode.Update : WizardMode.FirstDeployment;
    }

    private static FlowLayoutPanel BuildOption(RadioButton radio, string title, string subText)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Margin = new Padding(4, 10, 4, 10),
        };

        radio.Text = title;
        radio.AutoSize = true;
        radio.Font = new Font(radio.Font, FontStyle.Bold);

        var sub = new Label
        {
            Text = subText,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(24, 2, 0, 0),
        };

        panel.Controls.Add(radio);
        panel.Controls.Add(sub);
        return panel;
    }
}
