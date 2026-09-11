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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Пакетна зміна комірок — найгарячіший шлях запису.</summary>
public sealed class PatchCellsTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeColumnId = 11;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();

    /// <summary>Сховище документів — через нього йде «дотик» документа (`H-23d`).</summary>
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public PatchCellsTests()
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(column, VolumeColumnId);

        // ⚠ Q-148: PatchCellsHandler тепер шукає TableDef у Sheets, щоб
        // перевірити RowMode/MaxDynamicRows на створення рядка. Dynamic —
        // щоб тести, які не про Q-148, і далі вільно створювали рядки.
        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, DocumentId: 700, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L });
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        // ⚠ Створення за замовчуванням ДОЗВОЛЕНЕ: тести, які не про права, не
        // мають падати на правах. Заборону підставляє той тест, який про неї.
        //
        // ⛔ Порожній словник тут був би пасткою: обробник мусить трактувати
        // відсутність рішення як ВІДМОВУ, інакше повертається рівно той дефект,
        // який ці тести закривають, — «рішення немає, отже можна».
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => NewRows(call.ArgAt<IReadOnlyCollection<string>>(2), EditDecision.Allow(), EditDecision.Allow()));

        // ⛔ Q-243: PersistChangesAsync тепер виконує весь блок через
        // IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task>, ...).
        // Без цього налаштування NSubstitute повертає typed-default
        // (Task.CompletedTask) і НІКОЛИ не викликає передане замикання — тобто
        // жодна з перевірок нижче (cellStore.ApplyAsync, аудит, SaveChanges)
        // не виконалась би НАСПРАВДІ, і тести мовчки перестали б щось
        // доводити. Тут — виклик замикання НАПРАВДУ, тим самим ct, що йому
        // передали (те, що атомарність/rollback дотримані на РЕАЛЬНому
        // DbContext — доводить `PatchCellsAtomicityTests` в
        // Ecr.Infrastructure.Tests проти реального SQL Server, не тут).
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    private static void SetId(ColumnDef column, int id)
        => typeof(Ecr.Domain.Abstractions.Entity<int>)
            .GetProperty("Id")!.SetValue(column, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private PatchCellsHandler Handler()
        => new(_cells, _rows, _documents, _periods, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _audit, _jobs, _uow, _user, _clock);

    /// <summary>Відповідь служби доступу на створення рядків.</summary>
    /// <param name="keys">Ключі, про які питали.</param>
    /// <param name="row">Рішення на рядок.</param>
    /// <param name="column">Рішення на колонку <c>Volume</c>.</param>
    private static Dictionary<string, NewRowAccess> NewRows(
        IReadOnlyCollection<string> keys, EditDecision row, EditDecision column)
        => keys.ToDictionary(
            k => k,
            _ => new NewRowAccess(row, new Dictionary<int, EditDecision> { [VolumeColumnId] = column }),
            StringComparer.Ordinal);

    private static PatchCellsRequest Request(params PatchRow[] rows)
        => new(TableInstance, Period, "UserEdit", rows);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Значення_записується_і_повертається_нова_версія_рядка()
    {
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(
                 new Dictionary<string, string> { ["7001001"] = "0x0A" },
                 new Dictionary<string, string> { ["7001001"] = "0x0B" });

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);

        // Клієнт мусить отримати НОВУ версію рядка: без неї наступний патч
        // піде зі застарілою baseVersion і отримає 409 на власних змінах.
        Assert.Equal("0x0B", response.RowVersions["7001001"]);

        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.Upserts.Count == 1 && c.Deletes.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Рядок_без_базової_версії_трактується_як_створення()
    {
        _rows.CreateRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns([2002L]);

        await Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // null у BaseVersion — це намір СТВОРИТИ рядок (R-B2), а не
        // «мені байдуже до версії».
        await _rows.Received(1).CreateRowsAsync(
            TableInstance, Arg.Any<PeriodKey>(),
            Arg.Is<IReadOnlyList<RowKey>>(keys => keys.Count == 1 && keys[0].Value == "7009999"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Створення_рядка_з_наявним_ключем_відхиляється_ECR_ROW_0409()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow("7001001", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);

        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Fixed_таблиця_відхиляє_вигаданий_ключ_рядка_ECR_ROW_0409()
    {
        // ⛔ Q-148: PATCH /cells і POST /rows мають давати ОДНАКОВУ відповідь
        // на той самий намір — «додати рядок у Fixed». `CreateRowHandler` уже
        // відхиляв це за RowMode; тут той самий шлях був відкритий.
        WithTable(TableRowMode.Fixed);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow("DYN-vigadanyi", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Fixed_таблиця_дозволяє_матеріалізацію_ключа_з_RowsByKey()
    {
        // ⚠ «Вужче формулювання» (рішення людини): не заборона створення в
        // Fixed цілком, а дозвіл ЛИШЕ на ключ, який справді описаний у
        // шаблоні (`snapshot.RowsByKey`) — вигаданий ключ і далі відхиляється.
        var rowDef = new RowDef(
            tableDefId: 3, RowKey.Create("R1"), 1,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Row 1" }), RowKind.Item);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(rowDef, 1);

        WithTable(TableRowMode.Fixed, rowsByKey: new Dictionary<(int, string), RowDef> { [(3, "R1")] = rowDef });

        _rows.CreateRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns([3001L]);

        await Handler().HandleAsync(
            Request(new PatchRow("R1", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        await _rows.Received(1).CreateRowsAsync(
            TableInstance, Arg.Any<PeriodKey>(),
            Arg.Is<IReadOnlyList<RowKey>>(keys => keys.Count == 1 && keys[0].Value == "R1"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task MaxDynamicRows_блокує_створення_що_перевищило_б_межу()
    {
        // ⛔ Q-148: та сама стеля, яку вже стереже CreateRowHandler, можна
        // було обійти пакетним записом через PATCH.
        WithTable(TableRowMode.Dynamic, maxDynamicRows: 1);

        // У таблиці вже є один рядок (7001001, з дефолтного фікстурного
        // GetRowIdsAsync) — другий створюваний перевищив би межу в 1.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow("DYN-2", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Підміняє знімок метаданих таблицею з обраним RowMode.</summary>
    private void WithTable(
        TableRowMode rowMode, int? maxDynamicRows = null,
        IReadOnlyDictionary<(int, string), RowDef>? rowsByKey = null)
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, rowMode);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(column);

        if (maxDynamicRows is not null)
        {
            table.SetMaxDynamicRows(maxDynamicRows);
        }

        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
                ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
                RowsByKey: rowsByKey ?? new Dictionary<(int, string), RowDef>()));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.7")]
    public async Task Конфлікт_в_одному_рядку_відхиляє_весь_батч_із_переліком_конфліктів()
    {
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A", ["7001002"] = "0xFF" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L, ["7001002"] = 1002L });

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Handler().HandleAsync(
            Request(
                new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)]),   // версія збігається
                new PatchRow("7001002", "0x0A", [new PatchCell("Volume", 2m)])),  // а тут — ні
            CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", ex.ErrorCode);

        // Коректний рядок теж НЕ застосований: батч є одним цілим (B04 §2.3).
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Перелік конфліктів іде клієнтові: «перезаписати мовчки» не є опцією.
        Assert.NotNull(ex.Details);
        Assert.True(ex.Details!.ContainsKey("conflicts"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-4.4")]
    public async Task Заборонена_комірка_відхиляє_батч_із_причиною()
    {
        var address = new CellAddress(new PeriodKey(Period), 1001L, VolumeColumnId);
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>
               {
                   [address] = EditDecision.Deny(EditDenyReason.PeriodClosed, "Період закрито 05.02.2026")
               });

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);

        // Причина доходить до клієнта: користувач має розуміти, ЧОМУ комірка
        // сіра, інакше він піде до адміністратора, а той — до розробника.
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), ex.Details!["reason"]);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-4.4")]
    public async Task Заборона_відхиляє_і_створення_рядка_а_не_лише_оновлення()
    {
        // ⛔ Це перетин двох осей, кожну з яких набір перевіряв ОКРЕМО:
        // «створення» (`Рядок_без_базової_версії_трактується_як_створення`) і
        // «заборона» (`Заборонена_комірка_відхиляє_батч_із_причиною`). Перший
        // не налаштовував прав узагалі, другий ішов виключно шляхом оновлення.
        // Дефект жив рівно в їхньому перетині: адреси для перевірки збиралися
        // тільки з `updates`, тож на створенні `addresses.Count == 0` і весь
        // блок прав пропускався.
        //
        // ⚠ Виміряно живим прогоном, не виведено: `PATCH` у період `state = 3`
        // (`Closed`) із `baseVersion: null` віддавав `200` і клав значення в
        // базу — при тому, що той самий рядок з `baseVersion` віддавав `403`.
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => NewRows(
                   call.ArgAt<IReadOnlyCollection<string>>(2),
                   EditDecision.Deny(EditDenyReason.PeriodClosed, "Період закрито 05.02.2026"),
                   EditDecision.Deny(EditDenyReason.PeriodClosed, "Період закрито 05.02.2026")));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), ex.Details!["reason"]);

        // Ані рядка, ані комірок: відмова має спинити батч ПОВНІСТЮ.
        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Заборона_на_КОЛОНЦІ_відхиляє_створення_попри_дозвіл_на_рядок()
    {
        // ⛔ Друга половина перевірки, і вона не зайва: грант оголошується в
        // тому числі на колонку (`ResourceKind.Column`), тож «писати в цю
        // таблицю можна» і «писати в цю колонку можна» — різні відповіді.
        // Перевіряй ми лише рішення на рядок, `isDeny`-грант на колонку не
        // спрацював би саме там, де рядок створюють.
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => NewRows(
                   call.ArgAt<IReadOnlyCollection<string>>(2),
                   EditDecision.Allow(),
                   EditDecision.Deny(EditDenyReason.NoGrant)));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.NoGrant), ex.Details!["reason"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Рішення_на_створюваний_рядок_не_прийшло_це_відмова_а_не_дозвіл()
    {
        // ⛔ Напрям замовчування — половина цієї вимоги. Служба зобов'язана
        // повернути рішення на КОЖЕН запитаний ключ; якщо не повернула,
        // єдина безпечна відповідь — відмовити. Протилежне замовчування
        // («немає рішення, отже можна») і є той самий дефект, лише переписаний
        // акуратніше.
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, NewRowAccess>(StringComparer.Ordinal));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Відсутнє_поле_в_запиті_не_змінює_комірку()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // У запиті одна комірка — застосована рівно одна. Колонки, яких немає
        // в Cells, у набір змін не потрапляють узагалі: «не чіпати» і «стерти»
        // лишаються різними намірами (R-B4).
        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.Upserts.Count == 1 && c.Deletes.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23d")]
    public async Task Правка_комірок_піднімає_дату_зміни_ДОКУМЕНТА()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // ⛔ Регресія: «дотик» документа знову зникає, і `ModifiedAt` із
        // `ModifiedByUserId` назавжди лишаються моментом СТВОРЕННЯ. Ніщо не
        // падає: рядки оновлюються, аудит пишеться, а перелік документів
        // показує дату, якої зміни не мали. Колонка, що показує неправду,
        // знецінює й сусідні — правдиві.
        await _documents.Received(1).TouchAsync(700, 9, Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23d")]
    public async Task Дотик_документа_йде_ДО_коміту_а_не_після()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // ⚠ Порядок тут не косметика: дата зміни має лягти ТИМ САМИМ комітом,
        // що й самі значення. Окремим збереженням після коміту вона пережила б
        // відкат — і документ отримав би дату зміни, якої не було.
        Received.InOrder(() =>
        {
            _documents.TouchAsync(700, 9, Now, Arg.Any<CancellationToken>());
            _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Перерахунок_ставиться_в_чергу_ПОЗА_транзакцією_запису()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // Порядок критичний: спершу commit, потім черга. Інакше воркер почне
        // читати рядки, яких ще не видно, і отримає або старі значення, або
        // блокування на піку останнього дня періоду.
        Received.InOrder(() =>
        {
            _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
            _jobs.EnqueueAsync<IFormulaRecalculationJob>(
                Arg.Any<object>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.4")]
    public async Task Комірковий_Error_валідації_блокує_запис()
    {
        WithRule(ValidationSeverity.Error, scope: 0, "[Volume] >= 0");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", -5m)])),
                CancellationToken.None));

        Assert.Equal("ECR-CELL-0422", error.ErrorCode);

        // Нічого не записано: комірковий Error — єдиний рівень, який блокує
        // запис (R-B3), і блокує він увесь батч, а не одну комірку.
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Рядковий_Error_валідації_НЕ_блокує_запис_а_повертається_у_відповіді()
    {
        WithRule(ValidationSeverity.Error, scope: 1, "[Volume] >= 1000");

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 5m)])),
            CancellationToken.None);

        // ⚠ Той самий рівень Error, інший наслідок: рядок може бути
        // незавершеним посеред заповнення, і заборона зберегти проміжний стан
        // зробила б роботу з великою таблицею неможливою.
        Assert.Equal(1, response.AppliedCells);
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());

        var message = Assert.Single(response.Validation);
        Assert.Equal("Error", message.Severity);
        Assert.Equal("7001001", message.RowKey);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Попередження_не_блокує_запис()
    {
        WithRule(ValidationSeverity.Warning, scope: 0, "[Volume] <= 100");

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.Equal("Warning", Assert.Single(response.Validation).Severity);

        // Попередження саме ПОВЕРТАЄТЬСЯ, а не мовчки зникає: інакше рівні
        // валідації не мали б жодного сенсу, крім Error.
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Після_запису_залежні_комірки_ставляться_в_чергу_перерахунку()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        // ⛔ Задача — саме `IFormulaRecalculationJob`. Раніше тут стояла
        // задача МЕТОДОЛОГІЙ, тіла якої вона не розуміє: її запит має
        // `ProjectId`/`DocumentId`, а надсилався `TableInstanceId`. Розбір
        // давав нулі, і задача не робила нічого — а цей тест був зелений, бо
        // питав лише «чи поставили в чергу» (`A7-63`).
        await _jobs.Received(1).EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        // ⛔ І тіло несе ЗМІНЕНІ КОМІРКИ — насіння каскаду. Без них
        // перерахунок був би повним на кожну правку, і граф залежностей
        // коштував би, не даючи нічого.
        var payload = _jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueAsync))
            .Select(c => c.GetArguments()[0])
            .Last();

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains("\"Cells\"", json, StringComparison.Ordinal);
        Assert.Contains("\"TableInstanceId\"", json, StringComparison.Ordinal);

        // ⚠ Черга — ПІСЛЯ commit і поза транзакцією: воркер інакше почав би
        // читати рядки, яких ще не видно, і отримав би або старі значення,
        // або блокування на піку останнього дня періоду.
        Received.InOrder(() =>
        {
            _uow.SaveChangesAsync(Arg.Any<CancellationToken>());
            _jobs.EnqueueAsync<IFormulaRecalculationJob>(
                Arg.Any<object>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Код_колонки_резолвиться_в_МЕЖАХ_таблиці_а_не_всієї_версії()
    {
        // ⛔ Коди колонок унікальні в межах ТАБЛИЦІ. У реальному шаблоні
        // дев'яносто таблиць, і `Volume` є в багатьох; до `A7-27` мапа
        // будувалася по всій версії з `GroupBy(...).First()`, тобто код
        // резолвився в колонку ВИПАДКОВОЇ таблиці.
        //
        // ⚠ Дані рятував складений `FK_CellValue_Column` (`D-84`): комірку з
        // колонкою чужої таблиці база відхиляє. Цей тест перевіряє КОД, щоб
        // помилка адресації не доходила до бази взагалі; що база її ловить —
        // окремий інтеграційний тест.
        const int OtherTableColumnId = 99;

        var mine = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(mine, VolumeColumnId);

        // Та сама назва колонки в ІНШІЙ таблиці тієї ж версії.
        var alien = new ColumnDef(
            tableDefId: 4, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(alien, OtherTableColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(mine);
        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],

                // ⚠ Чужа колонка йде ПЕРШОЮ: саме її брав `First()`.
                ColumnsById: new Dictionary<int, ColumnDef>
                {
                    [OtherTableColumnId] = alien,
                    [VolumeColumnId] = mine,
                },
                RowsByKey: new Dictionary<(int, string), RowDef>()));

        CellChangeSet? changes = null;
        await _cells.ApplyAsync(
            Arg.Do<CellChangeSet>(c => changes = c), Arg.Any<CancellationToken>());

        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 5m)])),
            CancellationToken.None);

        Assert.NotNull(changes);

        // Комірка адресується колонкою СВОЄЇ таблиці (TableDefId = 3).
        Assert.Equal(VolumeColumnId, Assert.Single(changes.Upserts).Address.ColumnDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "R-A2")]
    public async Task Аудит_несе_RowKey_і_старе_значення()
    {
        // Комірка вже має значення: саме воно і є половиною запису аудиту.
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>
              {
                  [new CellAddress(new PeriodKey(Period), 1001L, VolumeColumnId)] =
                      new CellValueData { ValueNumeric = 5m },
              });

        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 7m)])),
            CancellationToken.None);

        var record = Assert.Single(Audited());

        // ⛔ Обидва поля писалися константами: `RowKey` — порожнім рядком,
        // `OldValue` — `null`. Журнал відповідав «стало 7» і не міг сказати
        // ні де, ні що було до того (директива №09 `W8` п.4).
        Assert.Equal("7001001", record.RowKey);
        Assert.Equal("5", record.OldValue);
        Assert.Equal("7", record.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "R-A2")]
    public async Task Аудит_несе_RowKey_щойно_створеного_рядка()
    {
        _rows.CreateRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns([2002L]);

        await Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // ⚠ Рядка, який щойно створили, немає в мапі, прочитаній ДО вставки —
        // тобто найпростіший спосіб дістати ключ тут не працює. Саме тому
        // мапа збирається по ходу, а не «з того, що вже було».
        Assert.Equal("7009999", Assert.Single(Audited()).RowKey);

        // Комірки не існувало — «було» лишається порожнім чесно: незаповнена
        // комірка не матеріалізується взагалі (`ФВ-3.8`).
        Assert.Null(Assert.Single(Audited()).OldValue);
    }

    [Theory]
    [InlineData(PeriodState.Open, false)]
    [InlineData(PeriodState.Grace, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-70")]
    public async Task IsLateEdit_обчислюється_за_станом_періоду(PeriodState state, bool expected)
    {
        _periods.FindPeriodStateAsync(700L, Period, Arg.Any<CancellationToken>())
                .Returns((PeriodState?)state);

        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 7m)])),
            CancellationToken.None);

        // ⛔ Тут стояв літерал `false`, при тому що `Period.IsLateEditWindow`
        // існував і не мав жодного читача: пізніх правок у журналі не бувало
        // ніколи (директива №09 `W8` п.6). `Reopen` теж сюди входить — він
        // переводить період саме в `Grace`.
        Assert.Equal(expected, Assert.Single(Audited()).IsLateEdit);

        await _cells.Received(1).ApplyAsync(
            Arg.Is<CellChangeSet>(c => c.IsLateEdit == expected), Arg.Any<CancellationToken>());
    }

    /// <summary>Записи, які обробник віддав у журнал аудиту.</summary>
    private IReadOnlyList<CellChangeRecord> Audited()
    {
        var call = _audit.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAuditWriter.WriteCellChangesAsync));

        return (IReadOnlyList<CellChangeRecord>)call.GetArguments()[0]!;
    }

    /// <summary>Додає таблицю з одним правилом валідації у знімок метаданих.</summary>
    private void WithRule(ValidationSeverity severity, byte scope, string expression)
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);

        table.AddColumn(column);
        table.AddValidationRule(new ValidationRule(
            tableDefId: 3, EcrCode.Create("RULE"), severity, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Порушено RULE" })));
        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
                ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
                RowsByKey: new Dictionary<(int, string), RowDef>()));
    }
}
