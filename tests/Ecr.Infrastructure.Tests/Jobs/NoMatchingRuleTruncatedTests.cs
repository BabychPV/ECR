// tests/Ecr.Infrastructure.Tests/Jobs/NoMatchingRuleTruncatedTests.cs
using Ecr.Application.Calculations;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>AN-69: повідомлення задачі каже, скільки рядків без правила не потрапило в таблицю через стелю на таблицю.</summary>
/// <remarks>Мутація: прибрати <c>Math.Max(0, …)</c> або стелю в <c>TruncatedIssues</c> - червоніє.</remarks>
public sealed class NoMatchingRuleTruncatedTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Понад_стелю_на_таблицю_рахується_лише_надлишок_кожної_таблиці()
    {
        List<UnmatchedRow> rows =
        [
            .. Enumerable.Range(1, 1003).Select(n => new UnmatchedRow(1, n, $"A{n}")),
            .. Enumerable.Range(1, 1000).Select(n => new UnmatchedRow(2, n, $"B{n}")),
            .. Enumerable.Range(1, 5).Select(n => new UnmatchedRow(3, n, $"C{n}")),
        ];

        Assert.Equal(3, RecalculationJob.TruncatedIssues(rows));
        Assert.Equal(0, RecalculationJob.TruncatedIssues(rows.Where(r => r.TableInstanceId != 1)));
    }
}
