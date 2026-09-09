using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>Журнал покриття збору на реальному SQL Server (<c>Q-170</c>).</summary>
[Collection("SqlServer")]
public sealed class CoverageJournalTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кілька_подій_записуються_одним_запитом()
    {
        int sourceEntityId;
        await using (var setup = CreateContext())
        {
            var dataSource = new DataSource(
                EcrCode.Create("SrcQ170"), Name("Source"), ExternalTransport.PiWebApi, "https://example.test", "secret");
            setup.DataSources.Add(dataSource);
            await setup.SaveChangesAsync(CancellationToken.None);

            var entity = new SourceEntity(dataSource.Id, "EntQ170", RegistrySourceKind.External);
            setup.SourceEntities.Add(entity);
            await setup.SaveChangesAsync(CancellationToken.None);

            sourceEntityId = entity.Id;
        }

        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var journal = new CoverageJournal(counting, new TestClock(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc)));

        await journal.RecordManyAsync(
            [
                new CoverageEvent(sourceEntityId, new PeriodKey(202601), "ConflictKeptManual", "R1:C1"),
                new CoverageEvent(sourceEntityId, new PeriodKey(202601), "ConflictKeptManual", "R2:C2"),
            ],
            CancellationToken.None);

        // ⛔ Q-170: дві події — а команд, що торкаються `itg.CollectionCoverage`,
        // рівно ОДНА (один `SaveChangesAsync` на весь набір), не дві.
        var writes = executed.Count(cmd => cmd.Contains("CollectionCoverage", StringComparison.Ordinal));
        Assert.Equal(1, writes);

        var count = await CountCoverageAsync(sourceEntityId, CancellationToken.None);
        Assert.Equal(2, count);
    }

    private async Task<int> CountCoverageAsync(int sourceEntityId, CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = @id";
        command.Parameters.AddWithValue("@id", sourceEntityId);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
