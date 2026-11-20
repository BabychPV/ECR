using System.Globalization;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок «Транспорт» (D14-08): HTTPS із сертифікатом замовника, TLS на зворотному проксі або
/// HTTP лише для стенда.
/// </summary>
/// <remarks>
/// ⛔ Без явного вибору <c>deploy-ecr.ps1</c> зупиняється: мовчазний HTTP дав би службу, у яку не можна
/// увійти з жодної іншої машини (cookie сеансу Secure по HTTP не відсилається). HTTP тут — окремий,
/// названий вибір «лише стенд», а не дефолт. У списку — лише сертифікати із закритим ключем; сертифікат
/// видає PKI замовника, майстер його не створює. HTTPS слухає на порту з кроку «Service Account and
/// Network» — тому й брандмауер, який відкриває MSI, той самий.
/// </remarks>
internal sealed class TransportStep(ICertificateSource source, Func<DateTime> now) : IWizardStep
{
    private RadioButton? _httpsOption;
    private RadioButton? _proxyOption;
    private RadioButton? _httpOption;
    private ListView? _list;
    private Label? _emptyLabel;
    private Button? _refreshButton;
    private string? _selected;
    private TextBox? _hostsBox;
    private bool _hostsEdited;
    private bool _settingHosts;
    private Dictionary<string, IReadOnlyList<string>> _dnsNames = new(StringComparer.Ordinal);

    public string Title => "Transport (HTTPS)";

