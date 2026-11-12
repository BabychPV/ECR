using Ecr.Application.Recalculation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ✎ C-1 (D-475): входи помилки формули — текстом, як їх бачить людина. Число зі сховища має масштаб 16
/// (<c>decimal(34,16)</c>), і e2e-stand на справжньому стенді отримав «A=10.0000000000000000» замість «A=10».
/// </summary>
public sealed class FormulaErrorInputsTests
{
    [Theory]
    [InlineData("10.0000000000000000", "10")]
    [InlineData("0.0000000000000000", "0")]
    [InlineData("0.6666666666666667", "0.6666666666666667")]
    [InlineData("-2.5000000000000000", "-2.5")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_сховища_без_хвостових_нулів(string stored, string shown)
    {
        var value = decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(shown, FormulaErrorInputs.Text(value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Текст_як_є()
    {
        Assert.Equal("abc", FormulaErrorInputs.Text("abc"));
        Assert.Equal("True", FormulaErrorInputs.Text(true));
    }
}
