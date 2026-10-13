// tests/Ecr.Application.Tests/Documents/VersionMigrationNumericWidenTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Precision/Scale колонки між версіями (LP-29): РОЗШИРЕННЯ (28/12 → 34/16) переносить значення без відмови,
/// ЗВУЖЕННЯ (і втрата цілих розрядів) з даними лишається <c>guardedWithData</c>.
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (червоніють названі тести): повернути в <c>CompareColumns</c> старі два
/// <c>Guarded(...Precision...)</c>/<c>Guarded(...Scale...)</c> замість <c>NumericFormat</c> — червоніють «розширення»
/// і «null»; у <c>IsWidening</c> прибрати перевірку <c>pa - sa &gt;= p0 - s0</c> — «цілих розрядів»; прибрати
/// <c>precisionBefore is { } pb &amp;&amp;</c> (null → число вважати розширенням) — «було без межі».
/// </remarks>
public sealed class VersionMigrationNumericWidenTests
{
    private const string Sheet = "S29";

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Розширення_28_12_до_34_16_з_даними_переносить_значення_без_відмови()
    {
        var plan = Plan((28, 12), (34, 16), values: 7);

        Assert.Equal(7, plan.TransferredValues);
        Assert.Equal(0, plan.LostValues);
        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        var column = Assert.Single(plan.Columns);
        Assert.Equal((101, 201), (column.SourceColumnDefId, column.TargetColumnDefId));

        var widened = plan.Items.Where(i => i.Kind == "Widened").ToList();
        Assert.Equal(["Precision", "Scale"], widened.Select(i => i.Field).Order(StringComparer.Ordinal));
        Assert.All(widened, i => Assert.Equal(ChangeClass.Safe, i.ChangeClass));
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Modified");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Розширення_лише_Precision_або_лише_Scale_теж_без_відмови()
    {
        Assert.Empty(Plan((28, 12), (34, 12), values: 3).RefusalsFor(VersionMigrationMode.Safe));
        Assert.Empty(Plan((28, 12), (30, 14), values: 3).RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Без_змін_Precision_Scale_нічого_не_додає()
    {
        var plan = Plan((28, 12), (28, 12), values: 3);

        Assert.DoesNotContain(plan.Items, i => i.Kind is "Widened" or "Modified");
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Theory]
    [InlineData(34, 16, 28, 12)] // 34/16 → 28/12: звуження обох
    [InlineData(28, 12, 28, 10)] // Scale менша
    [InlineData(28, 12, 20, 12)] // Precision менша
    [InlineData(28, 12, 28, 20)] // цілих розрядів 16 → 8, хоч Scale більша
    [InlineData(28, 12, 34, 20)] // цілих розрядів 16 → 14, хоч обидві більші
    public void Звуження_і_втрата_цілих_розрядів_з_даними_лишаються_guardedWithData(int p0, int s0, int p1, int s1)
    {
        var plan = Plan(((byte)p0, (byte)s0), ((byte)p1, (byte)s1), values: 4);

        Assert.True(plan.GuardedValues >= 4, $"guarded {plan.GuardedValues}");
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Widened");
        Assert.Contains(plan.Items, i => i.Kind == "Modified" && i.Field is "Precision" or "Scale");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Було_без_межі_стало_з_межею_це_звуження()
    {
        var plan = Plan((null, null), (34, 16), values: 2);

        Assert.True(plan.GuardedValues >= 2, $"guarded {plan.GuardedValues}");
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));

        // Лише Precision з'явилась (Scale як не було, так немає): теж звуження.
        var onlyPrecision = Plan((null, null), (34, null), values: 2);
        Assert.Contains("guardedWithData", onlyPrecision.RefusalsFor(VersionMigrationMode.Safe));

        // Межу Scale знято при тій самій Precision — розширення.
        Assert.Empty(Plan((28, 12), (28, null), values: 2).RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Стало_без_межі_це_розширення()
    {
        var plan = Plan((28, 12), (null, null), values: 2);

        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Contains(plan.Items, i => i.Kind == "Widened" && i.Field == "Precision" && i.OldValue == "28" && i.NewValue is null);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Режим_Presentation_розширення_формату_не_пропускає()
    {
        var plan = Plan((28, 12), (34, 16), values: 3);

        Assert.Contains("structural", plan.RefusalsFor(VersionMigrationMode.Presentation));
    }

    private static VersionMigrationPlan Plan(
        (byte? Precision, byte? Scale) from, (byte? Precision, byte? Scale) to, long values)
        => VersionMigrationPlanner.Plan(
            Version(1, from, 100, 101),
            Version(2, to, 200, 201),
            [new VersionMigrationCellUsage(101, null, values)],
            new Dictionary<int, long>());

    private static TemplateVersionSnapshot Version(int versionId, (byte? Precision, byte? Scale) format, int tableId, int columnId)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create(Sheet), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T29"), Name("T29"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, tableId);
        var column = new ColumnDef(tableId, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal);
        SetId(column, columnId);
        column.SetNumericFormat(format.Precision, format.Scale);
        table.AddColumn(column);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
