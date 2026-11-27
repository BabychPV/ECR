using System.Diagnostics;
using Ecr.Application.Documents.RequiredInputs;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ D-577 (L6, Q4, Q6): перелік порожніх обов'язкових входів методологій по ВСІХ рядках таблиці — той самий обчислювач,
/// що ворота PATCH (<see cref="MethodologyRequiredInputs"/>), — і його ціна на розмірі Land.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітити): (1) у <c>RequiredInputsScanner.Scan</c> прибрати пропуск «порожнього запасного рядка»
/// (<c>!started.Contains(rowId) &amp;&amp; !predefined…</c>) — червоний <c>L6_1_…</c> (порожній рядок отримує вимогу);
/// (2) <c>EvaluateRow(…, humanBatch: false)</c> у сканері — червоний <c>L6_1_…</c> (рівень <c>Block</c> стає попередженням).
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const long ScanInstance = 9001;

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-577")]
    public async Task L6_1_рядок_з_введеним_значенням_і_порожнім_входом_у_переліку_порожній_запасний_ні()
    {
        var category = WithMethodology(RequiredInputSeverity.Block);
        var scanner = new RequiredInputsScanner(_methodologies, _periods);
        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var table = snapshot.Sheets[0].Tables[0];

        var plan = await scanner.PlanAsync(700, Period, 2, table, CancellationToken.None);
        Assert.NotNull(plan);

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3 };
        CellRecord[] cells =
        [
            Cell(1, VolumeColumnId, new CellValueData { ValueNumeric = 5m }),          // розпочатий, Category порожня
            Cell(3, VolumeColumnId, new CellValueData { ValueNumeric = 7m }),
            Cell(3, category, new CellValueData { ValueString = "Engine" }),        // заповнений
        ];

        var messages = RequiredInputsScanner.Scan(plan!, table, snapshot.ColumnsById, cells, rowIds, "en");

        var only = Assert.Single(messages);
        Assert.Equal("R1", only.RowKey);
        Assert.Equal("Category", only.ColumnCode);
        Assert.Equal("ECR-CALC-0437", only.RuleCode);
        Assert.True(only.BlocksSave);
        Assert.Equal(["ECW_TEST"], only.Methodologies);
    }

    /// <summary>
    /// ✎ D-578 (рішення людини 26.11): поле, яке ЧИТАЄ скрипт версії, — обов'язкове саме собою (виведена вимога): помилка
    /// для подання, але НЕ блокування збереження (BlocksSave = false). Виняток методолога Optional знімає; маркер «читає
    /// будь-що» (<c>*</c>, скрипт не скомпілювався) — виведених вимог немає.
    /// </summary>
    /// <remarks>
    /// Мутації (локально): (1) у <c>RequiredInputCollector.Add</c> <c>saveBlocks = error</c> — червоний (виведена блокує
    /// збереження); (2) прибрати пропуск <c>Optional</c> у <c>EvaluateRow</c> — червоний другий крок; (3) прибрати пропуск
    /// <c>AllFields</c> у <c>DerivedColumnsAsync</c> — червоний третій крок; (4) прибрати фільтр <c>!c.IsComputed</c> —
    /// червоний «calculated»; (5) завжди Block для виведених — червоний «norules».
    /// </remarks>
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-578")]
    [InlineData("derived")]
    [InlineData("optional")]
    [InlineData("anything")]
    [InlineData("norules")]
    [InlineData("calculated")]
    [InlineData("formula")]
    [InlineData("readonly")]
    public async Task D578_поле_яке_читає_скрипт_обовязкове_для_подання_але_не_для_збереження(string kind)
    {
        // ✎ Огляд H3: версія з правилом відбору (застосовність рядка) — Block; без правил — лише попередження; обчислена
        // колонка (Calculated) — не вхід, вимоги немає.
        var category = WithMethodology(
            kind == "optional" ? RequiredInputSeverity.Optional : RequiredInputSeverity.Block,
            categoryType: kind switch
            {
                "calculated" => CellDataType.Calculated,
                "formula" => CellDataType.Formula,
                _ => CellDataType.String,
            },
            engine: MethodologyEngine.Script, withRule: kind != "norules",
            requiredColumnId: kind == "optional" ? 12 : VolumeColumnId);
        var versionId = _lastMethodology.Version.Id;
        if (kind == "readonly")
        {
            // ✎ Огляд H3: колонка «лише для читання» — людина її не заповнює, тож і не вимагається.
            _lastMethodology.Category.SetReadOnly(true);
        }

        _methodologies.GetReadFieldsOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyReadFieldInfo>>(
                      [
                          new(versionId, "", false),
                          new(versionId, kind == "anything" ? "*" : "Category", kind == "anything"),
                          new(versionId, "Volume", false),
                      ]));
        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var table = snapshot.Sheets[0].Tables[0];
        var plan = await new RequiredInputsScanner(_methodologies, _periods).PlanAsync(700, Period, 2, table, CancellationToken.None);

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1 };
        CellRecord[] cells = [Cell(1, VolumeColumnId, new CellValueData { ValueNumeric = 5m })];
        var messages = RequiredInputsScanner.Scan(plan!, table, snapshot.ColumnsById, cells, rowIds, "en")
            .Where(m => m.ColumnCode == "Category")
            .ToList();

        if (kind == "derived")
        {
            var only = Assert.Single(messages);
            Assert.Equal(Domain.Enums.ValidationSeverity.Error, only.Severity);
            Assert.False(only.BlocksSave);
        }
        else if (kind == "norules")
        {
            Assert.Equal(Domain.Enums.ValidationSeverity.Warning, Assert.Single(messages).Severity);
        }
        else
        {
            Assert.Empty(messages);
        }

        _ = category;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-577")]
    public async Task L6_таблиця_без_методологій_план_порожній_без_звернень_за_періодом()
    {
        WithMethodology(RequiredInputSeverity.Block);
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _periods.ClearReceivedCalls();
        var table = (await _metadata.GetAsync(2, CancellationToken.None)).Sheets[0].Tables[0];
        Assert.Null(await new RequiredInputsScanner(_methodologies, _periods).PlanAsync(700, Period, 2, table, CancellationToken.None));
        await _periods.DidNotReceive().FindPeriodBoundsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Ціна на розмірі Land (174 тис. рядків): план — фіксована кількість звернень до сховищ (≤ 8: прив'язки, межі періоду, версії, правила, вимоги, поля читання D-578, підписи) незалежно від рядків (немає
    /// N+1), оцінка — лінійна в пам'яті. Бюджет щедрий (повільна машина CI), найкраще з трьох прогонів.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-577")]
    public async Task L6_перф_174_тис_рядків_без_N_плюс_1_і_в_бюджеті()
    {
        const int Rows = 174_000;
        var category = WithMethodology(RequiredInputSeverity.Block);
        var scanner = new RequiredInputsScanner(_methodologies, _periods);
        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var table = snapshot.Sheets[0].Tables[0];

        _methodologies.ClearReceivedCalls();
        _periods.ClearReceivedCalls();
        var plan = await scanner.PlanAsync(700, Period, 2, table, CancellationToken.None);
        var storeCalls = _methodologies.ReceivedCalls().Count() + _periods.ReceivedCalls().Count();
        Assert.InRange(storeCalls, 1, 8);

        var rowIds = new Dictionary<string, long>(Rows, StringComparer.Ordinal);
        var cells = new List<CellRecord>(Rows * 2);
        for (var i = 1; i <= Rows; i++)
        {
            rowIds[$"R{i}"] = i;
            cells.Add(Cell(i, VolumeColumnId, new CellValueData { ValueNumeric = i }));
            if (i % 2 == 0)
            {
                cells.Add(Cell(i, category, new CellValueData { ValueString = "Engine" }));
            }
        }

        var best = TimeSpan.MaxValue;
        var found = 0;
        for (var round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew();
            found = RequiredInputsScanner.Scan(plan!, table, snapshot.ColumnsById, cells, rowIds, "en").Count;
            watch.Stop();
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.Equal(Rows / 2, found);
        // Вимір для звіту: файл у теці тимчасових (тест не друкує в консоль xunit).
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "ecr-d577-l6-scan.txt"),
            $"L6 scan: {Rows} rows, {cells.Count} cells, {found} missing, best {best.TotalMilliseconds:F0} ms, store calls {storeCalls}");
        Assert.True(best < TimeSpan.FromSeconds(5), $"Сканування {Rows} рядків: {best.TotalMilliseconds:F0} мс (бюджет 5 с).");
    }

    private static CellRecord Cell(long rowId, int columnId, CellValueData value)
        => new(new CellAddress(PeriodKey.Parse(Period), rowId, columnId), 3, value);
}
