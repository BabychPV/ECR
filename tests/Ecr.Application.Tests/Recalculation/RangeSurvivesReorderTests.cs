// tests/Ecr.Application.Tests/Recalculation/RangeSurvivesReorderTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ФВ-0.2 («ідентичність явна: рядок — <c>RowKey</c>, не позиція») на ФАКТИЧНИХ шляхах продукту —
/// граф залежностей при публікації та читання комірки в рантаймі, — а не на власному вході тесту.
/// </summary>
/// <remarks>
/// ⛔ Попередній тест (<c>RangeExpansionTests.Зміна_Ordinal_після_публікації_НЕ_змінює_результат_формули</c>)
/// не міг впасти: <c>published</c> рахувався ДО <c>Reorder</c>, після нього розкриття не викликалось, а
/// «сума» складалась із цього ж списку ключів — тест порівнював вхід із самим собою
/// (REQ-COVERAGE-2026-10-11 §5 п. 5). Він видалений; його місце — ці два.
///
/// ⚠ Що ці тести НЕ закривають — і це відкрита прогалина продукту, а не тесту:
/// <c>SliceEvaluationContext.Read</c> розкриває діапазон <c>[A:B]</c> у рантаймі за ПОТОЧНИМ
/// <c>Ordinal</c> (<c>RangeExpander.Expand</c> над знімком), а <c>PATCH …/presentation</c> дозволяє
/// змінити <c>RowDef.Ordinal</c> в ОПУБЛІКОВАНІЙ версії (<c>ChangeClassifier.PresentationFields</c>,
/// <c>TemplateVersionStore.PresentationColumns</c>). Тобто після перестановки рядка в діапазон
/// <c>[7001001:7001003]</c> потрапляє інший набір рядків, і сума мовчки змінюється (відтворено:
/// 60 → 1039). Відтворення — у <c>.sync-local/ready/tests-weak-evidence/</c>; воно червоне, доки
/// продукт не виправлено, тому в гілку не потрапило.
///
/// Мутаційні докази (detached-worktree, не комітяться): (а) у <c>SliceEvaluationContext.Read</c>
/// адресувати одиничне посилання за ПОЗИЦІЄЮ (<c>rows.OrderBy(Ordinal).ElementAt(n)</c>) —
/// червоніє «Одиничне_посилання…»; (б) у <c>DependencyExtractor</c> передавати <c>SortOrder</c> =
/// <c>Ordinal</c> рядка замість лічильника — червоніє «Граф_залежностей…».
/// </remarks>
public sealed class RangeSurvivesReorderTests
{
    private static readonly PeriodContext Period =
        new(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1);

    private static readonly UnitCatalogSnapshot NoUnits = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Одиничне_посилання_читає_рядок_за_RowKey_а_не_за_позицією_навіть_після_перестановки()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var jan = builder.Column(table, "Jan");
        foreach (var (key, ordinal) in new[] { ("7001001", 1), ("7001002", 2), ("7001003", 3), ("7001004", 4) })
        {
            builder.Row(table, key, ordinal);
        }

        var snapshot = builder.Build();
        var values = new Dictionary<CellKey, ExpressionValue>();
        foreach (var (key, amount) in new[] { ("7001001", 10m), ("7001002", 20m), ("7001003", 30m), ("7001004", 999m) })
        {
            values[new CellKey(0, table.Id, key, jan.Id)] = ExpressionValue.Number(amount);
        }

        decimal Read(string rowKey)
        {
            var context = new SliceEvaluationContext(
                snapshot, values, new Dictionary<string, ExpressionValue>(), Period, NoUnits,
                new Dictionary<int, IReadOnlyDictionary<string, long>>())
            {
                CurrentTableDefId = table.Id,
            };

            return Expr.Number($"[Main].[{rowKey}].[Jan]", context);
        }

        Assert.Equal(20m, Read("7001002"));
        Assert.Equal(999m, Read("7001004"));

        // Презентаційна правка: 7001004 переїхав на другу позицію, 7001002 — на четверту.
        table.Rows.Single(r => r.RowKeyValue == "7001004").Reorder(2);
        table.Rows.Single(r => r.RowKeyValue == "7001002").Reorder(4);

        // Той самий ключ — те саме значення; жодна з двох не «перетекла» в сусіда.
        Assert.Equal(20m, Read("7001002"));
        Assert.Equal(999m, Read("7001004"));
        Assert.Equal(30m, Read("7001003"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Граф_залежностей_діапазону_несе_ключі_а_не_значення_Ordinal()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan");

        // Ordinal — не 1..4, а 10/20/30/40: якщо в граф просочиться значення Ordinal (а не ключ і
        // порядковий номер у списку), це видно одразу.
        foreach (var (key, ordinal) in new[] { ("7001001", 10), ("7001002", 20), ("7001003", 30), ("7001004", 40) })
        {
            builder.Row(table, key, ordinal);
        }

        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), new RangeExpander());
        var root = Expr.Parse("SUM([Main].[7001001:7001003].[Jan])").Expression!.Root;

        var dependencies = extractor.Extract(
            root, table.Id, null, new Dictionary<int, Ecr.Domain.Entities.Configuration.TableDef> { [table.Id] = table });

        Assert.Equal(["7001001", "7001002", "7001003"], dependencies.Select(d => d.RowKey));
        Assert.Equal([0, 1, 2], dependencies.Select(d => d.SortOrder));
        Assert.All(dependencies, d => Assert.NotNull(d.RowKey));
    }
}
