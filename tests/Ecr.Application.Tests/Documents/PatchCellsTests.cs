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

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [],
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
        => new(_cells, _rows, _documents, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _audit, _jobs, _uow, _user, _clock);

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
        _rows.CreateRowAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(2002L);

        await Handler().HandleAsync(
            Request(new PatchRow("7009999", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        // null у BaseVersion — це намір СТВОРИТИ рядок (R-B2), а не
        // «мені байдуже до версії».
        await _rows.Received(1).CreateRowAsync(
            TableInstance, Arg.Any<PeriodKey>(), Arg.Is<RowKey>(k => k.Value == "7009999"),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Створення_рядка_з_наявним_ключем_відхиляється_ECR_ROW_0409()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow("7001001", BaseVersion: null, [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);

        await _rows.DidNotReceive().CreateRowAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<RowKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [],

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
