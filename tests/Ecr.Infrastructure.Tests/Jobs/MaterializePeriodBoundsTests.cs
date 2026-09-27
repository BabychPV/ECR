// tests/Ecr.Infrastructure.Tests/Jobs/MaterializePeriodBoundsTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Згортка даних PI обмежена періодом екземпляра, а не вікном збору (D16-03).
/// </summary>
/// <remarks>
/// ⛔ Усе на справжньому SQL Server: межа періоду — це предикат у запиті до
/// <c>ext.RawDataPoint</c>, і перевірити її в пам'яті означало б перевірити
/// копію запиту, а не сам запит.
///
/// ⚠ Пояс проєкту будівника — <c>Asia/Almaty</c> (UTC+5 або +6 залежно від
/// редакції бази поясів), тож межі періоду 202601 в UTC — вечір 31 грудня і
/// вечір 31 січня. Саме тому точки поруч із межами поставлено за UTC-моментами
/// місцевої опівночі, а не за опівніччю UTC:
/// тест, що ставив би точки на опівніч UTC, не розрізнив би пояс проєкту й UTC.
///
/// ⚠ Значення точок — різні степені десяти: сума однозначно називає, ЯКІ саме
/// точки потрапили в згортку, і червоний тест одразу показує, яку межу зламано.
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializePeriodBoundsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Опівніч 1 січня 2026 в поясі проєкту будівника, у UTC.</summary>
    /// <remarks>
    /// ⚠ Рахується з бази поясів платформи, а не константою: Казахстан перейшов
    /// з UTC+6 на UTC+5 у 2024-му, і база поясів на різних машинах (Windows ICU,
    /// Linux tzdata) може мати будь-яку з двох редакцій. Константа 19:00 UTC
    /// виявилась хибною вже на першій машині (там +6). Незалежність від
    /// <c>Period.UtcBounds</c> збережено: тут пряме <c>ConvertTimeToUtc</c>.
    /// </remarks>
    private static readonly DateTime JanStartUtc = LocalMidnightUtc(new DateTime(2026, 1, 1));

    /// <summary>Опівніч 1 лютого 2026 в поясі проєкту, у UTC — кінець січня, виключно.</summary>
    private static readonly DateTime JanEndUtc = LocalMidnightUtc(new DateTime(2026, 2, 1));

    private static DateTime LocalMidnightUtc(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Almaty"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Згортка_бере_точки_за_напіввідкритими_межами_періоду_а_не_за_вікном_збору()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await OpenAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var stand = await ArrangeSourceAsync(db, chain, fields: 1);
        var field = stand.Fields[0];

        await PointsAsync(db, stand.SourceEntityId,
        [
            (field, JanStartUtc.AddSeconds(-1), 1m),      // 23:59:59 31 грудня місцевого — грудень
            (field, JanStartUtc, 10m),                    // 00:00:00 1 січня місцевого — січень (межа включно)
            (field, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), 100m),   // січень, ПОЗА вікном збору
            (field, new DateTime(2026, 1, 25, 0, 0, 0, DateTimeKind.Utc), 1000m),  // січень, у вікні
            (field, JanEndUtc.AddSeconds(-1), 10000m),    // 23:59:59 31 січня місцевого — січень
            (field, JanEndUtc, 100000m),                  // 00:00:00 1 лютого місцевого — лютий (межа виключно)
        ]);

        var patcher = RecordingPatcher();
        var job = new MaterializeCollectedDataJob(db, patcher, Substitute.For<ICoverageJournal>());

        // Вікно збору — останній тиждень січня. Воно НЕ має обмежувати згортку:
        // «Sum за місяць» — це сума за місяць, а не за останні 7 діб.
        await job.ExecuteAsync(
            new MaterializeTask(
                stand.SourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId,
                chain.PeriodKey.Value,
                new DateTime(2026, 1, 21, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 28, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        // ⛔ МУТАЦІЙНІ ДОКАЗИ (кожен перевірено відкатом рядка):
        // • фільтр за `task.FromUtc/ToUtc` замість меж періоду → 1000;
        // • `>= StartUtc` → `> StartUtc` → 11100 (губиться точка рівно на початку);
        // • `< EndUtc` → `<= EndUtc` → 111110 (домішується перша точка лютого);
        // • межі в UTC замість поясу проєкту → інша сума (точки на ±5 год).
        var value = Assert.Single(Written(patcher)[chain.TableInstanceId]);
        Assert.Equal(stand.RowKeys[0], value.RowKey);
        Assert.Equal(11110m, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    [Trait("Requirement", "ФВ-12.8")]
    public async Task Кілька_періодів_з_одним_вікном_кожен_екземпляр_отримує_лише_свої_точки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        // Січень і лютий відкриті (січень міг би бути і в Grace — стан тут не
        // важливий); березень лишається `Scheduled` і вікна не перетинає.
        var feb = await AddPeriodInstanceAsync(db, chain, 202602, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        var mar = await AddPeriodInstanceAsync(db, chain, 202603, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        await OpenAsync(db, chain.ProjectId, 202601);
        await OpenAsync(db, chain.ProjectId, 202602);

        var stand = await ArrangeSourceAsync(db, chain, fields: 1);
        var field = stand.Fields[0];

        await PointsAsync(db, stand.SourceEntityId,
        [
            (field, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), 1m),   // січень, поза вікном
            (field, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc), 2m),   // січень, у вікні
            (field, new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc), 10m),   // лютий, у вікні
            (field, new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc), 20m),  // лютий, у вікні
            (field, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), 500m), // березень
        ]);

        // Збір за вікном, що накриває кінець січня й більшу частину лютого.
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var collection = new CollectionJob(
            Substitute.For<ICollectionRunner>(),
            db,
            jobs,
            new TestClock(Now),
            Substitute.For<INotificationOutbox>(),
            new OutboxDispatcher(db, new TestClock(Now), Substitute.For<INotificationSender>()));

        await collection.ExecuteAsync(
            new CollectionJobRequest(
                stand.SourceEntityId,
                new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 2, 25, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        var tasks = jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueAsync))
            .Select(c => (MaterializeTask)c.GetArguments()[0]!)
            .ToList();

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати фільтр перетину періоду з вікном у
        // `CollectionJob.EnqueueMaterializationAsync` → задача з'являється й
        // для березня (`Scheduled`), і той пише в журнал покриття хибне
        // «пізній збір лишається сирим».
        Assert.Equal(
            [chain.TableInstanceId, feb],
            tasks.Select(t => t.TableInstanceId).Order());
        Assert.DoesNotContain(mar, tasks.Select(t => t.TableInstanceId));

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage);

        foreach (var task in tasks)
        {
            await job.ExecuteAsync(task, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: згортка за `task.FromUtc/ToUtc` → обидва
        // екземпляри отримують ОДНЕ число 32 (2 + 10 + 20) — точки вікна, а не
        // свого періоду.
        var written = Written(patcher);
        Assert.Equal(3m, Assert.Single(written[chain.TableInstanceId]).Value);
        Assert.Equal(30m, Assert.Single(written[feb]).Value);

        Assert.Empty(coverage.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Стеля_точок_на_поле_не_обрізає_хвіст_мовчки_а_пише_рядок_у_журнал_покриття()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await OpenAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var stand = await ArrangeSourceAsync(db, chain, fields: 2);
        var (over, atCeiling) = (stand.Fields[0], stand.Fields[1]);

        // Поле `over` має 4 точки при стелі 3 — його хвіст (4-та точка) і є
        // тим, що раніше зникало мовчки. Поле `atCeiling` має рівно 3 — межа
        // включно, значення пишеться. Разом 7 > 3: стеля на ПОЛЕ, не на сутність.
        var day = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        await PointsAsync(db, stand.SourceEntityId,
        [
            (over, day, 1m), (over, day.AddHours(1), 2m), (over, day.AddHours(2), 3m), (over, day.AddHours(3), 4m),
            (atCeiling, day, 10m), (atCeiling, day.AddHours(1), 20m), (atCeiling, day.AddHours(2), 30m),
        ]);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage) { PointCeilingPerField = 3 };

        await job.ExecuteAsync(
            new MaterializeTask(
                stand.SourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId,
                chain.PeriodKey.Value,
                new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку `series.Count > ceiling` → у комірку
        // `over` їде часткова сума 6 (1+2+3), журнал покриття порожній.
        var value = Assert.Single(Written(patcher)[chain.TableInstanceId]);
        Assert.Equal(stand.RowKeys[1], value.RowKey);
        Assert.Equal(60m, value.Value);

        var events = coverage.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICoverageJournal.RecordManyAsync))
            .SelectMany(c => (IReadOnlyList<CoverageEvent>)c.GetArguments()[0]!)
            .ToList();

        var ceiling = Assert.Single(events);
        Assert.Equal(MaterializeCollectedDataJob.PointCeilingStatus, ceiling.Status);
        Assert.Equal(chain.PeriodKey, ceiling.PeriodKey);
        Assert.Contains(over, ceiling.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(atCeiling, ceiling.Details, StringComparison.Ordinal);
    }

    /// <summary>Що саме заведено для тесту.</summary>
    private sealed record Stand(int SourceEntityId, IReadOnlyList<string> Fields, IReadOnlyList<string> RowKeys);

    /// <summary>Переводить період у <c>Open</c>: у <c>Scheduled</c> задача законно нічого не пише.</summary>
    private static async Task OpenAsync(EcrDbContext db, int projectId, int periodKey)
    {
        var period = await db.Periods.SingleAsync(p => p.ProjectId == projectId && p.PeriodKeyValue == periodKey);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Ще один період того самого проєкту і екземпляр тієї самої таблиці в ньому.</summary>
    private async Task<long> AddPeriodInstanceAsync(
        EcrDbContext db, TestDocument chain, int periodKey, DateOnly start, DateOnly end)
    {
        var key = new PeriodKey(periodKey);
        db.Periods.Add(new Period(chain.ProjectId, key, (byte)key.Sequence, start, end));

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);

        db.TableInstances.Add(new TableInstance(key, instanceId, chain.DocumentId, chain.TableDefId, Now));
        await db.SaveChangesAsync(CancellationToken.None);

        return instanceId;
    }

    /// <summary>Джерело, сутність і <paramref name="fields"/> матеріалізованих мапінгів (Sum).</summary>
    private static async Task<Stand> ArrangeSourceAsync(EcrDbContext db, TestDocument chain, int fields)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("D16-03"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна НАВМИСНО (див. `PausedMappingCollectionPathTests`):
        // активна сутність без завершеного збору лишається в спільній базі й
        // робить `SourcesHealthCheck` жовтим для кожного наступного прогону.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var names = new List<string>();
        var rowKeys = new List<string>();

        for (var i = 0; i < fields; i++)
        {
            var field = $"Flare_{tag}_F{i}";
            var rowKey = $"R{i + 1}_{tag}";

            // Числова колонка ланцюга, не перша: перша — текстова.
            var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
            map.SetMaterialization(rowKey, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);

            names.Add(field);
            rowKeys.Add(rowKey);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return new Stand(entity.Id, names, rowKeys);
    }

    /// <summary>Кладе точки бойовим шляхом збору (<c>CollectionStore.UpsertRawPointsAsync</c>).</summary>
    private static async Task PointsAsync(
        EcrDbContext db, int sourceEntityId, IReadOnlyList<(string Field, DateTime At, decimal Value)> points)
    {
        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            sourceEntityId,
            points.Min(p => p.At),
            points.Max(p => p.At).AddSeconds(1),
            isCatchUp: false,
            triggeredByUserId: null,
            CancellationToken.None);

        await store.UpsertRawPointsAsync(
            runId,
            sourceEntityId,
            [.. points.Select(p => new SourceDataPoint(p.Field, p.At, p.Value, null, null, "Good"))],
            CancellationToken.None);
    }

    /// <summary>Замінник запису в комірки, що відповідає «записано все».</summary>
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

    /// <summary>Що пішло в запис, за екземпляром таблиці.</summary>
    private static Dictionary<long, IReadOnlyList<IntegrationCellValue>> Written(ICellPatcher patcher)
        => patcher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync))
            .ToDictionary(
                c => (long)c.GetArguments()[1]!,
                c => (IReadOnlyList<IntegrationCellValue>)c.GetArguments()[3]!);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
