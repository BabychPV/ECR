using Ecr.Setup.Steps;

namespace Ecr.Setup;

/// <summary>
/// Оболонка майстра: заголовок кроку, панель вмісту, що підміняє вигляд
/// поточного кроку, і кнопки "Назад"/"Далі"/"Скасувати" внизу. Сама логіка
/// кожного екрана живе в <see cref="IWizardStep"/>-реалізаціях під
/// <c>Steps/</c> — ця форма лише координує навігацію між ними, зокрема
/// пропуск кроку 4 в режимі оновлення й перетворення кнопки "Далі" на
/// "Встановити"/"Завершити"/"Спробувати ще раз" на останніх двох кроках.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly WizardState _state = new();
    private readonly List<IWizardStep> _steps;
    private readonly Dictionary<IWizardStep, Control> _views = new();
    private readonly ReviewStep _reviewStep;
    private readonly InstallStep _installStep;

    private readonly Label _stepLabel;
    private readonly Panel _content;
    private readonly Button _backButton;
    private readonly Button _primaryButton;
    private readonly Button _cancelButton;

    private int _currentIndex;
    private bool _installRunning;

    public MainForm()
    {
        Text = "ECR Web Setup Wizard";
        StartPosition = FormStartPosition.CenterScreen;

        // ⛔ Q-227 (аудит/людина): +100px висоти проти попереднього
        // 680×520/720×560 — захисний запас для Review (крок 5), де
        // ListView показує заголовок + до 9 рядків (перевірено
        // ReviewStep.OnShow — 8 завжди, +1 "Bootstrap administrator
        // password" лише в First Deployment). На 96 DPI/100% арифметика
        // й так лишає запас (~200px), але Height=32 (_stepLabel), Height=56
        // (buttonBar) і Padding=12 (_content) — фіксовані пікселі, що НЕ
        // ростуть із DPI, тоді як заголовок/рядки нативного ListView
        // (шрифтозалежні) — ростуть; на реальній машині з масштабуванням
        // 125-200% цей розрив звужує запас аж до дефіциту. Не підтверджено
        // візуально (немає інтерактивного Windows-стенда в цій сесії) —
        // це свідомий, безпечний запас під конкретно названу гіпотезу, а
        // не точний розрахунок під один вимір екрана.
        MinimumSize = new Size(680, 620);
        Size = new Size(720, 660);

        _reviewStep = new ReviewStep();
        _installStep = new InstallStep();

        _steps = new List<IWizardStep>
        {
            new ModeStep(),
            new AccountAndNetworkStep(),
            new DatabaseStep(_state),
            new CredentialsStep(),
            _reviewStep,
            _installStep,
        };

        _installStep.Completed += OnInstallCompleted;

        _stepLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(12, 0, 12, 0),
        };

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

        _cancelButton = new Button { Text = "Cancel", AutoSize = true, Margin = new Padding(12, 8, 12, 8) };
        _cancelButton.Click += (_, _) => OnCancel();

        _backButton = new Button { Text = "Back", AutoSize = true, Margin = new Padding(0, 8, 8, 8) };
        _backButton.Click += (_, _) => OnBack();

        _primaryButton = new Button { Text = "Next", AutoSize = true, Margin = new Padding(0, 8, 12, 8) };
        _primaryButton.Click += (_, _) => OnPrimary();

        // ⛔ Реальний прогін (людина): кнопка "Next" зникала з поля зору.
        // WrapContents за замовчуванням true — якщо перший прикидковий
        // прохід AutoSize занизив доступну ширину (Dock=Right/Left разом з
        // AutoSize — відомий крихкий випадок), другий контрол переносився
        // на "новий рядок", який висота батьківської Panel (фіксовані 56px,
        // buttonBar) просто відрізає. WrapContents=false прибирає це явище
        // повністю: один рядок, завжди, незалежно від того, як порахувалась
        // ширина.
        var leftFlow = new FlowLayoutPanel { Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        leftFlow.Controls.Add(_cancelButton);

        var rightFlow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        rightFlow.Controls.Add(_backButton);
        rightFlow.Controls.Add(_primaryButton);

        var buttonBar = new Panel { Dock = DockStyle.Bottom, Height = 56 };
        buttonBar.Controls.Add(rightFlow);
        buttonBar.Controls.Add(leftFlow);

        Controls.Add(_stepLabel);
        Controls.Add(buttonBar);
        Controls.Add(_content);

        GoToIndex(0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installRunning)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    private void OnPrimary()
    {
        var current = _steps[_currentIndex];

        if (current is InstallStep install)
        {
            if (install.Succeeded == true)
            {
                Close();
            }
            else if (install.Succeeded == false)
            {
                GoToIndex(_steps.IndexOf(_reviewStep));
            }

            return;
        }

        if (!current.Validate(out var error))
        {
            MessageBox.Show(this, error, "Check the entered data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        current.Apply(_state);

        var nextIndex = AdjustedIndex(_currentIndex + 1, +1);
        if (nextIndex >= _steps.Count)
        {
            return;
        }

        GoToIndex(nextIndex);

        if (_steps[_currentIndex] is InstallStep startedInstall)
        {
            _installRunning = true;
            UpdateButtons();
            startedInstall.Begin(_state);
        }
    }

    private void OnBack()
    {
        if (_installRunning)
        {
            return;
        }

        var previousIndex = AdjustedIndex(_currentIndex - 1, -1);
        if (previousIndex < 0)
        {
            return;
        }

        GoToIndex(previousIndex);
    }

    private void OnCancel()
    {
        if (_installRunning)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "Are you sure you want to cancel the installation?",
            "Cancel Setup",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm == DialogResult.Yes)
        {
            Close();
        }
    }

    private void OnInstallCompleted(bool success)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnInstallCompleted(success)));
            return;
        }

        _installRunning = false;
        UpdateButtons();
    }

    private void GoToIndex(int index)
    {
        _currentIndex = index;
        var step = _steps[index];

        if (!_views.TryGetValue(step, out var view))
        {
            view = step.BuildView();
            view.Dock = DockStyle.Fill;
            _views[step] = view;
        }

        step.OnShow(_state);

        _content.Controls.Clear();
        _content.Controls.Add(view);

        UpdateStepIndicator();
        UpdateButtons();
    }

    private void UpdateStepIndicator()
    {
        var visibleSteps = _steps.Where(s => !ShouldSkip(s)).ToList();
        var current = _steps[_currentIndex];
        var position = visibleSteps.IndexOf(current) + 1;
        _stepLabel.Text = $"Step {position} of {visibleSteps.Count}: {current.Title}";
    }

    private void UpdateButtons()
    {
        var step = _steps[_currentIndex];

        if (step is InstallStep install)
        {
            // "Назад"/"Скасувати" заборонені лише ПОКИ встановлення дійсно
            // виконується (D-ключова вимога безпеки: не переривати MSI/зміни
            // реєстру/старт служби на півдорозі). Щойно скрипт завершився —
            // байдуже, успішно чи ні — обидва знову дозволені.
            _cancelButton.Enabled = !_installRunning;
            _backButton.Enabled = !_installRunning;

            if (_installRunning || install.Succeeded is null)
            {
                _primaryButton.Text = "Installing...";
                _primaryButton.Enabled = false;
            }
            else if (install.Succeeded == true)
            {
                _primaryButton.Text = "Finish";
                _primaryButton.Enabled = true;
            }
            else
            {
                _primaryButton.Text = "Try Again";
                _primaryButton.Enabled = true;
            }

            return;
        }

        _cancelButton.Enabled = true;
        _backButton.Enabled = AdjustedIndex(_currentIndex - 1, -1) >= 0;
        _primaryButton.Enabled = true;
        _primaryButton.Text = step is ReviewStep ? "Install" : "Next";
    }

    private int AdjustedIndex(int index, int direction)
    {
        while (index >= 0 && index < _steps.Count && ShouldSkip(_steps[index]))
        {
            index += direction;
        }

        return index;
    }

    private bool ShouldSkip(IWizardStep step)
    {
        return step is CredentialsStep && _state.Mode == WizardMode.Update;
    }
}
