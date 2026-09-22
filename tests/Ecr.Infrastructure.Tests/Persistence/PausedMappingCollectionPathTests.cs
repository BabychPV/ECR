// tests/Ecr.Infrastructure.Tests/Persistence/PausedMappingCollectionPathTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Призупинений мапінг не пише значень (директива №15, <c>BE-27</c>).
/// </summary>
/// <remarks>
/// ⛔ Доказ на ШЛЯХУ ЗБОРУ, а не на прапорці. «Пауза» варта чогось лише тоді,
/// коли її бачать обидва шляхи, і вони різні: <c>GetFieldMapsAsync</c> вирішує,
/// ЩО читати з джерела, а <c>MaterializeCollectedDataJob</c> — що класти в
/// комірки документа. Тест, який перевіряє <c>IsActive == false</c> на самій
/// сутності, лишався б зеленим на системі, яка й далі пише числа.
///
/// ⚠ Обидва мапінги заводяться ПОРУЧ, на одну сутність джерела, і точки
/// збираються за обома. Інакше «нічого не записано» доводило б лише те, що
/// даних не було.
/// </remarks>
[Collection("SqlServer")]
public sealed class PausedMappingCollectionPathTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Вікно збору: всередині періоду 202601 самого ланцюга.</summary>
    private static readonly DateTime FromUtc = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ToUtc = new(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-27")]
    public async Task Призупинений_мапінг_не_дає_значення_в_комірку_а_сусідній_дає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var stand = await ArrangeAsync(db, chain);

        // Період мусить приймати запис: у `Scheduled` задача законно нічого не
        // пише, і тест був би зеленим із зовсім іншої причини.
        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Шлях ПЕРШИЙ: що читається з джерела. Призупинений мапінг сюди не
        // потрапляє — інакше збір щоночі ходив би по атрибут, значення якого
        // нікуди не лягає.
        var forCollection = await new CollectionStore(db, new TestClock(Now))
            .GetFieldMapsAsync(stand.SourceEntityId, CancellationToken.None);

        Assert.Equal([stand.ActiveField], forCollection.Select(m => m.SourceField).Order(StringComparer.Ordinal));

        var patcher = Substitute.For<ICellPatcher>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(new IntegrationWriteResult(1, []));

        var job = new MaterializeCollectedDataJob(db, patcher, Substitute.For<ICoverageJournal>());

        await job.ExecuteAsync(
            new MaterializeTask(
                stand.SourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId,
                chain.PeriodKey.Value, FromUtc, ToUtc),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ. Прибрати `&& m.IsActive` у вибірці мапінгів
        // `MaterializeCollectedDataJob.ExecuteAsync` — і червоним стає рівно це
        // твердження: у комірки їде ДВА значення замість одного, причому
        // друге — з мапінгу, який людина свідомо спинила.
        var written = patcher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync))
            .Select(c => (IReadOnlyList<IntegrationCellValue>)c.GetArguments()[3]!)
            .Single();

        var value = Assert.Single(written);

        // Значення саме з активного мапінгу: 3 точки по 10 у згортці `Sum`.
        Assert.Equal(stand.ActiveRowKey, value.RowKey);
        Assert.Equal(30m, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-27")]
    public async Task Перегляд_мапінгу_приносить_і_призупинений_із_ознакою_isActive()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        // ⚠ Перегляд відкривається лише на АКТИВНІЙ сутності — тож вона активна
        // рівно на час читання й вимикається у `finally` (див. `ArrangeAsync`).
        var stand = await ArrangeAsync(db, chain, keepEntityActive: true);
        try
        {
            var data = await new MappingPreviewStore(db).LoadAsync(
                stand.SourceEntityId, FromUtc, ToUtc, maxPoints: 100, CancellationToken.None);

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути `&& m.IsActive` у вибірку мапінгів
            // `MappingPreviewStore` — і призупинений мапінг зникає з перегляду.
            Assert.NotNull(data);
            Assert.True(Assert.Single(data.Maps, m => m.SourceField == stand.ActiveField).IsActive);
            Assert.False(Assert.Single(data.Maps, m => m.SourceField == stand.PausedField).IsActive);
        }
        finally
        {
            var entity = await db.SourceEntities.SingleAsync(e => e.Id == stand.SourceEntityId);
            entity.Deactivate();
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-16.9")]
    public async Task Зміна_одиниці_ставить_мапінг_на_паузу_з_позначкою_яку_бачить_перегляд()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var stand = await ArrangeAsync(db, chain, keepEntityActive: true);
        try
        {
            var mapId = await db.EntityFieldMaps
                .Where(m => m.SourceEntityId == stand.SourceEntityId && m.SourceField == stand.ActiveField)
                .Select(m => m.Id).SingleAsync();

            var store = new CollectionStore(db, new TestClock(Now));
            await store.PauseForSourceUnitChangeAsync(mapId, "t", actualUnitId: null, CancellationToken.None);

            // Збір цей мапінг більше не читає.
            Assert.DoesNotContain(
                stand.ActiveField,
                (await store.GetFieldMapsAsync(stand.SourceEntityId, CancellationToken.None)).Select(m => m.SourceField));

            db.ChangeTracker.Clear();
            var data = await new MappingPreviewStore(db).LoadAsync(
                stand.SourceEntityId, FromUtc, ToUtc, maxPoints: 100, CancellationToken.None);

            var field = Assert.Single(data!.Maps, m => m.SourceField == stand.ActiveField);
            Assert.False(field.IsActive);
            Assert.Equal(
                new Ecr.Application.Sources.PendingSourceUnitChange("t", null, Now), field.PendingSourceUnitChange);
        }
        finally
        {
            var entity = await db.SourceEntities.SingleAsync(e => e.Id == stand.SourceEntityId);
            entity.Deactivate();
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Що саме заведено для тесту.</summary>
    private sealed record Stand(
        int SourceEntityId, string ActiveField, string PausedField, string ActiveRowKey);

    /// <summary>
    /// Джерело, сутність, два мапінги (діючий і призупинений) і зібрані точки
    /// за ОБОМА полями.
    /// </summary>
    /// <remarks><c>keepEntityActive</c> — тоді тест сам вимикає сутність у <c>finally</c>.</remarks>
    private async Task<Stand> ArrangeAsync(EcrDbContext db, TestDocument chain, bool keepEntityActive = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("BE-27"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);

        // ⛔ Неактивне НАВМИСНО — та сама пастка, що в `CollectionScheduleStateSaveTests`:
        // активне джерело без завершеного збору лишається в спільній базі й
        // робить `SourcesHealthCheck` жовтим для КОЖНОГО наступного прогону.
        // Ані `GetFieldMapsAsync`, ані `MaterializeCollectedDataJob` активності
        // джерела не питають — вони йдуть від мапінгів.
        if (!keepEntityActive)
        {
            entity.Deactivate();
        }

        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var activeField = $"Flare_{tag}_CO";
        var pausedField = $"Flare_{tag}_NO2";
        var activeRowKey = $"R1_{tag}";

        // ⚠ Числова колонка ланцюга, не перша: перша — текстова, і згортка в
        // неї не лягла б навіть на діючому мапінгу.
        var columnDefId = chain.ColumnDefIds[1];

        var active = EntityFieldMap.ToColumn(entity.Id, activeField, columnDefId);
        active.SetMaterialization(activeRowKey, AggregationKind.Sum);

        var paused = EntityFieldMap.ToColumn(entity.Id, pausedField, columnDefId);
        paused.SetMaterialization($"R2_{tag}", AggregationKind.Sum);
        paused.Pause();

        db.EntityFieldMaps.AddRange(active, paused);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, FromUtc, ToUtc, isCatchUp: false, triggeredByUserId: null, CancellationToken.None);

        // Точки за ОБОМА полями, і однакові числом: різниця в результаті може
        // взятися лише з паузи, а не з кількості даних.
        var points = new List<SourceDataPoint>();
        for (var i = 0; i < 3; i++)
        {
            points.Add(new SourceDataPoint(
                activeField, FromUtc.AddHours(i), 10m, null, null, "Good"));
            points.Add(new SourceDataPoint(
                pausedField, FromUtc.AddHours(i), 10m, null, null, "Good"));
        }

        await store.UpsertRawPointsAsync(runId, entity.Id, points, CancellationToken.None);

        return new Stand(entity.Id, activeField, pausedField, activeRowKey);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
