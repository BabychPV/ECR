// tests/Ecr.Infrastructure.Tests/Reporting/SeededTestCaseOrderer.cs
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Порядок тестів класу за зерном <c>ECR_TEST_ORDER_SEED</c> — щоб довести незалежність тестів спільної бази класу від порядку
/// (раннер xUnit 2 перемішувати не вміє). Без змінної — порядок раннера без змін; із зерном — детерміноване перемішування
/// (спершу за іменем, далі <see cref="Random"/> із зерном), тож падіння відтворюється тим самим зерном.
/// Чутливість (правило 417) — <c>SeededTestCaseOrdererTests</c>.
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
        int? seed = !string.IsNullOrWhiteSpace(seedText)
                    && int.TryParse(seedText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        return Order(testCases, t => t.UniqueID, seed);
    }

    /// <summary>
    /// Порядок за зерном: <paramref name="seed"/> <c>null</c> — вхідний порядок без змін; інакше — сортування за ключем
    /// (ordinal), далі перемішування Фішера–Єйтса з <see cref="Random"/>(seed).
    /// </summary>
    /// <typeparam name="T">Елемент (тест-кейс).</typeparam>
    /// <param name="items">Вхідний порядок.</param>
    /// <param name="key">Стабільний ключ елемента.</param>
    /// <param name="seed">Зерно або <c>null</c>.</param>
    /// <returns>Упорядкований перелік.</returns>
    internal static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> key, int? seed)
    {
        if (seed is not { } value)
        {
            return items;
        }

        var list = items.OrderBy(key, StringComparer.Ordinal).ToList();
#pragma warning disable CA5394 // не криптографія: відтворюваний порядок тестів
        var random = new Random(value);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
#pragma warning restore CA5394

        return list;
    }
}
