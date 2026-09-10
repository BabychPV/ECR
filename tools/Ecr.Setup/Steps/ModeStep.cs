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

    public string Title => "Deployment Mode";

    public Control BuildView()
    {
        var group = new GroupBox
        {
            Text = "Deployment mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
        };

        // ⛔ Обидва перемикачі МУСЯТЬ мати ОДНОГО спільного безпосереднього
        // батька: WinForms групує RadioButton у взаємовиключну групу за
        // безпосереднім контейнером, а не за спільним предком. Попередня
        // версія загортала кожен варіант в ОКРЕМИЙ FlowLayoutPanel — це
        // розбивало групу, і обидва перемикачі можна було позначити
        // одночасно (звідси скарга користувача "неможливо щось обрати" —
        // вибір другого варіанта не знімав позначку з першого).
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
        };

        _firstDeploymentOption = new RadioButton
        {
            Text = "First deployment",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(4, 10, 4, 0),
        };
        _firstDeploymentOption.Font = new Font(_firstDeploymentOption.Font, FontStyle.Bold);

        _updateOption = new RadioButton
        {
            Text = "Update an existing installation",
            AutoSize = true,
            Margin = new Padding(4, 14, 4, 0),
        };
        _updateOption.Font = new Font(_updateOption.Font, FontStyle.Bold);

        var firstDeploymentSub = new Label
        {
            Text = "Empty server, database not yet configured",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(24, 2, 0, 0),
        };

        var updateSub = new Label
        {
            Text = "Service is already installed — only the version needs to be updated",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(24, 2, 0, 0),
        };

        layout.Controls.Add(_firstDeploymentOption);
        layout.Controls.Add(firstDeploymentSub);
        layout.Controls.Add(_updateOption);
        layout.Controls.Add(updateSub);

        group.Controls.Add(layout);
        return group;
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
}
