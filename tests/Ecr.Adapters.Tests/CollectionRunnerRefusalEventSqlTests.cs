// tests/Ecr.Adapters.Tests/CollectionRunnerRefusalEventSqlTests.cs
using Ecr.Adapters.PiAf;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
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
/// Застряглий інтервал видно в журналі покриття (HSE301 Р3): адаптер відмовляє
/// віддати дані інтервалу з кодом каталогу — у <c>itg.CollectionCoverage</c>
/// лягає подія <see cref="CollectionCoverage.SourceDataRefused"/>, а інтервал
/// лишається прогалиною. Справжні збирач, сховище й читач журналу над SQL Server.
/// </summary>
/// <remarks>
/// ⛔ До фіксу runner писав лише покриті інтервали: відмова «дані нечитабельні»
/// лишала по собі тільки <c>Degraded</c> прогону, а панель подій покриття
/// (<c>CoverageEventsPanel</c>) не показувала нічого — наздоганяння стукало в
/// той самий інтервал щогодини мовчки.
/// <para>
/// ⚠ Кожен тест заводить ВЛАСНІ джерело й сутність і в <c>finally</c> прибирає
/// свої рядки: подія зі статусом у спільній базі потрапила б у зведення
/// <c>NotificationJob</c> чужих тестів.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionRunnerRefusalEventSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FromUtc = Now.AddDays(-1);

    private const string RefusalKey = "err.ECR-INT-0422.timestampUnreadable";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A7")]
    public async Task Відмова_даних_пише_подію_SourceDataRefused_без_дублів_і_інтервал_лишається_прогалиною()
    {
        // МУТАЦІЇ (прогнано):
        //  • код каталогу → 0503, як до фіксу (`Refused` без гілок EcrException/
        //    DomainException) → причина прогону не 0422, червоне (крок 1);
        //  • не писати подію (вимкнути гілку IsDataRefusal у
        //    CollectionRunner.ApplyPageAsync) → «подія є» червоне (крок 2);
        //  • рахувати відмову покриттям (додати відмовлений інтервал у `covered`)
        //    → інтервалів покриття два, червоне (крок 1);
        //  • без дедупу (`WHERE NOT EXISTS` вимкнено в CollectionStore) → після
        //    другого прогону подій дві, червоне (крок 4).
        await using var db = sql.CreateContext();
        var stand = await ArrangeAsync(db);

        try
        {
            var source = Source(request => request.FromUtc >= FromUtc
                ? throw TimestampUnreadable(request.SourcePath)
                : Task.FromResult(new CollectionResult([], [], null)));

            // 1. Перший прогін: Degraded із причиною 0422, покрито лише наздоганяння.
            await RunAsync(source, stand.SourceEntityId);

            await using (var check = sql.CreateContext())
            {
                var run = await check.CollectionRuns.AsNoTracking()
                    .SingleAsync(r => r.SourceEntityId == stand.SourceEntityId, CancellationToken.None);
                Assert.Equal("Degraded", run.Status);
                Assert.True(
                    CollectionRunnerMessageEnvelopeTests.IsReason(run.ErrorMessage, RefusalKey, "ECR-INT-0422"),
                    run.ErrorMessage);

                var covered = await Covered(check, stand.SourceEntityId);
                var interval = Assert.Single(covered);
                Assert.Equal(FromUtc, interval.CoveredTo);

                // 2. Подія зі статусом і конвертом причини.
                var events = await Events(check, stand.SourceEntityId);
                var refusal = Assert.Single(events);
                Assert.Equal(CollectionCoverage.SourceDataRefused, refusal.Status);
                Assert.Null(refusal.CollectionRunId);
                Assert.Null(refusal.PeriodKey);
                Assert.Equal(refusal.CoveredFrom, refusal.CoveredTo);

                var details = CollectionRunnerMessageEnvelopeTests.Decode(refusal.Details);
                Assert.Equal("coverageEvents.sourceDataRefused", details.Key);
                Assert.Equal(stand.Path, details.Params!["path"]);
                Assert.Equal(FromUtc, DateTime.Parse(details.Params["from"], null, System.Globalization.DateTimeStyles.RoundtripKind));
                Assert.Equal(Now, DateTime.Parse(details.Params["to"], null, System.Globalization.DateTimeStyles.RoundtripKind));
                Assert.Matches("^[0-9A-F]{16}$", details.Params[CollectionStore.DedupKeyParam]);

                Assert.Equal("jobs.collectionRunReason", details.Inner!.Key);
                Assert.Equal("ECR-INT-0422", details.Inner.Params!["code"]);
                Assert.Equal(RefusalKey, details.Inner.Inner!.Key);
                Assert.Equal(stand.Path, details.Inner.Inner.Params!["sourcePath"]);
                Assert.Equal("NULL", details.Inner.Inner.Params["valueType"]);

                // 3. Подію бачить стрічка подій покриття (панель /admin/sources).
                var page = await new CollectionRunReader(check).ListCoverageEventsAsync(
                    new CoverageEventFilter(null, stand.SourceEntityId, CollectionCoverage.SourceDataRefused, null),
                    new CursorRequest(50),
                    CancellationToken.None);
                Assert.Equal(refusal.Id, Assert.Single(page.Items).Id);
            }

            // 4. Повторний прогін тієї самої відмови не множить подій.
            await RunAsync(source, stand.SourceEntityId);

            await using (var check = sql.CreateContext())
            {
                Assert.Single(await Events(check, stand.SourceEntityId));

                // 5. ⛔ Подія — не покриття: наздоганяння й екран джерел і далі
                // бачать [FromUtc, Now) прогалиною.
                var store = new CollectionStore(check, new TestClock(Now));
                var islands = await store.GetCoverageAsync(
                    stand.SourceEntityId, FromUtc - CollectionRunner.CatchUpLookback, CancellationToken.None);
                var gaps = GapFinder.Find(islands, FromUtc - CollectionRunner.CatchUpLookback, Now);
                Assert.Contains(gaps, g => g.From <= FromUtc && g.To >= Now);

                var status = Assert.Single(
                    await store.ListSourceEntitiesAsync(CancellationToken.None),
                    s => s.Id == stand.SourceEntityId);
                Assert.Equal(FromUtc, status.OldestGap);
            }
        }
        finally
        {
            await CleanUpAsync(stand.SourceEntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A7")]
    public async Task Сирий_виняток_транспорту_лишається_0503_без_події_в_журналі_покриття()
    {
        // ⚠ Страж межі: недоступне джерело — затримка (ФВ-11.3), не застряглі дані.
        // Причина — як і до фіксу: `ECR-INT-0503` + `jobs.collectionSourceError`.
        await using var db = sql.CreateContext();
        var stand = await ArrangeAsync(db);

        try
        {
            var source = Source(request => request.FromUtc >= FromUtc
                ? throw new HttpRequestException("AF is down")
                : Task.FromResult(new CollectionResult([], [], null)));

            await RunAsync(source, stand.SourceEntityId);

            await using var check = sql.CreateContext();

            var run = await check.CollectionRuns.AsNoTracking()
                .SingleAsync(r => r.SourceEntityId == stand.SourceEntityId, CancellationToken.None);
            Assert.Equal("Degraded", run.Status);
            Assert.True(
                CollectionRunnerMessageEnvelopeTests.IsReason(run.ErrorMessage, "jobs.collectionSourceError", "ECR-INT-0503"),
                run.ErrorMessage);
            Assert.Equal(
                "AF is down",
                CollectionRunnerMessageEnvelopeTests.Decode(run.ErrorMessage).Inner!.Params!["detail"]);

            Assert.Empty(await Events(check, stand.SourceEntityId));
        }
        finally
        {
            await CleanUpAsync(stand.SourceEntityId);
        }
    }

    private static BusinessRuleException TimestampUnreadable(string path)
        => new(
            "ECR-INT-0422",
            $"Запит джерела для «{path}» повернув час типу NULL.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RefusalKey,
                ["dataSource"] = "PIAF",
                ["sourcePath"] = path,
                ["valueType"] = "NULL",
            });

    private async Task RunAsync(IExternalDataSource source, int sourceEntityId)
    {
        await using var db = sql.CreateContext();
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var store = new CollectionStore(db, new TestClock(Now));

        await new CollectionRunner(
                [source],
                new SourceUnitConverter(new UnitConverter(), catalog),
                new CatchUpPlanner(store, new TestClock(Now)),
                store)
            .RunAsync(sourceEntityId, FromUtc, Now, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    private static Task<List<CollectionCoverage>> Covered(EcrDbContext db, int sourceEntityId)
        => db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == sourceEntityId && c.Status == null)
            .OrderBy(c => c.CoveredFrom)
            .ToListAsync(CancellationToken.None);

    private static Task<List<CollectionCoverage>> Events(EcrDbContext db, int sourceEntityId)
        => db.CollectionCoverages.AsNoTracking()
            .Where(c => c.SourceEntityId == sourceEntityId && c.Status != null)
            .OrderBy(c => c.Id)
            .ToListAsync(CancellationToken.None);

    private static IExternalDataSource Source(Func<CollectionRequest, Task<CollectionResult>> read)
    {
        var source = Substitute.For<IExternalDataSource>();
        source.Transport.Returns(ExternalTransport.PiWebApi);
        source.ReadAsync(Arg.Any<CollectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => read(call.ArgAt<CollectionRequest>(0)));

        return source;
    }

    /// <summary>Що заведено для тесту.</summary>
    private sealed record Stand(int SourceEntityId, string Path);

    /// <summary>Власні джерело й АКТИВНА сутність (збирач вимкнених не збирає).</summary>
    private static async Task<Stand> ArrangeAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Ref{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "A7" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ref{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        // Без мапінгів збирач читає саму сутність: шлях — її код.
        return new Stand(entity.Id, entity.Code);
    }

    /// <summary>Прибирає журнал покриття, прогони й точки сутності та вимикає її.</summary>
    private async Task CleanUpAsync(int sourceEntityId)
    {
        await using var db = sql.CreateContext();

        await db.CollectionCoverages
            .Where(c => c.SourceEntityId == sourceEntityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.RawDataPoints
            .Where(p => p.SourceEntityId == sourceEntityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.CollectionRuns
            .Where(r => r.SourceEntityId == sourceEntityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.SourceEntities
            .Where(e => e.Id == sourceEntityId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsActive, false), CancellationToken.None);
    }
}
