// tests/Ecr.Application.Tests/Documents/RequiredByMethodologyCacheTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>RD-04</c>: ключ кешу «колонки, обов'язкові за методологією» — і те, що
/// він мусить розрізняти.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Число звернень до <b>БД</b> міряє
/// <c>Ecr.Infrastructure.Tests…TableSliceMethodologyQueryCountTests</c>
/// лічильником <c>DbCommandCounter</c> на живій базі. Тут — інше питання, якого
/// той тест не ставить: <b>чи не віддає кеш чужу відповідь</b>. Помилка в
/// ключі не видна за числом запитів взагалі: запитів стає навіть менше.
/// </para>
/// <para>
/// ⛔ Кожен тест нижче падає від власної мутації, і мутації різні:
/// прибрати склад прив'язок із ключа · прибрати дату · прибрати сортування.
/// </para>
/// </remarks>
public sealed class RequiredByMethodologyCacheTests : IDisposable
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int JanuaryPeriod = 202601;
    private const int JulyPeriod = 202607;
    private const int VolumeId = 11;
    private const int CategoryId = 12;
    private const int FirstMethodology = 100;
    private const int SecondMethodology = 200;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public RequiredByMethodologyCacheTests()
    {
        var volume = Column(VolumeId, "Volume", 1);
        var category = Column(CategoryId, "Category", 2);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(volume);
        table.AddColumn(category);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(ci => new TableInstanceRef(TableInstance, Document, TableDef, 2, CurrentPeriod));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long>());
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume, [CategoryId] = category },
                new Dictionary<(int, string), RowDef>()));

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        _periods.FindPeriodBoundsAsync(Document, JanuaryPeriod, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        _periods.FindPeriodBoundsAsync(Document, JulyPeriod, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
    }

    /// <summary>Період, який зараз віддає екземпляр таблиці.</summary>
    private int CurrentPeriod { get; set; } = JanuaryPeriod;

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Другий_зріз_не_повторює_жодного_запиту_методологій()
    {
        BindMethodologies(FirstMethodology);
        WithPublishedVersion(FirstMethodology, versionId: 1000, from: new DateOnly(2026, 1, 1), requires: CategoryId);

        var handler = Handler();
        await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);
        var second = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(second.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);

        await _methodologies.Received(1).GetPublishedVersionsAsync(FirstMethodology, Arg.Any<CancellationToken>());
        await _methodologies.Received(1).GetRulesAsync(1000, Arg.Any<CancellationToken>());
        await _methodologies.Received(1).GetRequiredInputsAsync(1000, Arg.Any<CancellationToken>());

        // ⚠ А цей — щоразу, і навмисно: він і є ключем кешу (нижче).
        await _methodologies.Received(2).GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Зміна складу прив'язок видно <b>негайно</b>, без очікування терміну
    /// життя запису.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати склад прив'язок із ключа
    /// (<c>MethodologyRequiredColumnsCache.ResolutionKey</c>) — зріз і далі
    /// показуватиме вимогу методології, яку з таблиці вже зняли, усі 30 с.
    ///
    /// ⚠ Заміна однієї методології на іншу, а не зняття «в нуль»: порожній
    /// перелік прив'язок обробник відсікає ще ДО кешу
    /// (<c>methodologyIds.Count == 0</c>), тож на ньому ця мутація вижила б.
    /// Перевірку писали двічі саме тому — перша версія була хибнозеленою.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Зміна_складу_привязок_видно_одразу()
    {
        WithPublishedVersion(FirstMethodology, versionId: 1000, from: new DateOnly(2026, 1, 1), requires: CategoryId);
        WithPublishedVersion(SecondMethodology, versionId: 2000, from: new DateOnly(2026, 1, 1), requires: VolumeId);

        var handler = Handler();

        BindMethodologies(FirstMethodology);
        var before = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        BindMethodologies(SecondMethodology);
        var after = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(before.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);
        Assert.False(before.Columns.Single(c => c.Code == "Volume").IsRequiredByMethodology);

        Assert.True(after.Columns.Single(c => c.Code == "Volume").IsRequiredByMethodology);
        Assert.False(after.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);
    }

    /// <summary>
    /// Прив'язок немає — жодного запиту методологій і жодної позначки.
    /// </summary>
    /// <remarks>
    /// ⚠ Швидкий вихід ДО кешу: зріз таблиці без методологій (переважна
    /// більшість таблиць) не має платити навіть за побудову ключа.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Без_привязок_методології_не_читаються_зовсім()
    {
        BindMethodologies();

        var slice = await Handler().HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.All(slice.Columns, c => Assert.False(c.IsRequiredByMethodology));
        await _methodologies.DidNotReceive().GetPublishedVersionsAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Інший період добирає іншу чинну версію, а не бере відповідь сусіда.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати дату з ключа — липневий зріз покаже вимогу
    /// січневої версії, тобто кеш почне переписувати історію.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Інший_період_бере_власну_чинну_версію()
    {
        BindMethodologies(FirstMethodology);
        WithPublishedVersions(
            FirstMethodology,
            (VersionId: 1000, From: new DateOnly(2026, 1, 1), Version: "1.0", Requires: CategoryId),
            (VersionId: 1001, From: new DateOnly(2026, 6, 1), Version: "2.0", Requires: VolumeId));

        var handler = Handler();

        CurrentPeriod = JanuaryPeriod;
        var january = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        CurrentPeriod = JulyPeriod;
        var july = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(january.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);
        Assert.False(january.Columns.Single(c => c.Code == "Volume").IsRequiredByMethodology);

        Assert.True(july.Columns.Single(c => c.Code == "Volume").IsRequiredByMethodology);
        Assert.False(july.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);
    }

    /// <summary>
    /// Порядок ідентифікаторів, у якому їх віддала СУБД, не створює другого
    /// запису кешу.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: прибрати <c>.Order()</c> з <c>ResolutionKey</c>. Тест падає,
    /// бо добір версій виконається двічі. Це не теорія:
    /// <c>MethodologyStore.GetMethodologyIdsBoundToTableAsync</c> робить
    /// <c>Distinct()</c> без <c>OrderBy</c> — порядок віддає СУБД, і кеш,
    /// який від нього залежить, працював би через раз.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Порядок_привязок_від_СУБД_не_подвоює_запис_кешу()
    {
        WithPublishedVersion(FirstMethodology, versionId: 1000, from: new DateOnly(2026, 1, 1), requires: CategoryId);
        WithPublishedVersion(SecondMethodology, versionId: 2000, from: new DateOnly(2026, 1, 1), requires: VolumeId);

        var handler = Handler();

        BindMethodologies(FirstMethodology, SecondMethodology);
        await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        BindMethodologies(SecondMethodology, FirstMethodology);
        await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        await _methodologies.Received(1).GetPublishedVersionsAsync(FirstMethodology, Arg.Any<CancellationToken>());
        await _methodologies.Received(1).GetPublishedVersionsAsync(SecondMethodology, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Обробник без сховища кешу поводиться рівно як до <c>RD-04</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Це не про продукт (у контейнері сховище є завжди), а про чесність
    /// решти наборів: вони конструюють обробник вручну, і мовчазна зміна
    /// поведінки під ними була б зміною, якої ніхто не замовляв.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Без_сховища_кеш_вимкнено_а_не_підмінено_заглушкою()
    {
        BindMethodologies(FirstMethodology);
        WithPublishedVersion(FirstMethodology, versionId: 1000, from: new DateOnly(2026, 1, 1), requires: CategoryId);

        var handler = new GetTableSliceHandler(
            _rows, _cells, _metadata, Units(), _access, _methodologies, _periods, Styles());

        Assert.False(handler.RequiredColumnsCache.IsEnabled);

        await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);
        var second = await handler.HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(second.Columns.Single(c => c.Code == "Category").IsRequiredByMethodology);
        await _methodologies.Received(2).GetPublishedVersionsAsync(FirstMethodology, Arg.Any<CancellationToken>());
    }

    private GetTableSliceHandler Handler()
        => new(_rows, _cells, _metadata, Units(), _access, _methodologies, _periods, Styles(), _memory);

    private void BindMethodologies(params int[] methodologyIds)
        => _methodologies.GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<int>>(methodologyIds));

    private void WithPublishedVersion(int methodologyId, int versionId, DateOnly from, int requires)
        => WithPublishedVersions(methodologyId, (versionId, from, "1.0", requires));

    private void WithPublishedVersions(
        int methodologyId, params (int VersionId, DateOnly From, string Version, int Requires)[] specs)
    {
        var versions = new List<MethodologyVersion>(specs.Length);

        foreach (var spec in specs)
        {
            var version = new MethodologyVersion(
                methodologyId, spec.Version, CalculationLevel.Configuration, 1, Now);
            SetId(version, spec.VersionId);

            var rules = (IReadOnlyList<MethodologyRule>)[version.AddRule(EcrCode.Create("all"), "{}", 100)];
            var requiredInput = version.AddRequiredInput(spec.Requires, RequiredInputSeverity.Block, hint: null);
            version.Publish(2, "тестова публікація", spec.From, testsPassed: true, Now);

            _methodologies.GetRulesAsync(spec.VersionId, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(rules));
            _methodologies.GetRequiredInputsAsync(spec.VersionId, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult<IReadOnlyList<MethodologyRequiredInput>>([requiredInput]));

            versions.Add(version);
        }

        _methodologies.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyVersion>>(versions));
    }

    private static ColumnDef Column(int id, string code, int ordinal)
    {
        var column = new ColumnDef(
            tableDefId: TableDef, EcrCode.Create(code), Text(code), ordinal, CellDataType.Decimal);
        SetId(column, id);

        return column;
    }

    private static IUnitCatalog Units()
    {
        var catalogue = Substitute.For<IUnitCatalog>();
        catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return catalogue;
    }

    private static IStyleCatalog Styles()
    {
        var catalogue = Substitute.For<IStyleCatalog>();
        catalogue.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<int, StyleDef>());

        return catalogue;
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
