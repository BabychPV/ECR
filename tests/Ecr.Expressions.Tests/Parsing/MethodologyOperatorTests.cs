using System.Reflection;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Оператори діалекту методологій (директива №05 §4, крок <c>E-2</c>).
/// </summary>
/// <remarks>
/// ⛔ Усі три факти нижче — **виміряні** на NCalc 1.3.8
/// (`tests/Ecr.Legacy.Probe`, `docs/legacy-ncalc-1.3.8.md`), а не взяті з
/// документа. Вони не ламають компіляцію — вони ламають ЧИСЛА: формула зі
/// степенем через <c>^</c> у чинній системі рахує XOR і дає інший результат
/// мовчки, без жодної ознаки помилки.
/// </remarks>
public sealed class MethodologyOperatorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Циркумфлекс_у_діалекті_методологій_дає_ECR_CALC_0431()
    {
        // Якщо ця перевірка зникне, `2^3` у методології розбереться степенем і
        // дасть 8 — там, де чинний рушій дає 1 (XOR). Розбіжність не побачить
        // ані компілятор, ані рев'ю: обидва числа «правдоподібні».
        var result = Expr.Parse("2 ^ 3", ExpressionDialect.Methodology);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Code == ExpressionErrors.CaretNotPower);

        // Повідомлення мусить назвати заміну. Помилка без неї змушує методолога
        // здогадуватися, а єдиний степінь діалекту B — саме `Pow`: оператора
        // `**` у 1.3.8 немає теж.
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Pow(a, b)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Циркумфлекс_у_діалекті_шаблонів_лишається_степенем()
    {
        // ⚠ Заборона рівно діалектна. Діалект шаблонів — наша мова, `^` там
        // означає степінь, і 216 формул чинного шаблону написані під це.
        Assert.True(DialectSyntax.Of(ExpressionDialect.Template).CaretIsPower);
        Assert.False(DialectSyntax.Of(ExpressionDialect.Methodology).CaretIsPower);

        var result = Expr.Parse("2 ^ 3", ExpressionDialect.Template);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(8m, Expr.Number("2 ^ 3"));
    }

    [Theory]
    [InlineData("5 % 3", 2)]
    [InlineData("-5 % 3", -2)]
    [InlineData("5 % -3", 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсоток_це_залишок_зі_знаком_діленого(string expression, int expected)
    {
        // ⛔ Знак успадковується від ДІЛЕНОГО (семантика .NET), і це не те саме,
        // що математичний модуль: `-5 % 3` дає −2, а не 1. Плутанина тут коштує
        // знака в результаті, а не помилки.
        //
        // ⚠ І це не `IEEERemainder`: та сама пара (5, 3) дає там −1 (замір).
        var value = Expr.Eval(expression, dialect: ExpressionDialect.Methodology);

        Assert.Equal((decimal)expected, value.AsNumber());
    }

    [Theory]
    [InlineData("TRUE and FALSE", "TRUE && FALSE", BinaryOperator.And)]
    [InlineData("TRUE or FALSE", "TRUE || FALSE", BinaryOperator.Or)]
    [InlineData("1 = 1", "1 == 1", BinaryOperator.Equal)]
    [InlineData("1 <> 2", "1 != 2", BinaryOperator.NotEqual)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обидва_написання_оператора_дають_ОДИН_вузол(
        string wordy, string symbolic, BinaryOperator expected)
    {
        // ⛔ Перевіряється саме вузол, а не «однакова відповідь». Якби написання
        // розбиралися в різні вузли, десь у рушії лишилася б друга гілка того
        // самого оператора — і розійтися вони могли б через рік, на одній
        // правці, непомітно для тестів на значення.
        //
        // Обидві форми — виміряний факт 1.3.8, а не наша ліберальність: у
        // корпусі трапляються і `and`, і `&&`.
        var first = Parse(wordy);
        var second = Parse(symbolic);

        Assert.Equal(expected, first.Operator);
        Assert.Equal(first.Operator, second.Operator);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Роздільник_аргументів_це_властивість_діалекту()
    {
        // Замір корпусу: 643 коми проти 7 крапок з комою — у діалекті B
        // роздільник саме кома.
        Assert.Equal(',', DialectSyntax.Of(ExpressionDialect.Methodology).ArgumentSeparator);

        // ⚠ Діалект A теж кома — ПОКИ ЩО. `B03-expressions.md:108` каже `;`,
        // EBNF `02b-expressions.md:61` каже `,`, і за комою написані всі чинні
        // тести шаблону. Питання відкрите перед замовником; тест фіксує чинне
        // значення, щоб зміна прийшла рішенням, а не непоміченою правкою.
        Assert.Equal(',', DialectSyntax.Of(ExpressionDialect.Template).ArgumentSeparator);

        Assert.True(Expr.Parse("Max(1, 2)", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("Max(1; 2)", ExpressionDialect.Methodology).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Коди_каталогу_виразів_відповідають_формату_і_унікальні()
    {
        // Код помилки — частина контракту: за ним клієнт добирає текст і
        // поведінку. Код поза форматом не з'явиться ні в таблиці контракту, ні
        // в довіднику помилок — тобто мовчки випаде з нього.
        var codes = typeof(ExpressionErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal("ECR-CALC-0431", ExpressionErrors.CaretNotPower);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());

        // ⚠ Помилки-ЗНАЧЕННЯ (`#DIV/0`, `#REF`) під формат не підпадають
        // навмисно: вони живуть у комірці, а не в діагностиці, і саме тому
        // виглядають як в Excel (02b §6.4).
        Assert.All(
            codes.Where(c => c.StartsWith("ECR-", StringComparison.Ordinal)),
            code => Assert.Matches(@"^ECR-[A-Z]+-\d{4}$", code));
    }

    /// <summary>Корінь виразу діалекту методологій як бінарний вузол.</summary>
    private static BinaryNode Parse(string expression)
    {
        var result = Expr.Parse(expression, ExpressionDialect.Methodology);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return Assert.IsType<BinaryNode>(result.Expression!.Root);
    }
}
