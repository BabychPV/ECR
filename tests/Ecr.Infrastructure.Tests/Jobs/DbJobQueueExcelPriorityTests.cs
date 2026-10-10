// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueExcelPriorityTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-123 (L1-04, AUDIT-2026-10-09c): застосування імпорту Excel, на яке чекає людина, не
/// стоїть у лейні <see cref="JobLanes.Excel"/> за чергою експортів.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати пріоритетний прохід у <c>DbJobQueue.ClaimAsync</c> (або повернути
/// <c>null</c> з <see cref="JobLaneMap.PriorityJobCodeOf"/>) — перший claim бере найстаріший
/// експорт, тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueExcelPriorityTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly string ExportCode = typeof(IExcelExportJob).FullName!;

    private static readonly string[] ExcelLane = [JobLanes.Excel];

    [Fact]
    public async Task Імпорт_не_чекає_за_експортами_а_експорти_беруться_після_нього()
    {
        await using var host = NewHost();

        var exports = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            exports.Add((await host.Queue.EnqueueAsync(Excel(ExportCode), CancellationToken.None)).JobId);
        }

        var import = (await host.Queue.EnqueueAsync(Excel(JobLaneMap.ExcelImportJobCode), CancellationToken.None)).JobId;

        var first = await ClaimExcelAsync(host);
        Assert.Equal(import, first?.Claim.JobId);

        // Решта — усі експорти (порядок між постановками в одну мілісекунду вирішує JobId,
        // тому множиною, а не послідовністю).
        var rest = new List<string>();
        for (var i = 0; i < exports.Count; i++)
        {
            rest.Add((await ClaimExcelAsync(host))!.Claim.JobId);
        }

        Assert.Equal(exports.Order(StringComparer.Ordinal), rest.Order(StringComparer.Ordinal));

        Assert.Null(await ClaimExcelAsync(host));
    }

    [Fact]
    public void Пріоритет_лише_в_лейні_Excel_і_лише_для_імпорту()
    {
        Assert.Equal("Ecr.Application.Ports.IExcelImportJob", JobLaneMap.PriorityJobCodeOf(JobLanes.Excel));
        Assert.Null(JobLaneMap.PriorityJobCodeOf(JobLanes.Default));
        Assert.Null(JobLaneMap.PriorityJobCodeOf(JobLanes.Interactive));
        Assert.Null(JobLaneMap.PriorityJobCodeOf(JobLanes.Recalc));
        Assert.Contains("q.JobCode = @priorityCode", DbJobQueue.ClaimPriorityQueuedSql, StringComparison.Ordinal);
    }

    private static JobEnqueueRequest Excel(string code) => new(code, JobLanes.Excel, "{}", null);

    private static Task<ClaimedJob?> ClaimExcelAsync(Host host)
        => host.Queue.ClaimAsync(ExcelLane, "test/excel", JobQueueLimits.DefaultLease, CancellationToken.None);
}
