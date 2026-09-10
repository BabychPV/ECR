using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 6 — жива панель виконання <c>deploy-ecr.ps1</c>. Не форма: після
/// того, як <see cref="MainForm"/> викликає <see cref="Begin"/>, кроки
/// скрипта позначаються ✓/✗ за рядками "Крок N/7: ..." з потоку виводу, а
/// повний журнал доступний під розгорткою "Детальний журнал".
/// </summary>
internal sealed partial class InstallStep : IWizardStep
{
    private static readonly (int Number, string Name)[] ScriptSteps =
    {
        (1, "Передумови"),
        (2, "Схема"),
        (3, "MSI"),
        (4, "Секрети служби"),
        (5, "Конфігурація"),
        (6, "Старт служби"),
        (7, "Перевірка здоров'я"),
    };

    private readonly Dictionary<int, Label> _checklistLabels = new();

    private Label? _bannerLabel;
    private TextBox? _logBox;
    private Button? _toggleLogButton;
    private DeployRunner? _runner;
    private int _lastAnnouncedStep;

    /// <summary>
    /// <c>null</c> — установка ще не завершилась (або не почалась); <c>true</c>/<c>false</c> —
    /// результат. <see cref="MainForm"/> читає це після <see cref="Completed"/>, щоб вирішити,
    /// яку кнопку показати — "Завершити" чи "Спробувати ще раз".
    /// </summary>
    public bool? Succeeded { get; private set; }

    /// <summary>Піднімається один раз, коли скрипт завершив роботу (успішно чи ні).</summary>
    public event Action<bool>? Completed;

    public string Title => "Встановлення";

    public Control BuildView()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _bannerLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Text = "Встановлення виконується...",
        };

        var checklistPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top };
        foreach (var (number, name) in ScriptSteps)
        {
            var label = new Label { Text = "• " + name, AutoSize = true, Margin = new Padding(4, 2, 4, 2) };
            _checklistLabels[number] = label;
            checklistPanel.Controls.Add(label);
        }

        _toggleLogButton = new Button { Text = "Показати детальний журнал", AutoSize = true, Dock = DockStyle.Top };
        _toggleLogButton.Click += (_, _) => ToggleLog();

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Visible = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        root.Controls.Add(_bannerLabel, 0, 0);
        root.Controls.Add(checklistPanel, 0, 1);
        root.Controls.Add(_toggleLogButton, 0, 2);
        root.Controls.Add(_logBox, 0, 3);

        return root;
    }

    public bool Validate(out string error)
    {
        error = string.Empty;
        return true;
    }

    public void Apply(WizardState state)
    {
        // Крок лише виконує дію — нічого нового не збирає в стан.
    }

    /// <summary>Стартує розгортання. Викликається <see cref="MainForm"/> одразу після показу цього кроку.</summary>
    public void Begin(WizardState state)
    {
        Succeeded = null;
        _lastAnnouncedStep = 0;
        foreach (var (number, name) in ScriptSteps)
        {
            _checklistLabels[number].Text = "• " + name;
            _checklistLabels[number].ForeColor = SystemColors.ControlText;
        }

        _bannerLabel!.Text = "Встановлення виконується...";
        _bannerLabel.ForeColor = SystemColors.ControlText;
        _logBox!.Clear();

        _runner = new DeployRunner();
        _runner.OutputReceived += OnOutput;

        var scriptPath = ResolveScriptPath();
        _ = RunAsync(state, scriptPath);
    }

    private async Task RunAsync(WizardState state, string scriptPath)
    {
        var success = false;
        try
        {
            success = await _runner!.RunAsync(state, scriptPath, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            Succeeded = success;

            if (success)
            {
                MarkCompleted(_lastAnnouncedStep == 0 ? 7 : _lastAnnouncedStep);
                _bannerLabel!.Text = "Готово — служба відповідає на /health/live";
                _bannerLabel.ForeColor = Color.DarkGreen;
            }
            else
            {
                MarkFailed(_lastAnnouncedStep);
                _bannerLabel!.Text = "Не вдалося завершити встановлення — див. детальний журнал нижче.";
                _bannerLabel.ForeColor = Color.DarkRed;
            }

            Completed?.Invoke(success);
        }
    }

    private void OnOutput(string line)
    {
        if (_logBox is null)
        {
            return;
        }

        if (_logBox.InvokeRequired)
        {
            _logBox.BeginInvoke(new Action(() => OnOutput(line)));
            return;
        }

        _logBox.AppendText(line + Environment.NewLine);

        var match = StepLineRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        // Нова оголошена позначка "Крок N/7" означає, що попередній крок
        // (N-1) успішно завершився — саме тому й дійшли до наступного.
        if (_lastAnnouncedStep > 0)
        {
            MarkCompleted(_lastAnnouncedStep);
        }

        _lastAnnouncedStep = number;
    }

    private void MarkCompleted(int number)
    {
        if (!_checklistLabels.TryGetValue(number, out var label))
        {
            return;
        }

        label.Text = "✓ " + ScriptSteps[number - 1].Name;
        label.ForeColor = Color.DarkGreen;
    }

    private void MarkFailed(int number)
    {
        if (!_checklistLabels.TryGetValue(number, out var label))
        {
            return;
        }

        label.Text = "✗ " + ScriptSteps[number - 1].Name;
        label.ForeColor = Color.DarkRed;
    }

    private void ToggleLog()
    {
        _logBox!.Visible = !_logBox.Visible;
        _toggleLogButton!.Text = _logBox.Visible ? "Сховати детальний журнал" : "Показати детальний журнал";
    }

    private static string ResolveScriptPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "deploy-ecr.ps1");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Сценарій розробки: EcrSetup.exe запущено з
        // tools/Ecr.Setup/bin/<config>/net10.0-windows/ — піднімаємось до
        // репозиторного tools/deploy-ecr.ps1. У готовій поставці екзешник і
        // скрипт лежать поряд (перша гілка вище), тож цей цикл не виконується.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "tools", "deploy-ecr.ps1");
            if (File.Exists(probe))
            {
                return probe;
            }
        }

        return candidate;
    }

    [GeneratedRegex(@"Крок (\d)/7")]
    private static partial Regex StepLineRegex();
}
