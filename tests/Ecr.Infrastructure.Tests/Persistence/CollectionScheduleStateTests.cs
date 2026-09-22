// tests/Ecr.Infrastructure.Tests/Persistence/CollectionScheduleStateTests.cs
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Стан постановки й версія рядка <c>ext.CollectionSchedule</c> на базі,
/// розгорнутій міграцією <c>B2CollectionScheduleState</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionScheduleStateTests(SqlServerFixture sql)
{
    private const string Hourly = "0 5 * * * ?";

    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Міграція_дає_три_колонки_потрібної_форми()
    {
        var columns = new List<string>();

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT c.name + N':' + t.name + N':' + CAST(c.max_length AS nvarchar(10)) + N':' + CAST(c.is_nullable AS nvarchar(1))
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'ext.CollectionSchedule')
              AND c.name IN (N'LastError', N'LastErrorAt', N'RowVersion')
            ORDER BY c.name;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        // nvarchar(400) — це 800 байтів; rowversion у каталозі зветься timestamp.
        Assert.Equal(["LastError:nvarchar:800:1", "LastErrorAt:datetime2:7:1", "RowVersion:timestamp:8:0"], columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_постановки_пишеться_й_очищається()
    {
        var id = await AddScheduleAsync();

        await using (var db = Context())
        {
            var schedule = await db.CollectionSchedules.SingleAsync(s => s.Id == id);
            schedule.MarkInvalid(new string('x', 1000), Now);
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var schedule = await db.CollectionSchedules.SingleAsync(s => s.Id == id);
            Assert.Equal(400, schedule.LastError!.Length);
            Assert.Equal(Now, schedule.LastErrorAt);

            schedule.ClearError();
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var schedule = await db.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
            Assert.Null(schedule.LastError);
            Assert.Null(schedule.LastErrorAt);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_поверх_чужої_правки_дає_конфлікт_версії()
    {
        var id = await AddScheduleAsync();

        await using var mine = Context();
        var schedule = await mine.CollectionSchedules.SingleAsync(s => s.Id == id);
        Assert.Equal(8, schedule.RowVersion.Length);

        await EditElsewhereAsync(id, "0 0 3 * * ?");

        schedule.Reschedule("0 0 4 * * ?");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => mine.SaveChangesAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збір_фіксує_прогін_поверх_правки_cron_зробленої_під_час_збору()
    {
        var id = await AddScheduleAsync();

        await using (var job = Context())
        {
            // Розклад прочитано до збору; поки збір ішов, cron поправили.
            var schedule = await job.CollectionSchedules.SingleAsync(s => s.Id == id);
            await EditElsewhereAsync(id, "0 0 3 * * ?");

            await CollectionJob.SaveRunAsync(job, schedule, Now, Now.AddHours(-1), CancellationToken.None);
        }

        await using var db = Context();
        var saved = await db.CollectionSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal(Now, saved.LastRunAt);
        Assert.Equal(Now.AddHours(-1), saved.Watermark);

        // Чужа правка не затерта.
        Assert.Equal("0 0 3 * * ?", saved.CronExpression);
    }

    private async Task<int> AddScheduleAsync()
    {
        await using var db = Context();

        var dataSource = new DataSource(
            EcrCode.Create($"Src{_tag}{Guid.NewGuid().ToString("N")[..4]}"),
            new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Source" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"Ent{_tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var schedule = new CollectionSchedule(entity.Id, Hourly);
        db.CollectionSchedules.Add(schedule);
        await db.SaveChangesAsync();

        return schedule.Id;
    }

    private async Task EditElsewhereAsync(int id, string cron)
    {
        await using var other = Context();
        var schedule = await other.CollectionSchedules.SingleAsync(s => s.Id == id);
        schedule.Reschedule(cron);
        await other.SaveChangesAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
