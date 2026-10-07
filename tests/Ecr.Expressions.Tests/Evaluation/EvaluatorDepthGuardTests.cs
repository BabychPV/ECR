using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Межа глибини ОБЧИСЛЕННЯ: завелике дерево відхиляється значенням
/// <c>#BUDGET</c>, а не падінням процесу.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який закриває цей файл, — та сама відмова в обслуговуванні, що й у
/// <c>ExpressionDepthGuardTests</c>, але поверхом нижче, і жодна з ТРЬОХ
/// наявних меж її не бачила:
/// <list type="bullet">
/// <item><see cref="Parser.MaxRecursionDepth"/> рахує спуски ПІД ЧАС РОЗБОРУ, а
/// плаский ланцюг <c>1+1+…+1</c> розбирається ЦИКЛОМ (<c>ParseAdditive</c>):
/// глибина розбору лишається O(1), дерево виходить лівим гребенем глибиною N.
/// <c>ExpressionDepthGuardTests.Сусідні_підвирази_не_накопичують_глибину</c>
/// прямо вимагає, щоб пласкі вирази розбиралися, — і вимагає слушно;</item>
/// <item>межа довжини (2000 символів, <c>cfg.FormulaDef.Expression</c>) пускає
/// рівно 1000 доданків: <c>1+1+…+1</c> — це 1999 символів;</item>
/// <item><see cref="Evaluator.MaxEvaluationSteps"/> = 20 000 не спрацьовує:
/// такий вираз коштує ~2000 кроків, тобто десяту частину бюджету. Стек кінчався
/// перший.</item>
/// </list>
///
/// ⚠ Замір ПОЗА тестами (окремий процес, збірка <c>Release</c>, потік зі
/// стандартним стеком 1 МБ): 1000 доданків завершують процес кодом
/// <c>0xC00000FD</c> після 767 повторів циклу
/// <c>Binary → EvaluateScalar</c>; у <c>Debug</c> — після 709. Один спуск
/// коштує ~1.34–1.48 КБ стека. <c>StackOverflowException</c> у .NET не
/// перехоплюється: падав би не перерахунок однієї комірки, а хост перерахунку
/// цілком — <c>RecalculationService</c>, <c>ValidationEngine</c> і
/// <c>GenericCalculationModule</c> ходять сюди всі троє.
///
/// ⚠ Як цей файл доводить те, чого НЕ МОЖНА піймати тестом — три частини, і
/// жодна не покладається на перехоплення неперехоплюваного:
/// <list type="number">
/// <item>Аварія відтворена ПОЗА тестами, окремим процесом, де смерть процесу є
/// спостережуваним кодом виходу. Це RED, і він у описі PR.</item>
/// <item>Тут перевіряється МЕЖА — рівно те місце, де поведінка змінюється: 96
/// доданків рахуються, 97-й дає <c>#BUDGET</c>. Межа детермінована й не
/// залежить від розміру стека, тому не «мигає» від машини до машини.</item>
/// <item>Справжня атака (сто тисяч доданків) проганяється на потоці з НАВМИСНО
/// МАЛИМ стеком 256 КБ.</item>
/// </list>
/// </remarks>
public sealed class EvaluatorDepthGuardTests
{
    /// <summary>
    /// Усі оператори, що дають ПЛАСКИЙ ланцюг, тобто ліве дерево глибиною N.
    /// </summary>
    /// <remarks>
    /// ⛔ Сторож на <c>+</c> — це сторож на <c>+</c>, а не на рекурсії. Кожен із
    /// цих операторів розбирається власним циклом і будує такий самий лівий
    /// гребінь; усі вони проходять через <c>Evaluator.Binary</c>, тобто кожен
    /// клав процес самостійно (перевірено тим самим окремим процесом).
    ///
    /// ⚠ Ланцюгів ПОРІВНЯНЬ (<c>1 &lt; 1 &lt; 1</c>) і рівностей
    /// (<c>1 = 1 = 1</c>) у переліку немає не з недогляду: ці оператори
    /// НЕасоціативні, і парсер відхиляє такий текст як
    /// <c>ECR-TMPL-0422</c> ще до обчислення (виміряно тим самим стендом).
    /// Додати їх сюди означало б перевіряти неіснуючу форму.
    /// </remarks>
    public static TheoryData<string> FlatChains => ["+", "-", "*", "/", "&", "AND", "OR"];

