using System.Globalization;

namespace Ecr.Setup.Steps;

/// <summary>
/// Крок «Сертифікат Data Protection» (S11): вибір сертифіката з
/// <c>Cert:\LocalMachine\My</c>, яким застосунок шифрує ключі сеансів у базі.
/// </summary>
/// <remarks>
/// ⛔ Без нього служба в Production не стартує, а <c>deploy-ecr.ps1</c>
/// зупиняється на кроці 1. У списку — лише сертифікати із закритим ключем
/// (<see cref="DataProtectionCertificateRules.Selectable"/>): без нього ключі
/// можна зашифрувати, але не розшифрувати.
/// </remarks>
internal sealed class DataProtectionStep(ICertificateSource source, Func<DateTime> now) : IWizardStep
{
    private ListView? _list;
    private Label? _emptyLabel;
    private string? _selected;

    public string Title => "Data Protection Certificate";

    public Control BuildView()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = "Select the certificate that protects session keys in the database. "
                + "Only certificates with a private key from Cert:\\LocalMachine\\My are listed. "
                + "Use the SAME certificate on every server behind the load balancer, "
                + "and grant the service account read access to its private key.",
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
            _selected = _list.SelectedItems.Count == 1 ? (string?)_list.SelectedItems[0].Tag : null;

        _emptyLabel = new Label { AutoSize = true, ForeColor = Color.DarkRed, Visible = false };

        // Кнопка — ті самі AutoSize-правила, що й «Browse...» (Q-227).
        var refreshButton = new Button { Text = "Refresh list", AutoSize = true };
        refreshButton.Click += (_, _) => Reload();

        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(_emptyLabel, 0, 2);
        root.Controls.Add(refreshButton, 0, 3);

        return root;
    }

    public void OnShow(WizardState state)
    {
        _selected ??= state.DataProtectionThumbprint;
        Reload();
    }

    public bool Validate(out string error)
        => DataProtectionCertificateRules.TryValidate(_selected, source, now(), out error);

    public void Apply(WizardState state) => state.DataProtectionThumbprint = _selected;

    private void Reload()
    {
        var selectable = DataProtectionCertificateRules.Selectable(source.List());

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
            + "Import the PFX (with the private key) into the local machine store and press \"Refresh list\".";
    }
}
