// tests/Ecr.Expressions.Tests/Parsing/MethodologyReferenceFormsTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Форми посилань діалекту методологій — те, чим ЧИННИЙ корпус відрізняється
/// від нашої граматики (директива ПК-1 №05 §2a і §4, крок `I.6`).
/// </summary>
/// <remarks>
/// ⛔ Усі три розбіжності виміряні на корпусі, а не виведені з документа:
/// <list type="number">
/// <item><description>імена аргументів із крапками — 2534 з 4230 посилань
/// <c>@</c>; без них HSE400 і Flert не розбираються ВЗАГАЛІ, тобто крок
/// зупиняв би міграцію, а не псував число;</description></item>
/// <item><description>голі імена без <c>@</c> — шість назв у профільних
/// модулях, причому <c>Total</c> і <c>@Total</c> стоять в одній
/// формулі;</description></item>
/// <item><description>рядкові літерали з кирилицею — <c>'No - Нет'</c> у
/// порівнянні зі значенням довідника.</description></item>
/// </list>
/// </remarks>
public sealed class MethodologyReferenceFormsTests
{
    [Theory]
    [InlineData("@GCV.HSE400_FG_Makat_Methane", "GCV.HSE400_FG_Makat_Methane")]
    [InlineData("@GCV.Temperature", "GCV.Temperature")]
    [InlineData("@Land_Measure_Component_337", "Land_Measure_Component_337")]
    [InlineData("@Land_Measure.Component.Ratio", "Land_Measure.Component.Ratio")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дотове_ім_я_аргументу_це_ОДНЕ_посилання(string expression, string name)
    {
        // ⛔ Що ламається при регресії: лексер повертається до «`@` плюс простий
        // ідентифікатор», і `@GCV.Temperature` розпадається на `@GCV`, `.` і
        // `Temperature`. Помилка виглядає як «зайвий текст після кінця виразу»
        // — тобто вказує не на те місце і не на ту причину, — і так поводяться
        // 2534 посилання корпусу з 4230.
        var result = Expr.Parse(expression, ExpressionDialect.Methodology);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var symbol = Assert.IsType<SymbolReferenceNode>(result.Expression!.Root);
        Assert.Equal(SymbolKind.Argument, symbol.Kind);
        Assert.Equal(name, symbol.Name);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дотові_імена_у_формулі_корпусу_лишаються_окремими_аргументами()
    {
        // Форма, у якій ці імена справді трапляються: два різні аргументи
        // одного джерела `GCV`. Схлопнути їх в одне посилання означало б
        // порахувати метан замість температури — тихо і правдоподібно.
        var result = Expr.Parse(
            "@GCV.HSE400_FG_Makat_Methane * @GCV.Temperature / 100", ExpressionDialect.Methodology);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var names = Arguments(result.Expression!.Root).Select(s => s.Name).ToList();
        Assert.Equal(["GCV.HSE400_FG_Makat_Methane", "GCV.Temperature"], names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крапка_імені_не_плутається_ні_з_префіксом_CST_ні_з_десятковою()
    {
        // ⛔ Рівно та небезпека, заради якої в чинній системі стоять негативні
        // перегляди `(?<!\d)\.(?!\d)`: там заміна крапок іде по ВСЬОМУ тексту,
        // і без них `CST.k4` та `1.5` постраждали б однаково. У нас крапка
        // клеїться лише всередині імені після `@`, і ці три твердження — межа,
        // яку клей не має права перетнути.
        var constant = Assert.IsType<SymbolReferenceNode>(
            Expr.Parse("CST.k4", ExpressionDialect.Methodology).Expression!.Root);

        Assert.Equal(SymbolKind.Constant, constant.Kind);
        Assert.Equal("k4", constant.Name);

        var header = Assert.IsType<SymbolReferenceNode>(
            Expr.Parse("HDR.Train", ExpressionDialect.Methodology).Expression!.Root);

        Assert.Equal(SymbolKind.Header, header.Kind);
        Assert.Equal("Train", header.Name);

        // Десяткова крапка лишається десятковою: `1.5` — число, а не ланка
        // імені `Rate.5`.
        var product = Assert.IsType<BinaryNode>(
            Expr.Parse("@Rate * 1.5", ExpressionDialect.Methodology).Expression!.Root);

        Assert.Equal("Rate", Assert.IsType<SymbolReferenceNode>(product.Left).Name);
        Assert.Equal(1.5m, Assert.IsType<LiteralNode>(product.Right).Value);

        // І з іншого боку: крапка перед ЦИФРОЮ ім'я не продовжує. Такий текст
        // має лишитися помилкою, а не мовчки стати іменем `Rate.5`.
        Assert.False(Expr.Parse("@Rate.5", ExpressionDialect.Methodology).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Голе_ім_я_приймає_ЛИШЕ_імпортер()
    {
        // Формула профільного модуля дослівно з корпусу: `FlareUnitMode` і
        // `Duration` без `@`, `@Category` і `@Total` — з ним.
        const string formula =
            "if (FlareUnitMode = 1 && @Category = 'D Island - HP Flare', 0, "
            + "@Total * @Density / Duration)";

        var imported = Expr.Parse(formula, ExpressionDialect.Methodology, ExpressionParseMode.Import);

        Assert.True(imported.IsSuccess, string.Join("; ", imported.Diagnostics.Select(d => d.Message)));

        // ⛔ Імпортер не просто ПРИЙМАЄ голе ім'я — він його НОРМАЛІЗУЄ: у
        // дереві лишається одна форма, і друк повертає вже `@FlareUnitMode`.
        // Інакше далі по системі жили б два написання того самого параметра.
        var printed = AstPrinter.Print(imported.Expression!.Root);
        Assert.Contains("@FlareUnitMode", printed, StringComparison.Ordinal);
        Assert.Contains("@Duration", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(" Duration", printed, StringComparison.Ordinal);

        // ⛔ Що ламається при регресії: голе ім'я стає законним і в редакторі.
        // Тоді описка в назві функції (`Roud(x, 2)` без дужок, `Duraton`)
        // мовчки перетворюється на посилання на неіснуючий параметр — а
        // однозначність мови, заради якої `@` існує, зникає.
        var editor = Expr.Parse(formula, ExpressionDialect.Methodology);

        Assert.False(editor.IsSuccess);
        Assert.Contains(editor.Diagnostics, d => d.Message.Contains("голе ім'я", StringComparison.Ordinal));
        Assert.Contains(editor.Diagnostics, d => d.Message.Contains("@FlareUnitMode", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Голе_ім_я_і_префіксоване_це_ТОЙ_САМИЙ_параметр()
    {
        // У корпусі `Total` і `@Total` стоять в ОДНІЙ формулі — отже `@` у
        // чинній системі необов'язковий, і два написання означають один
        // параметр. Розвести їх на два означало б порахувати половину.
        var result = Expr.Parse("@Total + Total", ExpressionDialect.Methodology, ExpressionParseMode.Import);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var arguments = Arguments(result.Expression!.Root).ToList();

        Assert.Equal(2, arguments.Count);
        Assert.All(arguments, a => Assert.Equal(SymbolKind.Argument, a.Kind));
        Assert.Equal(arguments[0].Name, arguments[1].Name);
        Assert.Equal("(@Total + @Total)", AstPrinter.Print(result.Expression.Root));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Голе_ім_я_у_діалекті_шаблонів_не_приймається_навіть_імпортером()
    {
        // ⚠ Послаблення належить діалекту B, а не режиму імпорту як такому.
        // У шаблоні операнд без дужок — це незакрите посилання на комірку, і
        // зробити з нього аргумент означало б приховати помилку імпорту з
        // Excel замість того, щоб її показати.
        var result = Expr.Parse("Duration * 2", ExpressionDialect.Template, ExpressionParseMode.Import);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("квадратних дужках", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кириличний_рядковий_літерал_розбирається_дослівно()
    {
        // ⚠ Значення довідника, а не текст користувача: `'No - Нет'` — це
        // рядок довідника чинної системи, і будь-яка «нормалізація» пробілів
        // чи регістру зробила б порівняння хибним.
        var result = Expr.Parse(
            "if(@Land_Measure_ReleaseColdVent = 'No - Нет', 0, 1)", ExpressionDialect.Methodology);

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));

        var call = Assert.IsType<FunctionNode>(result.Expression!.Root);
        var comparison = Assert.IsType<BinaryNode>(call.Arguments[0]);
        var literal = Assert.IsType<LiteralNode>(comparison.Right);

        Assert.Equal(ExpressionValueType.Text, literal.Type);
        Assert.Equal("No - Нет", literal.Value);
    }

    [Theory]
    [InlineData("'No - Нет' = 'No - Нет'", true)]
    [InlineData("'a' = 'A'", false)]
    [InlineData("'Нет' = 'нет'", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_рядків_порядкова_і_з_урахуванням_регістру(string expression, bool expected)
    {
        // ⛔ Виміряно на NCalc 1.3.8: `'a' = 'A'` → false
        // (`docs/legacy-ncalc-1.3.8.md`, розділ «Регістр»). Порівнюється
        // ЗНАЧЕННЯ ДОВІДНИКА, а не текст людини, тому послаблення до
        // `OrdinalIgnoreCase` зробило б `'Нет'` і `'нет'` одним значенням і
        // тихо перевело б рядки з однієї гілки `if` в іншу.
        var value = Expr.Eval(expression, null, ExpressionDialect.Methodology);

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.Equal(expected, (bool)value.Value!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Регістр_ІМЕНІ_ПАРАМЕТРА_не_значущий_а_імені_ФУНКЦІЇ_значущий()
    {
        // ⛔ Асиметрія навмисна і перенесена дослівно з чинної системи:
        // параметри шукаються `StringComparer.OrdinalIgnoreCase`
        // (`Utilities.cs:186`), а `EvaluateOptions.IgnoreCase` для функцій НЕ
        // виставлений (`Utilities.cs:42`). Звести їх до одного правила — хоч в
        // один бік, хоч в інший — означає розійтися з чинними числами: або ми
        // не знаходимо параметр, який чинна система знаходить, або приймаємо
        // `POW(2,3)`, якого вона не рахувала ніколи.
        var context = new TestEvaluationContext();
        context.Arguments["Total"] = ExpressionValue.Number(5m);

        Assert.Equal(5m, Expr.Eval("@tOtAl", context, ExpressionDialect.Methodology).AsNumber());

        Assert.NotNull(DialectCatalog.Find("Pow"));
        Assert.Null(DialectCatalog.Find("POW"));

        // ⚠ Перевіряється КАТАЛОГ, а не парсер: парсер поки що звіряється з
        // вигаданим `FunctionRegistry` (регістронезалежним), і зведення двох
        // каталогів — крок `I.14` (`E-7`). Пин тут стоїть на тому боці, який
        // після I.14 лишиться єдиним.
        Assert.Null(DialectCatalog.Find("ROUND"));
    }

    /// <summary>Усі посилання на аргументи в дереві, у порядку обходу.</summary>
    private static IEnumerable<SymbolReferenceNode> Arguments(AstNode node)
    {
        if (node is SymbolReferenceNode { Kind: SymbolKind.Argument } symbol)
        {
            yield return symbol;
        }

        foreach (var child in Children(node))
        {
            foreach (var found in Arguments(child))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<AstNode> Children(AstNode node)
        => node switch
        {
            BinaryNode binary => [binary.Left, binary.Right],
            UnaryNode unary => [unary.Operand],
            ConditionalNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            FunctionNode function => function.Arguments,
            _ => [],
        };
}
