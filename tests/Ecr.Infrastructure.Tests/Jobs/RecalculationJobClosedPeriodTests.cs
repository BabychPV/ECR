// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobClosedPeriodTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ЗАКРИТИЙ період і ПОДАНИЙ аркуш не переписуються перерахунком — на
/// **шляху запису**, а не лише на HTTP-вході (ФВ-9.7, ФВ-9.17).
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводять ці тести. <c>RecalculateDocumentHandler</c>
/// стверджував у власному коментарі, що стан періоду перевіряє
/// <c>RunCalculationHandler</c>, «якому задача передає керування». Такої
/// передачі не існувало: обробник кладе задачу в чергу САМ
/// (<c>EnqueueExclusiveAsync&lt;IRecalculationJob&gt;</c>), а
/// <c>RecalculationJob</c> кличе з <c>RunCalculationHandler</c> лише
/// <c>CompleteAsync</c> — метод завершення прогону, у якому періодів немає
/// взагалі. <c>RecalculationService</c> не мав перевірки стану періоду
/// жодної. Отже єдина перевірка ФВ-9.7 у дереві стояла на маршруті ПРОЄКТУ
/// (<c>RunCalculationHandler.HandleAsync</c>), а маршрути документа й нічного
/// розкладу писали в закритий період мовчки.
///
/// ⛔ Тести дивляться на <c>ICellStore.ApplyAsync</c> — тобто на сам ЗАПИС, а
/// не на повернений <c>jobId</c>: до фіксу задача чесно завершувалася
/// «успішно», і саме за виглядом успіху дефекту не було видно.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobClosedPeriodTests(SqlServerFixture sql)
{
    /// <summary>Версія шаблону в підставному знімку структури.</summary>
    private const int SnapshotVersion = 1;

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IDocumentHeaderStore _headers = CreateHeaderStore();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Закритий_період_не_перераховується_і_не_пише_жодної_комірки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await CloseAsync(builder, document.ProjectId, document.PeriodKey.Value);
        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. До фіксу цей виклик НЕ кидав нічого — задача
        // доходила до `ICellStore.ApplyAsync` і переписувала числа закритого
        // періоду, а прогін завершувався як успішний.
        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None));

        // ⛔ ПЕРШЕ твердження — про ЗАПИС, а не про виняток, і саме в цьому
        // порядку: на невиправленому коді виклик вище завершувався БЕЗ
        // помилки, а комірка закритого періоду вже була віддана на запис
        // (`Collection was not empty` — саме такий вигляд мав цей дефект).
        Assert.Empty(AppliedOrEmpty());

        var error = Assert.IsType<BusinessRuleException>(thrown);
        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.periodClosed", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Поданий_аркуш_не_перераховується_навіть_у_відкритому_періоді()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using (var setup = builder.CreateContext())
        {
            // Період ВІДКРИТИЙ — відмова тримається станом робочого процесу,
            // а не станом календаря (ФВ-9.17): подану цифру змінює Reopen.
            var period = await setup.Periods.SingleAsync(
                p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
            period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

            var state = new ApprovalState(
                document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(userId: 5, DateTime.UtcNow);
            setup.ApprovalStates.Add(state);

            await setup.SaveChangesAsync();
        }

        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7);

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None));

        // Той самий порядок, що й у тесті закритого періоду: спершу доказ, що
        // в базу не пішло нічого, і лише потім — що відмова названа кодом.
        Assert.Empty(AppliedOrEmpty());

        var error = Assert.IsType<BusinessRuleException>(thrown);
        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Відкритий_назад_період_перераховується_бо_саме_для_цього_Reopen_і_існує()
    {
        // ⚠ Контрольний тест межі: гейт НЕ повинен перетворити Reopen на
        // непотрібну кнопку. `Period.Reopen` переводить закритий період у
        // `Grace` — стан, у якому запис дозволений (`Period.AllowsEditing`),
        // і виправлення, заради якого відкриття й робили, мусить порахуватися.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await CloseAsync(builder, document.ProjectId, document.PeriodKey.Value);

        await using (var reopen = builder.CreateContext())
        {
            var period = await reopen.Periods.SingleAsync(
                p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
            period.Reopen(DateTime.UtcNow.AddDays(3), "Лист №17: помилка коефіцієнта", DateTime.UtcNow);
            await reopen.SaveChangesAsync();
        }

        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // Перерахунок дійшов до запису і порахував формулу: 10 + 5.
        var upsert = Assert.Single(Applied());
        Assert.Equal(15m, upsert.Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Нічний_прогін_на_весь_рік_обминає_закритий_період_і_рахує_відкритий()
    {
        // ⛔ Маршрут нічного розкладу (`NightlyRecalculationScheduling`) кладе
        // payload БЕЗ `PeriodKey` — «увесь рік». Тут відмовляти цілим прогоном
        // не можна: у будь-якому році після січня є закриті періоди, і прогін
        // перестав би працювати назавжди. Тому закритий період ВИЛУЧАЄТЬСЯ зі
        // скоупу ЗАПИСУ, а відкритий рахується — і це не «тихий дозвіл», а
        // протилежне: у закритий не пишеться нічого.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var openKey = new PeriodKey(202602);
        long openInstanceId;

        await using (var setup = builder.CreateContext())
        {
            var closed = await setup.Periods.SingleAsync(
                p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
            closed.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

            var open = new Period(
                document.ProjectId, openKey, (byte)openKey.Sequence,
                new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
            open.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            setup.Periods.Add(open);

            var loader = new BulkCellLoader(sql.ConnectionString, 1000);
            openInstanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            setup.TableInstances.Add(new TableInstance(
                openKey, openInstanceId, document.DocumentId, document.TableDefId, DateTime.UtcNow));

            await setup.SaveChangesAsync();
        }

        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: null,
            TriggeredByUserId: null);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ — про ЗАПИС, а не про читання. Закритий січень
        // усе одно ЧИТАЄТЬСЯ: формула лютого має право на `[Period:-1]`, і
        // заборонити читання означало б порахувати лютий зі порожнього січня —
        // тихо неправильне число замість захисту. Заборонено саме писати.
        var written = AppliedOrEmpty();

        var upsert = Assert.Single(written);
        Assert.Equal(openKey.Value, upsert.Address.PeriodKey.Value);
        Assert.Equal(15m, upsert.Value.ValueNumeric);

        // До фіксу тут було ДВА записи, і перший ліг у закритий січень.
        Assert.DoesNotContain(
            written, w => w.Address.PeriodKey.Value == document.PeriodKey.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Погоджений_перерахунок_закритого_періоду_заходить_бо_це_названий_виняток()
    {
        // ⛔ ЄДИНИЙ випадок, коли системний перерахунок таки пише в ЗАКРИТИЙ
        // період, і він названий, а не мовчазний: `ClosedPeriodApproval`
        // (причина + ДРУГА людина, правило чотирьох очей) на маршруті
        // перерахунку ПРОЄКТУ. `RunCalculationHandler` кладе в payload
        // `approvedBy`, і гейт мусить це прочитати.
        //
        // ⚠ Тест сторожить саме прохідність: без нього фікс міг би «закрити»
        // закриті періоди намертво — і зламати єдиний штатний шлях виправити
        // помилку в уже поданій звітності.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await CloseAsync(builder, document.ProjectId, document.PeriodKey.Value);
        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7,
            SheetDefId: null,
            ApprovedBy: 9);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        var upsert = Assert.Single(Applied());
        Assert.Equal(document.PeriodKey.Value, upsert.Address.PeriodKey.Value);
        Assert.Equal(15m, upsert.Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Погодження_НЕ_відкриває_поданий_аркуш_у_закритому_періоді()
    {
        // ⛔ Межа винятку вище. `ClosedPeriodApproval` погоджує перерахунок
        // ЗАКРИТОГО періоду — і НЕ погоджує зміну ПОДАНОЇ цифри (ФВ-9.17).
        // Без цього тесту `ApprovedBy` перетворився б на універсальний обхід
        // усього гейту, а не на названий виняток з однієї його половини.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using (var setup = builder.CreateContext())
        {
            var period = await setup.Periods.SingleAsync(
                p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
            period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

            var state = new ApprovalState(
                document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(userId: 5, DateTime.UtcNow);
            setup.ApprovalStates.Add(state);

            await setup.SaveChangesAsync();
        }

        Arrange(document);

        await using var db = builder.CreateContext();
        var job = Job(db);

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7,
            SheetDefId: null,
            ApprovedBy: 9);

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None));

        Assert.Empty(AppliedOrEmpty());

        var error = Assert.IsType<BusinessRuleException>(thrown);
        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
    }

    /// <summary>Переводить період проєкту в <c>Closed</c>.</summary>
    private static async Task CloseAsync(TestDocumentBuilder builder, int projectId, int periodKey)
    {
        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == projectId && p.PeriodKeyValue == periodKey);
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>Комірки, віддані на запис; порожньо — запису не було зовсім.</summary>
    /// <remarks>
    /// ⚠ Окремо від <see cref="Applied"/>: той вимагає РІВНО одного виклику
    /// <c>ApplyAsync</c> і на нулі падає сам, тобто не вміє відрізнити «запису
    /// не було» від «тест зламався». Тут потрібне саме перше.
    /// </remarks>
    private IReadOnlyList<CellRecord> AppliedOrEmpty()
        => [.. _cells.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync))
            .SelectMany(c => ((CellChangeSet)c.GetArguments()[0]!).Upserts)];

    /// <summary>Комірки, які служба віддала на запис.</summary>
    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyAsync));

        return ((CellChangeSet)call.GetArguments()[0]!).Upserts;
    }

    /// <summary>
    /// Знімок структури з ОДНІЄЮ формулою підсумку і значеннями входів —
    /// щоб перерахунок мав що записати, коли гейт його пропустить.
    /// </summary>
    private void Arrange(TestDocument document)
    {
        var template = new TemplateBuilder { TemplateVersionId = SnapshotVersion };
        var sheet = template.Sheet("Water");
        var table = template.Table(sheet, "Main");

        var jan = template.Column(table, "Jan", isMonthColumn: true);
        var feb = template.Column(table, "Feb", isMonthColumn: true);
        var total = template.Column(table, "Total");
        template.Row(table, "7001001", 1);
        template.Formula(table, "[Jan] + [Feb]", column: total);

        _metadata.GetAsync(SnapshotVersion, Arg.Any<CancellationToken>()).Returns(template.Build());
        _versions.ListFormulaDependenciesAsync(SnapshotVersion, Arg.Any<CancellationToken>()).Returns([]);

        const long rowId = 1001;

        // ⚠ Той самий екземпляр на будь-який період: тест про ГЕЙТ, а не про
        // адресацію екземплярів — головне, щоб запис був можливий у кожному
        // періоді, який гейт пропустить.
        _rows.GetTableInstancesAsync(document.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = (PeriodKey)callInfo[1]!;

                return (IReadOnlyList<TableInstanceRef>)
                [
                    new TableInstanceRef(
                        document.TableInstanceId, document.DocumentId, table.Id, SnapshotVersion, key.Value),
                ];
            });

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
            {
                [document.TableInstanceId] = new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["7001001"] = rowId,
                },
            });

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new Dictionary<long, IReadOnlyList<CellRecord>>
            {
                [document.TableInstanceId] =
                [
                    new CellRecord(
                        new CellAddress(document.PeriodKey, rowId, jan.Id), table.Id,
                        new CellValueData { ValueNumeric = 10m }),
                    new CellRecord(
                        new CellAddress(document.PeriodKey, rowId, feb.Id), table.Id,
                        new CellValueData { ValueNumeric = 5m }),
                ],
            });

        _cells.ReadCellsAsync(Arg.Any<IReadOnlyList<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<CellAddress, CellValueData>());

        _periods.FindPeriodBoundsAsync(
                document.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
    }

    private RecalculationJob Job(Ecr.Infrastructure.Persistence.EcrDbContext db)
        => new(db, new StubRunner(), RunHandler(), Formulas(), new TestClock(DateTime.UtcNow));

    private RecalculationService Formulas()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        // ⛔ Заглушку `IUnitOfWork` треба НАЛАШТУВАТИ на виклик замикання.
        // `DAT-02` п.4 обгорнув тіло циклу `RecalculationService.RunAsync` у
        // `ExecuteInTransactionAsync`, а NSubstitute за замовчуванням повертає
        // `Task.CompletedTask` і передане замикання НЕ ВИКЛИКАЄ взагалі — тобто
        // `ApplyAsync` не відбувається, і три тести цього файлу починають
        // падати на `Applied()` з «Sequence contains no matching element».
        //
        // ⚠ Той самий різновид пастки вже описано в конструкторі
        // `PatchCellsTests` (`Q-243`): без цього рядка тест мовчки перестає
        // щось доводити — тут він хоча б падає гучно.
        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
           .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells,
            _rows,
            _periods,
            _metadata,
            _versions,
            new RealFormulaEngine(),
            units,
            Substitute.For<IRegistryStore>(),
            _headers,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            uow, Substitute.For<Ecr.Application.Ports.ISheetEditGate>());
    }

    /// <summary>Порожня шапка документа — тести цього файлу її не читають.</summary>
    private static IDocumentHeaderStore CreateHeaderStore()
    {
        var store = Substitute.For<IDocumentHeaderStore>();
        store.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return store;
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow));

    /// <summary>Оркестратор методологій: прогін завжди «успішний і порожній».</summary>
    private sealed class StubRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
