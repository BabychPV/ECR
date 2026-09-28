// tests/Ecr.Adapters.Tests/CollectionRunnerSqlRobustnessTests.cs
using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Прогін збору не лишається «Running» і не падає на <c>UQ_RawDataPoint</c>
/// (аудит B4). Справжні збирач і сховище над SQL Server, підмінене лише джерело.
/// </summary>
/// <remarks>
/// ⛔ Саме над базою, а не над підміною <see cref="ICollectionStore"/>: усі три
/// шляхи до порушення унікальності живуть на стику пам'яті й
/// <c>datetime2(3)</c>/унікального індексу — підміна сховища зробила б їх
/// невидимими (так вони й прожили до аудиту поруч із зеленим
/// <c>CollectionRunnerTests</c>).
/// <para>
/// ⚠ Кожен тест заводить ВЛАСНІ джерело й сутність і в <c>finally</c> вимикає
/// сутність та прибирає свої сирі точки: активна сутність без завершеного збору
/// в спільній базі робить <c>SourcesHealthCheck</c> жовтим для наступних прогонів.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionRunnerSqlRobustnessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FromUtc = Now.AddDays(-1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Дві_точки_з_однаковою_міткою_в_батчі_не_валять_прогін_і_виграє_остання()
    {
        // ⛔ Шлях (б): PI допускає дві події з однаковою міткою. Раніше перша
        // потрапляла в `Add`, друга не бачила її в індексі — і `SaveChanges`
        // падав на `UQ_RawDataPoint`, відкидаючи ВЕСЬ батч, а прогін лишався
        // «Running» назавжди.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): прибрати `WHERE rn = 1` з
        // `UpsertRawPointsSql` (`CollectionStore`) → MERGE отримує два рядки на
        // один ключ і падає на 2601, тест червоний.
        var ts = FromUtc.AddHours(1);

        var outcome = await RunOnceAsync(path => [Point(path, ts, 1m), Point(path, ts, 2m)]);

        Assert.Null(outcome.Error);
        Assert.Equal("Succeeded", outcome.Run.Status);

        var stored = Assert.Single(outcome.Points);
        Assert.Equal(2m, stored.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Точки_що_збігаються_лише_після_обрізання_до_мілісекунд_не_валять_прогін()
    {
        // ⛔ Шлях (а): у пам'яті `…:00.1231` і `…:00.1234` — різні ключі, у
        // `datetime2(3)` — один (`.123`). Значення підібрані так, що й
        // відкидання, і округлення дають ту саму мілісекунду.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): `ToStoredPrecision` → `timestamp`,
        // `StoredTimestampFormat` → `.fffffff`, `Ts datetime2(7)` в `OPENJSON`
        // → дві точки лишаються різними ключами до вставки, і `datetime2(3)`
        // колонки зводить їх в один — 2601, прогін «Failed». Той самий
        // мутант валить міжбатчевий варіант у `CollectionStoreRawPointUpsertTests`.
        var ts = FromUtc.AddHours(1);

        var outcome = await RunOnceAsync(path =>
        [
            Point(path, ts.AddTicks(1_231_000), 1m),
            Point(path, ts.AddTicks(1_234_000), 2m),
        ]);

        Assert.Null(outcome.Error);
        Assert.Equal("Succeeded", outcome.Run.Status);

        var stored = Assert.Single(outcome.Points);
        Assert.Equal(ts.AddMilliseconds(123), stored.Timestamp);
        Assert.Equal(2m, stored.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Непередбачений_виняток_закриває_прогін_як_Failed_із_причиною_і_покриттям_прочитаного()
    {
        // ⛔ Будь-який виняток, крім правила й watchdog, раніше минав
        // `FinishRunAsync`. Тут його кидає прогрес ПІСЛЯ повністю прочитаного
        // першого інтервалу (прогалини наздоганяння): прогін мусить стати
        // «Failed» з назвою винятку, а цей інтервал — лишитися покритим.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): вимкнути фільтром `catch (Exception ex)
        // when (ex is not SourceAuthenticationException)` у
        // `CollectionRunner.RunAsync` → статус «Running».
        var progress = Substitute.For<IJobProgress>();
        progress.ReportAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("прогрес недоступний"));

        var outcome = await RunOnceAsync(_ => [], progress: progress);

        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.Equal("Failed", outcome.Run.Status);
        Assert.NotNull(outcome.Run.FinishedAt);
        Assert.Contains("InvalidOperationException", outcome.Run.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("прогрес недоступний", outcome.Run.ErrorMessage, StringComparison.Ordinal);

        // Покрито рівно перший інтервал — прогалину до запитаного діапазону.
        var coverage = Assert.Single(outcome.Coverage);
        Assert.Equal(FromUtc, coverage.CoveredTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Скасування_ззовні_закриває_прогін_і_кидає_скасування_далі()
    {
        // ⛔ Зовнішнє скасування (Quartz `Interrupt`) навмисно не ловить
        // watchdog-гілка — і раніше воно так само минало `FinishRunAsync`.
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): вимкнути фільтром `catch
        // (OperationCanceledException ex) when (ct.IsCancellationRequested)` →
        // скасування йде в загальну гілку і стає «Failed» (без обох гілок, як до
        // виправлення, — «Running»).
        using var cts = new CancellationTokenSource();

        var outcome = await RunOnceAsync(
            _ =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return [];
            },
            ct: cts.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(outcome.Error);
        Assert.Equal("Degraded", outcome.Run.Status);
        Assert.Contains("скасовано", outcome.Run.ErrorMessage, StringComparison.Ordinal);

        // Прогалина до запитаного діапазону прочитана до скасування — покрита;
        // запитаний діапазон — ні, він піде в наздоганяння.
        var coverage = Assert.Single(outcome.Coverage);
        Assert.Equal(FromUtc, coverage.CoveredTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Виняток_адаптера_лишається_відмовою_джерела_Degraded()
    {
        // ⚠ Страж, а не червоний тест: `InvalidOperationException` з АДАПТЕРА
        // гаситься в `CollectionRunner.ReadAsync` і до аудиту (він був зелений і
        // до виправлення). Тримає межу: загальний `catch` не мусить перетворити
        // звичайну відмову джерела на «Failed».
        var outcome = await RunOnceAsync(_ => throw new InvalidOperationException("адаптер зламався"));

        Assert.Null(outcome.Error);
        Assert.Equal("Degraded", outcome.Run.Status);

        // Покрита лише прогалина наздоганяння; запитаний діапазон, на якому
        // адаптер упав, — ні.
        var coverage = Assert.Single(outcome.Coverage);
        Assert.Equal(FromUtc, coverage.CoveredTo);
    }

    [Fact(Timeout = 120_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B4")]
    public async Task Два_паралельні_збори_однієї_сутності_завершуються_обидва_без_дублікатів()
    {
        // ⛔ Шлях (в): ручний «зібрати зараз» поруч із плановим. Обидва джерела
        // віддають ТІ САМІ точки й чекають одне одного перед поверненням, тож
        // обидва сховища пишуть одночасно. Раніше: обидва читали «наявних
        // немає», обидва вставляли — другий падав на `UQ_RawDataPoint` і лишав
        // свій прогін «Running».
        // МУТАЦІЙНИЙ ДОКАЗ (прогнано): прибрати `sp_getapplock` з
        // `UpsertRawPointsSql` → два MERGE без локу обидва не бачать рядків
        // одне одного, другий падає на 2601 — 3 червоні з 3 прогонів; з локом —
        // 5 зелених із 5. Гонка за природою ймовірнісна: зелений прогін без
        // локу був би можливий, червоний з локом — ні.
        const int PointCount = 2_000;

        await using var setupDb = sql.CreateContext();
        var stand = await ArrangeAsync(setupDb);

        try
        {
            var arrived = 0;
            var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<CollectionResult> ReadAsync(CollectionRequest request)
            {
                if (request.FromUtc != FromUtc)
                {
                    return new CollectionResult([], [], null);
                }

                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothArrived.SetResult();
                }

                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

                return new CollectionResult(
                    [.. Enumerable.Range(0, PointCount).Select(i => Point(stand.Path, FromUtc.AddSeconds(i), i))],
                    [],
                    null);
            }

            async Task<Exception?> RunAsync()
            {
                await using var db = sql.CreateContext();
                var runner = Runner(db, Source(r => ReadAsync(r)));

                try
                {
                    await runner.RunAsync(
                        stand.SourceEntityId, FromUtc, Now, Substitute.For<IJobProgress>(), CancellationToken.None);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            var errors = await Task.WhenAll(Task.Run(RunAsync), Task.Run(RunAsync));

            Assert.All(errors, e => Assert.True(e is null, e?.ToString()));

            await using var check = sql.CreateContext();
            var runs = await check.CollectionRuns.AsNoTracking()
                .Where(r => r.SourceEntityId == stand.SourceEntityId)
                .Select(r => r.Status)
                .ToListAsync();

            Assert.Equal(["Succeeded", "Succeeded"], runs);
            Assert.Equal(
                PointCount,
                await check.RawDataPoints.CountAsync(p => p.SourceEntityId == stand.SourceEntityId));
        }
        finally
        {
            await CleanUpAsync(stand.SourceEntityId);
        }
    }

    /// <summary>Що заведено для тесту.</summary>
    private sealed record Stand(int SourceEntityId, string Path);

    /// <summary>Підсумок одного прогону: виняток (якщо був) і те, що лишилося в базі.</summary>
    private sealed record Outcome(
        Exception? Error,
        Domain.Entities.Integration.CollectionRun Run,
        IReadOnlyList<RawDataPoint> Points,
        IReadOnlyList<Domain.Entities.Integration.CollectionCoverage> Coverage);

    /// <summary>
    /// Один прогін над власною сутністю: джерело на ЗАПИТАНОМУ інтервалі віддає
    /// <paramref name="points"/>, на прогалині наздоганяння — порожньо.
    /// </summary>
    private async Task<Outcome> RunOnceAsync(
        Func<string, IReadOnlyList<SourceDataPoint>> points,
        IJobProgress? progress = null,
        CancellationToken ct = default)
    {
        await using var db = sql.CreateContext();
        var stand = await ArrangeAsync(db);

        try
        {
            var source = Source(request => Task.FromResult(
                request.FromUtc == FromUtc
                    ? new CollectionResult(points(stand.Path), [], null)
                    : new CollectionResult([], [], null)));

            Exception? error = null;
            try
            {
                await Runner(db, source).RunAsync(
                    stand.SourceEntityId, FromUtc, Now, progress ?? Substitute.For<IJobProgress>(), ct);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            await using var check = sql.CreateContext();

            var run = await check.CollectionRuns.AsNoTracking()
                .SingleAsync(r => r.SourceEntityId == stand.SourceEntityId, CancellationToken.None);

            var stored = await check.RawDataPoints.AsNoTracking()
                .Where(p => p.SourceEntityId == stand.SourceEntityId)
                .OrderBy(p => p.Timestamp)
                .ToListAsync(CancellationToken.None);

            var coverage = await check.CollectionCoverages.AsNoTracking()
                .Where(c => c.SourceEntityId == stand.SourceEntityId)
                .OrderBy(c => c.CoveredFrom)
                .ToListAsync(CancellationToken.None);

            return new Outcome(error, run, stored, coverage);
        }
        finally
        {
            await CleanUpAsync(stand.SourceEntityId);
        }
    }

    private static CollectionRunner Runner(EcrDbContext db, IExternalDataSource source)
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var store = new CollectionStore(db, new TestClock(Now));

        return new CollectionRunner(
            [source],
            new SourceUnitConverter(new UnitConverter(), catalog),
            new CatchUpPlanner(store, new TestClock(Now)),
            store);
    }

    private static IExternalDataSource Source(Func<CollectionRequest, Task<CollectionResult>> read)
    {
        var source = Substitute.For<IExternalDataSource>();
        source.Transport.Returns(ExternalTransport.PiWebApi);
        source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => read(call.ArgAt<CollectionRequest>(0)));

        return source;
    }

    private static SourceDataPoint Point(string path, DateTime timestamp, decimal value)
        => new(path, timestamp, value, null, null, "Good");

    /// <summary>Власні джерело й АКТИВНА сутність (збирач вимкнених не збирає).</summary>
    private static async Task<Stand> ArrangeAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "B4" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        // Без мапінгів збирач читає саму сутність: шлях — її код.
        return new Stand(entity.Id, entity.Code);
    }

    /// <summary>Вимикає сутність і прибирає її сирі точки зі спільної бази.</summary>
    private async Task CleanUpAsync(int sourceEntityId)
    {
        await using var db = sql.CreateContext();

        await db.RawDataPoints
            .Where(p => p.SourceEntityId == sourceEntityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.SourceEntities
            .Where(e => e.Id == sourceEntityId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), CancellationToken.None);
    }
}
