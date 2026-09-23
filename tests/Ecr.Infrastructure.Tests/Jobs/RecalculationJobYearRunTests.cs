// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobYearRunTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
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
/// Прогін «на весь рік» (<c>PeriodKey = null</c> — нічний розклад) виконує
/// методології ПОПЕРІОДНО: кожен екземпляр таблиці рахується з КЛЮЧЕМ СВОГО
/// періоду, бо саме за датою періоду резолвиться версія методології.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводять ці тести. <c>RecalculationJob</c> віддавав
/// оркестраторові <c>new PeriodKey(request.PeriodKey ?? 0)</c> — один ключ на
/// весь прогін. Для нічного маршруту, де періоду немає за побудовою
/// (<c>NightlyRecalculationScheduling</c> кладе <c>PeriodKey = null</c>), це
/// давало <c>PeriodKey(0)</c>: ключ, який не є періодом
/// (<c>PeriodKey.IsValid</c> — false). <c>CalculationOrchestrator.PeriodDateAsync</c>
/// шукав межі такого періоду, не знаходив і кидав <c>ECR-PRD-0404</c> — тобто
/// нічний повний перерахунок падав для будь-якого проєкту, у якого взагалі є
/// активні прив'язки методологій. Задача за розкладом, яка ніколи не працювала
/// саме для того випадку, заради якого існує.
///
/// ⛔ Помилка називала ще й НЕ ТОГО суб'єкта: «періоду 0 для документа N не
/// існує» — правда про неіснуючий період, а не про справжню причину («прогін
/// на весь рік не мав, з чим працювати»). Той, хто розбирає нічне падіння,
/// шукав би зіпсовані дані документа, а не однорядкову ваду в самій задачі.
///
/// ⚠ Тести дивляться на те, ЩО ОТРИМАВ ОРКЕСТРАТОР (ключ періоду й склад
/// прив'язок у кожному виклику), а не на факт «прогін не впав»: прогін із
/// підставним оркестратором не падав і ДО фіксу — саме тому дефект і прожив
/// стільки, скільки прожив.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobYearRunTests(SqlServerFixture sql)
{
    /// <summary>Методологія, прив'язана до таблиці документа в цих тестах.</summary>
    private const int MethodologyId = 811;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_на_весь_рік_дає_оркестратору_ключ_КОЖНОГО_періоду_окремо()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var second = new PeriodKey(202602);
        var secondInstanceId = await AddPeriodWithInstanceAsync(builder, document, second);
        await BindMethodologyAsync(builder, document);

        var runner = new RecordingRunner();

        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, runner, RunHandler(), FormulaService(), new TestClock(DateTime.UtcNow));

        // Нічний маршрут: періоду в завданні немає — «усе, що можна».
        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: null,
            TriggeredByUserId: null);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. До фіксу виклик був ОДИН і ніс `PeriodKey(0)`
        // — ключ, за яким `CalculationOrchestrator` не може знайти межі й
        // обчислити дату резолвінгу версії. Тепер викликів два, по одному на
        // період, у порядку зростання.
        Assert.Equal(
            [document.PeriodKey.Value, second.Value],
            runner.Calls.Select(c => c.PeriodKey.Value).ToList());

        // ⛔ І кожен ключ — справжній період, а не нуль: саме `IsValid` і
        // відрізняє «період 202601» від «незв'язаного параметра» (`A7-28`).
        Assert.All(runner.Calls, c => Assert.True(c.PeriodKey.IsValid));

        // ⛔ Прив'язки НЕ звалені в один виклик: екземпляр січня рахується з
        // ключем січня, екземпляр лютого — з ключем лютого. Інакше методологія
        // лютого дістала б дату резолвінгу версії від січня і входи
        // (`CalculationInputBuilder`) із чужого періоду.
        var january = Assert.Single(runner.Calls[0].Bindings);
        Assert.Equal(document.TableInstanceId, january.TableInstanceId);
        Assert.Equal(MethodologyId, january.MethodologyId);

        var february = Assert.Single(runner.Calls[1].Bindings);
        Assert.Equal(secondInstanceId, february.TableInstanceId);
        Assert.Equal(MethodologyId, february.MethodologyId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Прогін_на_весь_рік_не_кличе_оркестратора_для_закритого_періоду()
    {
        // ⚠ Межа попереднього фіксу (`6e10b56`) не має права зсунутися разом
        // із поперіодним прогоном: закритий період ВИЛУЧАЄТЬСЯ зі скоупу
        // неназваного прогону, а не відмовляє прогін цілком. Поперіодний виклик
        // робить це видимим прямо — закритого ключа серед викликів немає взагалі.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var second = new PeriodKey(202602);
        var secondInstanceId = await AddPeriodWithInstanceAsync(builder, document, second);
        await BindMethodologyAsync(builder, document);
        await CloseAsync(builder, document.ProjectId, document.PeriodKey.Value);

        var runner = new RecordingRunner();

        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, runner, RunHandler(), FormulaService(), new TestClock(DateTime.UtcNow));

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: null,
            TriggeredByUserId: null);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(second.Value, call.PeriodKey.Value);

        var binding = Assert.Single(call.Bindings);
        Assert.Equal(secondInstanceId, binding.TableInstanceId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Названий_період_і_далі_дає_РІВНО_ОДИН_виклик_саме_з_ним()
    {
        // ⚠ Контрольний тест межі: поведінка названого періоду (кнопка
        // «Перерахувати» на документі) не змінюється ні на крок — один виклик
        // оркестратора з тим самим ключем, що й у завданні, хоча в проєкті є
        // ДРУГИЙ період зі своїм екземпляром.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await AddPeriodWithInstanceAsync(builder, document, new PeriodKey(202602));
        await BindMethodologyAsync(builder, document);

        var runner = new RecordingRunner();

        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, runner, RunHandler(), FormulaService(), new TestClock(DateTime.UtcNow));

        var request = new RecalculationRequest(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: 7);

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(document.PeriodKey.Value, call.PeriodKey.Value);

        var binding = Assert.Single(call.Bindings);
        Assert.Equal(document.TableInstanceId, binding.TableInstanceId);
    }

    /// <summary>Заводить ще один період проєкту і екземпляр тієї самої таблиці в ньому.</summary>
    /// <returns>Ідентифікатор нового екземпляра.</returns>
    private async Task<long> AddPeriodWithInstanceAsync(
        TestDocumentBuilder builder, TestDocument document, PeriodKey key)
    {
        await using var db = builder.CreateContext();

        var period = new Period(
            document.ProjectId, key, (byte)key.Sequence,
            new DateOnly(key.Year, key.Sequence, 1),
            new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence)));
        db.Periods.Add(period);

        // Id — із SEQUENCE, тим самим шляхом, що й у `TestDocumentBuilder`.
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);

        db.TableInstances.Add(new TableInstance(
            key, instanceId, document.DocumentId, document.TableDefId, DateTime.UtcNow));

        await db.SaveChangesAsync();

        return instanceId;
    }

    /// <summary>Активна прив'язка методології до таблиці документа.</summary>
    private static async Task BindMethodologyAsync(TestDocumentBuilder builder, TestDocument document)
    {
        await using var db = builder.CreateContext();

        db.CalculationBindings.Add(new CalculationBinding(
            document.TableDefId, document.ColumnDefIds[1], MethodologyId, "tons", "{}"));

        await db.SaveChangesAsync();
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

    /// <summary>Фаза формул шаблону нічого не пише — тести про методології.</summary>
    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            HeaderStore(),
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>());
    }

    /// <summary>Порожня шапка документа — тести цього файлу її не читають.</summary>
    private static IDocumentHeaderStore HeaderStore()
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

    /// <summary>Один виклик оркестратора: з яким періодом і з якими прив'язками.</summary>
    private sealed record RunnerCall(PeriodKey PeriodKey, IReadOnlyList<CalculationBindingRef> Bindings);

    /// <summary>Оркестратор, що запам'ятовує КОЖЕН свій виклик цілком.</summary>
    private sealed class RecordingRunner : ICalculationRunner
    {
        public List<RunnerCall> Calls { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Calls.Add(new RunnerCall(periodKey, [.. bindings]));

            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
