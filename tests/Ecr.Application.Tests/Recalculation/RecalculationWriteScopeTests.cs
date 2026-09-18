// tests/Ecr.Application.Tests/Recalculation/RecalculationWriteScopeTests.cs
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
/// Що саме перерахунок пише в базу — <c>DAT-02</c> директиви №14 частина 3.
/// </summary>
/// <remarks>
/// ⛔ <b>Найбільший множник навантаження в системі</b> — і водночас джерело
/// хибних відмов користувачеві. Колонкова формула віддає ціль у КОЖНОМУ рядку
/// таблиці (<c>RecalculationService.Targets</c>), а <c>Evaluate</c> додавав
/// <c>upsert</c> безумовно, не питаючи, що вже лежить у комірці. На таблиці в
/// 500 рядків правка ОДНІЄЇ комірки давала до 500 рядків <c>MERGE</c>, стільки
/// ж рядків аудиту, де <c>старе = нове</c>, і стільки ж піднятих
/// <c>RowVersion</c> — тобто <c>ECR-CELL-0409</c> у кожного, хто тримав ту
/// таблицю відкритою, на порожньому місці.
///
/// ⚠ Тут навмисно ДЕСЯТЬ рядків, а не два: на двох «1 замість 10» і «1 замість
/// 2» відрізняються слабко, а твердження саме про те, що число записів не
/// залежить від розміру таблиці.
/// </remarks>
public sealed class RecalculationWriteScopeTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Version = 1;
    private const int RowCount = 10;
    private const long FirstRowId = 1001;

    private static readonly PeriodKey Period = new(202601);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    /// <summary>Виклики, зроблені ВСЕРЕДИНІ транзакційного замикання.</summary>
    private readonly List<string> _insideTransaction = [];

    private bool _inTransaction;
    private int _c1Id;
    private int _c2Id;
    private int _c3Id;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-02")]
    public async Task Правка_одного_рядка_пише_одну_комірку_а_не_всю_колонку()
    {
        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. Таблиця на 10 рядків, колонкова формула
        // `C3 = C1 + C2`, змінено `C1` рівно в першому рядку. Решта дев'ять
        // рядків мають у `C3` рівно те число, яке формула для них і порахує, —
        // тобто записувати там нічого.
        Arrange();

        var written = await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        Assert.Equal(1, written);

        var upsert = Assert.Single(Applied());
        Assert.Equal(FirstRowId, upsert.Address.TableRowId);
        Assert.Equal(_c3Id, upsert.Address.ColumnDefId);

        // 100 + 1; у базі лежало 11 — число з часів, коли `C1` було 10.
        Assert.Equal(101m, upsert.Value.ValueNumeric);
        Assert.True(upsert.Value.IsCalculated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-02")]
    public async Task В_аудит_іде_рівно_один_запис_а_не_десять()
    {
        // ⛔ Доказ (а) зі стандарту директиви. Дев'ять записів «старе = нове»
        // — це не «трохи зайвого журналу»: журнал стверджує зміни, яких не
        // було, і саме за ним звіряють, хто і що правив.
        Arrange();

        await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        var change = Assert.Single(AuditRecords());

        Assert.Equal("Recalculation", change.Origin);
        Assert.Equal(FirstRowId, change.Address.TableRowId);
        Assert.Equal("11", change.OldValue);
        Assert.Equal("101", change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "D14-07")]
    public async Task Перерахунок_не_піднімає_RowVersion_жодного_рядка()
    {
        // ⛔ Доказ (б) зі стандарту директиви, він же `D14-07`. `TouchedRowIds`
        // — єдиний спосіб, яким цей шлях піднімає `RowVersion`
        // (`NormalizedCellStore.TouchRowsAsync`), і порожній перелік тут
        // означає: сітка, відкрита в оператора, не отримає чужої версії від
        // фонової задачі.
        //
        // ⚠ Перевірено перед зміною (`git grep RowVersion -- src/Ecr.Application
        // src/Ecr.Api`): у зрізу таблиці `ETag` немає взагалі, а єдиний
        // споживач версії рядка — `baseVersion` оптимістичного блокування
        // введених значень. Обчислену колонку людина не редагує
        // (`ECR-CELL-4221`), тож версія про неї нічого й не стереже.
        Arrange();

        await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        Assert.Empty(AppliedSet().TouchedRowIds);

        // ⚠ Заразом: перерахунок НЕ заявляє очікуваних версій — він пише від
        // імені формул, а не чиєїсь відкритої сітки (контракт `CellChangeSet`).
        Assert.True(AppliedSet().ExpectedRowVersions is null or { Count: 0 });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "S-04")]
    public async Task Значення_і_аудит_пишуться_всередині_однієї_транзакції()
    {
        // ⛔ `DAT-02` п. 4 (`S-04`). До цього було ТРИ незалежні коміти:
        // `ApplyAsync` власною короткою транзакцією, аудит поза нею,
        // `SaveChanges` наприкінці. Збій між ними лишав змінені числа без
        // жодного рядка аудиту.
        Arrange();

        await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        // Порядок теж значущий: старі значення читаються ДО запису — після
        // `ApplyAsync` їх уже немає ніде.
        Assert.Equal(["read", "apply", "audit", "save"], _insideTransaction);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "S-04")]
    public async Task Збій_аудиту_не_лишає_записаних_значень()
    {
        // ⛔ Доказ (г) зі стандарту директиви: виняток МІЖ записом і аудитом.
        // Твердження тут — саме те, що на цьому рівні можна довести чесно:
        // виняток вийшов ІЗ ЗАМИКАННЯ транзакції (а не був проковтнутий), і
        // `SaveChanges` не відбувся. Відкат самої транзакції — контракт
        // `IUnitOfWork.ExecuteInTransactionAsync` («коміт лише якщо без
        // винятку»), доведений на реальному `DbContext` окремо.
        Arrange();

        _audit.WhenForAnyArgs(a => a.WriteCellChangesAsync(null!, default))
              .Do(_ => throw new InvalidOperationException("аудит недоступний"));

        var boom = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None));

        Assert.Equal("аудит недоступний", boom.Message);

        // Запис устиг відбутися — і саме тому він мусить бути в тій самій
        // транзакції, що й аудит, який упав. Останній крок (`save`) не
        // настав: виняток вийшов із замикання, не дійшовши до нього.
        Assert.Equal(["read", "apply", "audit"], _insideTransaction);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-02")]
    public async Task Нічого_не_змінилось_нічого_не_пишеться_і_транзакція_не_відкривається()
    {
        // ⚠ Межовий випадок, який і дає основний виграш під автозбереженням:
        // формула перерахувалась, результат той самий. Ані запису, ані аудиту,
        // ані транзакції — фонова задача коштує рівно нуль звернень на запис.
        Arrange(firstRowC1: 10m);

        var written = await Service().RecalculateAsync(TableInstance, Dirty(), CancellationToken.None);

        Assert.Equal(0, written);
        await _cells.DidNotReceiveWithAnyArgs().ApplyAsync(null!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteCellChangesAsync(null!, default);
        await _uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(null!, default);
    }

    /// <summary>Змінена комірка <c>C1</c> першого рядка.</summary>
    private DirtySet Dirty()
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Period, FirstRowId, _c1Id));

        return dirty;
    }

    /// <summary>Набір змін, який служба віддала сховищу.</summary>
    private CellChangeSet AppliedSet()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return (CellChangeSet)call.GetArguments()[0]!;
    }

    private IReadOnlyList<CellRecord> Applied() => AppliedSet().Upserts;

    /// <summary>Записи, які служба віддала журналу.</summary>
    private IReadOnlyList<CellChangeRecord> AuditRecords()
        => [.. _audit.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteCellChangesAsync))
            .SelectMany(c => (IReadOnlyList<CellChangeRecord>)c.GetArguments()[0]!)];

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // ⛔ Замикання викликається НАСПРАВДІ — інакше NSubstitute повернув би
        // `Task.CompletedTask`, жодного запису не сталося б, і кожне
        // твердження цього класу було б хибнозеленим. Прапорець навколо
        // виклику — це і є доказ «усередині транзакції»: сам по собі
        // `Received()` сказав би лише «виклик був», не сказавши ДЕ.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _inTransaction = true;
                try
                {
                    await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                }
                finally
                {
                    _inTransaction = false;
                }
            });

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            _audit,
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow);
    }

    /// <summary>
    /// Таблиця на <see cref="RowCount"/> рядків із колонковою формулою
    /// <c>C3 = C1 + C2</c>; у базі вже лежать правильні підсумки всіх рядків.
    /// </summary>
    /// <param name="firstRowC1">
    /// Значення <c>C1</c> у першому рядку. Типово <c>100</c> — тобто вхід
    /// змінено, і підсумок першого рядка (<c>11</c>) застарів. <c>10</c>
    /// означає «вхід не змінився»: тоді не застаріло нічого.
    /// </param>
    private void Arrange(decimal firstRowC1 = 100m)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        var table = builder.Table(sheet, "Main");

        var c1 = builder.Column(table, "C1", isMonthColumn: true);
        var c2 = builder.Column(table, "C2", isMonthColumn: true);
        var c3 = builder.Column(table, "C3", CellDataType.Formula);

        _c1Id = c1.Id;
        _c2Id = c2.Id;
        _c3Id = c3.Id;

        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 1; i <= RowCount; i++)
        {
            builder.Row(table, $"R{i}", i);
            rowIds[$"R{i}"] = FirstRowId + i - 1;
        }

        var formula = builder.Formula(table, "[C1] + [C2]", column: c3);
        var snapshot = builder.Build();
        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(snapshot);

        var instance = new TableInstanceRef(TableInstance, DocumentId, table.Id, Version, Period.Value);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(instance);
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns([instance]);
        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [TableInstance] = rowIds });

        // ⛔ Зріз — уже ПІСЛЯ правки: саме так його й бачить фонова задача
        // (`PatchCellsHandler` записав нове число і лише потім поставив
        // перерахунок). `C3` у кожному рядку — те число, яке формула давала ДО
        // правки: для рядків 2…10 воно й лишається правильним.
        var slice = new List<CellRecord>();
        var storedFirstTotal = new CellValueData();
        for (var i = 1; i <= RowCount; i++)
        {
            var rowId = FirstRowId + i - 1;
            var input = i == 1 ? firstRowC1 : 10m * i;
            var total = new CellValueData { ValueNumeric = 11m * i, IsCalculated = true };

            slice.Add(new CellRecord(new CellAddress(Period, rowId, c1.Id), table.Id, new CellValueData { ValueNumeric = input }));
            slice.Add(new CellRecord(new CellAddress(Period, rowId, c2.Id), table.Id, new CellValueData { ValueNumeric = i }));
            slice.Add(new CellRecord(new CellAddress(Period, rowId, c3.Id), table.Id, total));

            if (i == 1)
            {
                storedFirstTotal = total;
            }
        }

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyList<CellRecord>> { [TableInstance] = slice });

        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<CellAddress, CellValueData>
            {
                [new CellAddress(Period, FirstRowId, c3.Id)] = storedFirstTotal,
            });

        // ⚠ Граф — той, який зберігає публікація: підсумок кожного рядка
        // залежить від двох колонок ТОГО САМОГО рядка.
        var dependencies = new List<FormulaDependency>();
        for (var i = 1; i <= RowCount; i++)
        {
            dependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, table.Id, $"R{i}", c1.Id, null, null, 0));
            dependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, table.Id, $"R{i}", c2.Id, null, null, 1));
        }

        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>()).Returns(dependencies);

        Record();
    }

    /// <summary>Записує, які кроки сталися всередині транзакційного замикання.</summary>
    private void Record()
    {
        _cells.WhenForAnyArgs(c => c.ReadCellsAsync(null!, default))
              .Do(_ => Note("read"));
        _cells.WhenForAnyArgs(c => c.ApplyAsync(null!, default))
              .Do(_ => Note("apply"));
        _audit.WhenForAnyArgs(a => a.WriteCellChangesAsync(null!, default))
              .Do(_ => Note("audit"));
        _uow.WhenForAnyArgs(u => u.SaveChangesAsync(default))
            .Do(_ => Note("save"));
    }

    private void Note(string step)
    {
        if (_inTransaction)
        {
            _insideTransaction.Add(step);
        }
    }
}
