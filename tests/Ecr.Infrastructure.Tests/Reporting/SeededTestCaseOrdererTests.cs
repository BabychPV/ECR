// tests/Ecr.Infrastructure.Tests/Reporting/SeededTestCaseOrdererTests.cs
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Правило 417 (AU F13 до B138): <see cref="SeededTestCaseOrderer"/> чутливий до зерна. Різні зерна — різні порядки того самого
/// набору; те саме зерно — той самий порядок; без зерна — вхідний порядок без змін. Чистий unit-тест, без бази і без змінної
/// оточення (зерно передається напряму — змінна процесу спільна з паралельними класами).
/// </summary>
/// <remarks>
/// Мутація (правило 410, 17.11, збірка OK): зерно ігнорується (<c>new Random(value)</c> → <c>new Random(value * 0)</c>) —
/// червоний «Різні_зерна…» (1/3; два інші за змістом лишаються зеленими).
/// </remarks>
[Trait("Requirement", "RPT-STABLE")]
public sealed class SeededTestCaseOrdererTests
{
    private static readonly string[] Cases = [.. Enumerable.Range(1, 22).Select(i => $"case-{i:D2}")];

    [Fact]
    public void Різні_зерна_дають_різні_порядки_того_самого_набору()
    {
        var a = Order(11);
        var b = Order(22);
        var c = Order(33);

        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
        Assert.NotEqual(a, c);
        foreach (var order in new[] { a, b, c })
        {
            Assert.Equal(Cases.OrderBy(x => x, StringComparer.Ordinal), order.OrderBy(x => x, StringComparer.Ordinal));   // перестановка, без втрат
        }
    }

    [Fact]
    public void Те_саме_зерно_дає_той_самий_порядок()
    {
        Assert.Equal(Order(22), Order(22));
    }

    [Fact]
    public void Без_зерна_порядок_вхідний()
    {
        var input = Enumerable.Reverse(Cases).ToArray();
        Assert.Equal(input, SeededTestCaseOrderer.Order(input, x => x, seed: null).ToArray());
    }

    private static string[] Order(int seed) => [.. SeededTestCaseOrderer.Order(Cases, x => x, seed)];
}
