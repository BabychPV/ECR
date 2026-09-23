// tests/Ecr.Application.Tests/Recalculation/RowLocalRecalculationTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// Скільки разів перерахунок ОБЧИСЛЮЄ формулу — <c>CAL-03</c> директиви №14
/// частина 3.
/// </summary>
/// <remarks>
/// ⛔ <c>Targets</c> для <c>FormulaScope.Column</c> віддає кожен
/// <c>rowIds.Keys</c>, тобто правка однієї комірки в таблиці на 500 рядків
/// давала 500 обчислень. Для <c>C3 = C1 + C2</c> 499 із них рахують те, що вже
/// лежить у базі: входи тих рядків ніхто не чіпав.
///
/// ⛔ <b>Ціна помилки несиметрична.</b> Зайве обчислення коштує часу; пропущене
/// — це НЕПЕРЕРАХОВАНА комірка, тобто неправдиве число у звітності, яке саме не
/// виправиться. Тому класифікатор консервативний, і половина тестів цього
/// класу — саме про те, що він НЕ звужує там, де не має права.
/// </remarks>
public sealed class RowLocalRecalculationTests
{
    private const long MainInstance = 500;
    private const long ExternalInstance = 501;
    private const long DocumentId = 700;
    private const int Version = 1;
    private const long FirstRowId = 1001;

    /// <summary>Лютий: попередній період існує — крос-періодний випадок має бути можливим.</summary>
    private static readonly PeriodKey Period = new(202602);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly CountingFormulaEngine _engine = new();

