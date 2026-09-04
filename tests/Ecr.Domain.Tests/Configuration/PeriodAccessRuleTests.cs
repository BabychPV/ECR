using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Правила доступу до періоду — заміна кнопки <c>Protect</c> (ФВ-2.15).
/// Фікстура задає аркуш `Waste_08` доступним лише в періодах 1–3.
/// </summary>
public sealed class PeriodAccessRuleTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(12, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Правило_діє_лише_для_періодів_у_заданому_діапазоні(byte sequence, bool applies)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Правило_без_меж_діє_для_всіх_періодів()
        => Assert.Fail("not implemented");
}
