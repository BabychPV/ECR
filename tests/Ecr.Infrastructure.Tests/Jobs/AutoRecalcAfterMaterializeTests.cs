// tests/Ecr.Infrastructure.Tests/Jobs/AutoRecalcAfterMaterializeTests.cs
using Ecr.Application;
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Автоперерахунок після матеріалізації даних PI (HSE301 A4, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⚠ Дедуплікацію тут доводить СПРАВЖНІЙ планувальник (Quartz, не запущений —
/// перевіряється сховище задач, як у <c>ExclusiveEnqueueTests</c>): тригер власної
/// не має, він ставить задачу тим самим маркером і ціллю, що й кнопка, і одну
/// живу задачу лишає черга. Стан періоду й подані аркуші — справжні сховища на
/// SQL Server, як у задачі в контейнері.
/// </remarks>
[Collection("SqlServer")]
public sealed class AutoRecalcAfterMaterializeTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Планувальники тесту — зупиняються після нього.</summary>
    private readonly List<IScheduler> schedulers = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>⚠ Незупинений планувальник лишається в статичному реєстрі Quartz до кінця процесу.</summary>
    public async Task DisposeAsync()
    {
        foreach (var scheduler in schedulers)
        {
            await scheduler.Shutdown(waitForJobsToComplete: false).ConfigureAwait(false);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Три_сутності_пишуть_у_той_самий_документ_лишається_одна_задача_перерахунку()
    {
        var (db, chain) = await OpenChainAsync();
        await using var _ = db;

        var entities = new List<int>();
        for (var row = 0; row < 3; row++)
        {
            entities.Add(await ArrangeEntityAsync(db, chain, [chain.RowIds[row]]));
        }

        var (jobs, quartz) = await QuartzAsync();
        var job = new MaterializeCollectedDataJob(
            db, RecordingPatcher(), Substitute.For<ICoverageJournal>(), Actor(db), Trigger(db, jobs));

        foreach (var entity in entities)
        {
            await job.ExecuteAsync(MaterializeFor(entity, chain), Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: у `CalculationTrigger` поставити задачу через
        // `EnqueueAsync` (без цілі) замість `EnqueueExclusiveAsync` → три живі
        // перерахунки одного документа за один період, що навперегін
        // перемикають актуальність результатів; тут три ключі замість одного.
        var keys = await KeysAsync(quartz);
        var key = Assert.Single(keys);
        Assert.StartsWith(
            $"IRecalculationJob~{RecalculateDocumentHandler.TargetOf(chain.DocumentId, chain.PeriodKey)}~",
            key,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Прогін_із_кількома_записаними_полями_ставить_рівно_одну_задачу()
    {
        var (db, chain) = await OpenChainAsync();
        await using var _ = db;

        // Одна сутність, три поля → три записані комірки одного документа.
        var entity = await ArrangeEntityAsync(db, chain, [chain.RowIds[0], chain.RowIds[1], chain.RowIds[2]]);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var job = new MaterializeCollectedDataJob(
            db, RecordingPatcher(), Substitute.For<ICoverageJournal>(), Actor(db), Trigger(db, jobs));

        await job.ExecuteAsync(MaterializeFor(entity, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: викликати тригер на кожне записане значення (цикл
        // по полях чи сутностях) → три постановки з тією самою ціллю, і кожна
        // наступна витісняє вже запущений перерахунок — гарячий документ так
        // і не дораховується. Тут `Received(1)` червоний.
        await jobs.Received(1).EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(chain.DocumentId, chain.PeriodKey),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IRecalculationJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Нічого_не_записано_нуль_задач()
    {
        var (db, chain) = await OpenChainAsync();
        await using var _ = db;

        var entity = await ArrangeEntityAsync(db, chain, [chain.RowIds[0]]);

        // Патчер нічого не застосував (усе — правки людини).
        var patcher = Substitute.For<ICellPatcher>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(new IntegrationWriteResult(0, ["R1:C2"]));

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var job = new MaterializeCollectedDataJob(db, patcher, Substitute.For<ICoverageJournal>(), Actor(db), Trigger(db, jobs));

        await job.ExecuteAsync(MaterializeFor(entity, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати умову `Applied > 0` → перерахунок на
        // кожен прогін збору, навіть коли в документі не змінилося нічого.
        Assert.Empty(jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Закритий_період_нуль_задач()
    {
        var (db, chain) = await OpenChainAsync();
        await using var _ = db;

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Closed, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = await ArrangeEntityAsync(db, chain, [chain.RowIds[0]]);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var trigger = Trigger(db, jobs);
        var job = new MaterializeCollectedDataJob(
            db, RecordingPatcher(), Substitute.For<ICoverageJournal>(), Actor(db), trigger);

        await job.ExecuteAsync(MaterializeFor(entity, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        Assert.Empty(jobs.ReceivedCalls());

        // І сам тригер на справжньому сховищі періодів: навіть прямий виклик
        // (як із вікна рядка, A1) у закритий період задачі не ставить.
        Assert.Null(await trigger.RequestAsync(chain.DocumentId, chain.PeriodKey, CancellationToken.None));
        Assert.Empty(jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "HSE301-A4")]
    public void Контейнер_реєструє_тригер()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати рядок `// HSE301:A4` у `DependencyInjection`
        // → параметр задачі необов'язковий, контейнер передає `null`, і
        // автоперерахунок мовчки не ставиться ніколи. Тут — червоний.
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns("Server=unused;Database=unused");
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        var registration = Assert.Single(services, d => d.ServiceType == typeof(ICalculationTrigger));
        Assert.Equal(typeof(CalculationTrigger), registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    private async Task<(EcrDbContext Db, TestDocument Chain)> OpenChainAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        return (db, chain);
    }

    private async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> QuartzAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            // Своє ім'я: спільний інстанс приніс би задачі сусіднього тесту.
            ["quartz.scheduler.instanceName"] = $"ecr-a4-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        var scheduler = await factory.GetScheduler().ConfigureAwait(false);
        schedulers.Add(scheduler);

        return (new QuartzJobScheduler(factory), scheduler);
    }

    private static async Task<List<string>> KeysAsync(IScheduler scheduler)
    {
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()).ConfigureAwait(false);

        return [.. keys.Select(k => k.Name)];
    }

    /// <summary>Тригер на справжніх сховищах періодів і робочого процесу — як у контейнері.</summary>
    private static CalculationTrigger Trigger(EcrDbContext db, IBackgroundJobScheduler jobs)
        => new(jobs, new PeriodStore(db), new WorkflowStore(db));

    /// <summary>
    /// Сутність із матеріалізованими мапінгами: поле на кожен рядок, числова
    /// колонка ланцюга, точки посеред січня.
    /// </summary>
    private static async Task<int> ArrangeEntityAsync(EcrDbContext db, TestDocument chain, IReadOnlyList<long> rowIds)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("A4 auto-recalc"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна НАВМИСНО (як у `MaterializeMappingScopeTests`): активна
        // сутність без завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var points = new List<SourceDataPoint>();
        for (var i = 0; i < rowIds.Count; i++)
        {
            var rowId = rowIds[i];
            var rowKey = await db.TableRows
                .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == rowId)
                .Select(r => r.RowKey)
                .SingleAsync();

            var field = $"F{i}_{tag}";
            var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
            map.SetMaterialization(rowKey.Value, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);

            points.Add(new SourceDataPoint(field, MidJanuary, 3m + i, null, null, "Good"));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);
        await store.UpsertRawPointsAsync(runId, entity.Id, points, CancellationToken.None);

        return entity.Id;
    }

    private static MaterializeTask MaterializeFor(int sourceEntityId, TestDocument chain)
        => new(
            sourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId,
            chain.PeriodKey.Value,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>Технічний автор задачі — як у контейнері (P0, <c>IntegrationActor</c>).</summary>
    private static IntegrationActor Actor(EcrDbContext db) => new(db, new Ecr.Application.Common.JobActorScope());

    /// <summary>Патчер, що «застосовує» все, що йому дали.</summary>
    private static ICellPatcher RecordingPatcher()
    {
        var patcher = Substitute.For<ICellPatcher>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(call => new IntegrationWriteResult(((IReadOnlyList<IntegrationCellValue>)call[3]).Count, []));
        return patcher;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
