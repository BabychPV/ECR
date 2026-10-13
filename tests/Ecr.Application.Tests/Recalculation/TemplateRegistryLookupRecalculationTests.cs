using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// LP-40: форми <c>REG*</c> (<c>REGFIND</c>…) у ФОРМУЛІ КОЛОНКИ ШАБЛОНУ через справжній
/// <see cref="RecalculationService"/> і <see cref="SliceEvaluationContext"/>.
/// </summary>
/// <remarks>
/// ⛔ До правки шаблонний контекст знімка довідників не мав, і <c>REGFIND</c> у формулі колонки завжди
/// давав <c>#REF</c> (комірка не писалась): мости HSE400 і NAE-групи Land були порожні при цілих даних і
/// зелених тестах рушія — ті обчислювали вираз у власних контекстах із <c>Registries =&gt; snapshot</c>.
/// Тут вираз іде тим шляхом, яким його рахує бойовий перерахунок.
///
/// ⚠ Мутаційний доказ: (1) не передавати знімок у <c>SliceEvaluationContext</c> (прибрати
/// <c>registries:</c> у <c>RecalculationService.RunAsync</c>) валить тести моста й NAE; (2) зняти гейт
/// <c>_formulaUsesRegistries</c> валить сторож «REGFIELD без REG* не змінив джерело»; (3) складати
/// <c>BuildAsync</c> знімок на кожну комірку валить лічильник завантажень.
/// </remarks>
public sealed class TemplateRegistryLookupRecalculationTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Version = 1;
    private const int PermitRegistryDefId = 900;
    private static readonly PeriodKey Period = new(202601);

    private const string BridgeFormula =
        "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION',[Period].Year,[Period].Sequence,"
        + "REGFIELD(REGFIND('LAND_HSE400_STREAM',[TypeOfFuel]),'PREFIX'),'Density20'),'VALUE')";

    private const string NaeFormula = "REGFIELD(REGFIND('LAND_NAE_GROUP',[IDCodeNAEProject]),'CATEGORY1')";

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryStore _registry = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IRegistrySnapshotLoader _loader = Substitute.For<IRegistrySnapshotLoader>();

    private readonly List<DateOnly> _builtFor = [];
    private readonly Dictionary<string, int> _formulaIds = [];
    private readonly Dictionary<string, int> _columnIds = [];
    private TableDef _table = null!;
    private int _fuelId;
    private int _naeId;
    private int _permitId;
    private int _resultId;
    private string _naeText = "NAE-1";
    private (DateOnly Start, DateOnly End) _bounds = (new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Міст_REGFIELD_REGFIND_у_шаблонній_колонці_віддає_значення_зі_знімка_довідників()
    {
        Arrange(new Dictionary<string, string> { ["Result"] = BridgeFormula }, Snapshot(visibleFrom: null));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        // До правки — #REF, комірка не записана (written == 0).
        Assert.Equal(2, written);
        var byRow = Applied().ToDictionary(r => r.Address.TableRowId, r => r.Value.ValueNumeric);
        Assert.Equal(1.5m, byRow[1001]);
        Assert.Equal(0.84m, byRow[1002]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Група_NAE_REGFIELD_REGFIND_віддає_текстове_поле()
    {
        Arrange(new Dictionary<string, string> { ["Result"] = NaeFormula }, Snapshot(visibleFrom: null));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.All(Applied(), r => Assert.Equal("Cat-1", r.Value.ValueString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Знімок_будується_на_останній_день_періоду()
    {
        // Запис видимий лише на 2026-01-31 (останній день періоду), не «сьогодні» й не перший день.
        Arrange(new Dictionary<string, string> { ["Result"] = BridgeFormula }, Snapshot(visibleOn: new DateOnly(2026, 1, 31)));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.Equal([new DateOnly(2026, 1, 31)], _builtFor.Distinct().ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Запис_поза_вікном_чинності_на_бізнес_дату_періоду_дає_NA_і_не_пишеться()
    {
        // Вікно чинності записів складу починається 2026-02-01: на 2026-01-31 запису для рушія немає
        // (#N/A), старе значення «з сьогодні» не підставляється.
        Arrange(new Dictionary<string, string> { ["Result"] = BridgeFormula }, Snapshot(visibleFrom: new DateOnly(2026, 2, 1)));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Знімок_вантажиться_раз_на_прогін_а_не_на_комірку_чи_формулу()
    {
        // Дві REG*-формули × два рядки = чотири обчислення; читання БД — одне, знімок на дату — один.
        Arrange(
            new Dictionary<string, string> { ["Result"] = BridgeFormula, ["Nae"] = NaeFormula },
            Snapshot(visibleFrom: null));

        var service = Service();
        await service.RecalculateAllAsync(DocumentId, Period, CancellationToken.None);
        await service.RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        var loads = _loader.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IRegistrySnapshotLoader.LoadSourceAsync));
        Assert.Equal(1, loads);
        Assert.Single(_builtFor);
        await _registry.Received(1).FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task REGFIELD_від_Lookup_без_REG_форм_не_змінив_джерело_і_не_вантажить_знімок()
    {
        // Сторож RT-24: формула без REG* читає вузький словник полів Lookup-комірок (IRegistryStore), а
        // завантажувача знімків не торкається взагалі.
        Arrange(new Dictionary<string, string> { ["Result"] = "REGFIELD([Permit], 'Limit')" }, Snapshot(visibleFrom: null));
        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.All(Applied(), r => Assert.Equal(12.5m, r.Value.ValueNumeric));
        Assert.DoesNotContain(
            _loader.ReceivedCalls(), c => c.GetMethodInfo().Name is nameof(IRegistrySnapshotLoader.LoadSourceAsync)
                or nameof(IRegistrySnapshotLoader.LoadAsync));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task REGFIELD_від_Lookup_у_прогоні_з_REG_формулою_читає_словник_Lookup_а_не_знімок()
    {
        // Знімок цього прогону (завантажений для моста) ЗНАЄ запис 5001 з іншим Limit (99): формула без
        // REG* усе одно мусить прочитати 12.5 з IRegistryStore — джерело не змінилось ні на біт.
        Arrange(
            new Dictionary<string, string> { ["Result"] = BridgeFormula, ["Guard"] = "REGFIELD([Permit], 'Limit')" },
            date => Snapshot(visibleFrom: null)(date) is InMemoryRegistrySnapshot s ? WithPermit(s) : throw new InvalidOperationException());

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(4, written);
        var applied = Applied();
        Assert.All(
            applied.Where(r => r.Address.ColumnDefId == _columnIds["Guard"]),
            r => Assert.Equal(12.5m, r.Value.ValueNumeric));
        Assert.Equal(
            [0.84m, 1.5m],
            applied.Where(r => r.Address.ColumnDefId == _columnIds["Result"]).Select(r => r.Value.ValueNumeric!.Value).Order());
    }

    // ── Вікно чинності запису довідника (ризик 2, LP-40: .sync-local\reports\VALIDFROM-RISK-2026-10-13.md) ──
    // Скрипти Land створюють записи БЕЗ вікна (ValidFrom/ValidTo = NULL — «чинний завжди»). Тут знімок
    // збирається справжнім RegistrySnapshotSource/RegistrySnapshot.Create з доменними RegistryEntry, а не
    // InMemory-заглушкою: правило видимості — те саме, що в бойовому перерахунку.

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(2026, 1, 1, 2026, 1, 31)] // період у минулому щодо «сьогодні» (2026-10-13)
    [InlineData(2026, 10, 1, 2026, 10, 31)] // період, що містить «сьогодні»
    public async Task Запис_довідника_без_вікна_чинності_дає_значення_і_на_минулий_і_на_сьогоднішній_період(
        int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        _bounds = (new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(toYear, toMonth, toDay));
        _naeText = "NAE1";
        Arrange(new Dictionary<string, string> { ["Result"] = NaeFormula }, RealNaeSnapshot(validFrom: null));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.All(Applied(), r => Assert.Equal("Cat-1", r.Value.ValueString));
        Assert.Equal([_bounds.End], _builtFor.Distinct().ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Запис_довідника_з_ValidFrom_сьогодні_на_минулий_період_дає_NA_і_комірка_не_пишеться()
    {
        // Контроль: той самий довідник і формула, але запис має ValidFrom = «сьогодні» (2026-10-13), а період —
        // січень 2026. Саме так «ValidFrom = дата імпорту» сховало б запис від минулих місяців.
        _naeText = "NAE1";
        Arrange(
            new Dictionary<string, string> { ["Result"] = NaeFormula },
            RealNaeSnapshot(validFrom: new DateOnly(2026, 10, 13)));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.Equal([new DateOnly(2026, 1, 31)], _builtFor.Distinct().ToList());
    }

    /// <summary>Довідник <c>LAND_NAE_GROUP</c> із одним записом <c>NAE1</c> і реальним вікном чинності.</summary>
    private static Func<DateOnly, IRegistrySnapshot> RealNaeSnapshot(DateOnly? validFrom)
    {
        var def = Definition("LAND_NAE_GROUP", 103);
        var category = new RegistryFieldDef(
            103, EcrCode.Create("CATEGORY1"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "CATEGORY1" }), CellDataType.String, ordinal: 0);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(category, 1);
        def.AddField(category);

        var entry = new RegistryEntry(
            103, EcrCode.Create("NAE1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "NAE1" }));
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(entry, 31L);
        if (validFrom is not null)
        {
            entry.SetValidity(validFrom, null);
        }

        var value = new Ecr.Application.Registries.RegistrySnapshotValue(
            31, category.Id, null, "Cat-1", null, null, null, null, null);
        var source = new Ecr.Application.Registries.RegistrySnapshotSource([def], [], [entry], [value]);
        return source.Build;
    }

    private static InMemoryRegistrySnapshot WithPermit(InMemoryRegistrySnapshot snapshot)
    {
        snapshot.AddRegistry("PERMITS", ["LIMIT"]);
        snapshot.AddEntry("PERMITS", 5001, "p1", new() { ["LIMIT"] = ExpressionValue.Number(99m) });
        return snapshot;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Змішана_формула_REGFIELD_від_Lookup_плюс_REG_форма_читає_обидва_джерела()
    {
        // REGFIELD([Permit]) читає запис, якого знімок не знає (довідник Permit у нього не входить) —
        // добирається зі словника Lookup-комірок; REGCOUNT читає знімок. 12.5 + 2 запису складу.
        Arrange(
            new Dictionary<string, string> { ["Result"] = "REGFIELD([Permit], 'Limit') + REGCOUNT('LAND_HSE400_COMPOSITION', ROW.VALUE > 0)" },
            Snapshot(visibleFrom: null));

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.All(Applied(), r => Assert.Equal(14.5m, r.Value.ValueNumeric));
    }

    // ── Побудова ─────────────────────────────────────────────────────────────────────────────

    private sealed class DateSource(Func<DateOnly, IRegistrySnapshot> build, List<DateOnly> builtFor) : IRegistrySnapshotSource
    {
        public IRegistrySnapshot Build(DateOnly businessDate)
        {
            builtFor.Add(businessDate);
            return build(businessDate);
        }
    }

    /// <summary>
    /// Джерело знімка: склад видимий або з заданої дати (<paramref name="visibleFrom"/>), або лише на одну
    /// (<paramref name="visibleOn"/>), або завжди.
    /// </summary>
    private Func<DateOnly, IRegistrySnapshot> Snapshot(DateOnly? visibleFrom = null, DateOnly? visibleOn = null)
        => date =>
        {
            var visible = (visibleFrom is null || date >= visibleFrom) && (visibleOn is null || date == visibleOn);

            var snapshot = new InMemoryRegistrySnapshot()
                .AddRegistry("LAND_HSE400_STREAM", ["TEXT", "PREFIX"], primaryKey: ["TEXT"])
                .AddRegistry(
                    "LAND_HSE400_COMPOSITION",
                    ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"],
                    primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"])
                .AddRegistry("LAND_NAE_GROUP", ["CODE", "CATEGORY1"], primaryKey: ["CODE"]);

            snapshot.AddEntry("LAND_HSE400_STREAM", 11, "s1", new()
            {
                ["TEXT"] = ExpressionValue.Text("Fuel gas"),
                ["PREFIX"] = ExpressionValue.Text("FG"),
            });
            snapshot.AddEntry("LAND_HSE400_STREAM", 12, "s2", new()
            {
                ["TEXT"] = ExpressionValue.Text("Diesel"),
                ["PREFIX"] = ExpressionValue.Text("DS"),
            });

            snapshot.AddEntry("LAND_HSE400_COMPOSITION", 21, "c1", Composition("FG", 1.5m), visible: visible);
            snapshot.AddEntry("LAND_HSE400_COMPOSITION", 22, "c2", Composition("DS", 0.84m), visible: visible);

            snapshot.AddEntry("LAND_NAE_GROUP", 31, "g1", new()
            {
                ["CODE"] = ExpressionValue.Text("NAE-1"),
                ["CATEGORY1"] = ExpressionValue.Text("Cat-1"),
            });

            return snapshot;
        };

    private static Dictionary<string, ExpressionValue> Composition(string stream, decimal value) => new()
    {
        ["YEAR"] = ExpressionValue.Number(2026),
        ["MONTH"] = ExpressionValue.Number(1),
        ["STREAM"] = ExpressionValue.Text(stream),
        ["INDICATOR"] = ExpressionValue.Text("Density20"),
        ["VALUE"] = ExpressionValue.Number(value),
    };

    /// <summary>
    /// Таблиця: <c>TypeOfFuel</c>, <c>IDCodeNAEProject</c> (текст), <c>Permit</c> (Lookup) і колонки
    /// результату з формулами <paramref name="formulas"/> (код колонки → текст формули).
    /// </summary>
    private void Arrange(Dictionary<string, string> formulas, Func<DateOnly, IRegistrySnapshot> snapshotFor)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Land");
        _table = builder.Table(sheet, "Main");

        var fuel = builder.Column(_table, "TypeOfFuel", CellDataType.String);
        var nae = builder.Column(_table, "IDCodeNAEProject", CellDataType.String);
        var permit = builder.Column(_table, "Permit", CellDataType.Lookup);
        permit.SetLookup(PermitRegistryDefId);

        _fuelId = fuel.Id;
        _naeId = nae.Id;
        _permitId = permit.Id;

        builder.Row(_table, "7001001", 1);
        builder.Row(_table, "7001002", 2);

        var dependencies = new List<FormulaDependency>();
        var order = 0;
        foreach (var (columnCode, text) in formulas)
        {
            var column = builder.Column(_table, columnCode, columnCode == "Nae" || text == NaeFormula ? CellDataType.String : CellDataType.Decimal);
            var formula = builder.Formula(_table, text, column: column);
            _formulaIds[columnCode] = formula.Id;
            _columnIds[columnCode] = column.Id;
            _resultId = column.Id;

            // Cell-ребра, як їх дає видобувач: по колонках, на які формула посилається.
            var read = new List<int>();
            if (text.Contains("[TypeOfFuel]", StringComparison.Ordinal))
            {
                read.Add(_fuelId);
            }

            if (text.Contains("[IDCodeNAEProject]", StringComparison.Ordinal))
            {
                read.Add(_naeId);
            }

            if (text.Contains("[Permit]", StringComparison.Ordinal))
            {
                read.Add(_permitId);
            }

            foreach (var rowKey in new[] { "7001001", "7001002" })
            {
                foreach (var columnId in read)
                {
                    dependencies.Add(FormulaDependency.ForFormula(
                        formula.Id, DependencyExtractor.KindCell, _table.Id, rowKey, columnId, null, null, order++));
                }

                if (read.Contains(_permitId))
                {
                    dependencies.Add(FormulaDependency.ForFormula(
                        formula.Id, DependencyExtractor.KindRegistry, _table.Id, rowKey, _permitId, "Limit", null, order++));
                }
            }
        }

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var instance = new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([instance]);
        _rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { ["7001001"] = 1001, ["7001002"] = 1002 },
            });

        var slice = new List<CellRecord>
        {
            Text(1001, _fuelId, "Fuel gas"), Text(1002, _fuelId, "Diesel"),
            Text(1001, _naeId, _naeText), Text(1002, _naeId, _naeText),
            new(new CellAddress(Period, 1001, _permitId), _table.Id, new CellValueData { ValueRegistryEntryId = 5001 }),
            new(new CellAddress(Period, 1002, _permitId), _table.Id, new CellValueData { ValueRegistryEntryId = 5001 }),
        };

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(dependencies);

        // Довідники: коди → визначення з id (для знімка); Permit — поле Limit для вузького словника.
        _registry.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<RegistryDef>)[.. call.ArgAt<IReadOnlyCollection<string>>(0)
                .Select(code => Definition(code, code.ToUpperInvariant() switch
                {
                    "LAND_HSE400_STREAM" => 101,
                    "LAND_HSE400_COMPOSITION" => 102,
                    "LAND_NAE_GROUP" => 103,
                    _ => 199,
                }))]);

        var permitDef = Definition("Permits", PermitRegistryDefId);
        var limit = new RegistryFieldDef(
            PermitRegistryDefId, EcrCode.Create("Limit"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Limit" }), CellDataType.Decimal, ordinal: 0);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(limit, 1);
        permitDef.AddField(limit);
        _registry.FindDefinitionByIdAsync(PermitRegistryDefId, Arg.Any<CancellationToken>()).Returns(permitDef);

        var value = new RegistryValue(5001, limit.Id);
        value.Set(CellDataType.Decimal, 12.5m, unitId: null);
        _registry.ListValuesAsync(5001, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<RegistryValue>)[value]);

        _loader.LoadSourceAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IRegistrySnapshotSource>(new DateSource(snapshotFor, _builtFor)));
    }

    private static RegistryDef Definition(string code, int id)
    {
        var def = new RegistryDef(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(def, id);
        return def;
    }

    private CellRecord Text(long rowId, int columnId, string text)
        => new(new CellAddress(Period, rowId, columnId), _table.Id, new CellValueData { ValueString = text });

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(_bounds.Start, _bounds.End));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units, _registry, _headers,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>(),
            registrySnapshots: _loader);
    }

    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Last(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));

        return [.. ((IReadOnlyCollection<CellChangeSet>)call.GetArguments()[0]!).SelectMany(set => set.Upserts)];
    }
}
