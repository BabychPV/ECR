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

        // ⛔ R5-U1/U1-03: служба EcrApi вже є на сервері — типово «Update», а не «First deployment»
        // (той режим обходить перевірку копії бази; deploy-ecr.ps1 на базі з міграціями однаково відмовить).
        var installed = DeployRunner.IsApiServiceInstalled();
        var defaultMode = WizardState.DefaultMode(installed);

        _firstDeploymentOption = new RadioButton
        {
            Text = "First deployment",
            AutoSize = true,
            Checked = defaultMode == WizardMode.FirstDeployment,
            Margin = new Padding(4, 10, 4, 0),
        };
        _firstDeploymentOption.Font = new Font(_firstDeploymentOption.Font, FontStyle.Bold);

        _updateOption = new RadioButton
        {
            Text = "Update an existing installation",
            AutoSize = true,
            Checked = defaultMode == WizardMode.Update,
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

        if (installed)
        {
            layout.Controls.Add(new Label
            {
                Text = "ECR is already installed on this server — \"Update\" is selected. " +
                       "\"First deployment\" is only for an empty database.",
                AutoSize = true,
                ForeColor = SystemColors.HotTrack,
                Margin = new Padding(4, 14, 0, 0),
            });
        }

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
