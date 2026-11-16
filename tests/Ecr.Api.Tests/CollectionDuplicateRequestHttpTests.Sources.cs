// tests/Ecr.Api.Tests/CollectionDuplicateRequestHttpTests.Sources.cs
using System.Globalization;
using System.Net;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Сутності збору, розклади, мапінги подій і прив'язки вікна рядка — два однакові запити (B158 c4–c7).</summary>
public sealed partial class CollectionDuplicateRequestHttpTests
{
    private const string FarFuture = "0 0 3 1 1 ? 2099";

    // ───────────────────────────── c4: сутності збору ─────────────────────────────

    /// <summary>
    /// c4: <c>POST /sources</c> двічі одночасно — один <c>201</c>, другий та сама відмова
    /// <c>ECR-INT-0409 sourceEntityDuplicate</c> з <c>{code, dataSource}</c> (раніше — 500 на <c>UQ_SourceEntity</c>:
    /// сховище зберігає саме, повз одиницю роботи).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c4_Створення_сутності_збору_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Integration.Manage"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (dataSourceId, dataSourceCode) = await AddDataSourceAsync().ConfigureAwait(true);
        object Body(string code) => new { dataSourceId, code };
        const string url = "/api/v1/sources";

        try
        {
            // Послідовно.
            var code = $"Ent{Guid.NewGuid():N}"[..14];
            var created = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            AssertRefusal(
                repeated, repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.sourceEntityDuplicate",
                ("code", code), ("dataSource", dataSourceCode));

            // Одночасно.
            var raced = $"Ent{Guid.NewGuid():N}"[..14];
            var replies = await RaceAsync("sources", first, second, url, Body(raced)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.Created),
                repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.sourceEntityDuplicate",
                ("code", raced), ("dataSource", dataSourceCode));

