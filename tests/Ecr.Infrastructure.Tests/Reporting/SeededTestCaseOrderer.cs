// tests/Ecr.Infrastructure.Tests/Reporting/SeededTestCaseOrderer.cs
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Порядок тестів класу за зерном <c>ECR_TEST_ORDER_SEED</c> — щоб довести незалежність тестів спільної бази класу від порядку
/// (раннер xUnit 2 перемішувати не вміє). Без змінної — порядок раннера без змін; із зерном — детерміноване перемішування
/// (спершу за іменем, далі <see cref="Random"/> із зерном), тож падіння відтворюється тим самим зерном.
/// </summary>
public sealed class SeededTestCaseOrderer : ITestCaseOrderer
{
    /// <summary>Змінна оточення із зерном (ціле число).</summary>
    public const string SeedVariable = "ECR_TEST_ORDER_SEED";

    /// <inheritdoc />
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        ArgumentNullException.ThrowIfNull(testCases);
        var seedText = Environment.GetEnvironmentVariable(SeedVariable);
        if (string.IsNullOrWhiteSpace(seedText)
            || !int.TryParse(seedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seed))
        {
            return testCases;
        }

        var list = testCases.OrderBy(t => t.UniqueID, StringComparer.Ordinal).ToList();
#pragma warning disable CA5394 // не криптографія: відтворюваний порядок тестів
        var random = new Random(seed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
#pragma warning restore CA5394

        return list;
    }
}
