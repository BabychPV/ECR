namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 5 — підсумок усіх відповідей, без ЖОДНОГО значення пароля (навіть
/// замаскованого — довжина теж підказка). Лише статус "вказано"/"не
/// вказано". Це точка неповернення: <see cref="MainForm"/> підписує кнопку
/// "Далі" тут як "Встановити".
/// </summary>
internal sealed class ReviewStep : IWizardStep
{
    private ListView? _list;

    public string Title => "Review";

    public Control BuildView()
    {
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Parameter", 220);
        _list.Columns.Add("Value", 320);

        return _list;
    }

    public void OnShow(WizardState state)
    {
        // Огляд мусить показувати ЖИВИЙ стан, не знімок з першого показу —
        // користувач міг повернутись "Назад" і щось змінити.
        _list!.Items.Clear();

        AddRow("Mode", state.Mode == WizardMode.FirstDeployment ? "First deployment" : "Update existing installation");
        AddRow("SQL Server instance", state.SqlInstance);
        AddRow("Database", state.Database);
        AddRow(
            "SQL Authentication",
            state.SqlAuthIsWindows
                ? "Windows Authentication"
                : $"SQL login ({state.SqlLogin}), password: {Presence(state.SqlLoginPassword is not null)}");
        AddRow("Service account", DescribeServiceAccount(state));
        AddRow("Port", state.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddRow("Ecr.msi file", state.MsiPath ?? "not specified");
        AddRow(
            "Database schema",
            state.Mode == WizardMode.Update
                ? (state.SkipSchema ? "do not apply (already applied separately)" : "apply")
                : "apply (first deployment)");

        if (state.Mode == WizardMode.FirstDeployment)
        {
            AddRow("Bootstrap administrator password", Presence(state.BootstrapPassword is not null));
        }
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        return true;
    }

    public void Apply(WizardState state)
    {
        // Огляд нічого не збирає — лише показує вже зібране.
    }

    private static string DescribeServiceAccount(WizardState state)
    {
        return state.ServiceAccountMode switch
        {
            ServiceAccountMode.LocalSystem => "Local System (not recommended)",
            ServiceAccountMode.Gmsa => $"gMSA ({state.ServiceAccountName})",
            ServiceAccountMode.DomainUser =>
                $"{state.ServiceAccountName}, password: {Presence(state.ServicePassword is not null)}",
            _ => "not specified",
        };
    }

    private static string Presence(bool provided) => provided ? "provided" : "not provided";

    private void AddRow(string parameter, string value)
    {
        _list!.Items.Add(new ListViewItem(new[] { parameter, value }));
    }
}
