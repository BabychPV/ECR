using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Паритет Land 13.10 (звіт LAND-PARITY-FULL, «Місяць 3 (Winter)»): у старій системі аргумент <c>Winter</c> -
/// <c>(місяць &gt;= 3 &amp;&amp; місяць &lt;= 8) ? 0 : 1</c> (ECW_C07_07.cs:90, ECW_C07_08.cs:72, ECW_C11_11_01.cs:81;
/// місяць береться з <c>Identity.Time</c>). Отже березень-серпень - літо (0), вересень-лютий - зима (1). Модель ECR
/// мала <c>Sequence &lt; 4 OR &gt; 9</c>, тобто березень = зима, вересень = літо. Тест обчислює вираз моделі для
/// кожного місяця й порівнює з правилом легасі; без перегенерації моделі (<c>run-model-gen.ps1</c>) він червоний.
/// </summary>
public sealed class LandWinterRuleModelTests
{
    private static readonly Regex Shape = new(
        @"^\(\[Period\]\.Sequence < (\d+) OR \[Period\]\.Sequence > (\d+)\) \? 1 : 0$", RegexOptions.CultureInvariant);

    private static List<string> WinterExpressions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        return doc.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray())
            .Where(c => c.GetProperty("code").GetString() == "Winter")
            .Select(c => c.GetProperty("expression").GetString()!)
            .ToList();
    }

    private static int Eval(string expression, int sequence)
    {
        var m = Shape.Match(expression);
        Assert.True(m.Success, $"Неочікувана форма виразу Winter: {expression}");
        var low = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var high = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return sequence < low || sequence > high ? 1 : 0;
    }

    // синтетична фікстура: легасі-правило ECW_C07_07/C07_08/C11_11_01
    private static int Legacy(int month) => month >= 3 && month <= 8 ? 0 : 1;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Winter_у_березні_це_літо_а_у_вересні_зима_як_у_старій_системі()
    {
        var expressions = WinterExpressions().ToList();
        Assert.NotEmpty(expressions);
        foreach (var expression in expressions)
        {
            Assert.Equal(0, Eval(expression, 3)); // березень = літо
            Assert.Equal(0, Eval(expression, 8)); // серпень = літо
            Assert.Equal(1, Eval(expression, 9)); // вересень = зима
            Assert.Equal(1, Eval(expression, 12));
            Assert.Equal(1, Eval(expression, 1));
            Assert.Equal(1, Eval(expression, 2));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Winter_збігається_з_легасі_правилом_для_всіх_дванадцяти_місяців()
    {
        foreach (var expression in WinterExpressions())
        {
            for (var month = 1; month <= 12; month++)
            {
                Assert.True(Legacy(month) == Eval(expression, month), $"Місяць {month}: модель {expression}");
            }
        }
    }
}