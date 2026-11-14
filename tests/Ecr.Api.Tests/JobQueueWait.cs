using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B112 (урок B94b): очікування, доки у ВЛАСНІЙ базі тесту не лишиться задач <c>Queued</c>/<c>Running</c>.
/// </summary>
/// <remarks>
/// ⛔ Кожен старт хоста (<c>EcrApiFactory</c>) ставить фонову задачу <c>IReportViewsJob</c>
/// (<c>ReportViewsJob.QueueAllOnStartupAsync</c>, <c>StartupSequence</c>). Тест, що порівнює ГЛОБАЛЬНІ лічильники
/// черги (<c>jobs.running</c>, <c>queued</c>) до і після своєї дії, під навантаженням CI бачить стартову задачу ще
/// <c>Queued</c>/<c>Running</c> на «до» і вже завершеною на «після» — і отримує «+0 замість +1». Тому «до» читається
/// лише тоді, коли незавершених задач немає (стан, а не таймінг; межа — лише запобіжник від зависання).
/// <para>
/// ⚠ Лише для власної бази (<see cref="SqlServerFixture.WithOwnDatabase"/>): у спільній базі колекції рядки
/// попередніх тестів можуть лишатися незавершеними назавжди, і очікування стало б тайм-аутом.
/// </para>
/// </remarks>
internal static class JobQueueWait
{
    /// <summary>Чекає, доки в базі не лишиться задач у станах <c>Queued</c>/<c>Running</c>.</summary>
    /// <param name="database">Власна база тесту.</param>
    /// <returns>Завдання.</returns>
    public static async Task WaitForNoUnfinishedJobsAsync(SqlServerFixture database)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            await using var db = database.CreateContext();
            var unfinished = await db.JobProgresses.AsNoTracking()
                .Where(p => p.State == "Queued" || p.State == "Running")
                .Select(p => p.JobCode + "=" + p.State)
                .ToListAsync();
            if (unfinished.Count == 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"задачі власної бази не завершились: {string.Join("; ", unfinished)}");
            await Task.Delay(200);
        }
    }

    /// <summary>Стани всіх задач бази одним рядком — для повідомлення падіння.</summary>
    /// <param name="database">База тесту.</param>
    /// <returns>«код=стан; …» у порядку ідентифікатора.</returns>
    public static async Task<string> DescribeJobsAsync(SqlServerFixture database)
    {
        await using var db = database.CreateContext();
        return string.Join("; ", await db.JobProgresses.AsNoTracking()
            .OrderBy(p => p.JobId)
            .Select(p => p.JobCode + "=" + p.State)
            .ToListAsync());
    }
}
