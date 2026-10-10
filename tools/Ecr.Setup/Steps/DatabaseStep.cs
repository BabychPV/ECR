using System.Security;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок 3 — SQL Server, база даних, спосіб автентифікації. У режимі
/// обидва режими показують прапорець "-SkipSchema" (перше розгортання — для повтору після кроку 2, R9-F5/F5-01).
/// </summary>
internal sealed class DatabaseStep : IWizardStep
{
    private readonly WizardState _state;

    private TextBox? _instanceBox;
    private TextBox? _databaseBox;
    private RadioButton? _windowsAuthOption;
    private RadioButton? _sqlAuthOption;
    private TextBox? _loginBox;
    private TextBox? _sqlPasswordBox;
    private Label? _sqlLoginWarning;
    private CheckBox? _sqlLoginAcknowledge;
    private CheckBox? _skipSchemaCheckBox;
    private CheckBox? _trustCertificateCheckBox;
    private Label? _trustCertificateWarning;
    private GroupBox? _backupGroup;
    private Label? _backupStatus;
    private CheckBox? _backupAcceptCheckBox;
    private Label? _backupAcceptWarning;
    private BackupCheckResult? _backup;
    private GroupBox? _otherNodesGroup;
    private CheckBox? _otherNodesAcceptCheckBox;
    private Label? _otherNodesAcceptWarning;

    public DatabaseStep(WizardState state)
    {
        _state = state;
    }

    public string Title => "Database";

