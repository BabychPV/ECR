using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// P-3 (аудит 2026-11-10): «Перевірити» і подання (спільний <c>TableValidation.RunAsync</c>, ФВ-5.4) будують словник
/// значень зрізу ОДИН раз на таблицю, а не на кожен рядок; правило таблиці бачить перше значення колонки, як і було.
/// </summary>
/// <remarks>
/// Мутації: повернути в <c>SliceContext.ForRow</c> побудову нового контексту з усіх комірок — тест алокацій червоний
/// (≈ 2 000 словників по 10 000 записів); віддавати з <c>_firstByColumn</c> останнє значення замість першого — тест
/// семантики червоний.
/// </remarks>
public sealed class TableValidationScaleTests(ITestOutputHelper output)
{
    private static readonly PeriodKey Period = new(202601);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Перевірка_рядків_не_перебудовує_зріз_на_кожен_рядок()
    {
        const int rows = 2_000;
        const int columns = 5;

        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("S"), "T", TableRowMode.Dynamic);
        var columnDefs = Enumerable.Range(0, columns).Select(c => builder.Column(table, $"C{c}")).ToList();
        table.AddValidationRule(Rule(table, "NONNEG", scope: 1, "[C0] >= 0"));
        var snapshot = builder.Build();

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var cells = new List<CellRecord>(rows * columns);
        for (var r = 0; r < rows; r++)
        {
            var rowId = r + 1L;
            rowIds[$"R{r}"] = rowId;
            foreach (var column in columnDefs)
            {
                var value = r == 7 && column.Code == "C0" ? -1m : 1m;
                cells.Add(new CellRecord(new CellAddress(Period, rowId, column.Id), table.Id, new CellValueData { ValueNumeric = value }));
            }
        }

        var engine = new ValidationEngine(new RealFormulaEngine());
        var registries = Substitute.For<IRegistryStore>();

        // Прогрів (JIT), потім замір.
        _ = await TableValidation.RunAsync(engine, registries, snapshot, table, cells, rowIds, NoHeaders, "en", default);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var messages = await TableValidation.RunAsync(engine, registries, snapshot, table, cells, rowIds, NoHeaders, "en", default);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"TableValidation.RunAsync, {rows} рядків × {columns} колонок: {watch.Elapsed.TotalMilliseconds:F0} мс, алокацій {allocated / 1024 / 1024} МБ");

        var message = Assert.Single(messages);
        Assert.Equal("R7", message.RowKey);
        Assert.Equal("NONNEG", message.RuleCode);
        Assert.True(
            allocated < 200_000_000,
            $"Перевірка {rows} рядків алокувала {allocated / 1024 / 1024} МБ (бюджет 190 МБ; словник зрізу на кожен рядок — ≈ 1,5 ГБ).");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[C1] = 5", false)]
    [InlineData("[C1] = 9", true)]
    public async Task Правило_таблиці_бачить_перше_значення_колонки(string expression, bool violated)
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("S"), "T", TableRowMode.Dynamic);
        var c1 = builder.Column(table, "C1");
        table.AddValidationRule(Rule(table, "FIRST", scope: 2, expression));
        var snapshot = builder.Build();

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["A"] = 20, ["B"] = 10 };

        // Порядок `cells` — не порядок id рядків: «перше» — саме за порядком зрізу.
        IReadOnlyList<CellRecord> cells =
        [
            new(new CellAddress(Period, 20, c1.Id), table.Id, new CellValueData { ValueNumeric = 5m }),
            new(new CellAddress(Period, 10, c1.Id), table.Id, new CellValueData { ValueNumeric = 9m }),
        ];

        var messages = await TableValidation.RunAsync(
            new ValidationEngine(new RealFormulaEngine()), Substitute.For<IRegistryStore>(), snapshot, table, cells, rowIds,
            NoHeaders, "en", default);

        Assert.Equal(violated, messages.Any(m => m.RuleCode == "FIRST"));
    }

    private static readonly IReadOnlyDictionary<string, ExpressionValue> NoHeaders = new Dictionary<string, ExpressionValue>();

    private static ValidationRule Rule(TableDef table, string code, byte scope, string expression)
        => new(
            table.Id, EcrCode.Create(code), ValidationSeverity.Error, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
}
