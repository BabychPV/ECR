using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Тест еквівалентності клієнт/сервер.
/// </summary>
/// <remarks>
/// Клієнтський обчислювач (<c>formulajs</c>) — **лише підказка** під час
/// введення; збережене значення завжди рахує сервер (D-20). Але якщо підказка
/// систематично розходиться з результатом, користувач перестає їй вірити —
/// і саме тому набір спільних випадків має збігатися.
///
/// Реалізація: набір виразів і очікувань зберігається у спільному JSON, який
/// читають і цей тест, і vitest-тест на клієнті (див. `06e`).
/// </remarks>
public sealed class ClientServerEquivalenceTests
{
    private static readonly JsonElement Cases = Load();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спільний_набір_виразів_дає_однакові_результати_на_сервері()
    {
        var failures = new List<string>();

        foreach (var item in Cases.EnumerateArray())
        {
            var expression = item.GetProperty("expression").GetString()!;
            var kind = item.GetProperty("kind").GetString()!;
            var expected = item.GetProperty("expected").GetString()!;

            var value = Expr.Eval(expression);

            var actual = value.Type switch
            {
                ExpressionValueType.Null => ("null", string.Empty),
                ExpressionValueType.Error => ("error", value.ErrorCode!),
                ExpressionValueType.Number => ("number", ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture)),
                ExpressionValueType.Boolean => ("boolean", (bool)value.Value! ? "true" : "false"),
                _ => ("text", value.Value?.ToString() ?? string.Empty),
            };

            if (actual.Item1 != kind || !Same(kind, actual.Item2, expected))
            {
                failures.Add($"{expression} → {actual.Item1}:{actual.Item2}, очікувалося {kind}:{expected}");
            }
        }

        // ⚠ Перевіряється ВЕСЬ набір, а не «до першої розбіжності»: якщо
        // клієнт і сервер розійшлися, треба бачити ВСІ місця розходження, бо
        // вони майже завжди одного роду.
        Assert.Empty(failures);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Набір_покриває_усі_одинадцять_функцій_діалекту_шаблонів()
    {
        var covered = Cases.EnumerateArray()
            .Select(c => c.GetProperty("function").GetString()!)
            .Where(name => name != "-")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = FunctionRegistry.Names(ExpressionDialect.Template);

        // Функція, якої немає в наборі, — це функція, чию поведінку клієнт і
        // сервер ніде не звіряють. Саме там і з'явиться перше розходження.
        Assert.Equal(11, declared.Count);
        Assert.Empty(declared.Except(covered, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Числа порівнюються як <c>decimal</c>, решта — як текст.</summary>
    private static bool Same(string kind, string actual, string expected)
        => kind == "number"
            ? decimal.Parse(actual, CultureInfo.InvariantCulture)
              == decimal.Parse(expected, CultureInfo.InvariantCulture)
            : string.Equals(actual, expected, StringComparison.Ordinal);

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expression-equivalence.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }
}
