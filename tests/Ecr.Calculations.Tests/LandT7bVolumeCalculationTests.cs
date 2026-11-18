// tests/Ecr.Calculations.Tests/LandT7bVolumeCalculationTests.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// B26 (P-0118-24/25): <c>ECW_C09_02_01</c> на <c>T7b_00</c> — <c>ECW_EC_tons = (@Concentration * @Volume) / k1</c>,
/// k1 = 1 000 000 (мг/л * м3 -> т). <c>Concentration</c> — колонка місяця m СВОГО рядка; <c>Volume</c> — комірка рядка
/// <c>S3_9</c> у колонці місяця m (одна на всі рядки речовин); S3_9 сам не рахується (фільтр D-425, колонка статусу).
/// Прив'язки й статус беруться з <c>land-template.model.json</c> і проганяються справжнім шляхом прогону:
/// <c>MethodologyResolver</c> (фільтр активності -> правило <c>{}</c>) -> <c>CalculationInputBuilder</c> (псевдоніми
/// місяця YearMonths) -> <c>GenericCalculationModule</c>.
/// </summary>
/// <remarks>
/// Значення колонки-формули <c>Volume_Mmm</c> рядка речовини = комірка, на яку вказує вираз моста в моделі (рядок і
/// колонка місяця m) — саме це обчислює граф перерахунку (доведено наскрізь у
/// <c>Ecr.Api.Tests.FixedRowReferenceRecalculationTests</c>). ⚠ Текст формули стенда (k1 — константа методології) у
/// репозиторії відсутній; k1 тут літерал. Мутації: (m1) міст на інший рядок, (m2) зсув місяця — інше число / немає
/// аргументу; S3_9 у пресетах статусу — S3_9 отримує результат.
/// </remarks>
public sealed class LandT7bVolumeCalculationTests
{
    private const int VersionId = 461;
    private const int TonneUnit = 1;
    private const long DocumentId = 96;
    private const long TableInstance = 7701;
    private const int TemplateVersion = 17;
    private const int TableDefId = 77;
    private const long RunningEntry = 501;
    private const string Formula = "(@Concentration * @Volume) / 1000000";

