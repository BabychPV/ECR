// tests/Ecr.Infrastructure.Tests/Persistence/CollectionCoverageDedupPlanTests.cs
using System.Xml.Linq;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// R7-Y2-01: дедуп подій покриття (<see cref="CollectionStore.RecordCoverageEventAsync"/>) читає
/// <c>itg.CollectionCoverage</c> під <c>UPDLOCK, HOLDLOCK</c> ІНДЕКСОМ за сутністю
/// (<c>Index Seek</c> по <c>IX_CollectionCoverage_RegistryEvents</c>), а не сканом кластерного ключа.
/// </summary>
/// <remarks>
/// ⛔ До виправлення предикат (<c>SourceEntityId = @ AND Status = @ AND Details LIKE …</c>) не мав
/// <c>PeriodKey IS NULL</c>, тож фільтрований індекс подій був непридатний, а острівний має фільтр
/// <c>Status IS NULL</c>. План — скан <c>PK_CollectionCoverage</c> під SERIALIZABLE: діапазонне
/// блокування до +∞, і БУДЬ-ЯКА вставка покриття (острів успішного прогону, подія іншої сутності)
/// стоїть до коміту дедупу.
/// <para>
/// ⛔ Мутація, що валить обидва тести: прибрати з запиту <c>Status IS NOT NULL AND PeriodKey IS NULL</c>
/// і підказку індексу. Тести/мутація — CI, локально не запускались.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionCoverageDedupPlanTests(SqlServerFixture sql)
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private const string ExpectedIndex = "[IX_CollectionCoverage_RegistryEvents]";

    /// <summary>Чужих рядків кожного виду: острови (<c>Status NULL</c>), події матеріалізації, події збору.</summary>
    private const int BackgroundRowsPerKind = 20_000;

    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R7-Y2-01")]
    public async Task Дедуп_події_іде_seek_по_сутності_без_скану_PK()
    {
        var (target, other) = await SeedEntitiesAsync().ConfigureAwait(true);
        XDocument? plan;
        try
        {
            await SeedJournalAsync(other).ConfigureAwait(true);
            await PrepareMeasurementAsync().ConfigureAwait(true);

            await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
            {
                var store = new CollectionStore(db, new TestClock(Now));
                Assert.True(await RecordAsync(store, target).ConfigureAwait(true), "Перша подія мусить записатися.");
                Assert.False(await RecordAsync(store, target).ConfigureAwait(true), "Той самий ключ удруге — дедуп.");
            }

            plan = await CachedPlanAsync().ConfigureAwait(true);
        }
        finally
        {
            await RemoveJournalAsync(target, other).ConfigureAwait(true);
        }

        Assert.NotNull(plan);

        var reads = plan!.Descendants(XName.Get("RelOp", ShowPlanNs))
            .Select(op => (
                Physical: (string?)op.Attribute("PhysicalOp") ?? string.Empty,
                Index: op.Elements().SelectMany(e => e.Elements(XName.Get("Object", ShowPlanNs)))
                    .Concat(op.Elements(XName.Get("Object", ShowPlanNs)))
                    .Where(o => (string?)o.Attribute("Table") == "[CollectionCoverage]")
                    .Select(o => (string?)o.Attribute("Index"))
                    .FirstOrDefault()))
            .Where(a => a.Index is not null && !a.Physical.Contains("Insert", StringComparison.Ordinal))
            .ToList();

        Assert.True(reads.Count > 0, "У плані дедупу немає читання itg.CollectionCoverage.");

        var described = string.Join(", ", reads.Select(a => $"{a.Physical} {a.Index}"));
        Assert.True(
            reads.All(a => a.Physical == "Index Seek" && a.Index == ExpectedIndex),
            $"Очікувався лише Index Seek по {ExpectedIndex}, у плані: {described}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R7-Y2-01")]
    public async Task Незакомічений_дедуп_сутності_A_не_блокує_вставок_покриття_сутності_B()
    {
        var (entityA, entityB) = await SeedEntitiesAsync().ConfigureAwait(true);

        // ⚠ Подія B вже є: наступний ключ індексу після діапазону A — (B, її Id), тож нова подія B
        // (більший Id) лежить поза діапазонним блокуванням A навіть за правильного плану.
        await using (var seed = new SqlConnection(sql.ConnectionString))
        {
            await seed.OpenAsync().ConfigureAwait(true);
            await using var command = seed.CreateCommand();
            command.CommandText = """
                INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
                VALUES (@b, NULL, NULL, '2026-09-30', '2026-09-30', N'SourceDataRefused', N'{"key":"seed"}');
                """;
            command.Parameters.AddWithValue("@b", entityB);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        Assert.True(await RecordAsync(new CollectionStore(db, new TestClock(Now)), entityA).ConfigureAwait(true));

        // Транзакція A відкрита й тримає блокування дедупу. Вставки B — з LOCK_TIMEOUT 1 с:
        // до виправлення обидві падають 1222 (діапазон до +∞ на скані PK).
        await using (var racing = new SqlConnection(sql.ConnectionString))
        {
            await racing.OpenAsync().ConfigureAwait(true);
            await using var command = racing.CreateCommand();
            command.CommandText = """
                SET LOCK_TIMEOUT 1000;
                INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
                VALUES (@b, NULL, NULL, '2026-10-01T10:00:00', '2026-10-01T11:00:00', NULL, NULL);
                INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
                VALUES (@b, NULL, NULL, '2026-10-01', '2026-10-01', N'SourceDataRefused', N'{"key":"racing"}');
                """;
            command.Parameters.AddWithValue("@b", entityB);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        await transaction.RollbackAsync().ConfigureAwait(true);
    }

    private static Task<bool> RecordAsync(CollectionStore store, int sourceEntityId)
        => store.RecordCoverageEventAsync(
            sourceEntityId,
            @"\\af\db\Element|Attr",
            new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc),
            CollectionCoverage.SourceDataRefused,
            "ECR-INT-TEST",
            new JobProgressMessageEnvelope("coverageEvents.sourceDataRefused"),
            CancellationToken.None);

    private async Task<(int First, int Second)> SeedEntitiesAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var dataSource = new DataSource(
            EcrCode.Create($"CDP{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://af.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⚠ Порядок збережень задає порядок Id: перша сутність має менший Id.
        var first = new SourceEntity(dataSource.Id, $"CovDedupA_{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(first);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var second = new SourceEntity(dataSource.Id, $"CovDedupB_{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(second);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (first.Id, second.Id);
    }

    /// <summary>Багато чужих рядків усіх трьох видів — щоб скан PK був для оптимізатора дорогим.</summary>
    /// <remarks>
    /// ⚠ Порціями з CHECKPOINT (як у <c>CollectionStoreCoverageWindowTests</c> і <c>RegistryImpactScanTests</c>):
    /// одна транзакція на 60 000 рядків з індексами <c>itg.CollectionCoverage</c> видувала журнал спільної
    /// тестової бази до 136 МБ — за стелю <c>TestDatabaseSizeTests</c> (інтеграція R7).
    /// </remarks>
    private async Task SeedJournalAsync(int other)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @from int = 0, @k int;
            WHILE @from < @n
            BEGIN
                SET @k = CASE WHEN @n - @from < @chunk THEN @n - @from ELSE @chunk END;
                ;WITH n AS (
                    SELECT TOP (@k) @from + ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i
                    FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
                INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
                SELECT @other, NULL, NULL, DATEADD(HOUR, i, '2026-01-01'), DATEADD(HOUR, i + 1, '2026-01-01'), NULL, NULL FROM n
                UNION ALL
                SELECT @other, NULL, 202601 + (i % 12), '2026-01-01', '2026-01-01', N'SkippedPeriodClosed', N'period closed ' + CONVERT(nvarchar(20), i) FROM n
                UNION ALL
                SELECT @other, NULL, NULL, '2026-01-01', '2026-01-01', N'SourceDataRefused',
                       N'{"key":"' + CONVERT(nvarchar(20), i) + N'"}' FROM n;
                SET @from = @from + @k;
                CHECKPOINT;
            END;
            """;
        command.Parameters.AddWithValue("@other", other);
        command.Parameters.AddWithValue("@n", BackgroundRowsPerKind);
        command.Parameters.AddWithValue("@chunk", 4_000);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Прибирання журналу обох сутностей — теж порціями з CHECKPOINT (спільна база, стеля журналу).</summary>
    private async Task RemoveJournalAsync(int first, int second)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @deleted int = 1;
            WHILE @deleted > 0
            BEGIN
                DELETE TOP (@chunk) FROM itg.CollectionCoverage WHERE SourceEntityId IN (@first, @second);
                SET @deleted = @@ROWCOUNT;
                CHECKPOINT;
            END;
            """;
        command.Parameters.AddWithValue("@first", first);
        command.Parameters.AddWithValue("@second", second);
        command.Parameters.AddWithValue("@chunk", 10_000);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Свіжа статистика й порожній кеш планів ЦІЄЇ бази (як у <c>RegistrySyncDedupPlanTests</c>).</summary>
    private async Task PrepareMeasurementAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = """
            UPDATE STATISTICS itg.CollectionCoverage WITH FULLSCAN;
            ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>План запиту з міткою <see cref="CollectionStore.CoverageEventDedupTag"/> із кешу цієї бази.</summary>
    /// <remarks>⛔ Лише через <see cref="PlanCache"/>: прямий <c>APPLY</c> над усім кешем відкриває чужі бази (Msg 924, <c>Z8-01</c>).</remarks>
    private async Task<XDocument?> CachedPlanAsync()
    {
        var text = await PlanCache.LatestPlanAsync(sql.ConnectionString, CollectionStore.CoverageEventDedupTag).ConfigureAwait(false);
        return text is null ? null : XDocument.Parse(text);
    }
}