    public Control BuildView()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = "How do users reach the application? The session cookie is Secure, so signing in from other "
                + "computers needs HTTPS. The service listens on the port chosen on the previous step "
                + "(use 443 for the usual https://server/).",
        };

        _httpsOption = new RadioButton { Text = "HTTPS with a certificate (recommended)", AutoSize = true, Checked = true };
        _proxyOption = new RadioButton
        {
            Text = "Behind an HTTPS reverse proxy (TLS ends on the proxy; the service listens on HTTP)",
            AutoSize = true,
        };
        _httpOption = new RadioButton
        {
            Text = "HTTP only - TEST STAND (session cookie is NOT Secure; the password travels in clear text)",
            AutoSize = true,
            ForeColor = Color.DarkRed,
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Subject", 240);
        _list.Columns.Add("Valid until", 90);
        _list.Columns.Add("Thumbprint", 300);
        _list.SelectedIndexChanged += (_, _) =>
        {
            _selected = _list.SelectedItems.Count == 1 ? (string?)_list.SelectedItems[0].Tag : null;
            SuggestHosts();
        };

        _emptyLabel = new Label { AutoSize = true, ForeColor = Color.DarkRed, Visible = false };

        _refreshButton = new Button { Text = "Refresh list", AutoSize = true };
        _refreshButton.Click += (_, _) => Reload();

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 8, 0, 0),
            Text = "Grant the service account read access to the certificate private key "
                + "(certlm.msc > All Tasks > Manage Private Keys). Details: install guide, section HTTPS.",
        };

        _httpsOption.CheckedChanged += (_, _) => UpdateEnabled();

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(_httpsOption, 0, 1);
        root.Controls.Add(_list, 0, 2);
        root.Controls.Add(_emptyLabel, 0, 3);
        root.Controls.Add(_refreshButton, 0, 4);
        root.Controls.Add(_proxyOption, 0, 5);
        root.Controls.Add(_httpOption, 0, 6);
        root.Controls.Add(hint, 0, 7);

        // ⛔ PSS-28: імена хоста. Пропонуються з DNS-імен вибраного сертифіката; редагуються; `*` — явно.
        var hostsLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 12, 0, 4),
            Text = "Host names the application answers to (AllowedHosts), separated by ';'. Suggested from the DNS names "
                + "of the selected certificate plus localhost (used by the post-install health check). "
                + "Requests with any other Host header get HTTP 400, so list every name users type in the browser "
                + "(and the proxy's name behind a proxy). Type * to allow any host (default of the application).",
        };
        _hostsBox = new TextBox { Dock = DockStyle.Fill, Text = AllowedHostsRules.Any };
        _hostsBox.TextChanged += (_, _) =>
        {
            if (!_settingHosts)
            {
                _hostsEdited = true;
            }
        };
        root.Controls.Add(hostsLabel, 0, 8);
        root.Controls.Add(_hostsBox, 0, 9);

        return root;
    }

    public void OnShow(WizardState state)
    {
        _selected ??= state.HttpsThumbprint;
        if (state.AllowedHosts is not null && !_hostsEdited)
        {
            SetHosts(state.AllowedHosts);
            _hostsEdited = true;
        }

        _httpsOption!.Checked = state.Transport == WizardTransport.Https;
        _proxyOption!.Checked = state.Transport == WizardTransport.Proxy;
        _httpOption!.Checked = state.Transport == WizardTransport.Http;
        UpdateEnabled();
        Reload();
        SuggestHosts();
    }

    public bool Validate(out string error)
    {
        // ⛔ PSS-28: пусте чи хибне значення не пропускаємо — воно зачинило б вхід (400 на кожен запит).
        if (!AllowedHostsRules.TryNormalize(_hostsBox!.Text, out _, out error))
        {
            return false;
        }

        if (!_httpsOption!.Checked)
        {
            error = string.Empty;
            return true;
        }

        return HttpsCertificateRules.TryValidate(_selected, source, now(), out error);
    }

    public void Apply(WizardState state)
    {
        state.Transport = _httpsOption!.Checked
            ? WizardTransport.Https
            : _proxyOption!.Checked
                ? WizardTransport.Proxy
                : WizardTransport.Http;

        // Відбиток лишається в стані й після перемикання на інший режим: повернення "Назад" не губить вибір.
        state.HttpsThumbprint = _selected;

        if (AllowedHostsRules.TryNormalize(_hostsBox!.Text, out var hosts, out _))
        {
            state.AllowedHosts = hosts;
        }
    }

    // Підставляє імена сертифіката, доки адміністратор не змінив поле сам.
    private void SuggestHosts()
    {
        if (_hostsEdited || _hostsBox is null || _selected is null || !_httpsOption!.Checked)
        {
            return;
        }

        if (_dnsNames.TryGetValue(_selected, out var names))
        {
            SetHosts(AllowedHostsRules.FromCertificate(names));
        }
    }

    private void SetHosts(string value)
    {
        _settingHosts = true;
        try
        {
            _hostsBox!.Text = value;
        }
        finally
        {
            _settingHosts = false;
        }
    }

    private void UpdateEnabled()
    {
        var https = _httpsOption!.Checked;
        _list!.Enabled = https;
        _refreshButton!.Enabled = https;
    }

    private void Reload()
    {
        var selectable = DataProtectionCertificateRules.Selectable(source.List());
        _dnsNames = selectable.ToDictionary(
            c => DataProtectionCertificateRules.Normalize(c.Thumbprint),
            c => c.DnsNames ?? [],
            StringComparer.Ordinal);

        _list!.BeginUpdate();
        _list.Items.Clear();
        foreach (var certificate in selectable)
        {
            var item = new ListViewItem(new[]
            {
                certificate.Subject,
                certificate.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                certificate.Thumbprint,
            })
            {
                Tag = DataProtectionCertificateRules.Normalize(certificate.Thumbprint),
                ToolTipText = DataProtectionCertificateRules.Describe(certificate),
            };

            if (certificate.NotAfter < now())
            {
                item.ForeColor = Color.Gray;
            }

            _list.Items.Add(item);

            if (string.Equals((string)item.Tag, DataProtectionCertificateRules.Normalize(_selected), StringComparison.Ordinal))
            {
                item.Selected = true;
            }
        }

        _list.EndUpdate();

        _emptyLabel!.Visible = selectable.Count == 0;
        _emptyLabel.Text = "No certificates with a private key in Cert:\\LocalMachine\\My. "
            + "Import the customer's PFX (with the private key) into the local machine store and press \"Refresh list\".";
    }
}