    [Theory]
    [MemberData(nameof(FlatChains))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_плаский_ланцюг_рахується_до_тисячі_доданків(string op)
    {
        // ✎ RC5: плаский ланцюг більше НЕ впирається в межу глибини. Раніше сума
        // зі 97 доданків мовчки давала #BUDGET, хоча кроків вона коштує ~200 із
        // 20 000. 1000 доданків — стеля колонки nvarchar(2000) (`1+1+…+1` = 1999
        // символів), тобто найдовший ланцюг, який взагалі можна зберегти.
        var value = Eval(Chain(op, 1000), out var deepest, out var spent);

        Assert.False(value.IsError, $"{op}: {value.ErrorCode}");
        Assert.Equal(Expected(op, 1000), value.Value);

        // Глибина стека — не довжина ланцюга: корінь і крайній лівий операнд.
        Assert.True(deepest <= 3, $"{op}: глибина {deepest} для плаского ланцюга.");

        // Кроки лишаються справжньою мірою розміру: вузол на крок, тобто
        // 999 бінарних вузлів + 1000 літералів. Межа 20 000 тут далеко.
        Assert.Equal(1999, spent);
    }

    [Theory]
    [MemberData(nameof(FlatChains))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Плаский_ланцюг_відхиляється_межею_КРОКІВ_а_не_глибини(string op)
    {
        // ⚠ Зворотний бік: «рахувати все» теж не можна. Ланцюг, що коштує понад
        // 20 000 кроків (тут 25 000 вузлів у коді — парсер такий уже не пустить),
        // зупиняє бюджет кроків, і значення те саме #BUDGET.
        var value = EvalTree(FlatTree(op, 25_000), out var deepest, out var spent);

        Assert.Equal(ExpressionErrors.BudgetExceeded, value.ErrorCode);
        Assert.Equal(Evaluator.MaxEvaluationSteps, spent);
        Assert.True(deepest <= 3, $"{op}: глибина {deepest}: ланцюг пішов у рекурсію.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Межа_глибини_проходить_рівно_між_96_і_97_рівнями_справжньої_вкладеності()
    {
        // Число тут навмисно написане цифрами, а не виведене з константи:
        // інакше тест погоджувався б із будь-якою зміною межі замість того,
        // щоб її помітити.
        Assert.Equal(96, EvaluationBudget.MaxNestingDepth);

        // Праве вкладення `1 + (1 + (1 + …))`: N вузлів + літерал = N + 1 спуск.
        var fits = EvalTree(RightNested(95), out var deepest, out _);
        Assert.Equal(96m, fits.AsNumber());
        Assert.Equal(96, deepest);

        Assert.Equal(ExpressionErrors.BudgetExceeded, EvalTree(RightNested(96), out _, out _).ErrorCode);
    }

    [Theory]
    [MemberData(nameof(FlatChains))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Усе_що_пускає_парсер_у_плаский_ланцюг_обчислювач_рахує(string op)
    {
        // ✎ RC5, узгодження меж: стеля парсера (`Parser.MaxChainLinks` ланок) і
        // межа обчислення — різні ресурси, і ланцюг найбільшої довжини, який
        // пускає розбір, мусить рахуватися, а не мовчки давати #BUDGET.
        // 1024 ланки = 1025 операндів = 2049 кроків — десята частина бюджету.
        var terms = Parser.MaxChainLinks + 1;
        var value = Eval(Chain(op, terms), out var deepest, out var spent);

        Assert.False(value.IsError, $"{op}: {value.ErrorCode}");
        Assert.Equal(Expected(op, terms), value.Value);
        Assert.Equal(2 * terms - 1, spent);
        Assert.True(spent * 8 <= Evaluator.MaxEvaluationSteps, $"{op}: {spent} кроків — запас менший за восьмикратний.");
        Assert.True(deepest <= 3);

        // Ланка №1025 розбір уже відхиляє: далі за ним межі обчислення не питають.
        Assert.False(Expr.Parse(Chain(op, terms + 1), ExpressionDialect.Template).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вкладеність_дужками_і_гілками_IF_рахується_як_глибина_а_ланцюг_ні()
    {
        // Парсер пускає лише ~63 рівні, тож тут — те, що він справді приймає.
        var parenthesised = "1" + Repeat(" + (1", 40) + new string(')', 40);
        var value = Eval(parenthesised, out var deepest, out _);
        Assert.Equal(41m, value.AsNumber());
        Assert.Equal(41, deepest);

        var conditional = Repeat("IF(TRUE, ", 30) + "1" + Repeat(", 0)", 30);
        value = Eval(conditional, out deepest, out _);
        Assert.Equal(1m, value.AsNumber());
        Assert.True(deepest >= 30, $"IF: глибина {deepest}.");

        // Той самий ланцюг у 300 доданків — глибина не росте.
        Eval(Chain("+", 300), out deepest, out _);
        Assert.True(deepest <= 3);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Межа_вища_за_стелю_вкладеності_парсера()
    {
        // ⛔ Найважливіше обмеження знизу, і воно не про смак. Парсер приймає 63
        // рівні вкладеності (`MaxRecursionDepth` = 192 спуски по 3 на рівень), і
        // КОЖЕН такий рівень коштує один спуск обчислювача. Межа обчислення
        // ≤ 64 відхиляла б вирази, які парсер щойно прийняв, — тобто ламала б
        // уже опубліковані шаблони, а не зупиняла зловживання.
        var nested = Repeat("SUM(", 63) + "1" + new string(')', 63);

        Assert.True(Expr.Parse(nested).IsSuccess);

        var value = Eval(nested, out var deepest);

        Assert.Equal(1m, value.AsNumber());
        Assert.True(
            deepest < EvaluationBudget.MaxNestingDepth,
            $"Стеля парсера коштує {deepest} спусків при межі {EvaluationBudget.MaxNestingDepth}: "
            + "межа обчислення не пускає того, що пускає розбір.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сусідні_підвирази_не_накопичують_глибину()
    {
        // ⛔ Сторож — це ПАРА: `EnterNesting` на вході й `LeaveNesting` у
        // `finally`. Загублений вихід перетворив би межу ГЛИБИНИ на межу
        // РОЗМІРУ: довга, але пласка формула почала б відхилятися як «занадто
        // глибока». Це не гіпотеза — рівно така помилка була в чернетці сторожа
        // ПАРСЕРА (`ExpressionDepthGuardTests`).
        //
        // Двісті сусідніх аргументів, кожен — ланцюг із 50 доданків: одночасно
        // в стеку щонайбільше 52 рівні, а сумарно входів — понад десять тисяч.
        var argument = Chain("+", 50);
        var expression = "SUM(" + string.Join(", ", Enumerable.Repeat(argument, 200)) + ")";

        var value = Eval(expression, out var deepest);

        Assert.Equal(200 * 50, value.AsNumber());
        Assert.True(
            deepest <= 52,
            $"Двісті сусідніх аргументів дали глибину {deepest}: вихід із рівня губиться.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Надмірна_глибина_не_перехоплюється_через_IFERROR()
    {
        // ⛔ Без цього сторож був би гіршим за його відсутність. `IFERROR`
        // існує, щоб замінити помилку-значення запасним числом; якби він ловив
        // і відмову за глибиною, формула, зупинена межею, тихо давала б нуль —
        // і в звіт ішло б підроблене число замість видимої відмови. Та сама
        // вимога, що й для вичерпаного бюджету кроків (02b §6.4), і виконана
        // вона тим самим кодом `#BUDGET` — власне, тому код і той самий.
        // ✎ RC5: глибину тепер дає лише справжня вкладеність, тож «надмірна
        // глибина» — це праве вкладення; і межа кроків, і межа глибини
        // непіймані.
        var deep = new FunctionNode(
            "IFERROR", [RightNested(200), new LiteralNode(0m, ExpressionValueType.Number)]);
        Assert.Equal(ExpressionErrors.BudgetExceeded, EvalTree(deep, out _, out _).ErrorCode);

        var long25k = new FunctionNode(
            "IFERROR", [FlatTree("+", 25_000), new LiteralNode(0m, ExpressionValueType.Number)]);
        Assert.Equal(ExpressionErrors.BudgetExceeded, EvalTree(long25k, out _, out _).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Праве_вкладення_у_сто_тисяч_вузлів_відпрацьовує_на_стеку_256_КБ()
    {
        // Справжня глибина — те, що тепер лишилося стеку: межа 96 тримає її й на
        // потоці в чверть стандартного стека.
        ExpressionValue result = default;
        var root = RightNested(100_000);
        var thread = new Thread(
            () => result = new Evaluator(new FunctionRegistry()).Evaluate(
                root, new TestEvaluationContext(), ExpressionDialect.Template,
                new EvaluationBudget(Evaluator.MaxEvaluationSteps)),
            maxStackSize: 256 * 1024);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Обчислення не завершилося за 30 с.");

        Assert.Equal(ExpressionErrors.BudgetExceeded, result.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Атака_у_сто_тисяч_доданків_відпрацьовує_на_стеку_256_КБ()
    {
        // ⛔ Саме та форма, якою вбивався процес, — лише в сто разів глибша за
        // ту, що вміщається в колонку `nvarchar(2000)`. Потік із 256 КБ стека —
        // чверть стандартного: якби сторож пропускав рекурсію бодай на частину
        // шляху, тут вона впала б раніше, ніж будь-де в продуктиві. Виміряна
        // ціна спуску (~1.4 КБ) означає, що 96 дозволених спусків — це ~135 КБ,
        // тобто половина цього стека.
        //
        // ⚠ Виняток із потоку НЕ перехоплюється навмисно: перехоплення зробило
        // б тест зеленим і на падінні. Якщо обчислення кине — тест червоний;
        // якщо переповнить стек — процес помре, і це теж не «зелено».
        //
        // ✎ L7-01: дерево будується В КОДІ — такий ланцюг парсер тепер відхиляє
        // сам (`Parser.MaxChainLinks`), а сторож обчислювача мусить тримати
        // будь-яке дерево, не лише те, що пройшло парсер.
        AstNode root = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 1; i < 100_000; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, root, new LiteralNode(1m, ExpressionValueType.Number));
        }

        ExpressionValue result = default;
        var thread = new Thread(
            () => result = new Evaluator(new FunctionRegistry()).Evaluate(
                root, new TestEvaluationContext(), ExpressionDialect.Template,
                new EvaluationBudget(Evaluator.MaxEvaluationSteps)),
            maxStackSize: 256 * 1024);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Обчислення не завершилося за 30 с.");

        Assert.Equal(ExpressionErrors.BudgetExceeded, result.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Запас_до_межі_щонайменше_восьмикратний_від_найглибшої_справжньої_формули()
    {
        // ⛔ «Межа має бути щедрою» — це твердження про КОРПУС, а не про смак.
        // Тому глибина кожної справжньої формули не оголошується, а МІРЯЄТЬСЯ
        // тим самим лічильником, який стоїть у продуктиві
        // (`EvaluationBudget.Deepest`), — розійтися з ним вимірювання не може
        // за побудовою.
        var corpus = CorpusExpressions();
        Assert.NotEmpty(corpus);

        var deepest = 0;
        var deepestText = string.Empty;

        foreach (var (text, dialect) in corpus)
        {
            Eval(text, out var depth, dialect);
            if (depth > deepest)
            {
                deepest = depth;
                deepestText = text;
            }
        }

        Assert.True(deepest > 0);
        Assert.True(
            deepest * 8 <= EvaluationBudget.MaxNestingDepth,
            $"Запас менший за восьмикратний: найглибша формула корпусу коштує {deepest} спусків "
            + $"при межі {EvaluationBudget.MaxNestingDepth} — «{deepestText}».");
    }

    /// <summary>Плаский ланцюг із <paramref name="terms"/> операндів.</summary>
    private static string Chain(string op, int terms)
        => string.Join($" {op} ", Enumerable.Repeat(Operand(op), terms));

    private static string Operand(string op) => op switch
    {
        "&" => "'a'",
        "AND" or "OR" => "TRUE",
        _ => "1",
    };

    /// <summary>Значення ланцюга рівно з <paramref name="terms"/> операндів.</summary>
    private static object Expected(string op, int terms) => op switch
    {
        "+" => (decimal)terms,

        // Ланцюг лівоасоціативний: 1 − 1 − … − 1 = 1 − (N−1).
        "-" => 2m - terms,
        "*" or "/" => 1m,
        "&" => new string('a', terms),
        _ => true,
    };

    private static BinaryOperator Operator(string op) => op switch
    {
        "+" => BinaryOperator.Add,
        "-" => BinaryOperator.Subtract,
        "*" => BinaryOperator.Multiply,
        "/" => BinaryOperator.Divide,
        "&" => BinaryOperator.Concat,
        "AND" => BinaryOperator.And,
        _ => BinaryOperator.Or,
    };

    private static LiteralNode OperandNode(string op) => op switch
    {
        "&" => new LiteralNode("a", ExpressionValueType.Text),
        "AND" or "OR" => new LiteralNode(true, ExpressionValueType.Boolean),
        _ => new LiteralNode(1m, ExpressionValueType.Number),
    };

    /// <summary>Лівий гребінь із <paramref name="terms"/> операндів, побудований у коді.</summary>
    private static AstNode FlatTree(string op, int terms)
    {
        AstNode root = OperandNode(op);
        for (var i = 1; i < terms; i++)
        {
            root = new BinaryNode(Operator(op), root, OperandNode(op));
        }

        return root;
    }

    /// <summary>Праве вкладення <c>1 + (1 + (… + 1))</c>: <paramref name="nodes"/> бінарних вузлів.</summary>
    private static AstNode RightNested(int nodes)
    {
        AstNode root = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 0; i < nodes; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, new LiteralNode(1m, ExpressionValueType.Number), root);
        }

        return root;
    }

    private static ExpressionValue EvalTree(AstNode root, out int deepest, out int spent)
    {
        var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(root, new TestEvaluationContext(), ExpressionDialect.Template, budget);

        deepest = budget.Deepest;
        spent = budget.Spent;
        return value;
    }

    private static string Repeat(string fragment, int times)
        => string.Concat(Enumerable.Repeat(fragment, times));

    /// <summary>Обчислює вираз, віддаючи досягнуту глибину.</summary>
    private static ExpressionValue Eval(
        string expression, out int deepest, ExpressionDialect dialect = ExpressionDialect.Template)
        => Eval(expression, out deepest, out _, dialect);

    private static ExpressionValue Eval(
        string expression, out int deepest, out int spent, ExpressionDialect dialect = ExpressionDialect.Template)
    {
        var parsed = Expr.Parse(expression, dialect);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), dialect, budget);

        deepest = budget.Deepest;
        spent = budget.Spent;
        return value;
    }

    /// <summary>
    /// Кожен вираз обох фікстур — і той діалект, яким він справді розбирається.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік читається з ФАЙЛІВ фікстур, а не переписується в тест:
    /// переписаний перелік старіє мовчки. Той самий прийом, що й у
    /// <c>ExpressionDepthGuardTests</c>.
    /// </remarks>
    internal static List<(string Text, ExpressionDialect Dialect)> CorpusExpressions()
    {
        var directory = Path.Combine(Root(), "tests", "Ecr.TestKit", "Fixtures");
        var result = new List<(string, ExpressionDialect)>();

        foreach (var file in new[] { "water-demo.json", "expression-equivalence.json" })
        {
            var text = File.ReadAllText(Path.Combine(directory, file));

            foreach (Match match in Regex.Matches(text, @"""expression""\s*:\s*""((?:[^""\\]|\\.)*)"""))
            {
                var expression = Regex.Unescape(match.Groups[1].Value);

                var dialect = Expr.Parse(expression).IsSuccess
                    ? ExpressionDialect.Template
                    : ExpressionDialect.Methodology;

                Assert.True(
                    Expr.Parse(expression, dialect).IsSuccess,
                    $"Вираз корпусу не розбирається жодним діалектом: «{expression}» ({file}).");

                result.Add((expression, dialect));
            }
        }

        return result;
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено кореня репозиторію (файла Ecr.sln).");
    }
}
