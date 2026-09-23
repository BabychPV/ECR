using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// <c>REGFIELD</c> у перерахунку: значення поля довідника, знімок якого
/// <c>RecalculationService</c> вантажить із <c>IRegistryStore</c> перед
/// обчисленням (<c>SliceEvaluationContext.GetRegistryField</c>).
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ — два тести. <c>Видобувач_без_Registry_залежності_
/// лишає_REGFIELD_з_REF</c> відтворює РУКАМИ стан, який дав би зламаний
/// <see cref="DependencyExtractor"/> (Registry-ребро не записане в
/// <c>cfg.FormulaDependency</c>): формула не перераховується правильно, хоча
/// Lookup-комірка заповнена. <c>Наскрізно_через_справжній_DependencyExtractor_
/// зміна_довідника_перераховує_формулу</c> іде через СПРАВЖНІЙ
/// <c>PublishChecks.Dependencies</c> — перевірено прогоном: закоментувати
/// виклик <c>AddRegistryDependency</c> у <c>DependencyExtractor.Visit</c>
/// валить саме цей тест (і <c>RegistryDependencyExtractionTests</c> у
/// <c>Ecr.Expressions.Tests</c>), лишаючи решту зеленими — це і є різниця
/// між «дані про залежність підставлені» і «залежність справді видобута».
/// </remarks>
public sealed class RegistryFieldRecalculationTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Version = 1;
    private const long EntryId = 5001;
    private const int RegistryDefId = 900;
    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IRegistryStore _registry = Substitute.For<IRegistryStore>();

    private TableDef _table = null!;
    private int _permitId;
    private int _resultId;
    private int _formulaId;
    private int _fieldDefId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-8.1")]
    public async Task REGFIELD_обчислює_поле_запису_на_який_показує_Lookup_комірка()
    {
        Arrange(fieldValue: 12.5m, withRegistryDependency: true);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(1, written);
        var upsert = Assert.Single(Applied());
        Assert.Equal(_resultId, upsert.Address.ColumnDefId);
        Assert.Equal(12.5m, upsert.Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-8.1")]
    public async Task Зміна_поля_довідника_перераховує_формулу_що_його_читає()
    {
        // ⚠ Це і є «залежність працює»: та сама версія, ті самі комірки — між
        // двома прогонами міняється лише те, що повертає IRegistryStore.
        Arrange(fieldValue: 12.5m, withRegistryDependency: true);
        var first = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);
        Assert.Equal(12.5m, Assert.Single(Applied()).Value.ValueNumeric);

        _cells.ClearReceivedCalls();
        SetRegistryValue(20m);

        var second = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(20m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Видобувач_без_Registry_залежності_лишає_REGFIELD_з_REF()
    {
        // ⛔ Мутаційний доказ: `withRegistryDependency: false` відтворює
        // РІВНО те, що дав би зламаний DependencyExtractor.AddRegistryDependency
        // — Cell-залежність від Lookup-комірки є, Registry-ребра немає.
        // RecalculationService тоді не знає, що REGFIELD цієї формули читає
        // довідник, і не підвантажує жодного поля: REGFIELD дає #REF, а
        // помилкові значення НЕ пишуться (Evaluate пропускає IsError/IsNull) —
        // тобто нічого не записується взагалі, хоча Lookup-комірка заповнена.
        Arrange(fieldValue: 12.5m, withRegistryDependency: false);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
        Assert.DoesNotContain(
            _cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутній_запис_довідника_не_валить_прогін_решта_формул_рахується()
    {
        // Lookup-комірка показує на запис, якого IRegistryStore не знає —
        // #REF лишається значенням ОДНІЄЇ комірки, а не винятком, що зупиняє
        // перерахунок цілого документа (02b §6.4).
        Arrange(fieldValue: 12.5m, withRegistryDependency: true);
        _registry.ListValuesAsync(EntryId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[]);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(0, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Наскрізно_через_справжній_DependencyExtractor_зміна_довідника_перераховує_формулу()
    {
        // ⛔ На відміну від решти тестів файлу (де граф залежностей будується
        // РУКАМИ через `FormulaDependency.ForFormula`, як і в
        // `CascadeRecalculationTests`), тут `dependencies` приходить із
        // СПРАВЖНЬОГО `PublishChecks.Dependencies` — тобто зі СПРАВЖНЬОГО
        // `DependencyExtractor.AddRegistryDependency`. Це і є той тест, який
        // мутація в `DependencyExtractor` ламає напряму, без посередника:
        // приберіть виклик `AddRegistryDependency` — Registry-запису в
        // `dependencies` не буде, `RecalculationService` не підвантажить
        // поле, і другий прогін нижче поверне ТЕ САМЕ старе значення замість
        // нового.
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");

        var permitColumn = builder.Column(_table, "Permit", CellDataType.Lookup);
        permitColumn.SetLookup(RegistryDefId);
        var resultColumn = builder.Column(_table, "Result");
        builder.Row(_table, "7001001", 1);

        var formula = builder.Formula(_table, "REGFIELD([Permit], 'Limit')", column: resultColumn);

        _permitId = permitColumn.Id;
        _resultId = resultColumn.Id;
        _formulaId = formula.Id;

        var version = builder.Version();
        var extracted = PublishChecks.Dependencies(version, new RealFormulaEngine());

        // Доказ, що це справді пройшло крізь AddRegistryDependency, а не
        // лише крізь звичайний Cell-обхід.
        Assert.Contains(extracted, d => d.DependsOnKind == DependencyExtractor.KindRegistry);

        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        var instance = new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([instance]);

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { ["7001001"] = 1001 },
            });

        var slice = new List<CellRecord>
        {
            new(
                new CellAddress(Period, 1001, _permitId), _table.Id,
                new CellValueData { ValueRegistryEntryId = EntryId }),
        };

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(extracted);

        var registryDef = new RegistryDef(
            EcrCode.Create("Permits"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permits" }),
            isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(registryDef, RegistryDefId);

        var fieldDef = new RegistryFieldDef(
            RegistryDefId, EcrCode.Create("Limit"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Limit" }), CellDataType.Decimal, ordinal: 0);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(fieldDef, 1);
        _fieldDefId = fieldDef.Id;
        registryDef.AddField(fieldDef);

        _registry.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(registryDef);

        SetRegistryValue(12.5m);
        var first = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);
        Assert.Equal(1, first);
        Assert.Equal(12.5m, Assert.Single(Applied()).Value.ValueNumeric);

        _cells.ClearReceivedCalls();
        SetRegistryValue(99m);
        var second = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(1, second);
        Assert.Equal(99m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    private void SetRegistryValue(decimal amount)
    {
        var value = new RegistryValue(EntryId, _fieldDefId);
        value.Set(CellDataType.Decimal, amount, unitId: null);
        _registry.ListValuesAsync(EntryId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);
    }

    /// <summary>
    /// Таблиця з Lookup-колонкою <c>Permit</c> і формулою
    /// <c>REGFIELD([Permit], 'Limit')</c> на колонці <c>Result</c>.
    /// </summary>
    /// <param name="fieldValue">Значення поля <c>Limit</c> у довіднику.</param>
    /// <param name="withRegistryDependency">
    /// <c>false</c> — граф залежностей формули несе ЛИШЕ Cell-ребро від
    /// Lookup-комірки, без Registry-ребра: так виглядає результат зламаного
    /// <c>DependencyExtractor.AddRegistryDependency</c> — той метод не
    /// публічний, тому мутація тут відтворена станом графа, а не викликом.
    /// </param>
    private void Arrange(decimal fieldValue, bool withRegistryDependency)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("Water");
        _table = builder.Table(sheet, "Main");

        var permitColumn = builder.Column(_table, "Permit", CellDataType.Lookup);
        permitColumn.SetLookup(RegistryDefId);
        var resultColumn = builder.Column(_table, "Result");

        builder.Row(_table, "7001001", 1);

        _permitId = permitColumn.Id;
        _resultId = resultColumn.Id;

        var formula = new FormulaDef(_table.Id, FormulaScope.Column, "REGFIELD([Permit], 'Limit')", ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, 100);
        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(formula, _resultId);
        _table.AddFormula(formula);
        _formulaId = formula.Id;

        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        var instance = new TableInstanceRef(TableInstance, DocumentId, _table.Id, Version, Period.Value);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([instance]);

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [TableInstance] = new Dictionary<string, long> { ["7001001"] = 1001 },
            });

        var slice = new List<CellRecord>
        {
            new(
                new CellAddress(Period, 1001, _permitId), _table.Id,
                new CellValueData { ValueRegistryEntryId = EntryId }),
        };

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var dependencies = new List<FormulaDependency>
        {
            FormulaDependency.ForFormula(_formulaId, DependencyExtractor.KindCell, _table.Id, "7001001", _permitId, null, null, 0),
        };

        if (withRegistryDependency)
        {
            dependencies.Add(FormulaDependency.ForFormula(
                _formulaId, DependencyExtractor.KindRegistry, _table.Id, "7001001", _permitId, "Limit", null, 1));
        }

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(dependencies);

        // ⚠ Довідник: одне поле "Limit" типу Decimal на довіднику RegistryDefId.
        var registryDef = new RegistryDef(
            Ecr.Domain.ValueObjects.EcrCode.Create("Permits"),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Permits" }),
            isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(registryDef, RegistryDefId);

        var fieldDef = new RegistryFieldDef(
            RegistryDefId,
            Ecr.Domain.ValueObjects.EcrCode.Create("Limit"),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Limit" }),
            CellDataType.Decimal,
            ordinal: 0);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(fieldDef, 1);
        _fieldDefId = fieldDef.Id;
        registryDef.AddField(fieldDef);

        _registry.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(registryDef);

        SetRegistryValue(fieldValue);
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            _registry,
            _audit,
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow);
    }

    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return ((CellChangeSet)call.GetArguments()[0]!).Upserts;
    }
}
