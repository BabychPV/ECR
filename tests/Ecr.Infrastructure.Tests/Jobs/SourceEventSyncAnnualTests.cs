// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncAnnualTests.cs
using System.Globalization;
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-464: синк подій PI (<see cref="SourceEventSyncJob"/>) на РІЧНОМУ періоді, на реальному SQL Server і справжньому
/// записі рядків: повна звірка всього року, поданий річний аркуш і негайний синк після повернення, помісячне читання,
/// стеля сторінок, підтвердження масового видалення, подія, відкрита на момент закриття року, переїзд початку через
/// межу року, вікно звірки лише за періодами документа.
/// </summary>
/// <remarks>
/// Проєкт у <c>Asia/Atyrau</c> (+05:00), річний період 2026 (<c>202601</c>, 1 січня — 31 грудня), годинник —
/// 31.10.2026. Підроблене лише джерело подій, годинник і черга (для перевірки негайного синку).
/// </remarks>
[Collection("SqlServer")]
public sealed partial class SourceEventSyncAnnualTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");
    private static readonly DateTime YearStartUtc = Period.UtcBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Atyrau).StartUtc;

    private static readonly DateTime Feb = new(2026, 2, 10, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime May = new(2026, 5, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jul = new(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Oct = new(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Без явного вікна синк читає ВЕСЬ відкритий рік: правка події за п'ять місяців до «зараз» пишеться в рядок, а
    /// видалена в PI лютнева подія видаляється разом із рядком.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Річна_повна_звірка_правка_і_видалення_за_пʼять_місяців_підхоплені()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("FEB", Feb, 1m), Ev("MAY", May, 1m), Ev("OCT", Oct, 1m));
        await RunAsync(stand, source);

        Assert.Equal(YearStartUtc, source.LastQuery!.FromUtc);
        Assert.Equal(["EF-FEB", "EF-MAY", "EF-OCT"], await RowKeysAsync(stand));

        source.Result = new SourceEventResult([Ev("MAY", May, 2m), Ev("OCT", Oct, 1m)], false, null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-MAY", "EF-OCT"], await RowKeysAsync(stand));
        Assert.Equal(2m, await VolumeAsync(stand, "EF-MAY"));
        Assert.Equal(1, await RemovedCountAsync(stand));
    }

    /// <summary>
    /// Поданий річний аркуш: ні запису, ні видалення, і ОДНА зведена подія журналу на період за прогін (раніше —
    /// по події на кожну подію року). Після повернення аркуша планувальник одразу ставить синк, і той дописує все.
    /// </summary>
    /// <remarks>
    /// Мутації: прибрати гілку <c>SheetLocked</c> планувальника — з'являються <c>eventWriteFailed</c> на кожну подію;
    /// прибрати виклик <c>SourceEventSyncTrigger</c> з <c>MaterializationScheduler</c> — немає постановки синку.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Поданий_річний_аркуш_без_шуму_а_після_повернення_негайний_синк_пише_й_видаляє()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("FEB", Feb, 1m), Ev("MAY", May, 1m));
        await RunAsync(stand, source);
        await SubmitSheetAsync(stand);

        source.Result = new SourceEventResult([Ev("MAY", May, 5m), Ev("JUL", Jul, 1m)], false, null);
        await RunAsync(stand, source);
        await RunAsync(stand, source);

        Assert.Equal(["EF-FEB", "EF-MAY"], await RowKeysAsync(stand));
        Assert.Equal(1m, await VolumeAsync(stand, "EF-MAY"));
        Assert.Equal(0, await CoverageCountAsync(stand, "eventWriteFailed"));
        Assert.Equal(0, await CoverageCountAsync(stand, "eventRemovalSheetSubmitted"));
        Assert.Equal(2, await CoverageCountAsync(stand, "coverageEvents.eventSheetLocked"));
        Assert.Contains("\"writes\":\"2\"", await LastCoverageAsync(stand, "coverageEvents.eventSheetLocked"), StringComparison.Ordinal);
        Assert.Contains("\"removals\":\"1\"", await LastCoverageAsync(stand, "coverageEvents.eventSheetLocked"), StringComparison.Ordinal);

        // Повернення: аркуш знову редагований — планувальник ставить синк подій цього аркуша (без вікна: повна звірка).
        await ExecuteAsync($"DELETE FROM wf.ApprovalState WHERE DocumentId = {stand.DocumentId}");
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        await using (var db = sql.CreateContext())
        {
            await new MaterializationScheduler(db, jobs).EnqueueAfterSheetReturnAsync(stand.DocumentId, stand.SheetDefId, 202601, default);
            await new MaterializationScheduler(db, jobs).EnqueueAfterSheetReturnAsync(stand.DocumentId, stand.SheetDefId + 1_000_000, 202601, default);
        }

        await jobs.Received(1).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            SourceEventSyncTarget.Of(stand.EntityId),
            Arg.Is<object?>(p => p is SourceEventSyncRequest && ((SourceEventSyncRequest)p).SourceEntityId == stand.EntityId
                                 && ((SourceEventSyncRequest)p).FromUtc == null && !((SourceEventSyncRequest)p).ConfirmRemoval),
            Arg.Any<CancellationToken>(),
            Arg.Any<int?>());

        await RunAsync(stand, source);

        Assert.Equal(["EF-JUL", "EF-MAY"], await RowKeysAsync(stand));
        Assert.Equal(5m, await VolumeAsync(stand, "EF-MAY"));
        Assert.Equal(1, await RemovedCountAsync(stand));
    }

    /// <summary>
    /// Відкриття / перевідкриття періоду ставить негайний синк сутностям із мапінгом подій у документи проєкту; закритий
    /// після переходу період — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Відкриття_періоду_ставить_негайний_синк_лише_для_відкритого_періоду()
    {
        await using var stand = await ArrangeAsync();
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await using (var db = sql.CreateContext())
        {
            await new MaterializationScheduler(db, jobs).EnqueueAfterTransitionAsync(stand.ProjectId, [202601], default);
        }

        await jobs.Received(1).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            SourceEventSyncTarget.Of(stand.EntityId), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        jobs.ClearReceivedCalls();
        await ExecuteAsync($"UPDATE doc.Period SET State = {(byte)PeriodState.Closed} WHERE ProjectId = {stand.ProjectId}");
        await using (var db = sql.CreateContext())
        {
            await new MaterializationScheduler(db, jobs).EnqueueAfterTransitionAsync(stand.ProjectId, [202601], default);
        }

        await jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<ISourceEventSyncJob>(default!, default, default);
    }

    /// <summary>
    /// Помісячне читання (<c>PiSqlClient:EventQueryChunk = Month</c>): подія через межу місяця й незакрита подія
    /// приходять у кількох шматках — один рядок і один зв'язок; обрізаний шматок вимикає Missing/видалення всього прогону.
    /// </summary>
    /// <remarks>Мутація: прибрати злиття за <c>EventId</c> у <c>ReadEventWindowAsync</c> — дублі в плані (зв'язків 4).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Помісячне_читання_зливає_подію_через_межу_і_незакриту_а_обрізаний_шматок_вимикає_видалення()
    {
        await using var stand = await ArrangeAsync();
        var span = Ev("SPAN", new DateTime(2026, 3, 31, 22, 0, 0, DateTimeKind.Utc), 1m, hours: 5);
        var open = Ev("OPEN", Feb, null);
        var april = Ev("APR", new DateTime(2026, 4, 10, 6, 0, 0, DateTimeKind.Utc), 1m);
        List<SourceEvent> all = [span, open, april];
        DateTime? truncatedChunk = null;
        var source = new FakeEventSource
        {
            Pages = q => new SourceEventResult(
                [.. all.Where(e => e.StartUtc < q.ToUtc && (e.EndUtc ?? DateTime.MaxValue) > q.FromUtc)],
                truncatedChunk is { } t && q.FromUtc <= t && t < q.ToUtc,
                null),
        };
        var monthly = new SourceEventSyncOptions(EventQueryChunk: SourceEventQueryChunk.Month);

        await RunAsync(stand, source, options: monthly);

        // Грудень (хвіст доби 31.12 UTC) + січень…жовтень = 11 шматків.
        Assert.Equal(11, source.Queries.Count);
        Assert.Equal(["EF-APR", "EF-SPAN"], await RowKeysAsync(stand));
        Assert.Equal(["APR", "OPEN", "SPAN"], (await LinksAsync(stand)).Select(l => l.SourceEventId).Order(StringComparer.Ordinal));

        // APR зник із PI, але червневий шматок обрізаний — прогін неповний: ні Missing, ні видалення.
        all = [span, open];
        truncatedChunk = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        await RunAsync(stand, source, options: monthly);

        Assert.Equal(["EF-APR", "EF-SPAN"], await RowKeysAsync(stand));
        Assert.Equal(SourceEventLinkStatus.Synced, (await LinksAsync(stand)).Single(l => l.SourceEventId == "APR").Status);
        Assert.Equal(0, await RemovedCountAsync(stand));

        // Усі шматки повні — тепер видаляється.
        truncatedChunk = null;
        await RunAsync(stand, source, options: monthly);
        Assert.Equal(["EF-SPAN"], await RowKeysAsync(stand));
    }

    /// <summary>
    /// Стеля сторінок налаштовується (<c>Collection:MaxEventPages</c>), а подія журналу називає стелю і ключ.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Стеля_сторінок_налаштовується_і_подія_журналу_називає_стелю()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource
        {
            Pages = q => new SourceEventResult([Ev($"E{q.FromUtc.Ticks}", q.FromUtc.AddMinutes(1), 1m)], true, null),
        };

        await RunAsync(stand, source, from: Feb, to: Feb.AddDays(1), options: new SourceEventSyncOptions(MaxEventPages: 2));

        Assert.Equal(2, (await RowKeysAsync(stand)).Count);
        var details = await LastCoverageAsync(stand, "coverageEvents.eventsTruncatedCap");
        Assert.Contains("\"maxPages\":\"2\"", details, StringComparison.Ordinal);
        Assert.Contains("\"cap\":\"4000\"", details, StringComparison.Ordinal);
    }

    /// <summary>
    /// Масове видалення понад ліміт блокується, підсумок задачі несе лічильники (для кнопки «Підтвердити видалення»),
    /// а підтверджений прогін видаляє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Масове_видалення_блокується_з_лічильниками_а_підтвердження_видаляє()
    {
        await using var stand = await ArrangeAsync();
        var events = Enumerable.Range(0, 12).Select(i => Ev($"E{i:00}", Feb.AddDays(i * 7), 1m)).ToArray();
        var source = new FakeEventSource(events);
        await RunAsync(stand, source);
        Assert.Equal(12, (await RowKeysAsync(stand)).Count);

        source.Result = new SourceEventResult([events[0]], false, null);
        var progress = new CapturingProgress();
        await RunAsync(stand, source, progress: progress);

        Assert.Equal(12, (await RowKeysAsync(stand)).Count);
        Assert.Equal(new SourceEventRemovalBlock(1, 11, 10, 12), SourceEventSyncOutcome.RemovalBlockOf("Succeeded", progress.Last));

        var confirmed = new CapturingProgress();
        await RunAsync(stand, source, confirm: true, progress: confirmed);

        Assert.Equal(["EF-E00"], await RowKeysAsync(stand));
        Assert.Equal(11, await RemovedCountAsync(stand));
        Assert.Null(SourceEventSyncOutcome.RemovalBlockOf("Succeeded", confirmed.Last));
    }

    /// <summary>Подія, що ще триває, коли її рік уже закрито, — рядка не буде; людині видно одну подію журналу.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Подія_відкрита_на_момент_закриття_року_видна_в_журналі()
    {
        await using var stand = await ArrangeAsync();
        await AddPeriodAsync(stand, 2025, PeriodState.Closed, withInstance: false);

        await RunAsync(stand, new FakeEventSource(Ev("LATE", new DateTime(2025, 12, 20, 6, 0, 0, DateTimeKind.Utc), null)));

        Assert.Empty(await RowKeysAsync(stand));
        Assert.Equal(SourceEventLinkStatus.Open, Assert.Single(await LinksAsync(stand)).Status);
        Assert.Equal(1, await CoverageCountAsync(stand, "coverageEvents.eventOpenAtPeriodClose", periodKey: 202501));
        Assert.Contains("\"count\":\"1\"", await LastCoverageAsync(stand, "coverageEvents.eventOpenAtPeriodClose"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Початок прив'язаної події переїхав через межу року: рядок лишається (без автоматичного переносу), зв'язок —
    /// <c>PeriodChanged</c>, подія журналу — один раз, у момент переходу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Початок_переїхав_через_межу_року_рядок_лишається_і_одна_подія_журналу()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", new DateTime(2026, 12, 31, 10, 0, 0, DateTimeKind.Utc), 1m));
        await RunAsync(stand, source, from: YearStartUtc, to: new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        source.Result = new SourceEventResult([Ev("E1", new DateTime(2026, 12, 31, 20, 0, 0, DateTimeKind.Utc), 1m)], false, null);
        await RunAsync(stand, source, from: YearStartUtc, to: new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        await RunAsync(stand, source, from: YearStartUtc, to: new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["EF-E1"], await RowKeysAsync(stand));
        Assert.Equal(SourceEventLinkStatus.PeriodChanged, Assert.Single(await LinksAsync(stand)).Status);
        Assert.Equal(1, await CoverageCountAsync(stand, "coverageEvents.eventPeriodChanged", periodKey: 202601));
        Assert.Contains("\"to\":\"—\"", await LastCoverageAsync(stand, "coverageEvents.eventPeriodChanged"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Вікно повної звірки — лише Open/Grace-періоди, де документ мапінгу має екземпляр таблиці: забутий відкритим
    /// період 2023 без екземпляра вікна не розтягує; з екземпляром — розтягує (запис туди законний).
    /// </summary>
    /// <remarks>Мутація: повернути вибір усіх відкритих періодів проєкту — перший прогін читає з 2023.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-464")]
    public async Task Вікно_звірки_лише_за_періодами_з_екземпляром_таблиці_документа()
    {
        await using var stand = await ArrangeAsync();
        await AddPeriodAsync(stand, 2023, PeriodState.Open, withInstance: false);
        var source = new FakeEventSource(Ev("MAY", May, 1m));

        await RunAsync(stand, source);
        Assert.Equal(YearStartUtc, source.LastQuery!.FromUtc);

        await AddInstanceAsync(stand, 202301);
        await RunAsync(stand, source);
        Assert.Equal(Period.UtcBounds(new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31), Atyrau).StartUtc, source.LastQuery!.FromUtc);
    }

    // ── Стенд ────────────────────────────────────────────────────────────────

    private sealed record Stand(
        ServiceProvider Provider,
        Func<ValueTask> Cleanup,
        int ProjectId,
        int TableDefId,
        int SheetDefId,
        int EntityId,
        int MapId,
        long DocumentId,
        long InstanceId,
        int VolumeColumn) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup();
    }

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 2, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        // Річний період: 202601 = увесь 2026 рік.
        await ExecuteAsync(
            $"UPDATE doc.Period SET PeriodEnd = '2026-12-31' WHERE ProjectId = {chain.ProjectId} AND PeriodKey = 202601");

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var year = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == 202601);
        year.TransitionTo(PeriodState.Open, Now);

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var dataSource = new DataSource(
            EcrCode.Create($"D464SRC{tag}"), Name("D-464"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"FlareYear{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var ids = chain.ColumnDefIds;
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == chain.TableDefId);
        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == chain.TableDefId).ToDictionaryAsync(c => c.Id);

        var map = SourceEventMap.Create(
            entity.Id,
            chain.DocumentId,
            table,
            [
                new(columns[start.Id], SourceEventMap.StartAttribute),
                new(columns[end.Id], SourceEventMap.EndAttribute),
                new(columns[ids[0]], SourceEventMap.NameAttribute),
                new(columns[ids[1]], "Volume"),
            ],
            SourceEventVolumeMode.EventAttribute);
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);

        var provider = BuildProvider();

        return new Stand(
            provider,
            async () =>
            {
                // Активна сутність у спільній базі фарбувала б SourcesHealthCheck інших тестів.
                await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entity.Id}");
                await provider.DisposeAsync();
            },
            chain.ProjectId,
            chain.TableDefId,
            chain.SheetDefId,
            entity.Id,
            map.Id,
            chain.DocumentId,
            chain.TableInstanceId,
            ids[1]);
    }

    private async Task AddPeriodAsync(Stand stand, int year, PeriodState state, bool withInstance)
    {
        await using (var db = sql.CreateContext())
        {
            var period = new Period(stand.ProjectId, new PeriodKey((year * 100) + 1), 1, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
            period.AdvanceTo(state, Now);
            db.Periods.Add(period);
            await db.SaveChangesAsync();
        }

        if (withInstance)
        {
            await AddInstanceAsync(stand, (year * 100) + 1);
        }
    }

    private async Task AddInstanceAsync(Stand stand, int periodKey)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        await using var db = sql.CreateContext();
        db.TableInstances.Add(new TableInstance(new PeriodKey(periodKey), id, stand.DocumentId, stand.TableDefId, Now));
        await db.SaveChangesAsync();
    }

    private async Task SubmitSheetAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        var userId = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var state = new ApprovalState(stand.DocumentId, stand.SheetDefId, 202601);
        state.Submit(userId, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private async Task RunAsync(
        Stand stand,
        FakeEventSource source,
        DateTime? from = null,
        DateTime? to = null,
        bool confirm = false,
        SourceEventSyncOptions? options = null,
        IJobProgress? progress = null)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var job = new SourceEventSyncJob(
            services.GetRequiredService<EcrDbContext>(),
            [source],
            services.GetRequiredService<ICellPatcher>(),
            services.GetRequiredService<ICoverageJournal>(),
            services.GetRequiredService<IntegrationActor>(),
            clock,
            options: options);

        // ⚠ Без вікна — повна звірка (як розклад і негайний синк); `to` — «зараз» годинника.
        await job.ExecuteAsync(
            new SourceEventSyncRequest(stand.EntityId, from, to ?? Now, confirm),
            progress ?? Substitute.For<IJobProgress>(),
            CancellationToken.None);
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Черга перерахунку — підробка: Quartz тримає планувальник у глобальному репозиторії процесу.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    private static SourceEvent Ev(string id, DateTime start, decimal? volume, int hours = 1)
        => new(
            id,
            "FlareEvent",
            "Flaring " + id,
            start,
            volume is null ? null : start.AddHours(hours),
            null,
            null,
            null,
            volume is { } v ? [new SourceEventAttribute("Volume", SourceEventAttributeScope.Event, v, null, null)] : []);

    // ── Читання стану ────────────────────────────────────────────────────────

    private async Task<List<string>> RowKeysAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        return [.. (await db.TableRows.AsNoTracking()
                .Where(r => r.TableInstanceId == stand.InstanceId && !r.IsDeleted)
                .Select(r => r.RowKeyValue)
                .ToListAsync())
            .Where(k => k.StartsWith("EF-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    private async Task<List<SourceEventLink>> LinksAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        return await db.SourceEventLinks.AsNoTracking().Where(l => l.SourceEventMapId == stand.MapId).ToListAsync();
    }

    private async Task<decimal?> VolumeAsync(Stand stand, string rowKey)
        => (decimal?)await ScalarAsync(
            "SELECT v.ValueNumeric FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + $"WHERE r.TableInstanceId = {stand.InstanceId} AND r.RowKey = N'{rowKey}' AND v.ColumnDefId = {stand.VolumeColumn}");

    private async Task<int> RemovedCountAsync(Stand stand)
        => Convert.ToInt32(
            await ScalarAsync(
                "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'SourceEventRowRemoved' "
                + $"AND OldJson LIKE N'%\"mapId\":{stand.MapId},%'"),
            CultureInfo.InvariantCulture);

    private async Task<int> CoverageCountAsync(Stand stand, string detailsFragment, int? periodKey = null)
        => Convert.ToInt32(
            await ScalarAsync(
                $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} "
                + $"AND Details LIKE N'%{detailsFragment}%'"
                + (periodKey is { } key ? $" AND PeriodKey = {key}" : string.Empty)),
            CultureInfo.InvariantCulture);

    private async Task<string> LastCoverageAsync(Stand stand, string detailsFragment)
        => (string)(await ScalarAsync(
            $"SELECT TOP (1) Details FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} "
            + $"AND Details LIKE N'%{detailsFragment}%' ORDER BY Id DESC"))!;

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Канал прогресу, що пам'ятає останнє повідомлення (підсумок задачі).</summary>
    private sealed class CapturingProgress : IJobProgress
    {
        public string? Last { get; private set; }

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Last = message;
            return Task.CompletedTask;
        }
    }

    /// <summary>Джерело подій: задана відповідь або відповідь за запитом; пам'ятає всі запити.</summary>
    private sealed class FakeEventSource(params SourceEvent[] events) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public SourceEventResult Result { get; set; } = new(events, false, null);

        public Func<SourceEventQuery, SourceEventResult>? Pages { get; init; }

        public List<SourceEventQuery> Queries { get; } = [];

        public SourceEventQuery? LastQuery => Queries.Count == 0 ? null : Queries[^1];

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(Pages is null ? Result : Pages(query));
        }
    }

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
