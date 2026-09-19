// tests/Ecr.Expressions.Tests/Parsing/ParseCacheTests.cs
using System.Collections.ObjectModel;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Кеш розібраних виразів (`CAL-05`): той самий текст розбирається один раз, а
/// результат спільний — і саме тому дерево мусить бути незмінним.
/// </summary>
/// <remarks>
/// ⛔ Чому кеш узагалі з'явився. <c>GenericCalculationModule</c> розбирав
/// КОЖНУ формулу версії двічі на кожен рядок документа (раз в обчисленні,
/// раз у зборі кодів констант), <c>RecalculationService</c> — раз на формулу
/// на кожен прогін. Текст формули опублікованої версії не змінюється за
/// побудовою, тобто це була та сама відповідь, порахована мільйони разів.
///
/// ⛔ Чому кеш безпечний — і чому це ПЕРЕВІРЯЄТЬСЯ, а не припускається.
/// Спільний екземпляр означає, що зіпсувати його може будь-який споживач.
/// Вузли AST — <c>record</c> із <c>init</c>-властивостями, але обидві
/// колекції дерева віддаються за інтерфейсом <c>IReadOnlyList</c>, за яким
/// цілком міг би стояти живий <c>List</c>. Тест нижче приводить їх до
/// <c>IList</c> і пробує дописати: поки ця спроба відмовляє, кешувати можна.
/// </remarks>
public sealed class ParseCacheTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Той_самий_вираз_розбирається_один_раз()
    {
        var parser = new Parser();

        var first = parser.Parse("[R1].[C1] + 1", ExpressionDialect.Template);
        var second = parser.Parse("[R1].[C1] + 1", ExpressionDialect.Template);

        // ⚠ Саме `Same`, а не `Equal`: `ParseResult` — record зі
        // структурною рівністю, тож `Equal` проходив би й БЕЗ кешу і не
        // доводив би нічого.
        Assert.Same(first, second);
        Assert.Same(first.Expression, second.Expression);
        Assert.Same(first.Expression!.Root, second.Expression!.Root);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різні_діалекти_не_діляться_записом_кеша()
    {
        // ⛔ Ціна помилки в ключі названа прямо: `^` у діалекті шаблонів —
        // степінь (`2^3 = 8`), у діалекті методологій — XOR чинного рушія
        // NCalc 1.3.8, тобто конструкція заборонена (`ECR-CALC-0431`).
        // Спільний запис кеша означав би, що формула методології порахувалася
        // за правилами шаблонів — тихо, правдоподібним числом і без жодної
        // діагностики.
        var parser = new Parser();

        var template = parser.Parse("2 ^ 3", ExpressionDialect.Template);
        var methodology = parser.Parse("2 ^ 3", ExpressionDialect.Methodology);

        Assert.True(template.IsSuccess);
        Assert.False(methodology.IsSuccess);
        Assert.Contains(methodology.Diagnostics, d => d.Code == ExpressionErrors.CaretNotPower);
        Assert.NotSame(template, methodology);

        // ⚠ І в зворотному порядку: якби ключ складався лише з тексту, перший
        // розбір визначав би відповідь другому — а який із двох перший,
        // залежало б від порядку прогону.
        var other = new Parser();
        var methodologyFirst = other.Parse("2 ^ 3", ExpressionDialect.Methodology);
        var templateSecond = other.Parse("2 ^ 3", ExpressionDialect.Template);

        Assert.False(methodologyFirst.IsSuccess);
        Assert.True(templateSecond.IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Режими_розбору_не_діляться_записом_кеша()
    {
        // ⛔ Третя частина ключа. Голе ім'я параметра приймає ЛИШЕ імпортер
        // (директива №05 §4, пункт 5): у редакторі `Total` — це відмова
        // `expr.bareNameNeedsAt`, в імпорті — посилання на аргумент. Один
        // запис на обидва означав би, що міграція читає корпус строгою
        // граматикою (не імпортувалися б профільні модулі) або що редактор
        // мовчки приймає те, чого приймати не має.
        var parser = new Parser();

        var editor = parser.Parse("Total", ExpressionDialect.Methodology);
        var import = parser.Parse("Total", ExpressionDialect.Methodology, ExpressionParseMode.Import);

        Assert.False(editor.IsSuccess);
        Assert.True(import.IsSuccess, string.Join("; ", import.Diagnostics.Select(d => d.Message)));

        var symbol = Assert.IsType<SymbolReferenceNode>(import.Expression!.Root);
        Assert.Equal(SymbolKind.Argument, symbol.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дерево_і_діагностики_незмінні_тому_їх_і_можна_ділити()
    {
        var parser = new Parser();

        // Виклик функції — єдине місце, де у вузлі живе КОЛЕКЦІЯ дітей.
        var call = parser.Parse("MAX(1, 2)", ExpressionDialect.Template);
        var function = Assert.IsType<FunctionNode>(call.Expression!.Root);

        Assert.True(
            function.Arguments is ReadOnlyCollection<AstNode>,
            $"Аргументи виклику приїхали як {function.Arguments.GetType().Name}, а не заморожені.");

        Assert.Throws<NotSupportedException>(
            () => ((IList<AstNode>)function.Arguments).Add(new LiteralNode(3m, ExpressionValueType.Number)));

        // Те саме для переліку діагностик: він теж їде в кеші.
        var broken = parser.Parse("1 +", ExpressionDialect.Template);

        Assert.NotEmpty(broken.Diagnostics);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExpressionDiagnostic>)broken.Diagnostics).Clear());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кеш_має_стелю_і_на_переповненні_очищається()
    {
        var parser = new Parser();

        // ⚠ На один вираз більше за стелю — інакше перевірка проходила б і
        // тоді, коли скидання немає взагалі.
        for (var i = 0; i <= Parser.MaxCachedExpressions; i++)
        {
            parser.Parse($"1 + {i}", ExpressionDialect.Template);
        }

        Assert.True(
            parser.CachedExpressionCount <= Parser.MaxCachedExpressions,
            $"Кеш виріс до {parser.CachedExpressionCount} записів при стелі {Parser.MaxCachedExpressions}.");

        // ⛔ І після скидання парсер лишається ПАРСЕРОМ: очищення кеша не
        // сміє змінювати відповідь, лише швидкість.
        var after = parser.Parse("1 + 1", ExpressionDialect.Template);
        Assert.True(after.IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тисяча_обчислень_із_кешем_дає_ТІ_САМІ_числа_побітно()
    {
        // ⛔ Доказ, названий директивою: кеш не сміє змінити жодного числа.
        // Звіряються не `decimal ==`, а БІТИ (`decimal.GetBits`): `1.0m` і
        // `1.00m` рівні за `==`, але це різні числа для округлення й для
        // друку у звіті, тож звичайна рівність пропустила б зміну масштабу.
        var cached = new Parser();
        var evaluator = new Evaluator(new FunctionRegistry());
        var context = new TestEvaluationContext();

        const string Text = "ROUND(1 / 3 * 1000, 4) + 2 ^ 3";

        for (var i = 0; i < 1000; i++)
        {
            // «До кешу» — свіжий парсер на кожній ітерації: його кеш порожній,
            // тобто це той самий шлях, яким код ішов до `CAL-05`.
            var fresh = new Parser().Parse(Text, ExpressionDialect.Template);
            var reused = cached.Parse(Text, ExpressionDialect.Template);

            var expected = evaluator.Evaluate(fresh.Expression!.Root, context, ExpressionDialect.Template);
            var actual = evaluator.Evaluate(reused.Expression!.Root, context, ExpressionDialect.Template);

            Assert.Equal(
                decimal.GetBits(expected.AsNumber()!.Value),
                decimal.GetBits(actual.AsNumber()!.Value));
        }

        // ⚠ І сам кеш при цьому справді працював: 1000 ітерацій дали один запис.
        Assert.Equal(1, cached.CachedExpressionCount);
    }
}