    private int _c1Id;
    private int _c2Id;
    private int _c3Id;
    private int _c4Id;
    private int _rowCount;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-03")]
    [InlineData("[C1] + [C2]", true)]
    [InlineData("[C1]", true)]
    [InlineData("ROUND([C1] / [C2], 2)", true)]
    [InlineData("IF([C1] > [C2], [C1], [C2])", true)]
    [InlineData("-[C1] + 1", true)]
    [InlineData("SUM([C1])", false)]
    [InlineData("SUM([R1:R3].[C1])", false)]
    [InlineData("[C1] + [Ext].[R1].[V]", false)]
    [InlineData("[C1] + [Period:-1].[C1]", false)]
    [InlineData("[C1] + [Period].Days", false)]
    [InlineData("[C1] + HDR.Train", false)]
    public void Класифікатор_визнає_локальними_лише_посилання_на_свій_рядок(string expression, bool expected)
    {
        // ⚠ Межа взята з граматики (`02b-expressions.md` §3.1: «[Jan] —
        // колонка в тому самому рядку»; §1 рядок `cell_ref`), а не з пам'яті.
        // Агрегати визначені ознакою `AcceptsRange` каталогу функцій
        // (`FunctionRegistry.cs:44-58`) — другого переліку «що таке агрегат»
        // тут немає навмисно.
        var formula = new FormulaDef(1, FormulaScope.Column, expression, ExpressionDialect.Template);
        var parsed = new RealFormulaEngine().Parse(expression, ExpressionDialect.Template);

        Assert.True(parsed.IsSuccess, $"вираз «{expression}» не розібрався: {parsed.Diagnostics.Count} зауважень");
        Assert.Equal(expected, RowLocalFormulaClassifier.IsRowLocal(formula, parsed.Expression!.Root));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-03")]
    public async Task Правка_одного_рядка_рахує_локальну_формулу_ОДИН_раз_а_не_пʼятсот()
    {
        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ директиви, дослівно: 500 рядків,
        // `C3 = C1 + C2`, правка одного рядка → `Evaluate` викликано 1 раз.
        Arrange("[C1] + [C2]", rowCount: 500);

        var written = await Service().RecalculateAsync(MainInstance, Dirty(), CancellationToken.None);

        Assert.Equal(1, _engine.EvaluateCalls);
        Assert.Equal(1, written);

        var upsert = Assert.Single(Applied());
        Assert.Equal(FirstRowId, upsert.Address.TableRowId);
        Assert.Equal(_c3Id, upsert.Address.ColumnDefId);
        Assert.Equal(2m, upsert.Value.ValueNumeric);

        // ⚠ І розбір — один на прогін, а не один на рядок: класифікатор
        // працює з AST, тож розбір винесено в передпрохід.
        Assert.Equal(1, _engine.ParseCalls);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-03")]
    [InlineData("SUM([C1])", false, false)]
    [InlineData("[C1] + [Ext].[R1].[V]", true, false)]
    [InlineData("[C1] + [Period:-1].[C1]", false, true)]
    public async Task Нелокальна_формула_рахується_в_КОЖНОМУ_рядку(
        string expression, bool external, bool crossPeriod)
    {
        // ⛔ Найважливіший тест класу, і директива його не називає. Формула,
        // яку недбалий класифікатор визнав би локальною, мусить лишитися
        // нелокальною — інакше «оптимізація» тихо перестане перераховувати те,
        // що має. Три випадки, три різні причини нелокальності: агрегат,
        // ЧУЖА таблиця, ІНШИЙ період.
        //
        // ⚠ Доказ не в лічильнику самому по собі, а в тому, що всі десять
        // рядків справді ПЕРЕПИСАНІ: у базі лежать застарілі підсумки, і
        // звуження до брудного рядка лишило б дев'ять із них неправдивими.
        Arrange(expression, rowCount: 10, external: external, crossPeriod: crossPeriod);

        var written = await Service().RecalculateAsync(MainInstance, Dirty(), CancellationToken.None);

        Assert.Equal(_rowCount, _engine.EvaluateCalls);
        Assert.Equal(_rowCount, written);
        Assert.Equal(_rowCount, Applied().Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-03")]
    public async Task Локальна_формула_НЕ_звужується_якщо_в_ту_саму_таблицю_пише_нелокальна()
    {
        // ⛔ Межа безпеки, на якій тримається все звуження. `C3 = SUM([C1])`
        // — нелокальна, і в цьому прогоні вона міняє `C3` у ВСІХ рядках.
        // `C4 = [C3] * 2` — рядково-локальна, і якби її звузили до брудного
        // рядка, дев'ять рядків `C4` лишилися б порахованими зі старого `C3`.
        // Саме такий дефект і виглядає як «звіт майже правильний».
        Arrange("SUM([C1])", rowCount: 10, withDependentLocalFormula: true);

        var written = await Service().RecalculateAsync(MainInstance, Dirty(), CancellationToken.None);

        // Дві формули × десять рядків.
        Assert.Equal(2 * _rowCount, _engine.EvaluateCalls);
        Assert.Equal(2 * _rowCount, written);

        // ⛔ І саме `C4` — у кожному рядку: без цього твердження тест був би
        // зеленим і тоді, коли всі двадцять обчислень припали на `C3`.
        var c4 = Applied().Where(u => u.Address.ColumnDefId == _c4Id).ToList();
        Assert.Equal(_rowCount, c4.Count);

        // Рядок i: C3 = i, C4 = 2i. Перевіряємо найдальший від брудного.
        var last = c4.Single(u => u.Address.TableRowId == FirstRowId + _rowCount - 1);
        Assert.Equal(2m * _rowCount, last.Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "CAL-03")]
    public async Task Повний_прогін_рахує_всі_рядки_бо_брудного_набору_в_ньому_немає()
    {
        // ⚠ Директива обмежує звуження ІНКРЕМЕНТНИМ прогоном. Повний прогін
        // існує рівно для випадку «формула додана після введення даних», і
        // звузити його до брудних рядків означало б, що звужувати нема до
        // чого — брудного набору там немає, тобто жоден рядок не порахувався б.
        Arrange("[C1] + [C2]", rowCount: 10);

        var written = await Service().RecalculateAllAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal(_rowCount, _engine.EvaluateCalls);
        Assert.Equal(_rowCount, written);
    }

    /// <summary>Змінена комірка <c>C1</c> першого рядка.</summary>
    private DirtySet Dirty()
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, FirstRowId, _c1Id));

        return dirty;
    }

    /// <summary>Комірки, які служба віддала на запис (по всіх екземплярах).</summary>
    private IReadOnlyList<CellRecord> Applied()
        => [.. _cells.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync))
            .SelectMany(c => ((CellChangeSet)c.GetArguments()[0]!).Upserts)];

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, _engine, _units,
            Substitute.For<IRegistryStore>(),
            _audit,
            new TestClock(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow);
    }

    /// <summary>
    /// Таблиця <c>Main</c> на <paramref name="rowCount"/> рядків; у базі лежать
    /// ЗАСТАРІЛІ підсумки (нуль), тож кожен порахований рядок дає запис.
    /// </summary>
    /// <param name="expression">Вираз формули колонки <c>C3</c>.</param>
    /// <param name="rowCount">Скільки рядків у таблиці.</param>
    /// <param name="external">Чи заводити сусідню таблицю <c>Ext</c> з колонкою <c>V</c>.</param>
    /// <param name="crossPeriod">Чи несе граф крос-періодне ребро.</param>
    /// <param name="withDependentLocalFormula">
    /// Чи додати другу формулу <c>C4 = [C3] * 2</c> — рядково-локальну, яка
    /// читає результат першої.
    /// </param>
    private void Arrange(
        string expression,
        int rowCount,
        bool external = false,
        bool crossPeriod = false,
        bool withDependentLocalFormula = false)
    {
        _rowCount = rowCount;

        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        var table = builder.Table(sheet, "Main");

        _c1Id = builder.Column(table, "C1", isMonthColumn: true).Id;
        _c2Id = builder.Column(table, "C2", isMonthColumn: true).Id;
        var c3 = builder.Column(table, "C3", CellDataType.Formula);
        _c3Id = c3.Id;

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 1; i <= rowCount; i++)
        {
            builder.Row(table, $"R{i}", i);
            rowIds[$"R{i}"] = FirstRowId + i - 1;
        }

        TableDef? ext = null;
        var extColumnId = 0;
        if (external)
        {
            ext = builder.Table(sheet, "Ext");
            extColumnId = builder.Column(ext, "V").Id;
            builder.Row(ext, "R1", 1);
        }

        var formula = builder.Formula(table, expression, column: c3);
        formula.SetEvaluationOrder(0);

        FormulaDef? dependent = null;
        if (withDependentLocalFormula)
        {
            var c4 = builder.Column(table, "C4", CellDataType.Formula);
            _c4Id = c4.Id;
            dependent = builder.Formula(table, "[C3] * 2", column: c4);

            // ⚠ Порядок обчислення береться з ПУБЛІКАЦІЇ, і без нього обидві
            // формули мали б `0`: `List.Sort` нестабільний, тож `C4` могла б
            // порахуватися раніше за `C3` — і тест доводив би випадковість.
            dependent.SetEvaluationOrder(1);
        }

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        var main = new TableInstanceRef(MainInstance, DocumentId, table.Id, Version, Period.Value);
        _rows.ResolveTableInstanceAsync(MainInstance, Arg.Any<CancellationToken>()).Returns(main);

        var instances = new List<TableInstanceRef> { main };
        if (ext is not null)
        {
            instances.Add(new TableInstanceRef(ExternalInstance, DocumentId, ext.Id, Version, Period.Value));
        }

        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = (PeriodKey)callInfo[1]!;

                return (IReadOnlyList<TableInstanceRef>)
                    [.. instances.Select(t => t with { PeriodKey = key.Value })];
            });

        var extRows = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 9001 };
        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [MainInstance] = rowIds,
                [ExternalInstance] = extRows,
            });

        // ⚠ Зріз — уже ПІСЛЯ правки: `PatchCellsHandler` записав нове число і
        // лише потім поставив перерахунок. `C3` усюди нуль — застаріле число,
        // яке формула жодного рядка не дає.
        var slice = new List<CellRecord>();
        for (var i = 1; i <= rowCount; i++)
        {
            var rowId = FirstRowId + i - 1;
            slice.Add(new CellRecord(
                new CellAddress(Period, rowId, _c1Id), table.Id, new CellValueData { ValueNumeric = i }));
            slice.Add(new CellRecord(
                new CellAddress(Period, rowId, _c2Id), table.Id, new CellValueData { ValueNumeric = i }));
            slice.Add(new CellRecord(
                new CellAddress(Period, rowId, _c3Id), table.Id,
                new CellValueData { ValueNumeric = 0m, IsCalculated = true }));

            if (withDependentLocalFormula)
            {
                slice.Add(new CellRecord(
                    new CellAddress(Period, rowId, _c4Id), table.Id,
                    new CellValueData { ValueNumeric = 0m, IsCalculated = true }));
            }
        }

        var extSlice = new List<CellRecord>();
        if (ext is not null)
        {
            extSlice.Add(new CellRecord(
                new CellAddress(Period, 9001, extColumnId), ext.Id,
                new CellValueData { ValueNumeric = 100m }));
        }

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [MainInstance] = slice,
                [ExternalInstance] = extSlice,
            });

        // ⚠ Граф — той, який зберігає публікація: підсумок кожного рядка
        // залежить від двох колонок ТОГО САМОГО рядка.
        var dependencies = new List<FormulaDependency>();
        for (var i = 1; i <= rowCount; i++)
        {
            dependencies.Add(
                FormulaDependency.ForFormula(formula.Id, 0, table.Id, $"R{i}", _c1Id, null, null, 0));
            dependencies.Add(
                FormulaDependency.ForFormula(formula.Id, 0, table.Id, $"R{i}", _c2Id, null, null, 1));
        }

        if (ext is not null)
        {
            dependencies.Add(
                FormulaDependency.ForFormula(formula.Id, 0, ext.Id, "R1", extColumnId, null, null, 2));
        }

        if (crossPeriod)
        {
            dependencies.Add(
                FormulaDependency.ForFormula(formula.Id, 3, table.Id, "R1", _c1Id, null, -1, 3));
        }

        if (dependent is not null)
        {
            // Залежність БЕЗ конкретного рядка — так граф кодує «читає цю
            // колонку в кожному рядку»; вона ж дає ребро формула → формула.
            dependencies.Add(
                FormulaDependency.ForFormula(dependent.Id, 0, table.Id, null, _c3Id, null, null, 0));
        }

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(dependencies);
    }
}
