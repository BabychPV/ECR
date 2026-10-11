using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ФВ-0.2 / D-17: діапазон <c>[A:B]</c> у рантаймі читає ті рядки, які розкрила публікація
/// (<c>cfg.FormulaDependency</c>), а не поточний <c>RowDef.Ordinal</c>. Презентаційний патч порядку
/// (ФВ-7.2) в опублікованій версії не змінює суму.
/// </summary>
public sealed class RangeRuntimeReorderTests
{
    private static readonly PeriodContext Period =
        new(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1);

    private static readonly UnitCatalogSnapshot NoUnits = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>());

    private readonly RealFormulaEngine _engine = new();

    private sealed record Fixture(
        TableDef Table,
        FormulaDef Formula,
        TemplateVersionSnapshot Snapshot,
        IReadOnlyList<FormulaDependency> Published,
        Dictionary<CellKey, ExpressionValue> Values,
        ColumnDef Jan);

    private Fixture Arrange(string expression)
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var jan = builder.Column(table, "Jan");
        var total = builder.Column(table, "Total");
        foreach (var (key, ordinal) in new[] { ("7001001", 1), ("7001002", 2), ("7001003", 3), ("7001004", 4) })
        {
            builder.Row(table, key, ordinal);
        }

        var formula = builder.Formula(table, expression, column: total);

        // Граф залежностей — той самий код, що й при публікації.
        var published = PublishChecks.Dependencies(builder.Version(), _engine);
        var snapshot = builder.Build();

        var values = new Dictionary<CellKey, ExpressionValue>();
        foreach (var (key, amount) in new[] { ("7001001", 10m), ("7001002", 20m), ("7001003", 30m), ("7001004", 999m) })
        {
            values[new CellKey(0, table.Id, key, jan.Id)] = ExpressionValue.Number(amount);
        }

        return new Fixture(table, formula, snapshot, published, values, jan);
    }

    private decimal Evaluate(Fixture f, bool withPublishedGraph)
    {
        var context = new SliceEvaluationContext(
            f.Snapshot, f.Values, new Dictionary<string, ExpressionValue>(), Period, NoUnits,
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            formulaDependencies: withPublishedGraph ? f.Published : null)
        {
            CurrentTableDefId = f.Table.Id,
        };

        var parsed = _engine.Parse(f.Formula.Expression, ExpressionDialect.Template);
        Assert.True(parsed.IsSuccess);
        context.UseFormula(f.Formula, parsed.Expression!.Root);

        var result = _engine.Evaluate(parsed.Expression, context).Value;
        Assert.False(result.IsError);
        return Convert.ToDecimal(result.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SwapRowsInsideAndOutsideRange(TableDef table)
    {
        // Презентаційна правка: 7001004 переїхав усередину «діапазону», 7001002 — за нього.
        table.Rows.Single(r => r.RowKeyValue == "7001004").Reorder(2);
        table.Rows.Single(r => r.RowKeyValue == "7001002").Reorder(4);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Перестановка_рядків_після_публікації_не_змінює_суму_діапазону_у_рантаймі()
    {
        var f = Arrange("SUM([Main].[7001001:7001003].[Jan])");

        Assert.Equal(60m, Evaluate(f, withPublishedGraph: true));

        SwapRowsInsideAndOutsideRange(f.Table);

        Assert.Equal(60m, Evaluate(f, withPublishedGraph: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Кілька_діапазонів_однієї_формули_кожен_читає_свій_опублікований_пробіг()
    {
        // Суміжні й перетинні діапазони в одній формулі: пробіг кожного посилання береться за його
        // власною позицією в графі, а не за ключами «будь-де в формулі».
        var f = Arrange("SUM([Main].[7001002:7001003].[Jan]) + SUM([Main].[7001001:7001002].[Jan])");

        Assert.Equal(80m, Evaluate(f, withPublishedGraph: true));

        SwapRowsInsideAndOutsideRange(f.Table);

        Assert.Equal(80m, Evaluate(f, withPublishedGraph: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Без_збереженого_графа_чернетка_діапазон_береться_за_поточним_порядком()
    {
        var f = Arrange("SUM([Main].[7001001:7001003].[Jan])");

        Assert.Equal(60m, Evaluate(f, withPublishedGraph: false));

        SwapRowsInsideAndOutsideRange(f.Table);

        // 7001001 (1), 7001004 (2), 7001003 (3): чернетка рахує за порядком, який бачить автор.
        Assert.Equal(10m + 999m + 30m, Evaluate(f, withPublishedGraph: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Граф_публікації_несе_ключі_діапазону_у_порядку_публікації()
    {
        var f = Arrange("SUM([Main].[7001001:7001003].[Jan])");

        var keys = f.Published
            .Where(d => d.FormulaDefId == f.Formula.Id)
            .OrderBy(d => d.SortOrder)
            .Select(d => d.RowKey)
            .ToList();

        Assert.Equal(["7001001", "7001002", "7001003"], keys);
    }
}
