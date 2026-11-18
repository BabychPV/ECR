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
/// B26 (P-0118-23, рішення людини, відповідь (a)): <c>ECW_C09_02_01</c> на <c>T7b_00</c> — <c>ECW_EC_tons =
/// (@Concentration * @Volume) / k1</c>, k1 = 1 000 000 (мг/л * м3 -> т). Прив'язку аргументів беремо з
/// <c>land-template.model.json</c> (мости-селектори <c>Concentration</c>/<c>Volume</c>), перетворюємо так само, як етап
/// YearMonths (селектор -> <c>cfg.MonthInputAlias</c> місяця m), і проганяємо справжній шлях прогону:
/// <c>CalculationInputBuilder</c> (псевдоніми місяця) -> <c>GenericCalculationModule</c>.
/// </summary>
/// <remarks>
/// ⚠ Текст формули на стенді (k1 — константа методології) у репозиторії відсутній; тут k1 підставлено літералом —
/// перевіряється прив'язка аргументів, а не резолвінг констант. Мутації: без моста Volume (прибрати з
/// <c>DECIDED_MONTH_ARGS</c>) — аргументу Volume немає, результату немає; Volume на сусідній місяць — інше число.
/// </remarks>
public sealed class LandT7bVolumeCalculationTests
{
    private const int VersionId = 461;
    private const int TonneUnit = 1;
    private const long DocumentId = 96;
    private const long TableInstance = 7701;
    private const int TemplateVersion = 17;
    private const int TableDefId = 77;
    private const long RowId = 990001;
    private const string Formula = "(@Concentration * @Volume) / 1000000";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    [InlineData(202609, 2.5, 1000, 0.0025)]
    [InlineData(202603, 7.2, 500, 0.0036)]
    public async Task Тонни_це_концентрація_на_обсяг_місяця_розрахунку(int period, double concentration, double volume, double tonnes)
    {
        var world = World.FromModel();
        var month = period % 100;

        // кожен інший місяць має інші значення: прив'язка не до того місяця дала б інше число
        var input = Assert.Single(await world.Build(period, m => m == month ? (decimal)concentration : 10m + m, m => m == month ? (decimal)volume : 3000m + m));

        Assert.Equal((decimal)concentration, Assert.Single(input.Arguments, a => a.ArgumentCode == "Concentration").Value);
        Assert.Equal((decimal)volume, Assert.Single(input.Arguments, a => a.ArgumentCode == "Volume").Value);
        Assert.Equal((decimal)tonnes, await RunAsync(input, period));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "B26")]
    public async Task Порожній_обсяг_місяця_не_дає_аргументу_Volume()
    {
        var world = World.FromModel();

        var input = Assert.Single(await world.Build(202609, _ => 2.5m, m => m == 9 ? null : 1000m));

        Assert.Contains(input.Arguments, a => a.ArgumentCode == "Concentration");
        Assert.DoesNotContain(input.Arguments, a => a.ArgumentCode == "Volume");
    }

    private sealed class World
    {
        private readonly Dictionary<int, ColumnDef> columns = [];
        private readonly Dictionary<string, int> idByCode = new(StringComparer.Ordinal);
        private readonly Dictionary<(int, int), IReadOnlyList<MonthInputAlias>> aliases = [];

        /// <summary>
        /// Колонки T7b_00 з моделі (лише місячні Decimal) і псевдоніми місяця з мостів Concentration/Volume — так, як їх
        /// перетворює YearMonths (Get-LandSelectorSources): гілка «[Period].Sequence = m ? [X]» -> псевдонім місяця m на X.
        /// </summary>
        public static World FromModel()
        {
            var path = Path.Combine(RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var table = doc.RootElement.GetProperty("sheets").EnumerateArray()
                .SelectMany(s => s.GetProperty("tables").EnumerateArray())
                .Single(t => t.GetProperty("code").GetString() == "T7b_00");

            var world = new World();
            var bridges = new Dictionary<string, string>(StringComparer.Ordinal);
            var id = 1000;
            foreach (var c in table.GetProperty("columns").EnumerateArray())
            {
                var code = c.GetProperty("code").GetString()!;
                if (code is "Concentration" or "Volume")
                {
                    bridges[code] = c.GetProperty("expression").GetString()!;
                    continue;
                }

                if (c.GetProperty("dataType").GetString() != "Decimal"
                    || !c.TryGetProperty("month", out var mo) || mo.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var column = new ColumnDef(
                    TableDefId,
                    EcrCode.Create(code),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                    ordinal: id,
                    CellDataType.Decimal);
                column.SetMonth(true, (byte)mo.GetInt32());
                world.columns[++id] = column;
                world.idByCode[code] = id;
            }

            foreach (var (alias, expression) in bridges)
            {
                var sources = Sources(expression);
                for (var m = 1; m <= 12; m++)
                {
                    var key = (TableDefId, m);
                    var list = world.aliases.TryGetValue(key, out var have) ? new List<MonthInputAlias>(have) : [];
                    list.Add(new MonthInputAlias(TableDefId, alias, (byte)m, world.idByCode[sources[m - 1]]));
                    world.aliases[key] = list;
                }
            }

            return world;
        }

        public async Task<IReadOnlyList<CalculationInput>> Build(int period, Func<int, decimal?> concentration, Func<int, decimal?> volume)
        {
            string[] names = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
            var key = new PeriodKey(period);
            var records = new List<CellRecord>();
            for (var m = 1; m <= 12; m++)
            {
                Add(names[m - 1], concentration(m));
                Add($"Volume_M{m:00}", volume(m));
            }

            void Add(string code, decimal? value)
            {
                if (value is { } v)
                {
                    records.Add(new CellRecord(new CellAddress(key, RowId, idByCode[code]), TableDefId, new CellValueData { ValueNumeric = v }));
                }
            }

            var rows = Substitute.For<IRowStore>();
            rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
                .Returns(new TableInstanceRef(TableInstance, DocumentId, TableDefId, TemplateVersion, period));
            rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, long> { ["S1_6"] = RowId });

            var cellStore = Substitute.For<ICellStore>();
            cellStore.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
                new TemplateVersionSnapshot(TemplateVersion, 0, [], columns, new Dictionary<(int TableDefId, string RowKey), RowDef>())
                {
                    MonthInputAliases = aliases,
                });

            return await new CalculationInputBuilder(cellStore, metadata, rows)
                .BuildAsync(TableInstance, ["S1_6"], key, Descriptor(), CancellationToken.None);
        }

        private static List<string> Sources(string expression)
        {
            var branches = Regex.Matches(expression, @"\[Period\]\.Sequence = (\d+) \? \[([^\]]+)\] : ");
            Assert.Equal(11, branches.Count);
            var result = branches.Select(b => b.Groups[2].Value).ToList();
            var last = Regex.Match(expression, @": \[([^\]]+)\]$");
            Assert.True(last.Success);
            result.Add(last.Groups[1].Value);
            return result;
        }
    }

    /// <summary>Вхід -> справжній модуль: число єдиного виходу ECW_EC_tons.</summary>
    private static async Task<decimal?> RunAsync(CalculationInput input, int period)
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

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        return Assert.Single(output.Values).Value;
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
