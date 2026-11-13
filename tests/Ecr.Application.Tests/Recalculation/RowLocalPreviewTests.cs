// tests/Ecr.Application.Tests/Recalculation/RowLocalPreviewTests.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// HSE401, пункт 3: після вибору коду забруднювача відповідь PATCH одразу несе рядково-локальні похідні (Компонент/Фаза),
/// не чекаючи фонового перерахунку. Перегляд - лише вигляд: нелокальне й усе, що його читає, не рахується.
/// </summary>
public sealed class RowLocalPreviewTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Version = 1;
    private const long EntryId = 5001;
    private const int RegistryDefId = 900;
    private const long RowId = 1001;
    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryStore _registry = Substitute.For<IRegistryStore>();

    private TableDef _table = null!;
    private TableInstanceRef _instance = null!;
    private Dictionary<CellAddress, CellValueData> _stored = [];

    public RowLocalPreviewTests()
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");

        var code = builder.Column(_table, "Code", CellDataType.Lookup);
        code.SetLookup(RegistryDefId);
        var component = builder.Column(_table, "Component", CellDataType.Formula);
        var doubled = builder.Column(_table, "Doubled", CellDataType.Formula);
        var a = builder.Column(_table, "A");
        var b = builder.Column(_table, "B");
        var rate = builder.Column(_table, "Rate", CellDataType.Formula);
        var nonLocal = builder.Column(_table, "NonLocal", CellDataType.Formula);
        var afterNonLocal = builder.Column(_table, "AfterNonLocal", CellDataType.Formula);
        var rateDoubled = builder.Column(_table, "RateDoubled", CellDataType.Formula);
        builder.Row(_table, "R1", 1);

        Order(builder.Formula(_table, "REGFIELD([Code], 'Limit')", column: component), 0);
        Order(builder.Formula(_table, "[Component] * 2", column: doubled), 1);
        Order(builder.Formula(_table, "[A] / IF([B] = NULL, [Period].Days, [B])", column: rate), 2);
        Order(builder.Formula(_table, "[A] + HDR.Train", column: nonLocal), 3);
        Order(builder.Formula(_table, "[NonLocal] * 2", column: afterNonLocal), 4);
        Order(builder.Formula(_table, "[Rate] * 2", column: rateDoubled), 5);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());
        _instance = new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value);

        _periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var registryDef = new RegistryDef(
            EcrCode.Create("Pollutants"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Pollutants" }), false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(registryDef, RegistryDefId);
        var fieldDef = new RegistryFieldDef(
            RegistryDefId, EcrCode.Create("Limit"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Limit" }), CellDataType.Decimal, ordinal: 0);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(fieldDef, 1);
        registryDef.AddField(fieldDef);
        _registry.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(registryDef);
        var value = new RegistryValue(EntryId, fieldDef.Id);
        value.Set(CellDataType.Decimal, 12.5m, unitId: null);
        _registry.ListValuesAsync(EntryId, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<RegistryValue>)[value]);
        _registry.ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);

        Store(code.Id, new CellValueData { ValueRegistryEntryId = EntryId });
        Store(a.Id, new CellValueData { ValueNumeric = 62m });
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)_stored
                .Where(p => call.ArgAt<IReadOnlyCollection<CellAddress>>(0).Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Вибір_коду_одразу_дає_REGFIELD_похідну_і_залежну_від_неї_локальну_формулу()
    {
        var computed = Assert.IsAssignableFrom<IReadOnlyList<ComputedCellDto>>(await PreviewAsync());

        Assert.Equal(12.5m, ValueOf(computed, "Component"));
        Assert.Equal(25m, ValueOf(computed, "Doubled"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Period_Days_і_NULL_у_локальній_формулі_рахуються_без_читання_інших_рядків()
    {
        var computed = await PreviewAsync();

        // B порожня -> [Period].Days = 31 днів січня; 62 / 31 = 2.
        Assert.Equal(2m, ValueOf(computed!, "Rate"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Нелокальна_формула_і_ті_що_її_читають_у_перегляд_не_потрапляють()
    {
        // Збережене (застаріле) значення нелокальної колонки: якби залежну від неї формулу порахували, вона дала б 20.
        Store(ColumnId("NonLocal"), new CellValueData { ValueNumeric = 10m, IsCalculated = true });

        var computed = await PreviewAsync();

        Assert.DoesNotContain(computed!, c => c.ColumnCode is "NonLocal" or "AfterNonLocal");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Збережене_значення_що_збігається_з_обчисленим_у_перегляд_не_кладеться()
    {
        Store(ColumnId("Component"), new CellValueData { ValueNumeric = 12.5m, IsCalculated = true });

        var computed = await PreviewAsync();

        Assert.DoesNotContain(computed!, c => c.ColumnCode == "Component");
        Assert.Equal(25m, ValueOf(computed!, "Doubled"));
    }

    /// <summary>
    /// ✎ C-1 (D-475): помилка формули в перегляді не дає значення ні самій комірці, ні тим, що її читають, — як фонова
    /// задача («нічого не оновлено»). До C-1 залежна бачила порожнє (<c>values.Remove</c>) і перегляд «очищав» її.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Помилка_в_перегляді_не_дає_значень_ні_собі_ні_залежним()
    {
        Store(ColumnId("B"), new CellValueData { ValueNumeric = 0m });
        Store(ColumnId("Rate"), new CellValueData { ValueNumeric = 2m, IsCalculated = true });
        Store(ColumnId("RateDoubled"), new CellValueData { ValueNumeric = 4m, IsCalculated = true });

        var computed = await PreviewAsync();

        Assert.DoesNotContain(computed!, c => c.ColumnCode is "Rate" or "RateDoubled");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Понад_двадцять_рядків_перегляду_немає()
    {
        var rows = Enumerable.Range(1, IRowLocalPreview.MaxRows + 1)
            .ToDictionary(i => $"R{i}", i => RowId + i);

        Assert.Null(await Preview().PreviewAsync(_instance, rows, CancellationToken.None));
    }

    private Task<IReadOnlyList<ComputedCellDto>?> PreviewAsync()
        => Preview().PreviewAsync(_instance, new Dictionary<string, long> { ["R1"] = RowId }, CancellationToken.None);

    private RowLocalPreview Preview()
        => new(_metadata, _cells, new RealFormulaEngine(), _periods, _units, _registry);

    private int ColumnId(string code) => _table.Columns.Single(c => c.Code == code).Id;

    private void Store(int columnId, CellValueData value) => _stored[new CellAddress(Period, RowId, columnId)] = value;

    private static object? ValueOf(IReadOnlyList<ComputedCellDto> cells, string column)
        => Assert.Single(cells, c => c.ColumnCode == column && c.RowKey == "R1").Value;

    private static void Order(FormulaDef formula, int order) => formula.SetEvaluationOrder(order);
}
