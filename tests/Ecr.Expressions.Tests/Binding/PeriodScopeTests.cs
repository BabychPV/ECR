// tests/Ecr.Expressions.Tests/Binding/PeriodScopeTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// B179 L1: обсяг читання довідника за періодом (<see cref="RegistryReadScopes"/>) — на реальних формах мостів Land
/// (<c>tools/land/model-gen/hse400_bridges.py</c>: місячний шаблон; <c>LandYearMonths.Lib.ps1</c>: річна розкладка
/// переписує <c>[Period].Sequence</c> на число m).
/// </summary>
/// <remarks>
/// Мутація (локально, B179 L1): <c>OfCall</c> завжди повертає <c>All</c> → червоні всі випадки, крім «інший рік»/«OR».
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class PeriodScopeTests
{
    private const string Stream = "REGFIELD(REGFIND('LAND_HSE400_STREAM', REGFIELD([Land_TypeFuel], 'NAME')), 'PREFIX')";

    [Fact]
    public void Міст_місячного_Land_рік_і_місяць_періоду_дають_Month_а_довідник_потоків_All()
    {
        var scopes = Collect(
            $"REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, {Stream}, 'Density_20'), 'VALUE')");

        Assert.Equal(PeriodScope.Month, scopes["land_hse400_composition"]);
        Assert.Equal(PeriodScope.All, scopes["LAND_HSE400_STREAM"]);
    }

    [Fact]
    public void Річна_розкладка_з_літеральним_місяцем_дає_рік_із_маскою_місяця()
    {
        var scopes = Collect(
            $"REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, 10, {Stream}, 'FuelLowerHeating'), 'VALUE')");

        Assert.Equal(PeriodScope.FixedMonth(10), scopes["LAND_HSE400_COMPOSITION"]);
        Assert.Equal(PeriodScopeKind.Year, scopes["LAND_HSE400_COMPOSITION"].Kind);
        Assert.Equal(1 << 10, scopes["LAND_HSE400_COMPOSITION"].MonthMask);
    }

    [Theory]
    [InlineData("REGFIND('X', [Period].Year, 'A')", PeriodScopeKind.Year)]
    [InlineData("REGFIND('X', 'A', [Period].Year)", PeriodScopeKind.Year)]
    [InlineData("REGFIND('X', [Period].Year - 1, [Period].Sequence)", PeriodScopeKind.All)]
    [InlineData("REGFIND('X', [Period].Sequence, 'A')", PeriodScopeKind.All)]
    [InlineData("REGFIND('X', 'A')", PeriodScopeKind.All)]
    [InlineData("REGSUM('X', ROW.YEAR = [Period].Year && ROW.MONTH = [Period].Sequence, ROW.VALUE)", PeriodScopeKind.Month)]
    [InlineData("REGSUM('X', [Period].Year = ROW.YEAR, ROW.VALUE)", PeriodScopeKind.Year)]
    [InlineData("REGSUM('X', ROW.YEAR = [Period].Year || ROW.MONTH = [Period].Sequence, ROW.VALUE)", PeriodScopeKind.All)]
    public void Ключі_виклику_визначають_обсяг(string text, PeriodScopeKind expected)
        => Assert.Equal(expected, Collect(text)["X"].Kind);

    [Fact]
    public void Методологія_читає_склад_за_місяцем_періоду()
    {
        var scopes = Collect(
            "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, @Stream, 'Density_20'), 'VALUE')",
            ExpressionDialect.Methodology);

        Assert.Equal(PeriodScope.Month, scopes["LAND_HSE400_COMPOSITION"]);
    }

    [Fact]
    public void Кілька_викликів_одного_довідника_обʼєднуються()
    {
        Assert.Equal(
            PeriodScope.FixedMonth(3).Join(PeriodScope.FixedMonth(10)),
            Collect("REGFIND('X', [Period].Year, 3) + REGFIND('X', [Period].Year, 10)")["X"]);
        Assert.Equal((1 << 3) | (1 << 10), Collect("REGFIND('X', [Period].Year, 3) + REGFIND('X', [Period].Year, 10)")["X"].MonthMask);
        Assert.Equal(PeriodScope.Year, Collect("REGFIND('X', [Period].Year, [Period].Sequence) + REGFIND('X', [Period].Year, 10)")["X"]);
        Assert.Equal(PeriodScope.All, Collect("REGFIND('X', [Period].Year, [Period].Sequence) + REGFIND('X', 'A')")["X"]);
        Assert.Equal(PeriodScope.Month, PeriodScope.Month.Join(PeriodScope.Month));
    }

    private static IReadOnlyDictionary<string, PeriodScope> Collect(string text, ExpressionDialect dialect = ExpressionDialect.Template)
    {
        var parsed = new Parser().Parse(text, dialect);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        return RegistryReadScopes.Collect(parsed.Expression!.Root, dialect);
    }
}
