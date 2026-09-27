// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeMappingScopeTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
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
/// Суміжне D16-03: матеріалізація екземпляра бере лише мапінги СВОЄЇ таблиці,
/// а «період закритий» означає лише справді закритий період.
/// </summary>
/// <remarks>
/// ⛔ Дефект мапінгів: одна сутність джерела живить дві таблиці (два мапінги).
/// Задача для екземпляра таблиці A брала мапінги обох, колонка таблиці B
/// поверталася з <c>IntegrationCellPatcher</c> у <c>KeptManual</c>, і в журнал
/// покриття щопрогону лягав хибний <c>ConflictKeptManual</c> «комірка має
/// правку людини» — про комірку, якої в цій таблиці немає.
///
/// ⛔ Дефект стану: період <c>Scheduled</c> (ще не відкритий — наприклад,
/// <c>OpenOffsetDays &gt; 0</c> або задача станів ще не пройшла) отримував
/// <c>SkippedPeriodClosed</c> «пізній збір лишається сирим». Він не закритий і
/// збір не пізній: точки лежать у <c>ext.RawDataPoint</c>, і перший прогін після
/// відкриття згорне їх усі — згортка йде за межами ПЕРІОДУ, не за вікном.
///
/// ⚠ Усе на справжньому SQL Server: звуження — предикат у запиті до
/// <c>ext.EntityFieldMap</c>, а «правка людини» — сирий запит до
/// <c>aud.CellChange</c>; у пам'яті це була б копія запиту, а не він сам.
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeMappingScopeTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Точка посеред січня — всередині періоду 202601 у будь-якому поясі.</summary>
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Сутність_живить_дві_таблиці_екземпляр_отримує_лише_колонки_своєї()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await OpenAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var other = await AddOtherTableAsync(db, chain);
        var stand = await ArrangeTwoTablesAsync(db, chain, other);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage, Actor(db));

        await job.ExecuteAsync(MaterializeFor(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати звуження мапінгів до таблиці екземпляра
        // в `MaterializeCollectedDataJob` → у запис екземпляра A їде і колонка
        // таблиці B (дві комірки замість однієї).
        var written = Written(patcher);
        var cell = Assert.Single(Assert.Single(written).Value);
        Assert.Equal(chain.TableInstanceId, Assert.Single(written).Key);
        Assert.Equal(chain.ColumnDefIds[1], cell.ColumnDefId);
        Assert.Equal(stand.OwnRowKey, cell.RowKey);
        Assert.Equal(7m, cell.Value);
        Assert.DoesNotContain(other.ColumnDefId, written.Values.SelectMany(v => v).Select(v => v.ColumnDefId));

        Assert.Empty(coverage.ReceivedCalls());

        // Контроль: екземпляр таблиці B своєю задачею отримує свою колонку.
        await job.ExecuteAsync(MaterializeFor(stand.SourceEntityId, chain, other.TableInstanceId), Substitute.For<IJobProgress>(), CancellationToken.None);
        var otherCell = Assert.Single(Written(patcher)[other.TableInstanceId]);
        Assert.Equal(other.ColumnDefId, otherCell.ColumnDefId);
        Assert.Equal(700m, otherCell.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Правка_людини_у_своїй_таблиці_дає_рівно_один_ConflictKeptManual_без_хибного_від_чужої()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await OpenAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var other = await AddOtherTableAsync(db, chain);
        var stand = await ArrangeTwoTablesAsync(db, chain, other);

        // Справжній конфлікт: остання зміна комірки своєї таблиці — правка людини.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES ({Now}, {chain.PeriodKey.Value}, {chain.DocumentId}, {chain.RowIds[0]}, {stand.OwnRowKey},
                    {chain.ColumnDefIds[1]}, N'1', N'42', 1, N'UserEdit', 0)
            """);

        var coverage = Substitute.For<ICoverageJournal>();

        // ⚠ Справжній `IntegrationCellPatcher`: саме він повертає `KeptManual`.
        // Обробник запису — `null!` навмисно: єдина комірка своєї таблиці
        // правлена людиною, тож до запису справа дійти НЕ має; спроба запису
        // впала б тут `NullReferenceException`, а не пройшла б мовчки.
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        var job = new MaterializeCollectedDataJob(
            db, new IntegrationCellPatcher(db, rows, new NormalizedCellStore(db), null!), coverage, Actor(db));

        await job.ExecuteAsync(MaterializeFor(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        var ownCode = await db.ColumnDefs.Where(c => c.Id == chain.ColumnDefIds[1]).Select(c => c.Code).SingleAsync();

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: без звуження мапінгів подій ДВІ — друга
        // «Комірка RB…:columnDef=… має правку людини» про колонку таблиці B.
        var events = Events(coverage);
        var conflict = Assert.Single(events);
        Assert.Equal("ConflictKeptManual", conflict.Status);
        Assert.Equal(chain.PeriodKey, conflict.PeriodKey);
        Assert.Contains($"{stand.OwnRowKey}:{ownCode}", conflict.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("columnDef=", conflict.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Період_Scheduled_не_пише_SkippedPeriodClosed_і_не_пише_в_комірки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        // Період лишається `Scheduled` — стан будівника за замовчуванням.
        Assert.Equal(
            PeriodState.Scheduled,
            await db.Periods.Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
                .Select(p => p.State).SingleAsync());

        var other = await AddOtherTableAsync(db, chain);
        var stand = await ArrangeTwoTablesAsync(db, chain, other);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage, Actor(db));

        await job.ExecuteAsync(MaterializeFor(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку `Scheduled` → у журнал покриття
        // лягає `SkippedPeriodClosed` «Період у стані Scheduled: пізній збір
        // лишається сирим» — про період, який не закритий і збір якого не пізній.
        Assert.Empty(coverage.ReceivedCalls());
        Assert.Empty(Written(patcher));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D16-03")]
    public async Task Період_Closed_і_далі_пише_SkippedPeriodClosed()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await OpenAsync(db, chain.ProjectId, chain.PeriodKey.Value);

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Closed, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var other = await AddOtherTableAsync(db, chain);
        var stand = await ArrangeTwoTablesAsync(db, chain, other);

        var patcher = RecordingPatcher();
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage, Actor(db));

        await job.ExecuteAsync(MaterializeFor(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        // Контроль до гілки `Scheduled`: справді закритий період — як і був.
        await coverage.Received(1).RecordAsync(
            stand.SourceEntityId, chain.PeriodKey, "SkippedPeriodClosed", Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(Written(patcher));
    }

    /// <summary>Друга таблиця того самого аркуша: її колонка і екземпляр у тому ж документі й періоді.</summary>
    private sealed record OtherTable(int TableDefId, int ColumnDefId, long TableInstanceId);

    /// <summary>Що заведено для сутності, яка живить обидві таблиці.</summary>
    private sealed record Stand(int SourceEntityId, string OwnRowKey);

    private static MaterializeTask MaterializeFor(int sourceEntityId, TestDocument chain, long? instanceId = null)
        => new(
            sourceEntityId, chain.ProjectId, chain.DocumentId, instanceId ?? chain.TableInstanceId,
            chain.PeriodKey.Value,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc));

    private static async Task OpenAsync(EcrDbContext db, int projectId, int periodKey)
    {
        var period = await db.Periods.SingleAsync(p => p.ProjectId == projectId && p.PeriodKeyValue == periodKey);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<OtherTable> AddOtherTableAsync(EcrDbContext db, TestDocument chain)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var table = new TableDef(
            chain.SheetDefId, EcrCode.Create($"TBB{tag}"), Name("Table B"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync(CancellationToken.None);

        var column = new ColumnDef(table.Id, EcrCode.Create($"CB{tag}"), Name("Col B"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync(CancellationToken.None);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(chain.PeriodKey, instanceId, chain.DocumentId, table.Id, Now));
        await db.SaveChangesAsync(CancellationToken.None);

        return new OtherTable(table.Id, column.Id, instanceId);
    }

    /// <summary>
    /// Сутність із двома матеріалізованими мапінгами: поле F0 → рядок 1 числової
    /// колонки таблиці ланцюга, поле F1 → колонка таблиці B. Точки — посеред січня.
    /// </summary>
    private static async Task<Stand> ArrangeTwoTablesAsync(EcrDbContext db, TestDocument chain, OtherTable other)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("D16-03 scope"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна НАВМИСНО (як у `MaterializePeriodBoundsTests`): активна
        // сутність без завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var ownRowKey = await db.TableRows
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKey)
            .SingleAsync();

        var ownField = $"Own_{tag}";
        var otherField = $"Other_{tag}";

        var own = EntityFieldMap.ToColumn(entity.Id, ownField, chain.ColumnDefIds[1]);
        own.SetMaterialization(ownRowKey.Value, AggregationKind.Sum);

        var foreign = EntityFieldMap.ToColumn(entity.Id, otherField, other.ColumnDefId);
        foreign.SetMaterialization($"RB1_{tag}", AggregationKind.Sum);

        db.EntityFieldMaps.AddRange(own, foreign);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);

        await store.UpsertRawPointsAsync(
            runId,
            entity.Id,
            [
                new SourceDataPoint(ownField, MidJanuary, 3m, null, null, "Good"),
                new SourceDataPoint(ownField, MidJanuary.AddHours(1), 4m, null, null, "Good"),
                new SourceDataPoint(otherField, MidJanuary, 300m, null, null, "Good"),
                new SourceDataPoint(otherField, MidJanuary.AddHours(1), 400m, null, null, "Good"),
            ],
            CancellationToken.None);

        return new Stand(entity.Id, ownRowKey.Value);
    }

    /// <summary>Технічний автор задачі — як у контейнері (P0, <c>IntegrationActor</c>).</summary>
    private static IntegrationActor Actor(EcrDbContext db) => new(db, new Ecr.Application.Common.JobActorScope());

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

    private static Dictionary<long, IReadOnlyList<IntegrationCellValue>> Written(ICellPatcher patcher)
        => patcher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync))
            .ToDictionary(
                c => (long)c.GetArguments()[1]!,
                c => (IReadOnlyList<IntegrationCellValue>)c.GetArguments()[3]!);

    private static List<CoverageEvent> Events(ICoverageJournal coverage)
        => [.. coverage.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICoverageJournal.RecordManyAsync))
            .SelectMany(c => (IReadOnlyList<CoverageEvent>)c.GetArguments()[0]!)];

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
