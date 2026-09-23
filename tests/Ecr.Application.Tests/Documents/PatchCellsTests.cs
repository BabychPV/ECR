using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
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
    private const int RegistryLinkColumnId = 12;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();

    /// <summary>Сховище документів — через нього йде «дотик» документа (`H-23d`).</summary>
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    /// <summary>Шапка документа — тести цього файлу її не читають.</summary>
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    /// <summary>Читач журналу — джерело автора й часу чужої правки (`BE-06`).</summary>
    private readonly IAuditReader _auditReader = Substitute.For<IAuditReader>();
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

        // ⛔ Директива registry-lookup, PR A2: колонка Lookup, потрібна лише
        // для перевірки посилання на неіснуючий запис довідника.
        var registryLinkColumn = new ColumnDef(
            tableDefId: 3, EcrCode.Create("RegistryLink"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry link" }), 2,
            CellDataType.Lookup);
        registryLinkColumn.SetLookup(registryDefId: 1);
        SetId(registryLinkColumn, RegistryLinkColumnId);

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
        table.AddColumn(registryLinkColumn);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [VolumeColumnId] = column,
                [RegistryLinkColumnId] = registryLinkColumn,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, DocumentId: 700, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

        // ⚠ Таблиця без прив'язаної методології — найчастіший випадок і
        // fast-path gate-у обов'язкових вхідних колонок (директива «обов'язкові
        // вхідні колонки методології»): без цього налаштування поведінка тестів,
        // які не про методологію, лишається РІВНО такою, як до gate-у.
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L });
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(Profile());
        // ⛔ Тут стояв ПОРОЖНІЙ словник, і всі тести нижче проходили — бо
        // обробник трактував відсутність рішення про доступ як ДОЗВІЛ
        // (`DIRECTIVE-14-ARCH.md`, `DAT-04`; `S-15` частини 1). Після
        // виправлення з 31 тесту цього файлу впало **20**: саме стільки їх
        // спиралося на дефект, навіть не знаючи про нього.
        //
        // ⚠ Тепер передумова названа явно: рішення на адреси, які тести
        // чіпають, ІСНУЮТЬ і дозволяють. Це не послаблення перевірки, а
        // повернення їй предмета — заборону підставляє той тест, що про неї.
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>
               {
                   [new CellAddress(PeriodKey.Parse(Period), 1001L, VolumeColumnId)] = EditDecision.Allow(),
                   [new CellAddress(PeriodKey.Parse(Period), 1001L, RegistryLinkColumnId)] = EditDecision.Allow(),
               });

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

        // ⛔ Директива registry-lookup, PR A2: за замовчуванням усе, про що
        // питають, «існує» — тести, які не про Lookup-посилання, не мають
        // падати на новій перевірці. Той тест, що про неї, підставляє інше.
        _registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());
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
               _methodologies, _registries, _headers, _audit, _auditReader, _jobs, _uow, _user, _clock);

    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

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

    /// <summary>
    /// <c>BE-06</c>: подробиці конфлікту коштують запитів ЛИШЕ на шляху відмови.
    /// </summary>
    /// <remarks>
    /// ⛔ Це замір, а не стиль. Гарячий шлях запису має бюджет p95 300 мс на
    /// 100 комірок (tz/08 §8.2), і «дочитати автора чужої правки» на КОЖНОМУ
    /// успішному збереженні з'їло б його дарма: у 99 випадках зі ста жодного
    /// конфлікту немає. Тому `DescribeConflictsAsync` живе за `throw`, а цей
    /// тест стереже, щоб воно там і лишилося.
    ///
    /// ⚠ Мутація, від якої тест падає: перенести читання журналу з гілки
    /// конфлікту в `LoadContextAsync` (тобто «щоб значення вже було в пам'яті»)
    /// — `Received(0)` стане `Received(1)`.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "BE-06")]
    public async Task Успішний_батч_не_питає_журнал_змін_жодного_разу()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        await _auditReader.DidNotReceive().ReadLastChangesAsync(
            Arg.Any<long>(),
            Arg.Any<IReadOnlyCollection<(long TableRowId, int ColumnDefId)>>(),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());

        // ⚠ Читання значень на успішному шляху рівно ОДНЕ — і воно не про
        // конфлікт, а про аудит: старі значення потрібні, щоб журнал знав, ЩО
        // було до запису (`ReadPreviousValuesAsync`).
        await _cells.Received(1).ReadCellsAsync(
            Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
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

    /// <summary>
    /// Прив'язує таблицю до методології з одним правилом («вся таблиця») й
    /// однією обов'язковою вхідною колонкою <c>Category</c> — окремою від
    /// <c>Volume</c>, яку патчить сам тест (директива «обов'язкові вхідні
    /// колонки методології»).
    /// </summary>
    /// <returns>Ідентифікатор колонки <c>Category</c>.</returns>
    /// <param name="severity">Рівень вимоги.</param>
    /// <param name="categoryType">
    /// Тип обов'язкової колонки. Date/Unit тут не косметика: саме для них
    /// `PatchCellsHandler.Text()` не мав гілки (аудит §3.1), і gate бачив
    /// порожнечу в заповненій комірці.
    /// </param>
    private int WithMethodology(
        RequiredInputSeverity severity, CellDataType categoryType = CellDataType.String)
    {
        const int CategoryColumnId = 12;

        var volume = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        SetId(volume, VolumeColumnId);

        var category = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Category"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Category" }), 2, categoryType);
        SetId(category, CategoryColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(volume);
        table.AddColumn(category);
        sheet.AddTable(table);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
                ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = volume, [CategoryColumnId] = category },
                RowsByKey: new Dictionary<(int, string), RowDef>()));

        const int MethodologyId = 100;

        var methodology = new Methodology(EcrCode.Create("ECW_TEST"), new LocalizedText(
            new Dictionary<string, string> { ["en"] = "Test methodology" }));
        var version = new MethodologyVersion(MethodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
        var rule = version.AddRule(EcrCode.Create("all"), "{}", 100);
        var requiredInput = version.AddRequiredInput(CategoryColumnId, severity, hint: null);
        version.Publish(publishedByUserId: 2, "тестова публікація", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([MethodologyId]));
        _methodologies.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyVersion>>([version]));
        _methodologies.GetRulesAsync(version.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRule>>([rule]));
        _methodologies.GetRequiredInputsAsync(version.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRequiredInput>>([requiredInput]));
        _methodologies.FindByVersionAsync(version.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<Methodology?>(methodology));

        _periods.FindPeriodBoundsAsync(700, Period, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return CategoryColumnId;
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

    /// <summary>
    /// Період із тіла не збігається з періодом екземпляра таблиці — <c>422</c>
    /// зі стабільним кодом, а не <c>500</c> (<c>DAT-04</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Не звірялося ніде. Розбіжність доїжджала аж до порушення зовнішнього
    /// ключа, і назовні виходив голий <c>500</c>: помилка в запиті виглядала як
    /// збій сервера, а клієнт не мав чого розрізняти (<c>02-contracts.md</c> §7).
    ///
    /// ⚠ Перевірка живе в ОБРОБНИКУ, хоч директива називає контролер: обробника
    /// кличе не лише HTTP — <c>ExcelImporter.ApplyAsync</c> ходить у нього
    /// напряму. У контролері правило захищало б один шлях із двох.
    ///
    /// ⚠ Випадок не теоретичний: період і аркуш живуть в адресі
    /// (<c>ФВ-14.29</c>), тож застаріла вкладка з попереднім періодом надсилає
    /// рівно таку пару.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-14.29")]
    public async Task Чужий_період_у_тілі_відхиляється_кодом_а_не_падінням()
    {
        // Екземпляр таблиці належить періоду 202601 (див. `_rows.Resolve…`),
        // а запит приходить за 202512 — рівно те, що надсилає застаріла вкладка.
        var request = new PatchCellsRequest(
            TableInstance, 202512, "UserEdit",
            [new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])]);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(request, CancellationToken.None));

        Assert.Equal(Ecr.Domain.Errors.ErrorCodes.RequestInvalid, ex.ErrorCode);

        // ⛔ Ключ каталогу обов'язковий: без нього відмова поїде українським
        // реченням мовою, якої немає серед мов продукту (`D-95`).
        Assert.NotNull(ex.Details);
        Assert.Equal("err.ECR-REQ-0422.periodMismatch", ex.Details!["messageKey"]);

        // Нічого не записано: розбіжність зупиняє запит, а не супроводжує його.
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Немає рішення про доступ — це ВІДМОВА, а не дозвіл (<c>DAT-04</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ До виправлення тут стояло
    /// <c>decisions.TryGetValue(a, out var d) &amp;&amp; !d.IsAllowed</c>: адреса,
    /// якої обчислювач не повернув, ПРОХОДИЛА як дозволена. Поруч, у гілці
    /// створення рядків, той самий метод замовчував протилежне і навіть
    /// пояснював чому — тобто дві протилежні політики жили в одному методі, і
    /// небезпечніша припадала на оновлення, тобто на гарячий шлях.
    ///
    /// ⚠ Відсутнє рішення — не теоретичний випадок: <c>CanEditSliceAsync</c>
    /// будує словник із рядків, прочитаних окремим запитом, і рядок, створений
    /// паралельним запитом між тими двома читаннями, у словник не потрапляє.
    ///
    /// ⛔ Масштаб дефекту видно з того, що НЕ в цьому тесті: після
    /// виправлення з 31 тесту цього файлу впало <b>20</b> — саме стільки їх
    /// спиралося на «рішення немає, отже можна», не знаючи про це.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Відсутнє_рішення_про_доступ_відхиляє_батч()
    {
        // Порожній словник рішень при НЕпорожньому батчі — рівно та ситуація,
        // що раніше означала «можна».
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 1m)])),
            CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);

        // ⛔ І нічого не записано: відмова має зупиняти батч, а не
        // супроводжувати його.
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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

            // ⚠ `BE-05`: третій аргумент — `createdByUserId`. Без `Arg.Any<int?>()`
            // збіг вимагав би саме `null`, тобто перевірка мовчки перестала б
            // бачити виклик, щойно обробник почав називати автора правки.
            _jobs.EnqueueAsync<IFormulaRecalculationJob>(
                Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Незаповнена_Block_вхідна_колонка_методології_відхиляє_запис_ECR_CALC_0437()
    {
        // ⛔ Директива «обов'язкові вхідні колонки методології», §1.3, PR 3:
        // мутаційний доказ — той самий патч без gate-у пройшов би без питань
        // (`Category` до цієї директиви ніхто не перевіряв).
        WithMethodology(RequiredInputSeverity.Block);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
                CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);

        // Повідомлення НАЗИВАЄ конкретну незаповнену колонку й методологію —
        // «дані неповні» саме по собі відповіді не дає.
        var details = System.Text.Json.JsonSerializer.Serialize(error.Details);
        Assert.Contains("Category", details, StringComparison.Ordinal);
        Assert.Contains("ECW_TEST", details, StringComparison.Ordinal);

        // Той самий блок, що й комірковий Error (R-B3): нічого не записано.
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CellDataType.Date)]
    [InlineData(CellDataType.Unit)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Заповнена_Date_чи_Unit_вхідна_колонка_НЕ_блокує_запис(CellDataType type)
    {
        // ⛔ Аудит 2026-09-16, §3.1. `PatchCellsHandler.Text()` — те, чим
        // живиться `ValueOf()` у `EnforceRequiredInputsAsync` — не мав гілок для
        // `ValueDate`/`ValueUnitId` (на відміну від сусіднього `Describe()`, що
        // обробляв усі шість полів `CellValueData`). Тож методологія з
        // обов'язковою Date-колонкою БЛОКУВАЛА рядок НАЗАВЖДИ: комірка
        // заповнена, а `ValueOf()` завжди `null` → `ECR-CALC-0437` на кожній
        // спробі, і жодного способу це обійти з інтерфейсу.
        var categoryId = WithMethodology(RequiredInputSeverity.Block, type);
        Assert.Equal(12, categoryId);

        object filled = type == CellDataType.Date
            ? new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)
            : 7; // ValueUnitId

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Category", filled)])),
            CancellationToken.None);

        // Запис пройшов, і жодної згадки про незаповнений обов'язковий вхід.
        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Порожня_Date_вхідна_колонка_і_далі_блокує_запис()
    {
        // Зворотний бік §3.1: розширення `Text()` не має ослабити сам gate —
        // НЕзаповнена Date-колонка мусить блокувати так само, як String.
        WithMethodology(RequiredInputSeverity.Block, CellDataType.Date);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
                CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Незаповнена_Warn_вхідна_колонка_методології_не_блокує_запис_але_повідомляє()
    {
        WithMethodology(RequiredInputSeverity.Warn);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        // Запис відбувся — Warn НЕ блокує (на відміну від Block вище).
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());

        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("7001001", warning.RowKey);
        Assert.Equal("Category", warning.ColumnCode);
        Assert.Contains("Category", warning.Message, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Рядок_без_прив_язаної_методології_ігнорує_gate_обов_язкових_входів()
    {
        // ⚠ Регресійний доказ у той самий бік, що й решта 24 тестів файлу
        // (усі — без прив'язаної методології за замовчуванням конструктора):
        // тут явно перевіряється, що ЦЕЙ конкретний факт не зачепив
        // GetPublishedVersionsAsync/GetRulesAsync/GetRequiredInputsAsync —
        // gate виходить, щойно `GetMethodologyIdsBoundToTableAsync` порожній.
        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.Empty(response.Validation);

        await _methodologies.DidNotReceive().GetPublishedVersionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
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

    /// <summary>
    /// Директива registry-lookup, PR A2: до цього перевірка ловила лише ФОРМУ
    /// значення (`ValidateValue`, синхронна) — посилання на РЕАЛЬНО ІСНУЮЧИЙ
    /// запис довідника не перевіряв ніхто, і до `FK_CellValue_Entry` (Q-316)
    /// таке значення мовчки записувалось.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Lookup_комірка_з_неіснуючим_записом_довідника_блокує_запис_ECR_CELL_4223()
    {
        // ⛔ Дефолт конструктора («усе, про що питають, існує») тут навмисно
        // замінений на порожню множину — жоден запит про існування не
        // повертає жодного id.
        _registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new HashSet<long>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("RegistryLink", 999_999_999L)])),
                CancellationToken.None));

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);

        // Нічого не записано — той самий блокуючий контракт, що інші
        // структурні відмови комірки (R-B3).
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
            Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

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
                Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        });
    }

    /// <summary>
    /// `DAT-05`: з переданою колекцією обробник НЕ ставить задачу сам, а
    /// віддає насіння каскаду викликачеві.
    /// </summary>
    /// <remarks>
    /// ⛔ Це половина контракту тимчасового параметра
    /// <c>deferRecalculationUntilMi02</c>. Друга половина — що викликач
    /// (<c>ExcelImporter</c>) справді ставить ОДНУ задачу після коміту —
    /// доводиться в <c>Ecr.Adapters.Tests</c> і наскрізно в
    /// <c>Ecr.Scenarios.Tests</c>. Порізно ці дві перевірки нічого не варті:
    /// «не поставив» без «хтось поставив» означало б, що перерахунок після
    /// імпорту не відбувається взагалі.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-05")]
    public async Task Відкладений_перерахунок_не_ставить_задачу_а_віддає_насіння()
    {
        var seeds = new List<RecalculationSeed>();

        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None,
            deferRecalculationUntilMi02: seeds);

        // ⛔ Жодної задачі: поставлена звідси, вона стартувала б усередині ще
        // не закоміченої транзакції імпорту — і під RCSI прочитала б старі
        // дані або дані, яких після відкату не буде взагалі.
        await _jobs.DidNotReceive().EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        // ⚠ Насіння — не «щось непорожнє», а РІВНО та комірка, яку записали:
        // перелік, зібраний із іншого джерела, одного дня розійшовся б із тим,
        // що насправді лежить у базі.
        var seed = Assert.Single(seeds);
        Assert.Equal(1001L, seed.RowId);
        Assert.Equal(VolumeColumnId, seed.ColumnDefId);

        // Запис при цьому відбувся: відкладається постановка задачі, а не робота.
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// `DAT-05`: без параметра поведінка не змінилася — одна задача на батч.
    /// </summary>
    /// <remarks>
    /// ⚠ Опудало проти «полагодив імпорт — зламав сітку»: звичайний
    /// <c>PATCH</c> із сітки документа передає <c>null</c>, і перерахунок
    /// мусить ставитися так само, як до `DAT-05`.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "DAT-05")]
    public async Task Без_відкладання_задача_ставиться_як_і_раніше()
    {
        await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None,
            deferRecalculationUntilMi02: null);

        await _jobs.Received(1).EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    /// <summary>
    /// `BE-05`: ідентифікатор поставленої задачі доходить до клієнта, а автором
    /// задачі записано ТОГО, ХТО ПРАВИВ.
    /// </summary>
    /// <remarks>
    /// ⛔ Дві половини одного твердження, і порізно вони нічого не варті.
    /// Ідентифікатор без автора — це <c>403</c> на першому ж опитуванні
    /// (<c>GetJobStatusHandler</c> пускає до чужої задачі лише за
    /// <c>System.ViewHealth</c>, Q-156), тобто клієнт отримує ключ до дверей,
    /// яких йому не відчинять. Автор без ідентифікатора — нікому не потрібне
    /// поле в планувальнику.
    ///
    /// ⚠ Результат <c>EnqueueAsync</c> тут навмисно НЕ <c>Arg.Any</c>-значення
    /// за замовчуванням: підробка віддає конкретний рядок, і тест звіряє саме
    /// його. Інакше твердження «непорожній» задовольнив би будь-який рядок,
    /// зокрема вигаданий обробником.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "BE-05")]
    public async Task Ідентифікатор_задачі_перерахунку_повертається_у_відповіді_і_несе_автора_правки()
    {
        const string JobId = "IFormulaRecalculationJob#77";

        _jobs.EnqueueAsync<IFormulaRecalculationJob>(
                 Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
             .Returns(JobId);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None);

        Assert.Equal(JobId, response.RecalculationJobId);

        // ⛔ Саме `9` — `_user.UserId` цього набору. `Arg.Any<int?>()` тут
        // пропустив би `null`, тобто системну задачу без автора: рівно те, що
        // повертає редактору `403` на власний перерахунок.
        await _jobs.Received(1).EnqueueAsync<IFormulaRecalculationJob>(
            Arg.Any<object>(), Arg.Any<CancellationToken>(), 9);
    }

    /// <summary>
    /// `BE-05` + `DAT-05`: у гілці відкладання поле — рівно <c>null</c>, а не
    /// порожній рядок.
    /// </summary>
    /// <remarks>
    /// ⛔ Різниця не косметична. <c>null</c> клієнт читає як «стежити нема за
    /// чим» і мовчить; порожній рядок пройшов би перевірку «поле є» і послав
    /// статус-рядок опитувати <c>GET /api/v1/jobs/</c> — адресу без сегмента,
    /// тобто перелік задач замість стану однієї, під правом
    /// <c>System.ViewHealth</c>, якого в редактора немає.
    /// </remarks>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "BE-05")]
    public async Task Відкладений_перерахунок_дає_recalculationJobId_рівно_null()
    {
        // ⚠ Підробка ГОТОВА віддати ідентифікатор — саме тому тест доводить, що
        // `null` тут від гілки відкладання, а не від ненаповненого substitute.
        _jobs.EnqueueAsync<IFormulaRecalculationJob>(
                 Arg.Any<object>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
             .Returns("IFormulaRecalculationJob#77");

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])),
            CancellationToken.None,
            deferRecalculationUntilMi02: []);

        Assert.Null(response.RecalculationJobId);
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
