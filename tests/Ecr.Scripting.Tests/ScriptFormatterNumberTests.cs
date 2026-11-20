using System.Globalization;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// Текст числа константи не повинен міняти <c>double</c> числа: <c>(double)decimal</c> для мантис понад 2^53
/// (константи з БД — <c>decimal(38,16)</c>) дає сусіднє значення (HSE400, паритет Module).
/// </summary>
public sealed class ScriptFormatterNumberTests
{
    private static string Format(decimal value) => ScriptFormatter.FormatConst(ScriptConst.FromScalar(ExpressionValue.Number(value)));

    [Fact]
    public void Нулі_зрізаються_коли_double_той_самий()
    {
        Assert.Equal("1.5", Format(1.5000000000000000m));
        Assert.Equal("1000", Format(1000.0000000000000000m));
        Assert.Equal("0.8313", Format(0.8313000000000000m));
    }

    [Fact]
    public void Нулі_лишаються_коли_їх_зрізання_змінило_б_double()
    {
        var stored = 34.0760002136230000m; // як у БД: scale 16
        var trimmed = decimal.Parse("34.076000213623", CultureInfo.InvariantCulture);

        // Передумова: одне й те саме число, але різні double.
        Assert.Equal(stored, trimmed);
        Assert.NotEqual((double)stored, (double)trimmed);

        var text = Format(stored);
        Assert.Equal("34.0760002136230000", text);

        // Ідемпотентність: розібраний текст форматується в той самий текст.
        Assert.Equal(text, Format(decimal.Parse(text, CultureInfo.InvariantCulture)));
        Assert.Equal((double)stored, (double)decimal.Parse(text, CultureInfo.InvariantCulture));
    }
}
