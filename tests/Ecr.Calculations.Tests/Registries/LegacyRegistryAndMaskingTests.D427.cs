using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Registries;

/// <summary>D-427 (Q-088): маска NaN/±∞ → 0 на кожній формулі Legacy; Strict не змінюється.</summary>
public sealed partial class LegacyRegistryAndMaskingTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public async Task D427_Legacy_проміжний_нуль_ділення_маскується_до_наступної_формули()
    {
        // A = 1/0 → B = !A + 5: легасі (`Utilities.cs:58-71`) дає 5, а не +∞.
        var legacy = await Run(NumericMode.Legacy, [("A", "1 / 0"), ("B", "!A + 5")], null, outputCode: "B");

        Assert.Equal(5m, Assert.Single(legacy.Values).Value);
        Assert.Contains(legacy.Trace, s => s.StepCode == "A" && s.Masked == MaskedZeroReason.Infinity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public async Task D427_Legacy_нуль_ділити_на_нуль_NaN_теж_маскується()
    {
        var legacy = await Run(NumericMode.Legacy, [("A", "0 / 0"), ("B", "!A + 3")], null, outputCode: "B");

        Assert.Equal(3m, Assert.Single(legacy.Values).Value);
        Assert.Contains(legacy.Trace, s => s.StepCode == "A" && s.Masked == MaskedZeroReason.NotANumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public async Task D427_Strict_проміжний_DIV0_поширюється_і_виходу_немає()
    {
        var strict = await Run(NumericMode.Strict, [("A", "1 / 0"), ("B", "!A + 5")], null, outputCode: "B");

        Assert.Empty(strict.Values);
    }
}
