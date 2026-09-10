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

    public string Title => "Огляд";

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
        _list.Columns.Add("Параметр", 220);
        _list.Columns.Add("Значення", 320);

        return _list;
    }

    public void OnShow(WizardState state)
    {
        // Огляд мусить показувати ЖИВИЙ стан, не знімок з першого показу —
        // користувач міг повернутись "Назад" і щось змінити.
        _list!.Items.Clear();

        AddRow("Режим", state.Mode == WizardMode.FirstDeployment ? "Перше розгортання" : "Оновлення наявної установки");
        AddRow("Екземпляр SQL Server", state.SqlInstance);
        AddRow("База даних", state.Database);
        AddRow(
            "Автентифікація SQL",
            state.SqlAuthIsWindows
                ? "Windows-автентифікація"
                : $"SQL-логін ({state.SqlLogin}), пароль: {Presence(state.SqlLoginPassword is not null)}");
        AddRow("Обліковий запис служби", DescribeServiceAccount(state));
        AddRow("Порт", state.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddRow("Файл Ecr.msi", state.MsiPath ?? "не вказано");
        AddRow(
            "Схема бази даних",
            state.Mode == WizardMode.Update
                ? (state.SkipSchema ? "не застосовувати (вже накочена окремо)" : "застосувати")
                : "застосувати (перше розгортання)");

        if (state.Mode == WizardMode.FirstDeployment)
        {
            AddRow("Пароль bootstrap-адміністратора", Presence(state.BootstrapPassword is not null));
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
            ServiceAccountMode.LocalSystem => "Local System (не рекомендовано)",
            ServiceAccountMode.Gmsa => $"gMSA ({state.ServiceAccountName})",
            ServiceAccountMode.DomainUser =>
                $"{state.ServiceAccountName}, пароль: {Presence(state.ServicePassword is not null)}",
            _ => "не вказано",
        };
    }

    private static string Presence(bool provided) => provided ? "вказано" : "не вказано";

    private void AddRow(string parameter, string value)
    {
        _list!.Items.Add(new ListViewItem(new[] { parameter, value }));
    }
}
