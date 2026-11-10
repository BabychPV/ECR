// tests/Ecr.Application.Tests/Templates/PublishMonthGroupWarningTests.cs
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Land 10.11: місячні колонки без групи заголовків — попередження публікації
/// (<see cref="PublishChecks.FindMonthColumnsWithoutMonthGroup"/>), а не відмова; таблиця, де всі місячні колонки
/// в групах, і таблиця без місячних колонок попереджень не дають.
/// </summary>
/// <remarks>
/// Червоне до зміни (прогнано): заглушка, що повертала порожній перелік, — перший і третій тести червоні.
/// Мутація (прогнано): прибрати перевірку <c>HeaderGroupId</c> (рахувати всі місячні колонки) — другий і третій тести червоні.
/// </remarks>
public sealed class PublishMonthGroupWarningTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Місячні_колонки_без_групи_дають_одне_попередження_на_таблицю_з_кількістю_і_прикладом()
    {
        var version = Version(("T3_00", [("Hours_M01", 1, null), ("Fuel_M01", 1, null), ("Hours_M02", 2, null), ("Plain", null, null)]));

        var warning = Assert.Single(PublishChecks.FindMonthColumnsWithoutMonthGroup(version));

        Assert.StartsWith("T3_00: 3 із 3", warning, StringComparison.Ordinal);
        Assert.Contains("Hours_M01", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Місячні_колонки_в_групах_і_таблиця_без_місяців_попереджень_не_дають()
    {
        var version = Version(
            ("T6_01", [("Jan", 1, "MB_M01"), ("Feb", 2, "MB_M02"), ("Note", null, null)]),
            ("T8_00", [("Waste", null, null)]));

        Assert.Empty(PublishChecks.FindMonthColumnsWithoutMonthGroup(version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Частина_без_групи_називає_лише_її()
    {
        var version = Version(("T9_01", [("Jan", 1, "MB_M01"), ("Feb", 2, null)]));

        var warning = Assert.Single(PublishChecks.FindMonthColumnsWithoutMonthGroup(version));

        Assert.StartsWith("T9_01: 1 із 2", warning, StringComparison.Ordinal);
        Assert.Contains("Feb", warning, StringComparison.Ordinal);
    }

    private static TemplateVersion Version(params (string Table, (string Code, int? Month, string? Group)[] Columns)[] tables)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, DateTime.UtcNow);
        SetId(version, 7);
        var sheet = new SheetDef(7, EcrCode.Create("S1"), Text("S1"), 1);
        SetId(sheet, 1);
        var nextId = 100;
        var tableId = 10;
        foreach (var (code, columns) in tables)
        {
            var table = new TableDef(1, EcrCode.Create(code), Text(code), tableId, TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
            SetId(table, tableId++);
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordinal = 0;
            foreach (var (columnCode, month, group) in columns)
            {
                int? groupId = null;
                if (group is not null)
                {
                    if (!groups.TryGetValue(group, out var gid))
                    {
                        var def = new ColumnHeaderGroupDef(table.Id, EcrCode.Create(group), Text(group), groups.Count + 1);
                        SetId(def, gid = nextId++);
                        table.AddHeaderGroup(def);
                        groups[group] = gid;
                    }

                    groupId = gid;
                }

                var column = new ColumnDef(table.Id, EcrCode.Create(columnCode), Text(columnCode), ++ordinal, CellDataType.Decimal);
                SetId(column, nextId++);
                column.SetMonth(month is not null, month is null ? null : (byte)month);
                column.SetHeaderGroup(groupId);
                table.AddColumn(column);
            }

            sheet.AddTable(table);
        }

        version.AddSheet(sheet);
        return version;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
