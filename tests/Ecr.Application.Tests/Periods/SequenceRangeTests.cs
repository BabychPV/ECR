// tests/Ecr.Application.Tests/Periods/SequenceRangeTests.cs
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
        => Assert.Fail("not implemented");

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(0)] [InlineData(13)] [InlineData(99)]
    public void Значення_поза_межами_дають_ECR_PRD_4224(byte sequence)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Обмеження_діє_і_на_рівні_бази_а_не_лише_домену()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Custom_періодичність_теж_обмежена_дванадцятьма()
        => Assert.Fail("not implemented");
}
