// tests/Ecr.Application.Tests/Security/DocumentReadScopeTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// S6 (enterprise-аудит безпеки, 2026-09-28): заборона на аркуш, таблицю й
/// колонку діє на ЧИТАННЯ, а не лише сірить редагування (ФВ-6.6).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <see cref="EditRules.CanRead"/> повертає <c>true</c>
/// без огляду на заборони (читання «як до S6» — лише рівень проєкту) —
/// червоніють усі тести про заборону, регресійні лишаються зеленими.
/// </remarks>
public sealed class DocumentReadScopeTests
{
    private const int Project = AccessBuilder.ProjectId;

    private const int SheetA = 101;
    private const int SheetB = 102;
    private const int Table1 = 201; // аркуш A
    private const int Table2 = 202; // аркуш A
    private const int Table3 = 203; // аркуш B
    private const int Col1a = 301;
    private const int Col1b = 302;
    private const int Col2a = 311;
    private const int Col3a = 321;

    private static readonly TemplateVersionSnapshot Snapshot = Build();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Без_заборон_видно_все()
    {
        var scope = Scope(Reader());

        Assert.All([Table1, Table2, Table3], t => Assert.True(scope.CanReadTable(t)));
        Assert.All([Col1a, Col1b, Col2a, Col3a], c => Assert.True(scope.CanReadColumn(c)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Заборона_на_таблицю_ховає_її_і_кожну_її_колонку_але_не_сусідню()
    {
        var scope = Scope(Reader().Deny(ResourceKind.Table, Table1));

        Assert.False(scope.CanReadTable(Table1));
        Assert.False(scope.CanReadColumn(Col1a));
        Assert.False(scope.CanReadColumn(Col1b));
        Assert.False(scope.CanReadTableOf(Col1a));

        Assert.True(scope.CanReadTable(Table2));
        Assert.True(scope.CanReadColumn(Col2a));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Заборона_на_колонку_ховає_лише_її_таблиця_лишається_видимою()
    {
        var scope = Scope(Reader().Deny(ResourceKind.Column, Col1b));

        Assert.False(scope.CanReadColumn(Col1b));
        Assert.True(scope.CanReadColumn(Col1a));
        Assert.True(scope.CanReadTable(Table1));
        Assert.True(scope.CanReadTableOf(Col1b));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Заборона_на_аркуш_ховає_всі_його_таблиці_але_не_інший_аркуш()
    {
        var scope = Scope(Reader().Deny(ResourceKind.Sheet, SheetA));

        Assert.False(scope.CanReadTable(Table1));
        Assert.False(scope.CanReadTable(Table2));
        Assert.False(scope.CanReadColumn(Col2a));

        Assert.True(scope.CanReadTable(Table3));
        Assert.True(scope.CanReadColumn(Col3a));
    }

    /// <summary>Заборона виграє і в дрібнішого ГРАНТА — той самий порядок, що й на запис.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Заборона_на_таблицю_перемагає_грант_Manage_на_її_колонку()
    {
        var scope = Scope(Reader()
            .Deny(ResourceKind.Table, Table1)
            .Grant(ResourceKind.Column, Col1a, GrantLevel.Manage));

        Assert.False(scope.CanReadColumn(Col1a));
        Assert.False(scope.CanReadTable(Table1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Без_гранта_на_проєкт_не_видно_нічого_навіть_із_грантом_на_таблицю()
    {
        var scope = Scope(new AccessBuilder().Grant(ResourceKind.Table, Table1, GrantLevel.Manage));

        Assert.False(scope.CanReadTable(Table1));
        Assert.False(scope.CanReadColumn(Col1a));
    }

    /// <summary>
    /// Адреса повідомлення валідації (таблиця + код колонки): заборонена
    /// колонка, заборонена таблиця й невідомий код — невидимі (S6).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Адреса_повідомлення_шанує_заборони_колонки_й_таблиці()
    {
        var scope = Scope(Reader().Deny(ResourceKind.Column, Col1b).Deny(ResourceKind.Table, Table2));

        Assert.True(scope.CanReadAt(Table1, null));
        Assert.True(scope.CanReadAt(Table1, "C301"));
        Assert.False(scope.CanReadAt(Table1, "C302"));
        Assert.False(scope.CanReadAt(Table1, "C999"));
        Assert.False(scope.CanReadAt(Table2, null));
        Assert.Equal([Table2], scope.HiddenTableIds());
        Assert.Equal([Col1b, Col2a], scope.HiddenColumnIds());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Невідома_знімку_колонка_чи_таблиця_невидима()
    {
        var scope = Scope(Reader());

        Assert.False(scope.CanReadColumn(99_999));
        Assert.False(scope.CanReadTable(99_999));
        Assert.False(scope.CanReadTableOf(99_999));
    }

    /// <summary>
    /// Інтеграція читає без грантів (як і <c>CanReadDocumentAsync</c>), але
    /// заборона на таблицю діє і на неї.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Інтеграція_бачить_без_грантів_крім_забороненого()
    {
        var scope = DocumentReadScope.For(
            new AccessProfile
            {
                CacheKey = "i", UserId = 1, SecurityStamp = "s",
                Permissions = new HashSet<string>(StringComparer.Ordinal),
                Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
                Denies = new HashSet<string>(StringComparer.Ordinal) { $"{ResourceKind.Table}:{Table1}" },
                RoleIds = new HashSet<int>(),
                IsIntegrationWriter = true,
            },
            Project,
            Snapshot);

        Assert.False(scope.CanReadTable(Table1));
        Assert.True(scope.CanReadTable(Table2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Аркуш_видимий_без_заборон_закритий_забороною_на_нього_чи_на_всі_його_таблиці()
    {
        Assert.All([SheetA, SheetB], s => Assert.True(Scope(Reader()).CanReadSheet(s)));
        Assert.All(["SA", "SB"], c => Assert.True(Scope(Reader()).CanReadSheetCode(c)));

        // Заборона на аркуш закриває його, сусідній лишається.
        var sheetDenied = Scope(Reader().Deny(ResourceKind.Sheet, SheetA));
        Assert.False(sheetDenied.CanReadSheet(SheetA));
        Assert.False(sheetDenied.CanReadSheetCode("SA"));
        Assert.True(sheetDenied.CanReadSheet(SheetB));

        // Заборонена одна таблиця з двох - аркуш лишається видимим (бачить іншу).
        Assert.True(Scope(Reader().Deny(ResourceKind.Table, Table1)).CanReadSheet(SheetA));

        // Заборонені всі таблиці аркуша - аркуша для питального немає.
        var allTables = Scope(Reader().Deny(ResourceKind.Table, Table1).Deny(ResourceKind.Table, Table2));
        Assert.False(allTables.CanReadSheet(SheetA));
        Assert.True(allTables.CanReadSheet(SheetB));

        // Невідомий знімку код - закритий.
        Assert.False(Scope(Reader()).CanReadSheetCode("NOPE"));
    }

    private static AccessBuilder Reader()
        => new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Read);

    private static DocumentReadScope Scope(AccessBuilder builder)
        => DocumentReadScope.For(builder.Build(), Project, Snapshot);

    private static TemplateVersionSnapshot Build()
    {
        var sheetA = Sheet(SheetA, "A");
        var sheetB = Sheet(SheetB, "B");
        var columns = new Dictionary<int, ColumnDef>();

        sheetA.AddTable(Table(Table1, SheetA, columns, Col1a, Col1b));
        sheetA.AddTable(Table(Table2, SheetA, columns, Col2a));
        sheetB.AddTable(Table(Table3, SheetB, columns, Col3a));

        return new TemplateVersionSnapshot(1, 0, [sheetA, sheetB], columns, new Dictionary<(int, string), RowDef>());
    }

    private static SheetDef Sheet(int id, string code)
    {
        var sheet = new SheetDef(1, EcrCode.Create("S" + code), Text(code), id);
        SetId(sheet, id);
        return sheet;
    }

    private static TableDef Table(int id, int sheetId, Dictionary<int, ColumnDef> index, params int[] columnIds)
    {
        var table = new TableDef(
            sheetId, EcrCode.Create("T" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Text("T"), id, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, id);

        foreach (var columnId in columnIds)
        {
            var column = new ColumnDef(
                id, EcrCode.Create("C" + columnId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Text("C"), columnId, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
            index[columnId] = column;
        }

        return table;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
