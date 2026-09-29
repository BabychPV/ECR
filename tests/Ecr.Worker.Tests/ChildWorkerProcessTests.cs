// tests/Ecr.Worker.Tests/ChildWorkerProcessTests.cs

using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, I1) наскрізно: задачу перерахунку з черги в базі виконує
/// справжній процес <c>Ecr.Worker --child</c> з тестової збірки.
/// </summary>
/// <remarks>
/// Мутація (в описі коміту): лейни дочірнього <c>[JobLanes.Default]</c> — задача
/// стоїть <c>Queued</c>, тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class ChildWorkerProcessTests(SqlServerFixture sql) : ChildWorkerTestsBase(sql)
{
    [Fact]
    public async Task Дочірній_процес_бере_задачу_перерахунку_з_черги_і_виконує_її_з_роллю_wrk()
    {
        var document = await DocumentAsync();
        var jobId = await EnqueueAsync<IRecalculationJob>(RecalcOf(document));

        using var child = StartChild();
        var row = await WaitForStateAsync(jobId, "Succeeded", child.Tail);

        Assert.Equal(JobLanes.Recalc, row.Lane);

        // ⛔ Виконавець — процес ролі wrk (P3), а не цей тестовий процес і не Api.
        Assert.Contains($"/{JobProgressStore.RoleWorker}/", row.InstanceId, StringComparison.Ordinal);
        Assert.DoesNotContain(JobProgressStore.CurrentInstanceId, row.InstanceId, StringComparison.Ordinal);

        // Справжній перерахунок, а не порожній «успіх»: прогін періоду став актуальним.
        var current = Assert.Single(await RunsAsync(document.ProjectId), r => r.IsCurrent);
        Assert.Equal(document.PeriodKey.Value, current.PeriodKey);
        Assert.NotNull(current.FinishedAt);

        // Процес живий і далі чекає роботи — задача не була його останнім словом.
        Assert.False(child.Process.HasExited, child.Tail());
    }
}
