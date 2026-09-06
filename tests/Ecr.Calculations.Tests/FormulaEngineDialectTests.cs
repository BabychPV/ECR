using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.Infrastructure.Expressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Бойовий фасад рушія виразів обчислює вираз ТИМ САМИМ діалектом, яким його
/// розібрав (<c>I.14</c>, <c>Q-082</c>).
/// </summary>
/// <remarks>
/// ⛔ Тест стоїть тут, а не серед тестів <c>Ecr.Expressions</c>, бо предмет у
/// нього інший. Там перевіряють МОВУ — розбір і обчислення окремо. Тут
/// перевіряють, що між ними не загубився діалект: <c>ParsedExpression</c> несе
/// його з розбору, і саме <see cref="FormulaEngine"/> мусить передати його
/// обчислювачу. Доки цього не було, вирази методологій рахував каталог
/// діалекту шаблонів — вигаданий набір <c>02b</c> §8.
///
/// ⚠ Обʼєкт створюється руками, а не береться з DI: предмет перевірки — саме
/// склад виклику всередині фасаду, і контейнер тут нічого не додає.
/// </remarks>
public sealed class FormulaEngineDialectTests
{
    private static readonly FormulaEngine Engine =
        new(new Parser(), new Evaluator(new FunctionRegistry()), new TopologicalSorter());

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вираз_методології_рахується_виміряним_каталогом()
    {
        // ⛔ `Pow` існує ТІЛЬКИ у виміряному наборі: у діалекті шаблонів такого
        // імені немає взагалі. Тому цей вираз і є пробою на те, чи доїхав
        // діалект від розбору до обчислення — якщо ні, виклик іде в каталог
        // шаблонів і не знаходить там нічого.
        Assert.Equal(8m, Number("Pow(2, 3)", ExpressionDialect.Methodology));

        // ⚠ І навпаки: те саме число в діалекті шаблонів пишеться оператором,
        // якого в діалекті методологій немає (`^` там — XOR, `ECR-CALC-0431`).
        // Два діалекти — дві мови, а не одна з відтінками.
        Assert.Equal(8m, Number("2 ^ 3", ExpressionDialect.Template));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Агрегат_шаблону_недосяжний_із_методології()
    {
        // ⛔ `SUM` у діалекті методологій немає ЗА ПОБУДОВОЮ МОВИ: там немає
        // діапазонів, операнди скалярні. Доки обчислювач діалекту не знав, він
        // порахував би його як шаблонний агрегат — тобто дав би число там, де
        // чинна система дала б помилку.
        var parsed = Engine.Parse("SUM(1, 2)", ExpressionDialect.Methodology);

        Assert.False(parsed.IsSuccess);
        Assert.Contains(parsed.Diagnostics, d => d.Code == "ECR-TMPL-0422");
    }

    private static decimal Number(string expression, ExpressionDialect dialect)
    {
        var parsed = Engine.Parse(expression, dialect);

        Assert.True(
            parsed.IsSuccess,
            string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var value = Engine.Evaluate(parsed.Expression!, new TestEvaluationContext()).Value;

        return value.AsNumber()
               ?? throw new InvalidOperationException(
                   $"Вираз '{expression}' дав не число, а {value.Type} ({value.ErrorCode}).");
    }
}
