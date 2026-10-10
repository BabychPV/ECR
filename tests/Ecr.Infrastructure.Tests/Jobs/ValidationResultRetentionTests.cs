using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// P1-08: <c>wf.ValidationResult</c> не росте без меж — нічна ретенція лишає
/// <see cref="ReportRetentionJob.ValidationRunsKept"/> найновіших прогонів на «документ + період».
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати виклик <c>PruneValidationResultsAsync</c> із <c>RunAsync</c> — червоніє
/// <see cref="Лишає_останні_20_прогонів_на_документ_і_період_найновіший_на_місці"/>: усі 25 рядків лишаються.
/// </remarks>
[Collection("SqlServer")]
public sealed class ValidationResultRetentionTests(SqlServerFixture sql)
{
    private static readonly DateTime Start = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лишає_останні_20_прогонів_на_документ_і_період_найновіший_на_місці()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();
        var otherDocument = await chain.BuildAsync();

        var period = document.PeriodKey.Value;
        var otherPeriod = period + 1;

        await using (var db = chain.CreateContext())
        {
            // 25 прогонів одного документа й періоду: i = 0 — найстаріший, 24 — найновіший.
            for (var i = 0; i < 25; i++)
            {
                db.ValidationResults.Add(Run(document.DocumentId, period, i));
            }

            // Інший період ТОГО Ж документа: рахується окремо, 3 < 20 — не чіпається.
            for (var i = 0; i < 3; i++)
            {
                db.ValidationResults.Add(Run(document.DocumentId, otherPeriod, i));
            }

            // Інший документ того самого періоду: теж окремий лічильник, рівно на межі + 1.
            for (var i = 0; i < 21; i++)
            {
                db.ValidationResults.Add(Run(otherDocument.DocumentId, otherDocument.PeriodKey.Value, i));
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var jobDb = chain.CreateContext())
        {
            var job = new ReportRetentionJob(jobDb, new TestClock(Start.AddDays(1)));
            await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        await using var verify = chain.CreateContext();

        async Task<List<int>> Kept(long documentId, int periodKey)
            => [.. (await verify.ValidationResults
                .AsNoTracking()
                .Where(v => v.DocumentId == documentId && v.PeriodKey == periodKey)
                .Select(v => v.ErrorCount)
                .ToListAsync(CancellationToken.None)).Order()];

        // ErrorCount несе номер прогону: лишилися 5..24 — саме НАЙНОВІШІ 20, а не довільні.
        Assert.Equal([.. Enumerable.Range(5, 20)], await Kept(document.DocumentId, period));
        Assert.Equal([0, 1, 2], await Kept(document.DocumentId, otherPeriod));
        Assert.Equal([.. Enumerable.Range(1, 20)], await Kept(otherDocument.DocumentId, otherDocument.PeriodKey.Value));

        // «Останній прогін» — те, що читають усі споживачі, — не змінився.
        var latest = await new ValidationResultStore(verify).GetLatestAsync(document.DocumentId, period, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(24, latest.ErrorCount);

        var run = await verify.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == ReportRetentionJob.Code)
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync(CancellationToken.None);
        Assert.Equal("Succeeded", run.Status);
        Assert.Matches("\"validationResultsDeleted\":([5-9]|[1-9]\\d+)", run.DetailsJson);
    }

    /// <summary>Прогін з номером <paramref name="n"/> у <c>ErrorCount</c> і зростаючим <c>RunAt</c>.</summary>
    private static ValidationResult Run(long documentId, int periodKey, int n)
        => new(documentId, periodKey, Start.AddMinutes(n), errorCount: n, warningCount: 0, infoCount: 0, messagesJson: "[]");
}
