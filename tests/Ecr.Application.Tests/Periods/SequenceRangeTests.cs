// tests/Ecr.Application.Tests/Periods/SequenceRangeTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// `Sequence` обмежений 1…12 (ФВ-1.5a, D-108). Межа не довільна: партиційна
/// функція перелічує `YYYY01…YYYY12`, і `Sequence = 13` мовчки ліг би в
/// грудневу партицію та поїхав в архів разом із груднем.
/// </summary>
public sealed class SequenceRangeTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(1)] [InlineData(12)]
    public void Значення_в_межах_приймаються(byte sequence)
    {
        var key = PeriodCalendar.KeyFor(2026, sequence);

        Assert.Equal(2026, key.Year);
        Assert.Equal(sequence, key.Sequence);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(0)] [InlineData(13)] [InlineData(99)]
    [Trait("Requirement", "ФВ-1.5a")]
    public void Значення_поза_межами_дають_ECR_PRD_4224(byte sequence)
    {
        var error = Assert.Throws<DomainException>(() => PeriodCalendar.KeyFor(2026, sequence));

        // ⚠ 13 і 99 самі по собі валідні для PeriodKey (він допускає 1..99) —
        // і саме тому перевірка потрібна окремо: обмеження йде не від формату
        // ключа, а від меж партиційної функції.
        Assert.Equal("ECR-PRD-4224", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.11")]
    public void Обмеження_діє_і_на_рівні_бази_а_не_лише_домену()
    {
        // Одного місця замало: дані потрапляють у doc.Period не тільки через
        // домен — є генератор, є міграція legacy, є прямі виправлення DBA.
        var configuration = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Configurations",
            "DocumentConfiguration.cs"));

        Assert.Contains("CK_Period_Seq", configuration, StringComparison.Ordinal);
        Assert.Contains("Sequence BETWEEN 1 AND 12", configuration, StringComparison.Ordinal);

        var schema = File.ReadAllText(Path.Combine(
            SolutionRoot(), "docs", "build", "02a-db-schema.md"));

        Assert.Contains("CONSTRAINT CK_Period_Seq   CHECK (Sequence BETWEEN 1 AND 12)",
            schema, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Custom_періодичність_теж_обмежена_дванадцятьма()
    {
        // Custom — єдина періодичність, де кількість задає людина, і саме тому
        // єдина, де можна помилитися вгору.
        Assert.Equal(6, PeriodCalendar.CountFor(PeriodKind.Custom, customCount: 6));

        var error = Assert.Throws<DomainException>(
            () => PeriodCalendar.CountFor(PeriodKind.Custom, customCount: 13));

        Assert.Equal("ECR-PRD-4224", error.ErrorCode);
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Ecr.sln не знайдено.");
    }
}
