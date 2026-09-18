using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
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
    public void Кожен_плаский_ланцюг_відхиляє_надмірну_глибину_значенням(string op)
    {
        // Удесятеро глибше за межу: ніякий «майже вистачило» тут не пройде.
        var value = Eval(Chain(op, EvaluationBudget.MaxNestingDepth * 10), out _);

        Assert.True(value.IsError, $"{op}: очікували помилку, дістали {value.Type} = {value.Value}.");
        Assert.Equal(ExpressionErrors.BudgetExceeded, value.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(FlatChains))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_плаский_ланцюг_рахується_рівно_до_межі(string op)
    {
        // ⚠ Зворотний бік тієї самої межі, і без нього перший тест нічого не
        // вартий: «відхиляти все» теж зробив би його зеленим.
        var value = Eval(Chain(op, EvaluationBudget.MaxNestingDepth), out var deepest);

        Assert.False(value.IsError, $"{op}: {value.ErrorCode}");
        Assert.Equal(Expected(op), value.Value);

        // Глибина названа ЧИСЛОМ: ланцюг із N доданків коштує рівно N спусків
        // (N−1 вузол лівого гребеня плюс крайній лівий літерал).
        Assert.Equal(EvaluationBudget.MaxNestingDepth, deepest);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Межа_проходить_рівно_між_96_і_97_доданками()
    {
        // Число тут навмисно написане цифрами, а не виведене з константи:
        // інакше тест погоджувався б із будь-якою зміною межі замість того,
        // щоб її помітити.
        Assert.Equal(96, EvaluationBudget.MaxNestingDepth);

        Assert.Equal(96m, Eval(Chain("+", 96), out _).AsNumber());
        Assert.Equal(ExpressionErrors.BudgetExceeded, Eval(Chain("+", 97), out _).ErrorCode);
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
        var value = Eval($"IFERROR({Chain("+", 500)}, 0)", out _);

        Assert.Equal(ExpressionErrors.BudgetExceeded, value.ErrorCode);
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
        var expression = Chain("+", 100_000);

        ExpressionValue result = default;
        var thread = new Thread(
            () => result = Eval(expression, out _),
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

    /// <summary>Значення ланцюга рівно з <see cref="EvaluationBudget.MaxNestingDepth"/> операндів.</summary>
    private static object Expected(string op) => op switch
    {
        "+" => (decimal)EvaluationBudget.MaxNestingDepth,

        // Ланцюг лівоасоціативний: 1 − 1 − … − 1 = 1 − (N−1).
        "-" => 2m - EvaluationBudget.MaxNestingDepth,
        "*" or "/" => 1m,
        "&" => new string('a', EvaluationBudget.MaxNestingDepth),
        _ => true,
    };

    private static string Repeat(string fragment, int times)
        => string.Concat(Enumerable.Repeat(fragment, times));

    /// <summary>Обчислює вираз, віддаючи досягнуту глибину.</summary>
    private static ExpressionValue Eval(
        string expression, out int deepest, ExpressionDialect dialect = ExpressionDialect.Template)
    {
        var parsed = Expr.Parse(expression, dialect);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), dialect, budget);

        deepest = budget.Deepest;
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
    private static List<(string Text, ExpressionDialect Dialect)> CorpusExpressions()
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
