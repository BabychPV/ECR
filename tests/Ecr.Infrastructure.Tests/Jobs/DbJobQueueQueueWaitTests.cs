// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueQueueWaitTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>ClaimedJob.QueueWaitMs</c> (<c>ФВ-12.2</c>): скільки задача була готова
/// (<c>AvailableAt</c>) до захоплення — за годинником СУБД; відкладена задача міряється
/// від СВОГО <c>AvailableAt</c>, а не від постановки; переклейм значення не дає.
/// </summary>
/// <remarks>
/// Детермінізм: момент готовності зсувається SQL-ом на ціле число секунд назад, тож
/// нижня межа точна, а верхня — з запасом на повільну машину. Мутації: OUTPUT без
/// DATEDIFF — усі червоні (null); міряти від <c>CreatedAt</c> — другий червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueQueueWaitTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Claim_повертає_час_готовності_від_AvailableAt_у_мілісекундах()
    {
        var jobId = await EnqueueAsync();
        await ExecAsync("UPDATE itg.JobProgress SET AvailableAt = DATEADD(second, -30, SYSUTCDATETIME()) WHERE JobId = @id;", jobId);

        await using var host = NewHost();
        var claimed = await host.ClaimAsync();

        Assert.NotNull(claimed?.QueueWaitMs);
        Assert.InRange(claimed.QueueWaitMs.Value, 29_900L, 300_000L);
    }

    [Fact]
    public async Task Відкладена_задача_міряється_від_свого_AvailableAt_а_не_від_постановки()
    {
        var jobId = await EnqueueAsync();

        // Створена 10 хв тому, але стала ДОСТУПНОЮ лише 5 с тому (ретрай/відкладення).
        await ExecAsync(
            "UPDATE itg.JobProgress SET CreatedAt = DATEADD(minute, -10, SYSUTCDATETIME()), " +
            "AvailableAt = DATEADD(second, -5, SYSUTCDATETIME()) WHERE JobId = @id;", jobId);

        await using var host = NewHost();
        var claimed = await host.ClaimAsync();

        Assert.NotNull(claimed?.QueueWaitMs);
        Assert.InRange(claimed.QueueWaitMs.Value, 4_900L, 120_000L); // не 600 000
    }

    [Fact]
    public async Task Переклейм_простроченої_оренди_не_дає_затримки_черги()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();
        Assert.NotNull(await host.ClaimAsync());

        await ExpireLeaseAsync(jobId);
        var reclaimed = await host.ClaimAsync();

        Assert.NotNull(reclaimed);
        Assert.True(reclaimed.Reclaimed);
        Assert.Null(reclaimed.QueueWaitMs);
    }
}
