// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeRejectedCellTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
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
/// F1-03 (аудит 9): одне значення, яке обробник запису відхиляє (коміркове правило <c>Error</c>,
/// <c>ECR-CELL-0422</c> <c>validationBlocked</c>), не зупиняє запис решти полів сутності в таблиці.
/// </summary>
/// <remarks>
/// ⛔ Матеріалізація шле ВСІ поля сутності в таблиці одним батчем, а <c>PatchCellsHandler</c>
/// відхиляє батч цілком. Сплеск датчика понад правило валив задачу без повтору й без подій
/// покриття, і жодне інше поле не лягало в таблицю.
///
/// ⚠ Патчер підроблений навмисно: рішення «що відхилити» тут — його відповідь, а перевіряється
/// поведінка задачі на відмову батча (розведення по комірках і журнал покриття).
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeRejectedCellTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <remarks>
    /// МУТАЦІЙНИЙ ДОКАЗ: у <c>MaterializeCollectedDataJob.WriteAsync</c> прибрати розведення по
    /// комірках (кидати далі з першого <c>catch</c>) — задача падає <c>ECR-CELL-0422</c>, <c>VOL</c> не записано.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1-03")]
    public async Task Відхилене_значення_одного_поля_не_зупиняє_запис_решти_і_лягає_в_журнал_покриття()
    {
        var (db, chain, stand) = await ArrangeAsync();
        await using var _ = db;

        var patcher = RejectingPatcher(stand.RejectedColumnId, "ECR-CELL-0422");
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage, Actor(db));

        await job.ExecuteAsync(For(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None);

        var written = Accepted(patcher, stand.RejectedColumnId);
        var accepted = Assert.Single(written);
        Assert.Equal(stand.AcceptedColumnId, accepted.ColumnDefId);
        Assert.Equal(10m, accepted.Value);

        var rejectedCode = await db.ColumnDefs.Where(c => c.Id == stand.RejectedColumnId).Select(c => c.Code).SingleAsync();
        var coverageEvent = Assert.Single(Events(coverage));
        Assert.Equal(CollectionCoverage.SkippedWriteConflict, coverageEvent.Status);
        Assert.True(JobProgressMessageCodec.TryDecode(coverageEvent.Details, out var envelope));
        Assert.Equal(CoverageDetails.CellRejectedKey, envelope.Key);
        Assert.Equal($"{stand.RowKey}:{rejectedCode}", envelope.Params!["cell"]);
        Assert.Equal("ECR-CELL-0422", envelope.Params["code"]);
    }

    /// <summary>
    /// Транзієнтна відмова (архівування) — не вердикт про дані: задача падає й повторюється, як і
    /// раніше, а не пише «відхилено» для кожного поля.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1-03")]
    public async Task Транзієнтна_відмова_батча_не_розводиться_по_комірках()
    {
        var (db, chain, stand) = await ArrangeAsync();
        await using var _ = db;

        var patcher = RejectingPatcher(stand.RejectedColumnId, ErrorCodes.Archiving);
        var coverage = Substitute.For<ICoverageJournal>();
        var job = new MaterializeCollectedDataJob(db, patcher, coverage, Actor(db));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => job.ExecuteAsync(For(stand.SourceEntityId, chain), Substitute.For<IJobProgress>(), CancellationToken.None));

        Assert.Equal(ErrorCodes.Archiving, error.ErrorCode);
        Assert.Single(patcher.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync));
        Assert.Empty(Events(coverage));
    }

    private sealed record Stand(int SourceEntityId, string RowKey, int RejectedColumnId, int AcceptedColumnId);

    private async Task<(EcrDbContext Db, TestDocument Chain, Stand Stand)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("F1-03"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна НАВМИСНО (як у `MaterializeMappingScopeTests`): активна сутність без
        // завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var rowKey = await db.TableRows
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKey)
            .SingleAsync();

        var flareField = $"T_{tag}";
        var volumeField = $"VOL_{tag}";

        var flare = EntityFieldMap.ToColumn(entity.Id, flareField, chain.ColumnDefIds[1]);
        flare.SetMaterialization(rowKey.Value, AggregationKind.Max);
        var volume = EntityFieldMap.ToColumn(entity.Id, volumeField, chain.ColumnDefIds[2]);
        volume.SetMaterialization(rowKey.Value, AggregationKind.Sum);
        db.EntityFieldMaps.AddRange(flare, volume);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);
        await store.UpsertRawPointsAsync(
            runId,
            entity.Id,
            [
                new SourceDataPoint(flareField, MidJanuary, 1350m, null, null, "Good"),
                new SourceDataPoint(volumeField, MidJanuary, 10m, null, null, "Good"),
            ],
            CancellationToken.None);

        return (db, chain, new Stand(entity.Id, rowKey.Value, chain.ColumnDefIds[1], chain.ColumnDefIds[2]));
    }

    /// <summary>
    /// Патчер, що відхиляє будь-який батч із комірки <paramref name="rejectedColumnId"/> кодом
    /// <paramref name="code"/> — так, як обробник відхиляє батч цілком; решту приймає.
    /// </summary>
    private static ICellPatcher RejectingPatcher(int rejectedColumnId, string code)
    {
        var patcher = Substitute.For<ICellPatcher>();
        patcher
            .ApplyIntegrationAsync(
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyList<IntegrationCellValue>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cells = (IReadOnlyList<IntegrationCellValue>)call[3];
                if (cells.Any(c => c.ColumnDefId == rejectedColumnId))
                {
                    throw new BusinessRuleException(
                        code,
                        "batch refused",
                        new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0422.validationBlocked" });
                }

                return Task.FromResult(new IntegrationWriteResult(cells.Count, []));
            });
        return patcher;
    }

    /// <summary>Комірки з батчів, які патчер прийняв (без відхиленої колонки).</summary>
    private static List<IntegrationCellValue> Accepted(ICellPatcher patcher, int rejectedColumnId)
        => [.. patcher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICellPatcher.ApplyIntegrationAsync))
            .Select(c => (IReadOnlyList<IntegrationCellValue>)c.GetArguments()[3]!)
            .Where(cells => cells.All(cell => cell.ColumnDefId != rejectedColumnId))
            .SelectMany(cells => cells)];

    private static List<CoverageEvent> Events(ICoverageJournal coverage)
        => [.. coverage.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICoverageJournal.RecordManyAsync))
            .SelectMany(c => (IReadOnlyList<CoverageEvent>)c.GetArguments()[0]!)];

    private static MaterializeTask For(int sourceEntityId, TestDocument chain)
        => new(
            sourceEntityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId, chain.PeriodKey.Value,
            new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc));

    private static IntegrationActor Actor(EcrDbContext db) => new(db, new Ecr.Application.Common.JobActorScope());

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
