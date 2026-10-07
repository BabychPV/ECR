// tests/Ecr.Infrastructure.Tests/Jobs/MaintenanceFailureCorrelationTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// SEC (TIER2): текст провалу прогону обслуговування несе ту саму кореляцію, що стоїть у
/// рядках журналу задачі, — інакше оператор не зв'яже екранний текст із журналом.
/// </summary>
/// <remarks>
/// Доказ: до виправлення текст містив «maintenance run N» — номер, якого в журналі немає.
/// Мутація: прибрати <c>JobCorrelation.Current ??</c> у <c>MaintenanceRunFailure.Details</c> — червоний.
/// </remarks>
public sealed class MaintenanceFailureCorrelationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Текст_провалу_прогону_несе_кореляцію_задачі_без_тексту_винятку()
    {
        using var scope = JobCorrelation.Begin("0123456789abcdef0123456789abcdef");

        var json = MaintenanceRunFailure.Details(
            new InvalidOperationException("Server=db01;Password=Secret123;Database=Ecr"), runId: 77);

        Assert.Contains("(correlation 0123456789abcdef0123456789abcdef)", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("maintenance run", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Поза_задачею_лишається_запасний_номер_прогону()
    {
        Assert.Null(JobCorrelation.Current);

        var json = MaintenanceRunFailure.Details(new InvalidOperationException("boom"), runId: 77);

        Assert.Contains("maintenance run 77", json, StringComparison.Ordinal);
    }
}
