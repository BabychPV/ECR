// tests/Ecr.Infrastructure.Tests/Jobs/RegistrySyncDedupPlanTests.cs
using System.Xml.Linq;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-34 L4-11: дедуп подій синку довідника читає <c>itg.CollectionCoverage</c> ІНДЕКСОМ за
/// сутністю (<c>Index Seek</c> по <c>IX_CollectionCoverage_RegistryEvents</c>), а не сканом
/// усього журналу.
/// </summary>
/// <remarks>
/// ⛔ До індексу єдиний шлях до рядків сутності - скан кластерного ключа: інший індекс за
/// сутністю (<c>IX_CollectionCoverage_SourceEntity_CoveredTo</c>) має фільтр
/// <c>Status IS NULL</c> і подій синку (<c>Status IS NOT NULL</c>) не містить. Журнал росте
/// з кожною подією збору, тож запит, що на старті мілісекундний, через рік читає всю таблицю
/// щопрогону синку.
/// <para>
/// ⚠ Чому план, а не секунди: база мала, і скан кількох сторінок дешевий так само, як seek
/// (так само, як у <c>RegistryWhereUsedPlanTests</c>). Тест сам задає форму журналу -
/// десятки тисяч чужих рядків усіх трьох видів (покриття, події матеріалізації, події синку
/// іншої сутності) - і бере з кешу план саме бойового запиту
/// (<see cref="RegistrySyncJob.DedupJournal"/>, мітка <see cref="RegistrySyncJob.DedupJournalTag"/>).
/// </para>
/// <para>
/// ⛔ Мутація, що валить тест: прибрати <c>IX_CollectionCoverage_RegistryEvents</c> з
/// <c>CollectionCoverageConfiguration</c> і міграції - у плані лишається скан
/// <c>PK_CollectionCoverage</c>. Червоним він був і ДО коміту схеми. Прибрати з INCLUDE
/// <c>PeriodKey</c> - у плані з'являється <c>Clustered Index Seek [PK_CollectionCoverage]</c>
/// (Key Lookup лише заради <c>PeriodKey IS NULL</c>): перевірено, червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySyncDedupPlanTests(SqlServerFixture sql)
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private const string ExpectedIndex = "[IX_CollectionCoverage_RegistryEvents]";

    /// <summary>Чужих рядків кожного виду: покриття (<c>Status NULL</c>), події матеріалізації, події синку.</summary>
    private const int BackgroundRowsPerKind = 20_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-11")]
    public async Task Дедуп_подій_синку_читає_журнал_індексом_за_сутністю_а_не_сканом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var (target, other) = await SeedEntitiesAsync(tag).ConfigureAwait(true);

        await SeedJournalAsync(target, other).ConfigureAwait(true);
        await PrepareMeasurementAsync().ConfigureAwait(true);

        List<string> journal;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            journal = await RegistrySyncJob.DedupJournal(db, target).ToListAsync().ConfigureAwait(true);
        }

        // Запит повертає рівно події цієї сутності (а не порожньо через хибний план/фільтр).
        Assert.Equal(["a; key=s1:1", "b; key=s2:2", "c; key=s3:3"], journal);

        var plan = await CachedPlanAsync().ConfigureAwait(true);
        Assert.NotNull(plan);

        var accesses = plan!.Descendants(XName.Get("RelOp", ShowPlanNs))
            .Select(op => (
                Physical: (string?)op.Attribute("PhysicalOp") ?? string.Empty,
                Index: op.Elements().SelectMany(e => e.Elements(XName.Get("Object", ShowPlanNs)))
                    .Concat(op.Elements(XName.Get("Object", ShowPlanNs)))
                    .Where(o => (string?)o.Attribute("Table") == "[CollectionCoverage]")
                    .Select(o => (string?)o.Attribute("Index"))
                    .FirstOrDefault()))
            .Where(a => a.Index is not null)
            .ToList();

        Assert.True(accesses.Count > 0, "У плані запиту дедупу немає доступу до itg.CollectionCoverage.");

        var described = string.Join(", ", accesses.Select(a => $"{a.Physical} {a.Index}"));
        Assert.True(
            accesses.All(a => a.Physical == "Index Seek" && a.Index == ExpectedIndex),
            $"Очікувався лише Index Seek по {ExpectedIndex}, у плані: {described}.");
    }

    private async Task<(int Target, int Other)> SeedEntitiesAsync(string tag)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var dataSource = new DataSource(
            EcrCode.Create($"DDP{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://af.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var target = new SourceEntity(dataSource.Id, $"DedupTarget_{tag}", RegistrySourceKind.External);
        var other = new SourceEntity(dataSource.Id, $"DedupOther_{tag}", RegistrySourceKind.External);
        db.SourceEntities.AddRange(target, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (target.Id, other.Id);
    }

    /// <summary>
    /// Журнал: три події синку цільової сутності й багато чужих рядків усіх видів. Чужі
    /// події синку мають ТОЙ САМИЙ вигляд (<c>Status</c> із <see cref="CollectionCoverage.RegistryStatuses"/>,
    /// <c>PeriodKey NULL</c>, ключ дедупу) - щоб різницю робив лише <c>SourceEntityId</c>.
    /// </summary>
    private async Task SeedJournalAsync(int target, int other)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = $"""
            SET NOCOUNT ON;
            INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
            VALUES (@target, NULL, NULL, '2026-10-01', '2026-10-01', N'RegistryPendingUpdate', N'a; key=s1:1'),
                   (@target, NULL, NULL, '2026-10-01', '2026-10-01', N'RegistryDiverged', N'b; key=s2:2'),
                   (@target, NULL, NULL, '2026-10-01', '2026-10-01', N'RegistryAutoCreated', N'c; key=s3:3');

            ;WITH n AS (
                SELECT TOP ({BackgroundRowsPerKind}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i
                FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
            INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
            SELECT @other, NULL, NULL, DATEADD(HOUR, i, '2026-01-01'), DATEADD(HOUR, i + 1, '2026-01-01'), NULL, NULL FROM n
            UNION ALL
            SELECT @other, NULL, 202601 + (i % 12), '2026-01-01', '2026-01-01', N'SkippedPeriodClosed', N'period closed ' + CONVERT(nvarchar(20), i) FROM n
            UNION ALL
            SELECT @other, NULL, NULL, '2026-01-01', '2026-01-01', N'RegistryPendingUpdate',
                   N'other; key=s' + CONVERT(nvarchar(20), i) + N':' + CONVERT(nvarchar(20), i) FROM n;
            """;
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@other", other);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Свіжа статистика й порожній кеш планів ЦІЄЇ бази: план компілюється для щойно заданої
    /// форми журналу. <c>DATABASE SCOPED</c>, а не <c>DBCC FREEPROCCACHE</c> - сервер спільний.
    /// </summary>
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

    /// <summary>План запиту з міткою <see cref="RegistrySyncJob.DedupJournalTag"/> із кешу цієї бази.</summary>
    private async Task<XDocument?> CachedPlanAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ dbid - з атрибутів плану (для параметризованих запитів dm_exec_sql_text.dbid порожній).
        command.CommandText = """
            SELECT TOP (1) CAST(qp.query_plan AS nvarchar(max))
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
            CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
            WHERE pa.attribute = N'dbid' AND CAST(pa.value AS int) = DB_ID()
              AND st.text LIKE N'%' + @tag + N'%'
              AND st.text NOT LIKE N'%dm_exec_query_stats%'
            ORDER BY qs.last_execution_time DESC;
            """;
        command.Parameters.AddWithValue("@tag", RegistrySyncJob.DedupJournalTag);

        var text = await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
        return text is null ? null : XDocument.Parse(text);
    }
}
