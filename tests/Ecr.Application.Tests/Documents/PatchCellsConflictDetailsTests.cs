using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>BE-06</c>: конфлікт версій називає ЧИЄ значення, ХТО і КОЛИ.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який ці тести стережуть, був гіршим за відсутність полів.
/// <c>CellConflictDto</c> мав <c>TheirValue</c>, <c>TheirUser</c> і
/// <c>TheirChangedAt</c>, а обробник заповнював їх <c>null</c>, <c>""</c> і
/// <c>clock.UtcNow</c>. Тобто діалог конфлікту показував би порожнечу й
/// ПОТОЧНИЙ ЧАС СЕРВЕРА як момент чужої правки: користувач бачив би «їхня
/// правка 14:02» на правці, зробленій о 9:15, і вирішував би «беру їхнє /
/// лишаю своє» за вигаданим числом.
///
/// ⚠ Годинник обробника навмисно ЗСУНУТИЙ на годину від моменту чужої правки
/// (<see cref="Now"/> проти <see cref="TheirMoment"/>). Без цього зсуву
/// «повернути <c>clock.UtcNow</c>» і «повернути момент із журналу» дали б
/// однакове число, і тест лишався б зеленим на невиправленому коді.
///
/// ⚠ Перевірка версій відбувається ДО перевірки прав
/// (<c>HandleAsync</c>: <c>EnsureNoVersionConflictsAsync</c> →
/// <c>EnsureAccessAsync</c>), тому тут немає ані налаштування доступу, ані
/// транзакції: конфліктний батч до них не доходить. Це не спрощення фікстури, а
/// факт порядку кроків.
/// </remarks>
public sealed class PatchCellsConflictDetailsTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int TableDefId = 3;
    private const int VolumeColumnId = 11;
    private const string RowKey = "7001001";
    private const long TableRowId = 1001L;

    /// <summary>Момент, у який працює обробник.</summary>
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Момент ЧУЖОЇ правки — на годину раніше.</summary>
    private static readonly DateTime TheirMoment = new(2026, 1, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAuditReader _auditReader = Substitute.For<IAuditReader>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PatchCellsConflictDetailsTests()
    {
        var column = new ColumnDef(
            TableDefId, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, TableDefId);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(
                 TableInstance, DocumentId, TableDefId, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

        // Версія в базі не та, яку заявляє клієнт, — це і є конфлікт.
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { [RowKey] = "0xFF" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { [RowKey] = TableRowId });
    }

    private PatchCellsHandler Handler()
        => new(_cells, _rows, _documents, _periods, _metadata, _access,
               new Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _methodologies, _registries, _headers, _audit, _auditReader, _jobs, _uow, _user, _clock,
               Substitute.For<ISheetEditGate>());

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

    private static CellAddress Address(long rowId = TableRowId, int columnDefId = VolumeColumnId)
        => new(PeriodKey.Parse(Period), rowId, columnDefId);

    /// <summary>Батч, що заявляє застарілу версію рядка.</summary>
    private static PatchCellsRequest StaleRequest(params PatchCell[] cells)
        => new(TableInstance, Period, "UserEdit", [new PatchRow(RowKey, "0x0A", cells)]);

    private void StoredValue(CellValueData value)
        => _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<CellAddress, CellValueData> { [Address()] = value });

    private void LastChange(LastCellChange change)
        => _auditReader.ReadLastChangesAsync(
               Arg.Any<long>(), Arg.Any<IReadOnlyCollection<(long, int)>>(),
               Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
           .Returns(new Dictionary<(long TableRowId, int ColumnDefId), LastCellChange>
           {
               [(TableRowId, VolumeColumnId)] = change,
           });

    private static IReadOnlyList<CellConflictDto> ConflictsOf(ConcurrencyConflictException ex)
    {
        Assert.NotNull(ex.Details);
        return Assert.IsAssignableFrom<IReadOnlyList<CellConflictDto>>(ex.Details!["conflicts"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Конфлікт_несе_чуже_значення_автора_і_момент_із_журналу()
    {
        StoredValue(new CellValueData { ValueNumeric = 12.40m });
        LastChange(new LastCellChange(TheirMoment, ChangedByUserId: 77, "A. Serikbayev", "UserEdit"));

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(StaleRequest(new PatchCell("Volume", 9m)), CancellationToken.None));

        var details = Assert.Single(ConflictsOf(conflict));

        Assert.Equal(RowKey, details.RowKey);
        Assert.Equal("Volume", details.ColumnCode);

        // Своє значення — те, що надіслали; чуже — те, що лежить у базі.
        Assert.Equal(9m, details.YourValue);
        Assert.Equal(12.40m, details.TheirValue);

        // ⛔ ІМ'Я, а не логін і не SID (R-A2, D-86).
        Assert.Equal("A. Serikbayev", details.TheirUser);
        Assert.Equal("UserEdit", details.TheirOrigin);

        // ⛔ ГОЛОВНЕ твердження файлу: момент узято з журналу, а не з годинника
        // сервера. Мутація «повернути clock.UtcNow» валить рівно цей рядок —
        // 08:00 проти 09:00.
        Assert.Equal(TheirMoment, details.TheirChangedAt);
        Assert.NotEqual(Now, details.TheirChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Журнал_питають_рівно_один_раз_і_з_вікном_часу()
    {
        StoredValue(new CellValueData { ValueNumeric = 1m });
        LastChange(new LastCellChange(TheirMoment, 77, "A. Serikbayev", "UserEdit"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(
                StaleRequest(new PatchCell("Volume", 9m)), CancellationToken.None));

        // ⚠ Замір ціни шляху відмови: рівно ОДИН додатковий запит до журналу і
        // ОДИН до значень на весь батч, скільки б комірок у ньому не було.
        await _auditReader.Received(1).ReadLastChangesAsync(
            DocumentId,
            Arg.Is<IReadOnlyCollection<(long TableRowId, int ColumnDefId)>>(
                cells => cells.Count == 1
                         && cells.Any(cell => cell.Item1 == TableRowId && cell.Item2 == VolumeColumnId)),

            // ⛔ Вікно ОБОВ'ЯЗКОВЕ: без нього засічка по `IX_CellChange_Cell`
            // пробиває всі партиції `aud.CellChange`, включно з архівними.
            // 13 місяців — те саме вікно, що стеля історії однієї комірки в `BE-03`.
            Now.AddMonths(-13),
            Arg.Any<CancellationToken>());

        await _cells.Received(1).ReadCellsAsync(
            Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Зміну_машини_підписано_system_із_названим_походженням()
    {
        StoredValue(new CellValueData { ValueNumeric = 12.40m });

        // Перерахунок теж несе UserId — того, хто його запустив. Підставити
        // ЙОГО ім'я означало б сказати «Серікбаєв змінив 12.40» про число,
        // яке порахувала формула.
        LastChange(new LastCellChange(TheirMoment, ChangedByUserId: 77, "A. Serikbayev", "Recalculation"));

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(StaleRequest(new PatchCell("Volume", 9m)), CancellationToken.None));

        var details = Assert.Single(ConflictsOf(conflict));

        Assert.Equal("system", details.TheirUser);
        Assert.Equal("Recalculation", details.TheirOrigin);
        Assert.Equal(TheirMoment, details.TheirChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Комірка_без_запису_в_журналі_лишається_без_автора_а_не_без_значення()
    {
        StoredValue(new CellValueData { ValueNumeric = 12.40m });

        _auditReader.ReadLastChangesAsync(
                Arg.Any<long>(), Arg.Any<IReadOnlyCollection<(long, int)>>(),
                Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(long TableRowId, int ColumnDefId), LastCellChange>());

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(StaleRequest(new PatchCell("Volume", 9m)), CancellationToken.None));

        var details = Assert.Single(ConflictsOf(conflict));

        // Значення відоме — воно з `doc.CellValue`, а не з журналу.
        Assert.Equal(12.40m, details.TheirValue);

        // ⛔ А от автор і момент — `null`, і це чесно: «у вікні журналу змін
        // немає» не дає права ні вигадати ім'я, ні поставити поточний час.
        Assert.Null(details.TheirUser);
        Assert.Null(details.TheirOrigin);
        Assert.Null(details.TheirChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Понад_сто_розбіжних_комірок_обрізаються_лічильником()
    {
        const int total = 137;

        StoredValue(new CellValueData { ValueNumeric = 12.40m });
        LastChange(new LastCellChange(TheirMoment, 77, "A. Serikbayev", "UserEdit"));

        // ⚠ Той самий код колонки повторюється навмисно: батч на сотні комірок
        // — це вставка з буфера, і стеля має спрацювати на КІЛЬКОСТІ комірок, а
        // не на кількості різних колонок.
        var cells = Enumerable.Range(0, total).Select(_ => new PatchCell("Volume", 9m)).ToArray();

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(StaleRequest(cells), CancellationToken.None));

        Assert.Equal(100, ConflictsOf(conflict).Count);

        // ⛔ Решта не зникає мовчки: користувач має знати, що перелік обрізано.
        Assert.Equal("37", conflict.Details!["moreConflicts"]);

        // ⚠ Рядок при цьому один — і число рядків рахується по ПОВНОМУ переліку,
        // не по показаному.
        Assert.Equal("1", conflict.Details!["rowCount"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Рядка_якого_немає_описати_нічим_і_обробник_нічого_не_вигадує()
    {
        // Рядок відсутній у версіях: клієнт заявив `baseVersion` на рядок, який
        // тим часом зник.
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(StaleRequest(new PatchCell("Volume", 9m)), CancellationToken.None));

        var details = Assert.Single(ConflictsOf(conflict));

        Assert.Equal("*", details.ColumnCode);
        Assert.Null(details.TheirValue);
        Assert.Null(details.TheirUser);

        // ⛔ Тут стояв `clock.UtcNow` — поточний час сервера в полі «коли
        // змінили» на рядку, якого взагалі немає.
        Assert.Null(details.TheirChangedAt);

        // ⚠ І жодного запиту: описувати нема чого, тож і питати нема про що.
        await _auditReader.DidNotReceive().ReadLastChangesAsync(
            Arg.Any<long>(), Arg.Any<IReadOnlyCollection<(long, int)>>(),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ReadCellsAsync(
            Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }
}