            await using var db = Db();
            Assert.Equal(
                1, await db.SourceEntities.CountAsync(e => e.DataSourceId == dataSourceId && e.Code == raced).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── c5: розклади збору ─────────────────────────────

    /// <summary>
    /// c5: <c>POST /collection-schedules</c> двічі одночасно на ту саму сутність — один <c>201</c>, другий та сама
    /// відмова <c>409 ECR-JOB-0409 collectionScheduleExists</c> з <c>{scheduleId}</c> ПЕРЕМОЖЦЯ, що й послідовний
    /// повтор (раніше — 500 на <c>UQ_CollectionSchedule</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c5_Створення_розкладу_двічі_одночасно_одне_201_друге_та_сама_відмова_з_ключем_переможця()
    {
        var (one, two) = await SignInTwiceAsync(["Integration.EditSchedule"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (dataSourceId, _) = await AddDataSourceAsync().ConfigureAwait(true);
        static object Body(int sourceEntityId) => new { sourceEntityId, cron = FarFuture, isEnabled = false };
        const string url = "/api/v1/collection-schedules";

        try
        {
            // Послідовно.
            var entity = await AddSourceEntityAsync(dataSourceId, active: false).ConfigureAwait(true);
            var created = await SendAsync(first, HttpMethod.Post, url, Body(entity)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(entity)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            AssertRefusal(
                repeated, repeated.Status, "ECR-JOB-0409", "err.ECR-JOB-0409.collectionScheduleExists", ("scheduleId", created.Id));

            // Одночасно.
            var raced = await AddSourceEntityAsync(dataSourceId, active: false).ConfigureAwait(true);
            var replies = await RaceAsync("collection-schedules", first, second, url, Body(raced)).ConfigureAwait(true);

            AssertNoServerError(replies);
            var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.Created),
                repeated.Status, "ECR-JOB-0409", "err.ECR-JOB-0409.collectionScheduleExists", ("scheduleId", winner.Id));

            await using var db = Db();
            Assert.Equal(1, await db.CollectionSchedules.CountAsync(s => s.SourceEntityId == raced).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── c6: мапінги подій ─────────────────────────────

    /// <summary>
    /// c6: <c>POST /source-event-maps</c> двічі одночасно — один <c>200</c>, другий та сама відмова
    /// <c>409 ECR-INT-0409 eventMapExists</c> з <c>{sourceEntityId}</c> (раніше — 500 на <c>UQ_SourceEventMap</c>:
    /// сховище зберігає саме); типовий розклад сутності — один.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c6_Створення_мапінгу_подій_двічі_одночасно_одне_200_друге_та_сама_відмова_що_й_послідовно()
    {
        var stand = await ArrangeTableAsync().ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Integration.Manage"], (ResourceKind.Project, stand.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        object Body(int sourceEntityId) => new
        {
            sourceEntityId,
            documentId = stand.DocumentId,
            tableDefId = stand.TableDefId,
            volumeMode = "None",
            fields = new[]
            {
                new { targetColumnDefId = stand.Start, sourceAttribute = "$start", attributeScope = "Event", valueKind = "Direct" },
                new { targetColumnDefId = stand.End, sourceAttribute = "$end", attributeScope = "Event", valueKind = "Direct" },
            },
        };

        const string url = "/api/v1/source-event-maps";

        try
        {
            // Послідовно.
            var entity = await AddSourceEntityAsync(stand.DataSourceId, active: true).ConfigureAwait(true);
            var created = await SendAsync(first, HttpMethod.Post, url, Body(entity)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(entity)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.OK, $"{created}\n{App.ErrorsText}");
            AssertRefusal(
                repeated, repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.eventMapExists",
                ("sourceEntityId", entity.ToString(CultureInfo.InvariantCulture)));

            // Одночасно.
            var raced = await AddSourceEntityAsync(stand.DataSourceId, active: true).ConfigureAwait(true);
            var replies = await RaceAsync("source-event-maps", first, second, url, Body(raced)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.OK),
                repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.eventMapExists",
                ("sourceEntityId", raced.ToString(CultureInfo.InvariantCulture)));

            await using var db = Db();
            Assert.Equal(1, await db.SourceEventMaps.CountAsync(m => m.SourceEntityId == raced).ConfigureAwait(true));
            Assert.Equal(1, await db.CollectionSchedules.CountAsync(s => s.SourceEntityId == raced).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── c7: прив'язки вікна рядка ─────────────────────────────

    /// <summary>
    /// c7: <c>POST /row-window-maps</c> двічі одночасно на ту саму колонку-ціль — один <c>201</c>, другий та сама
    /// відмова <c>409 ECR-INT-0409 rowWindowTargetTaken</c> з <c>{targetColumn}</c> (раніше — 500 на
    /// <c>UQ_RowWindowMap_Target</c>: сховище зберігає саме).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c7_Створення_прив_язки_вікна_рядка_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var stand = await ArrangeTableAsync().ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Integration.Manage"], (ResourceKind.Project, stand.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var entity = await AddSourceEntityAsync(stand.DataSourceId, active: true).ConfigureAwait(true);

        object Body(int target) => new
        {
            tableDefId = stand.TableDefId,
            targetColumnDefId = target,
            startColumnDefId = stand.Start,
            endColumnDefId = stand.End,
            selectorColumnDefId = stand.Selector,
            summary = "Total",
            isStep = false,
            targetUnitId = stand.UnitTarget,
            sources = new[]
            {
                new { selectorValue = (string?)"A", sourceEntityId = entity, sourceField = "Flare.Total", sourceUnitId = stand.UnitSource },
                new { selectorValue = (string?)null, sourceEntityId = entity, sourceField = "Flare.Default", sourceUnitId = stand.UnitSource },
            },
        };

        const string url = "/api/v1/row-window-maps";

        try
        {
            // Послідовно.
            var created = await SendAsync(first, HttpMethod.Post, url, Body(stand.TargetA)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(stand.TargetA)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            AssertRefusal(
                repeated, repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.rowWindowTargetTaken", ("targetColumn", stand.TargetACode));

            // Одночасно.
            var replies = await RaceAsync("row-window-maps", first, second, url, Body(stand.TargetB)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.Created),
                repeated.Status, "ECR-INT-0409", "err.ECR-INT-0409.rowWindowTargetTaken", ("targetColumn", stand.TargetBCode));

            await using var db = Db();
            Assert.Equal(
                1, await db.RowWindowMaps.CountAsync(m => m.TargetColumnDefId == stand.TargetB).ConfigureAwait(true));
        }
        finally
        {
            await using var db = Db();
            await db.Database.ExecuteSqlAsync(
                $"DELETE s FROM ext.RowWindowSource s JOIN ext.RowWindowMap m ON m.Id = s.RowWindowMapId WHERE m.TableDefId = {stand.TableDefId}")
                .ConfigureAwait(true);
            await db.Database.ExecuteSqlAsync($"DELETE FROM ext.RowWindowMap WHERE TableDefId = {stand.TableDefId}")
                .ConfigureAwait(true);
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    // ───────────────────────────── стенд ─────────────────────────────

    private sealed record TableStand(
        int ProjectId,
        int TableDefId,
        long DocumentId,
        int DataSourceId,
        int Start,
        int End,
        int Selector,
        int TargetA,
        string TargetACode,
        int TargetB,
        string TargetBCode,
        int UnitSource,
        int UnitTarget);

    /// <summary>Документ із динамічною таблицею, колонками вікна й двома цілями, з'єднання й дві одиниці.</summary>
    private async Task<TableStand> ArrangeTableAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None)
            .ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQS_{tag}"), Name("Start"), 20, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQE_{tag}"), Name("End"), 21, CellDataType.Date);
        var targetA = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQA_{tag}"), Name("Volume A"), 22, CellDataType.Decimal);
        var targetB = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQB_{tag}"), Name("Volume B"), 23, CellDataType.Decimal);
        var selector = new ColumnDef(chain.TableDefId, EcrCode.Create($"DQK_{tag}"), Name("Selector"), 24, CellDataType.String);
        db.ColumnDefs.AddRange(start, end, targetA, targetB, selector);

        var source = new DataSource(
            EcrCode.Create($"DQSRC{tag}"), Name("B158c"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);

        var dimensionId = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).FirstAsync().ConfigureAwait(false);
        var unitSource = new Unit(
            EcrCode.Create($"DQU{tag}"), Name("src"), Name("src"), dimensionId, isBase: false, factorToBase: 1m, offsetToBase: 0m);
        var unitTarget = new Unit(
            EcrCode.Create($"DQV{tag}"), Name("dst"), Name("dst"), dimensionId, isBase: false, factorToBase: 1000m, offsetToBase: 0m);
        db.Units.AddRange(unitSource, unitTarget);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return new TableStand(
            chain.ProjectId,
            chain.TableDefId,
            chain.DocumentId,
            source.Id,
            start.Id,
            end.Id,
            selector.Id,
            targetA.Id,
            $"DQA_{tag}",
            targetB.Id,
            $"DQB_{tag}",
            unitSource.Id,
            unitTarget.Id);
    }

    /// <summary>З'єднання без сутностей; повертає ключ і код.</summary>
    private async Task<(int Id, string Code)> AddDataSourceAsync()
    {
        await using var db = Db();
        var source = new DataSource(
            EcrCode.Create($"DQ{Guid.NewGuid():N}"[..12]), Name("B158c"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (source.Id, source.Code);
    }

    /// <summary>Сутність збору без розкладу; повертає її ключ.</summary>
    private async Task<int> AddSourceEntityAsync(int dataSourceId, bool active)
    {
        await using var db = Db();
        var entity = new SourceEntity(dataSourceId, $"Ent{Guid.NewGuid():N}"[..14], RegistrySourceKind.External);
        if (!active)
        {
            entity.Deactivate();
        }

        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return entity.Id;
    }

    /// <summary>Вимикає з'єднання і всі його сутності: активні без збору фарбували б <c>SourcesHealthCheck</c> інших тестів.</summary>
    private async Task DeactivateAsync(int dataSourceId)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE DataSourceId = {dataSourceId}")
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"UPDATE ext.DataSource SET IsActive = 0 WHERE Id = {dataSourceId}")
            .ConfigureAwait(false);
    }
}