    public Control BuildView()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
        };

        root.Controls.Add(BuildConnectionGroup());
        root.Controls.Add(BuildAuthGroup());
        root.Controls.Add(BuildCertificateGroup());

        _skipSchemaCheckBox = new CheckBox
        {
            Text = "Schema already applied separately (skip)",
            AutoSize = true,
            Margin = new Padding(4, 12, 4, 0),
        };
        root.Controls.Add(_skipSchemaCheckBox);

        root.Controls.Add(BuildBackupGroup());
        root.Controls.Add(BuildOtherNodesGroup());
        _skipSchemaCheckBox.CheckedChanged += (_, _) => UpdateBackupVisibility();

        return root;
    }

    public void OnShow(WizardState state)
    {
        // ⛔ R9-F5/F5-01: прапорець видно в обох режимах. У першому розгортанні — лише для повтору спроби,
        // що впала ПІСЛЯ кроку 2 скрипта (схему вже накочено): тоді майстер передає разом
        // -FirstDeployment -SkipSchema -BootstrapPassword. Раніше пропуск був лише в «Update», який пароля
        // bootstrap не передає, — повтор давав систему без адміністратора. Крок будується один раз, тож
        // підпис оновлюємо щоразу, коли користувач сюди повертається (могли змінити режим на кроці 1).
        _skipSchemaCheckBox!.Visible = true;
        _skipSchemaCheckBox.Text = WizardState.SkipSchemaLabel(state.Mode);

        UpdateBackupVisibility();
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(_instanceBox!.Text))
        {
            error = "Enter the SQL Server instance.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_databaseBox!.Text))
        {
            error = "Enter the database name.";
            return false;
        }

        if (_sqlAuthOption!.Checked)
        {
            if (string.IsNullOrWhiteSpace(_loginBox!.Text))
            {
                error = "Enter the SQL login.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(_sqlPasswordBox!.Text))
            {
                error = "Enter the SQL login password.";
                return false;
            }

            // D-282: SQL-логін служби дозволено лише свідомим вибором.
            if (!_sqlLoginAcknowledge!.Checked)
            {
                error = "Confirm that the ECR service will connect with this SQL login, or choose Windows Authentication.";
                return false;
            }
        }

        // ⛔ Q-228: без цього виклику відсутня база виявлялася лише на кроці
        // 6, глибоко в deploy-ecr.ps1, сирим текстом sqlcmd — після того, як
        // користувач уже пройшов ще три кроки й запустив усе встановлення.
        // Інсталятор базу НЕ створює (це лишається адміністратору БД,
        // `install-guide.md` §0) — тут лише повідомляємо про це раніше й
        // зрозуміліше, а не змінюємо, хто відповідає за створення бази.
        if (!SqlPreflight.TryVerifyDatabaseExists(
                _instanceBox!.Text.Trim(), _databaseBox!.Text.Trim(),
                _windowsAuthOption!.Checked, _loginBox!.Text.Trim(), _sqlPasswordBox!.Text,
                _trustCertificateCheckBox!.Checked, out var databaseMissing, out error))
        {
            // L10-04, D-333: без прапорця сертифікат SQL перевіряється — самопідписаний не пройде.
            if (!_trustCertificateCheckBox.Checked)
            {
                error += Environment.NewLine + Environment.NewLine
                    + "If the error is about the server certificate (\"certificate chain\", \"not trusted\"): "
                    + "install a certificate trusted by this machine on SQL Server, or tick "
                    + "\"Trust the SQL Server certificate (unsafe)\".";
            }

            return false;
        }

        // ⛔ R5-U1/U1-04: оновлення потребує НАЯВНОЇ бази. Відсутня — хибне чи типове ім'я; майстер більше
        // не передає -CreateDatabaseIfMissing для оновлення, а скрипт без -FirstDeployment базу не створює.
        if (databaseMissing && _state.Mode == WizardMode.Update)
        {
            error = $"Database '{_databaseBox.Text.Trim()}' does not exist on '{_instanceBox.Text.Trim()}'. " +
                    "An update needs the existing ECR database — check the database name " +
                    "(\"First deployment\" creates a new one).";
            return false;
        }

        // ⛔ R9-F5/F5-01: повтор першого розгортання з пропуском схеми має сенс лише на базі, яку попередня
        // спроба вже створила й накотила; на відсутній скрипт однаково впаде на звірці штампа схеми.
        if (databaseMissing && _skipSchemaCheckBox!.Checked)
        {
            error = $"Database '{_databaseBox.Text.Trim()}' does not exist on '{_instanceBox.Text.Trim()}', " +
                    "so its schema cannot have been applied. Untick \"" + _skipSchemaCheckBox.Text + "\".";
            return false;
        }

        return ValidateBackup(out error);
    }

    /// <summary>
    /// ⛔ AN-117 (S2-04): оновлення, що змінює схему, — лише зі свіжою копією бази (той самий запит до
    /// msdb.dbo.backupset, що в deploy-ecr.ps1) АБО з явною позначкою «копію зроблено поза SQL Server / я
    /// приймаю ризик», яка й дає скрипту -SkipBackupCheck. Без цього кроку майстер доходив до «Install» і
    /// зупинявся на кроці 2/7 скрипта без способу визнати копію, зроблену VSS чи на вторинній репліці.
    /// </summary>
    private bool ValidateBackup(out string error)
    {
        error = string.Empty;
        _backup = null;

        if (_state.Mode != WizardMode.Update || _skipSchemaCheckBox!.Checked)
        {
            return true;
        }

        _backup = SqlPreflight.CheckBackup(
            _instanceBox!.Text.Trim(), _databaseBox!.Text.Trim(),
            _windowsAuthOption!.Checked, _loginBox!.Text.Trim(), _sqlPasswordBox!.Text,
            _trustCertificateCheckBox!.Checked);

        var fresh = _backup.Freshness == BackupFreshness.Fresh;
        _backupStatus!.Text = _backup.Detail;
        _backupStatus.ForeColor = fresh ? Color.DarkGreen : Color.DarkRed;

        if (fresh || _backupAcceptCheckBox!.Checked)
        {
            return true;
        }

        error = _backup.Detail + Environment.NewLine + Environment.NewLine
            + $"The update changes the database schema; rollback (runbook 9) needs a backup made right before it "
            + $"(not older than {SchemaBackupRules.MaxAgeHours} h). Make one, for example:" + Environment.NewLine
            + $"BACKUP DATABASE [{_databaseBox.Text.Trim()}] TO DISK = N'<path>' WITH COPY_ONLY, CHECKSUM" + Environment.NewLine
            + "and press Next again, or - if the backup was made outside SQL Server (VSS tool, another AG node) - tick "
            + "\"" + _backupAcceptCheckBox.Text + "\".";
        return false;
    }

    public void Apply(WizardState state)
    {
        state.SqlInstance = _instanceBox!.Text.Trim();
        state.Database = _databaseBox!.Text.Trim();
        state.SqlAuthIsWindows = _windowsAuthOption!.Checked;
        state.SqlLogin = state.SqlAuthIsWindows ? null : _loginBox!.Text.Trim();
        state.SqlLoginPassword = state.SqlAuthIsWindows ? null : ToSecure(_sqlPasswordBox!.Text);
        state.SkipSchema = _skipSchemaCheckBox!.Checked;
        state.TrustSqlServerCertificate = _trustCertificateCheckBox!.Checked;

        // AN-117: позначка діє лише там, де скрипт і перевіряв би копію (оновлення зі зміною схеми).
        var backupRequired = state.Mode == WizardMode.Update && !state.SkipSchema;
        state.Backup = backupRequired ? _backup : null;
        state.BackupRiskAccepted = backupRequired && _backupAcceptCheckBox!.Checked;

        // ⛔ X4-05: позначка діє лише там, де скрипт перевіряв би інші вузли (крок 2 зі зміною схеми).
        state.OtherNodesStoppedAccepted = !state.SkipSchema && _otherNodesAcceptCheckBox!.Checked;
    }

    /// <summary>
    /// ⛔ X4-05 (аудит R6): позначка «інші вузли зупинено вручну / це не ECR» → <c>-SkipOtherNodesCheck</c>.
    /// Крок 2 скрипта відмовляє, якщо до бази під'єднано застосунок з іншого хоста (D-32), а фільтр
    /// <c>SqlClient</c> ловить і сторонні засоби (звітні, наші <c>Ecr.MethodologyImport</c>/<c>Ecr.Migration.PiAf</c>).
    /// Без цієї позначки оператор мусив іти в командний рядок і звикав вмикати обхід. Типово знята; поставлена —
    /// червоне попередження тут і червоний рядок на кроці Review.
    /// </summary>
    private GroupBox BuildOtherNodesGroup()
    {
        _otherNodesGroup = new GroupBox
        {
            Text = "Other nodes (schema change)",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            Margin = new Padding(0, 12, 0, 0),
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };

        layout.Controls.Add(new Label
        {
            Text = "Before changing the schema deploy-ecr.ps1 refuses if an application session from ANOTHER host "
                + "is connected to the database (an old node would write into the new schema). It stops the ECR "
                + "services only on this machine. If it refuses and those sessions are another program, or you "
                + "have stopped EcrWorker and EcrApi on every other node yourself, tick the box below.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 4, 6, 0),
        });

        _otherNodesAcceptCheckBox = new CheckBox
        {
            Text = "Other ECR nodes are stopped manually / the other sessions are not ECR",
            AutoSize = true,
            Checked = _state.OtherNodesStoppedAccepted,
            Margin = new Padding(0, 8, 6, 0),
        };
        _otherNodesAcceptWarning = new Label
        {
            Text = "Warning: deploy-ecr.ps1 will run with -SkipOtherNodesCheck and will NOT look for other nodes. "
                + "You are responsible for EcrWorker and EcrApi being stopped on EVERY other node.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DarkRed,
            Visible = _state.OtherNodesStoppedAccepted,
            Margin = new Padding(24, 4, 6, 0),
        };
        _otherNodesAcceptCheckBox.CheckedChanged +=
            (_, _) => _otherNodesAcceptWarning.Visible = _otherNodesAcceptCheckBox.Checked;

        layout.Controls.Add(_otherNodesAcceptCheckBox);
        layout.Controls.Add(_otherNodesAcceptWarning);

        _otherNodesGroup.Controls.Add(layout);
        return _otherNodesGroup;
    }

    /// <summary>
    /// ⛔ AN-117 (S2-04): крок «копію бази зроблено» — лише в оновленні, що змінює схему. Позначка типово знята;
    /// поставлена — червоне попередження тут і червоний рядок «Database backup» на кроці Review.
    /// </summary>
    private GroupBox BuildBackupGroup()
    {
        _backupGroup = new GroupBox
        {
            Text = "Database backup (update)",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            Margin = new Padding(0, 12, 0, 0),
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };

        layout.Controls.Add(new Label
        {
            Text = "Before changing the schema the update requires a full or differential backup of the database "
                + $"not older than {SchemaBackupRules.MaxAgeHours} h in msdb.dbo.backupset; the ECR services are stopped "
                + "for the schema change. The backup history is checked when you press Next.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 4, 6, 0),
        });

        _backupStatus = new Label
        {
            Text = "Not checked yet.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 6, 6, 0),
        };
        layout.Controls.Add(_backupStatus);

        _backupAcceptCheckBox = new CheckBox
        {
            Text = "A backup was made outside SQL Server / I accept the risk",
            AutoSize = true,
            Checked = _state.BackupRiskAccepted,
            Margin = new Padding(0, 8, 6, 0),
        };
        _backupAcceptWarning = new Label
        {
            Text = "Warning: deploy-ecr.ps1 will run with -SkipBackupCheck and will NOT verify the backup. "
                + "If there is no backup made right before the update, a failed update cannot be rolled back "
                + "(runbook 9) without losing data.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DarkRed,
            Visible = _state.BackupRiskAccepted,
            Margin = new Padding(24, 4, 6, 0),
        };
        _backupAcceptCheckBox.CheckedChanged +=
            (_, _) => _backupAcceptWarning.Visible = _backupAcceptCheckBox.Checked;

        layout.Controls.Add(_backupAcceptCheckBox);
        layout.Controls.Add(_backupAcceptWarning);

        _backupGroup.Controls.Add(layout);
        return _backupGroup;
    }

    private void UpdateBackupVisibility()
    {
        if (_backupGroup is null || _skipSchemaCheckBox is null)
        {
            return;
        }

        var required = _state.Mode == WizardMode.Update && !_skipSchemaCheckBox.Checked;
        _backupGroup.Visible = required;
        if (!required)
        {
            _backupAcceptCheckBox!.Checked = false;
        }

        // ⛔ X4-05: перевірка інших вузлів іде в кроці 2 в ОБОХ режимах, але лише коли схему змінюють.
        if (_otherNodesGroup is not null)
        {
            var schemaChanges = !_skipSchemaCheckBox.Checked;
            _otherNodesGroup.Visible = schemaChanges;
            if (!schemaChanges)
            {
                _otherNodesAcceptCheckBox!.Checked = false;
            }
        }
    }

    /// <summary>
    /// ⛔ L10-04, D-333 (HU-12 R3 = A): довіра до сертифіката SQL Server без перевірки —
    /// типово вимкнена. Увімкнена — служба й sqlcmd не автентифікують сервер (MITM на шляху
    /// до SQL бачить і пароль SQL-логіна, і дані); вибір повторюється на кроці «Огляд».
    /// </summary>
    private GroupBox BuildCertificateGroup()
    {
        var group = new GroupBox { Text = "Encryption", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };

        layout.Controls.Add(new Label
        {
            Text = "The connection to SQL Server is always encrypted (Encrypt=Mandatory). By default the "
                + "server certificate is verified: SQL Server must present a certificate trusted by this "
                + "machine whose name matches the instance name.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 4, 6, 0),
        });

        _trustCertificateCheckBox = new CheckBox
        {
            Text = "Trust the SQL Server certificate (unsafe)",
            AutoSize = true,
            Checked = _state.TrustSqlServerCertificate,
            Margin = new Padding(0, 8, 6, 0),
        };
        _trustCertificateWarning = new Label
        {
            Text = "Warning: the server certificate will NOT be verified (TrustServerCertificate=True). "
                + "Anyone on the network path to SQL Server can impersonate it and read the data and the "
                + "SQL login password. Use only for a self-signed certificate on a test stand.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DarkRed,
            Visible = _state.TrustSqlServerCertificate,
            Margin = new Padding(24, 4, 6, 0),
        };
        _trustCertificateCheckBox.CheckedChanged +=
            (_, _) => _trustCertificateWarning.Visible = _trustCertificateCheckBox.Checked;

        layout.Controls.Add(_trustCertificateCheckBox);
        layout.Controls.Add(_trustCertificateWarning);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildConnectionGroup()
    {
        var group = new GroupBox { Text = "Server", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _instanceBox = new TextBox { Dock = DockStyle.Fill, Text = _state.SqlInstance };
        _databaseBox = new TextBox { Dock = DockStyle.Fill, Text = _state.Database };

        layout.Controls.Add(new Label { Text = "SQL Server instance:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        layout.Controls.Add(_instanceBox, 1, 0);

        layout.Controls.Add(new Label { Text = "Database name:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, 0, 1);
        layout.Controls.Add(_databaseBox, 1, 1);

        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildAuthGroup()
    {
        var group = new GroupBox { Text = "Authentication", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _windowsAuthOption = new RadioButton { Text = "Windows Authentication", AutoSize = true, Checked = true };
        _sqlAuthOption = new RadioButton { Text = "SQL login", AutoSize = true };

        _loginBox = new TextBox { Dock = DockStyle.Fill, Enabled = false };
        _sqlPasswordBox = new TextBox { Dock = DockStyle.Fill, Enabled = false, UseSystemPasswordChar = true };

        _windowsAuthOption.CheckedChanged += (_, _) => UpdateEnabled();
        _sqlAuthOption.CheckedChanged += (_, _) => UpdateEnabled();

        layout.Controls.Add(_windowsAuthOption, 0, 0);
        layout.SetColumnSpan(_windowsAuthOption, 2);
        layout.Controls.Add(_sqlAuthOption, 0, 1);
        layout.SetColumnSpan(_sqlAuthOption, 2);

        layout.Controls.Add(new Label { Text = "Login:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 2);
        layout.Controls.Add(_loginBox, 1, 2);

        layout.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(24, 6, 6, 0) }, 0, 3);
        layout.Controls.Add(_sqlPasswordBox, 1, 3);

        // ⛔ L10-04 (аудит 2026-10-03), D-282 (HU-11 Q9 = A+B): SQL-логін не лише
        // накочує схему — він стає рядком підключення СЛУЖБИ. Логін DBA дав би
        // застосунку DDL-права всупереч D-66. Дозволено, але свідомо: попередження
        // і обов'язкове підтвердження. Типово — Windows Authentication (gMSA).
        _sqlLoginWarning = new Label
        {
            Text = "Warning: the ECR service will also use this SQL login at run time. "
                + "A DBA login gives the application DDL rights (D-66). Prefer Windows Authentication "
                + "with a gMSA. The password is stored in the service registry key, readable only by "
                + "SYSTEM and Administrators.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.DarkRed,
            Visible = false,
            Margin = new Padding(24, 8, 6, 0),
        };
        _sqlLoginAcknowledge = new CheckBox
        {
            Text = "I understand: the service will connect with this SQL login",
            AutoSize = true,
            Visible = false,
            Margin = new Padding(24, 4, 6, 0),
        };

        layout.Controls.Add(_sqlLoginWarning, 0, 4);
        layout.SetColumnSpan(_sqlLoginWarning, 2);
        layout.Controls.Add(_sqlLoginAcknowledge, 0, 5);
        layout.SetColumnSpan(_sqlLoginAcknowledge, 2);

        group.Controls.Add(layout);
        return group;
    }

    private void UpdateEnabled()
    {
        _loginBox!.Enabled = _sqlAuthOption!.Checked;
        _sqlPasswordBox!.Enabled = _sqlAuthOption!.Checked;
        _sqlLoginWarning!.Visible = _sqlAuthOption!.Checked;
        _sqlLoginAcknowledge!.Visible = _sqlAuthOption!.Checked;
        if (!_sqlAuthOption!.Checked)
        {
            _sqlLoginAcknowledge.Checked = false;
        }
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
