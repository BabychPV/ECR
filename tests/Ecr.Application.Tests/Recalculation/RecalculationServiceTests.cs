// tests/Ecr.Application.Tests/Recalculation/RecalculationServiceTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>Інкрементний перерахунок за dirty-set (ФВ-3.5, ФВ-12.1).</summary>
public sealed class RecalculationServiceTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перераховується_лише_залежне_піддерево()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_аркушний_rollup_відкладається_а_не_рахується_синхронно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порядок_береться_з_публікації_а_не_будується_щоразу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Повторний_прогін_на_тих_самих_даних_дає_ті_самі_числа()
        => Assert.Fail("not implemented");
}