    private static readonly string[] Names =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    [InlineData(202609, "S1_6", 2.5, 1000, 0.0025)]
    [InlineData(202609, "S2_7", 7.2, 1000, 0.0072)]
    [InlineData(202603, "S1_6", 4.0, 500, 0.002)]
    public async Task Тонни_концентрація_рядка_на_обсяг_S3_9_того_самого_місяця(
        int period, string row, double concentration, double volume, double tonnes)
    {
        var world = World.FromModel();
        var month = period % 100;

        // інші місяці й інші рядки мають інші значення: не той рядок чи місяць дав би інше число
        world.Concentration = (rk, m) => rk == row && m == month ? (decimal)concentration : 10m + m + (rk == "S2_7" ? 100m : 0m);
        world.VolumeRow = m => m == month ? (decimal)volume : 3000m + m;

        var match = await world.MatchAsync(period, statusAfterStep: true);
        Assert.Contains(row, match.Matches.Select(x => x.RowKey));

        var input = Assert.Single(await world.BuildAsync(period, [row]));
        Assert.Equal((decimal)concentration, Assert.Single(input.Arguments, a => a.ArgumentCode == "Concentration").Value);
        Assert.Equal((decimal)volume, Assert.Single(input.Arguments, a => a.ArgumentCode == "Volume").Value);

        var output = await RunAsync(input, period);
        Assert.Equal((decimal)tonnes, Assert.Single(output.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    public async Task S3_9_не_рахується_і_не_дає_проблеми_рядки_речовин_рахуються()
    {
        var world = World.FromModel();
        world.Concentration = (_, m) => 2.5m;
        world.VolumeRow = m => 1000m;

        var match = await world.MatchAsync(202609, statusAfterStep: true);

        // виключений фільтром рядок не потрапляє в зіставлення: ні результату, ні MissingInputs (проблеми пишуться
        // лише для рядків, яким правило підійшло) -> у банері S3_9 немає
        Assert.Equal(["S1_6", "S2_7"], match.Matches.Select(x => x.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(["S3_9"], match.Excluded);
        Assert.False(match.FilterInert);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    public async Task Наявний_документ_без_статусу_не_рахує_нічого_після_етапу_рахує_рядки_речовин()
    {
        var world = World.FromModel();
        world.Concentration = (_, m) => 2.5m;
        world.VolumeRow = m => 1000m;

        // наявний документ до етапу T7bStatus: колонка статусу нова й порожня -> фільтр виключає всі рядки
        var before = await world.MatchAsync(202609, statusAfterStep: false);
        Assert.Empty(before.Matches);
        Assert.Equal(["S1_6", "S2_7", "S3_9"], before.Excluded.Order(StringComparer.Ordinal));

        // після етапу (Running у statusPresets.rows моделі — те, що пише Get-LandStatusPresetPlan, див.
        // Ecr.Architecture.Tests.LandT7bStatusStageTests): рядки речовин рахуються, S3_9 — ні
        var after = await world.MatchAsync(202609, statusAfterStep: true);
        Assert.Equal(["S1_6", "S2_7"], after.Matches.Select(x => x.RowKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    public async Task Порожній_обсяг_S3_9_місяця_немає_Volume_немає_результату_проблема_називає_Volume()
    {
        var world = World.FromModel();
        world.Concentration = (_, m) => 2.5m;
        world.VolumeRow = m => m == 9 ? null : 1000m;

        var input = Assert.Single(await world.BuildAsync(202609, ["S1_6"]));
        Assert.Contains(input.Arguments, a => a.ArgumentCode == "Concentration");
        Assert.DoesNotContain(input.Arguments, a => a.ArgumentCode == "Volume");

        // рядок речовини підходить правилу, але без @Volume результату немає: модуль називає відсутній аргумент —
        // це проблема MissingInputs «Volume» у банері (рядок речовини, не S3_9)
        var output = await RunAsync(input, 202609);
        Assert.Empty(output.Values);
        Assert.Contains("Volume", output.MissingArguments ?? []);
    }

    private sealed class World
    {
        private readonly Dictionary<int, ColumnDef> columns = [];
        private readonly Dictionary<string, int> idByCode = new(StringComparer.Ordinal);
        private readonly Dictionary<(int, int), IReadOnlyList<MonthInputAlias>> aliases = [];
        private readonly Dictionary<string, long> rowIds = new(StringComparer.Ordinal);
        private readonly Dictionary<int, (string Row, string Column)> volumeSource = [];
        private readonly List<string> runningRows = [];
        private int statusColumn;

        public Func<string, int, decimal?> Concentration { get; set; } = (_, _) => null;

        public Func<int, decimal?> VolumeRow { get; set; } = _ => null;

        /// <summary>
        /// T7b_00 із моделі: рядки, місячні колонки концентрації, колонка статусу; міст Concentration -> псевдоніми місяця
        /// на January…December (селектор, як Get-LandSelectorSources); міст Volume -> Volume_Mmm (змінна формула YearMonths)
        /// з джерелом «рядок × колонка місяця m», розібраним із виразу моста.
        /// </summary>
        public static World FromModel()
        {
            var path = Path.Combine(RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var table = doc.RootElement.GetProperty("sheets").EnumerateArray()
                .SelectMany(s => s.GetProperty("tables").EnumerateArray())
                .Single(t => t.GetProperty("code").GetString() == "T7b_00");

            var world = new World();
            long rowId = 990000;
            foreach (var r in table.GetProperty("rows").EnumerateArray())
            {
                world.rowIds[r.GetProperty("key").GetString()!] = ++rowId;
            }

            var bridges = new Dictionary<string, string>(StringComparer.Ordinal);
            var id = 1000;
            foreach (var c in table.GetProperty("columns").EnumerateArray())
            {
                var code = c.GetProperty("code").GetString()!;
                var type = c.GetProperty("dataType").GetString();
                if (code is "Concentration" or "Volume")
                {
                    bridges[code] = c.GetProperty("expression").GetString()!;
                    continue;
                }

                if (type == "Decimal" && c.TryGetProperty("month", out var mo) && mo.ValueKind == JsonValueKind.Number)
                {
                    world.Add(code, CellDataType.Decimal, (byte)mo.GetInt32(), ++id);
                }
                else if (type == "Lookup" && code == table.GetProperty("statusPresets").GetProperty("column").GetString())
                {
                    world.statusColumn = ++id;
                    world.Add(code, CellDataType.Lookup, null, id);
                }
            }

            world.runningRows.AddRange(table.GetProperty("statusPresets").GetProperty("rows").EnumerateArray().Select(x => x.GetString()!));

            // Concentration: селектор місяця свого рядка
            var concentration = Selector(bridges["Concentration"]);
            for (var m = 1; m <= 12; m++)
            {
                world.Alias("Concentration", m, world.idByCode[concentration[m - 1]]);
            }

            // Volume: YearMonths робить Volume_Mmm; псевдонім місяця m -> Volume_Mmm, значення = комірка джерела виразу
            var volume = RowRefs(bridges["Volume"]);
            Assert.Equal(12, volume.Count);
            for (var m = 1; m <= 12; m++)
            {
                world.Add($"Volume_M{m:00}", CellDataType.Formula, (byte)m, ++id);
                world.Alias("Volume", m, id);
                world.volumeSource[m] = volume[m - 1];
            }

            return world;
        }

        public async Task<RowMatchOutcome> MatchAsync(int period, bool statusAfterStep)
        {
            var key = new PeriodKey(period);
            var cells = Cells(key, statusAfterStep);

            var store = Substitute.For<IMethodologyStore>();
            store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
                .Returns([new MethodologyRule(VersionId, EcrCode.Create("R_C09_02_01_01"), "{}", priority: 10)]);

            var activity = Substitute.For<IRowActivityFilterSource>();
            activity.ResolveForVersionAsync(VersionId, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                .Returns(new RowActivityFilterResolution(
                    RunningEntry.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new Dictionary<int, IReadOnlyList<int>> { [TableDefId] = [statusColumn] }));

            return await new MethodologyResolver(store, CellStore(cells), Rows(period), columnMapper: null, rowActivity: activity)
                .MatchRowsDetailedAsync(VersionId, TableInstance, CancellationToken.None);
        }

        public async Task<IReadOnlyList<CalculationInput>> BuildAsync(int period, IReadOnlyList<string> rowKeys)
        {
            var key = new PeriodKey(period);
            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
                new TemplateVersionSnapshot(TemplateVersion, 0, [], columns, new Dictionary<(int TableDefId, string RowKey), RowDef>())
                {
                    MonthInputAliases = aliases,
                });

            return await new CalculationInputBuilder(CellStore(Cells(key, statusAfterStep: true)), metadata, Rows(period))
                .BuildAsync(TableInstance, rowKeys, key, Descriptor(), CancellationToken.None);
        }

        private List<CellRecord> Cells(PeriodKey key, bool statusAfterStep)
        {
            var records = new List<CellRecord>();
            foreach (var (rowKey, rowId) in rowIds)
            {
                for (var m = 1; m <= 12; m++)
                {
                    // колонки місяця: концентрація рядка речовини; у рядку S3_9 — об'єм (як у Water Report)
                    var value = rowKey == "S3_9" ? VolumeRow(m) : Concentration(rowKey, m);
                    Add(records, key, rowId, Names[m - 1], value);
                }

                if (statusAfterStep && runningRows.Contains(rowKey))
                {
                    records.Add(new CellRecord(
                        new CellAddress(key, rowId, statusColumn), TableDefId, new CellValueData { ValueRegistryEntryId = RunningEntry }));
                }
            }

            // Volume_Mmm кожного рядка = комірка джерела виразу моста (рядок × колонка місяця m)
            foreach (var (rowKey, rowId) in rowIds)
            {
                for (var m = 1; m <= 12; m++)
                {
                    var (srcRow, srcColumn) = volumeSource[m];
                    var source = records.SingleOrDefault(r => r.Address.TableRowId == rowIds[srcRow]
                                                              && r.Address.ColumnDefId == idByCode[srcColumn]);
                    Add(records, key, rowId, $"Volume_M{m:00}", source?.Value.ValueNumeric);
                }
            }

            return records;
        }

        private void Add(List<CellRecord> records, PeriodKey key, long rowId, string code, decimal? value)
        {
            if (value is { } v)
            {
                records.Add(new CellRecord(new CellAddress(key, rowId, idByCode[code]), TableDefId, new CellValueData { ValueNumeric = v }));
            }
        }

        private void Add(string code, CellDataType type, byte? month, int id)
        {
            var column = new ColumnDef(
                TableDefId, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);
            if (month is { } m)
            {
                column.SetMonth(true, m);
            }

            columns[id] = column;
            idByCode[code] = id;
        }

        private void Alias(string name, int month, int columnId)
        {
            var key = (TableDefId, month);
            var list = aliases.TryGetValue(key, out var have) ? new List<MonthInputAlias>(have) : [];
            list.Add(new MonthInputAlias(TableDefId, name, (byte)month, columnId));
            aliases[key] = list;
        }

        private IRowStore Rows(int period)
        {
            var rows = Substitute.For<IRowStore>();
            rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
                .Returns(new TableInstanceRef(TableInstance, DocumentId, TableDefId, TemplateVersion, period));
            rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, long>(rowIds, StringComparer.Ordinal));
            return rows;
        }

        private static ICellStore CellStore(List<CellRecord> records)
        {
            var store = Substitute.For<ICellStore>();
            store.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<CellRecord>)records);
            return store;
        }

        private static List<string> Selector(string expression)
        {
            var branches = Regex.Matches(expression, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\] : ");
            Assert.Equal(11, branches.Count);
            var result = branches.Select(b => b.Groups[2].Value).ToList();
            var last = Regex.Match(expression, @": \[([^\]]+)\]$");
            Assert.True(last.Success);
            result.Add(last.Groups[1].Value);
            return result;
        }

        private static List<(string Row, string Column)> RowRefs(string expression)
            => [.. Regex.Matches(expression, @"\[T7b_00\]\.\[([^\]]+)\]\.\[([^\]]+)\]")
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value))];
    }

    /// <summary>Вхід -> справжній модуль.</summary>
    private static async Task<CalculationOutput> RunAsync(CalculationInput input, int period)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("ECW_EC_tons"), Formula);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("ECW_EC_tons"), TonneUnit)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        var month = new DateOnly(period / 100, period % 100, 1);
        periods.FindPeriodBoundsAsync(DocumentId, period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(month, month.AddMonths(1).AddDays(-1)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new(TonneUnit, "t", 1, 1000m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    private static MethodologyDescriptor Descriptor()
        => new(
            MethodologyId: 46,
            MethodologyVersionId: VersionId,
            Code: "ECW_C09_02_01",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
