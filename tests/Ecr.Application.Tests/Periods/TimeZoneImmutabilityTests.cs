// tests/Ecr.Application.Tests/Periods/TimeZoneImmutabilityTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// `TimeZoneId` незмінний після відкриття першого періоду (ФВ-1.1a, D-110):
/// ретроактивна зміна зсунула б межі **закритих** періодів і переписала б
/// `IsLateEdit` на **поданих** формах.
/// </summary>
public sealed class TimeZoneImmutabilityTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_стані_Draft_пояс_змінюється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Після_відкриття_першого_періоду_зміна_дає_ECR_PRD_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_поясу_в_Draft_перераховує_межі_періодів()
        => Assert.Fail("not implemented");
}
