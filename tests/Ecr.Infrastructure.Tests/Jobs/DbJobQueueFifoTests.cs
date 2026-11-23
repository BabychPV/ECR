// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueFifoTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ JQ-FIFO (b10; флейк CI 45157932160 <c>RecalculationYearPerPeriodCommitTests</c>): задачі з ОДНАКОВИМ <c>AvailableAt</c>
/// (дві постановки в одну мілісекунду — <c>datetime2(3)</c>) claim бере в порядку ВСТАВКИ, а не за випадковим GUID у
/// <c>JobId</c>; так само переклейм простроченої оренди.
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально, не в коміті): повернути розв'язання нічиї <c>q.AvailableAt, q.JobId</c> у claim / переклеймі
/// <see cref="DbJobQueue"/> — червоні обидва тести (порядок 20 задач за GUID; збіг з порядком вставки — 1/20!).
/// Однаковий <c>AvailableAt</c> ставиться прямим UPDATE: дві постановки в одну мілісекунду в тесті не відтворити напевно.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "JQ-FIFO")]
public sealed class DbJobQueueFifoTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Count = 20;

    [Fact]
    public async Task Задачі_з_однаковим_AvailableAt_беруться_в_порядку_вставки()
    {
        var enqueued = await EnqueueManyAsync();
        await SameAvailableAtAsync(enqueued);

        var claimed = new List<string>();
        await using var host = NewHost();
        for (var i = 0; i < Count; i++)
        {
            claimed.Add((await host.ClaimAsync())!.Claim.JobId);
        }

        Assert.Equal(enqueued, claimed);
        Assert.Null(await host.ClaimAsync());
    }

    [Fact]
    public async Task Переклейм_з_однаковим_AvailableAt_у_порядку_вставки()
    {
        var enqueued = await EnqueueManyAsync();
        await SameAvailableAtAsync(enqueued);

        await using (var dead = NewHost())
        {
            for (var i = 0; i < Count; i++)
            {
                Assert.NotNull(await dead.ClaimAsync("host/dead"));
            }
        }

        foreach (var jobId in enqueued)
        {
            await ExpireLeaseAsync(jobId);
        }

        var reclaimed = new List<string>();
        await using var host = NewHost();
        for (var i = 0; i < Count; i++)
        {
            reclaimed.Add((await host.ClaimAsync("host/alive"))!.Claim.JobId);
        }

        Assert.Equal(enqueued, reclaimed);
    }

    /// <summary>Постановки по одній — порядок вставки; кожен рядок отримує значення послідовності від бази.</summary>
    private async Task<List<string>> EnqueueManyAsync()
    {
        var ids = new List<string>();
        for (var i = 0; i < Count; i++)
        {
            ids.Add(await EnqueueAsync());
        }

        await using var db = Sql.CreateContext();
        var seqs = await db.JobProgresses.AsNoTracking()
            .Where(p => ids.Contains(p.JobId))
            .Select(p => new { p.JobId, p.EnqueueSeq })
            .ToListAsync();
        Assert.All(seqs, s => Assert.NotNull(s.EnqueueSeq));
        Assert.Equal(ids, [.. seqs.OrderBy(s => s.EnqueueSeq).Select(s => s.JobId)]);

        return ids;
    }

    private async Task SameAvailableAtAsync(List<string> ids)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @at datetime2(3) = DATEADD(second, -1, CAST(SYSUTCDATETIME() AS datetime2(3)));
            UPDATE itg.JobProgress SET AvailableAt = @at
             WHERE JobId IN (SELECT [value] FROM OPENJSON(@ids));
            """;
        command.Parameters.AddWithValue("@ids", System.Text.Json.JsonSerializer.Serialize(ids));
        Assert.Equal(ids.Count, await command.ExecuteNonQueryAsync());
    }
}
