using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Межа глибини рекурсивного спуску: глибоко вкладений вираз відхиляється
/// ЗВИЧАЙНОЮ діагностикою, а не падінням процесу.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який закриває цей файл, був відмовою в обслуговуванні, а не
/// помилкою розбору. <c>Parser</c> — рекурсивного спуску без жодного лічильника
/// глибини, а <c>POST /api/v1/expressions/validate</c> доступний БУДЬ-ЯКОМУ
/// автентифікованому користувачеві. Замір на цій самій збірці (окремий процес,
/// стандартний стек 1 МБ): <c>"("×606</c> вичерпує стек і CLR завершує процес
/// кодом <c>0xC00000FD</c>. <c>StackOverflowException</c> у .NET не
/// перехоплюється — падав не запит, падав сервер, разом із сеансами всіх інших.
///
/// ⚠ Як цей файл доводить те, чого НЕ МОЖНА піймати тестом. Наївний тест
/// «згодуй 10^5 дужок і переконайся, що не впало» на несправленому коді не
/// червоніє, а ВБИВАЄ хост тестів — і виглядає це як збій інфраструктури, а не
/// як знайдений дефект. Тому доказ розділено на три частини, і жодна з них не
/// покладається на перехоплення неперехоплюваного:
/// <list type="number">
/// <item>Аварія відтворена ПОЗА тестами — окремим процесом
/// (<c>depthprobe</c>), де смерть процесу є спостережуваним результатом:
/// код виходу <c>0xC00000FD</c> і стек із 606 повторів циклу
/// <c>ParseExpression → … → ParsePrimary</c>. Це RED, і він у описі PR.</item>
/// <item>Тут перевіряється МЕЖА — рівно те місце, де поведінка змінюється:
/// 63 дужки розбираються, 64-та дає діагностику. Межа детермінована й не
/// залежить від розміру стека, тому не «мигає» від машини до машини.</item>
/// <item>Справжня атака (10^5 рівнів) проганяється на потоці з
/// НАВМИСНО МАЛИМ стеком 256 КБ. Без сторожа такий потік помер би ще
/// впевненіше, ніж зі стандартним; зі сторожем рекурсія до стека просто не
/// доходить. Це доводить, що межа обрана із запасом ДО реального стека, а не
/// підігнана під стек цієї машини.</item>
/// </list>
/// </remarks>
public sealed class ExpressionDepthGuardTests
{
    /// <summary>
    /// Усі сім рекурсивних ребер граматики, кожне — окремим текстом.
    /// </summary>
    /// <remarks>
    /// ⛔ Сторож на дужках — це сторож на дужках, а не на рекурсії. Чотири
    /// ребра (дужки, аргументи функції, гілки тернарного оператора, предикат
    /// <c>[WHERE …]</c>) справді замикаються через <c>ParseExpression</c>, але
    /// три інших — <c>NOT NOT …</c>, <c>- - …</c> і правоасоціативний
    /// <c>2^2^2…</c> — замикаються НИЖЧЕ за нього і повз нього. Кожне з них
    /// валило процес самостійно.
    /// </remarks>
    public static TheoryData<string> RecursionPaths =>
    [
        "parens",
        "functionArgs",
        "ternaryWhenTrue",
        "ternaryWhenFalse",
        "unaryMinus",
        "not",
        "power",
        "predicate",
    ];

    [Theory]
    [MemberData(nameof(RecursionPaths))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_шлях_рекурсії_відхиляє_надмірну_вкладеність_діагностикою(string path)
    {
        // Удесятеро глибше за межу: ніякий «майже вистачило» тут не пройде.
        var result = Expr.Parse(Build(path, Parser.MaxRecursionDepth * 10));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Expression);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.MessageKey == "expr.nestingTooDeep");

        // Код — той самий стабільний код синтаксичної відмови, яким уже
        // відповідає решта парсера: клієнт показує це в редакторі формул, а не
        // як аварію транспорту.
        Assert.Equal(ExpressionErrors.Syntax, diagnostic.Code);
        Assert.Equal("63", diagnostic.MessageParams!["max"]);
    }

    [Theory]
    [MemberData(nameof(RecursionPaths))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_шлях_рекурсії_приймає_вкладеність_до_шістдесяти_трьох_рівнів(string path)
    {
        // ⚠ Зворотний бік тієї самої межі, і без нього перший тест нічого не
        // вартий: «відхиляти все» теж зробило б його зеленим.
        //
        // 63 — межа найдорожчих ребер (дужки, аргумент функції, гілка
        // тернарного оператора, предикат коштують по 3 спуски кожне). Дешевші
        // ребра (`NOT`, унарний знак, `^`) уміщають більше — і теж мусять
        // уміщати 63.
        var result = Expr.Parse(Build(path, 63));

        Assert.True(
            result.IsSuccess,
            $"{path}: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Межа_проходить_рівно_між_63_і_64_рівнями_вкладеності()
    {
        // Число тут навмисно написане цифрами, а не виведене з константи:
        // інакше тест погоджувався б із будь-якою зміною межі замість того,
        // щоб її помітити. 192 спуски = 63 рівні дужок (по 3 спуски на рівень
        // плюс 3 на сам вираз).
        Assert.Equal(192, Parser.MaxRecursionDepth);

        Assert.True(Expr.Parse(Build("parens", 63)).IsSuccess);

        var rejected = Expr.Parse(Build("parens", 64));
        Assert.False(rejected.IsSuccess);
        Assert.Contains(rejected.Diagnostics, d => d.MessageKey == "expr.nestingTooDeep");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сусідні_підвирази_не_накопичують_глибину()
    {
        // ⛔ Сторож — це ПАРА: `EnterNesting` на вході й `LeaveNesting` на
        // виході. Загублений `LeaveNesting` перетворив би межу глибини на межу
        // РОЗМІРУ: довга, але пласка формула (двісті доданків) почала б
        // відхилятися як «занадто вкладена». Це не гіпотеза — непарний
        // `LeaveNesting` у чернетці цього фіксу справді був.
        //
        // Двісті сусідніх аргументів, кожен глибиною 55 рівнів: одночасно в
        // стеку — щонайбільше 57 рівнів, сумарно входів — понад одинадцять
        // тисяч. Без парного виходу бюджет 192 вичерпався б на четвертому.
        var argument = new string('(', 55) + "1" + new string(')', 55);
        var expression = "SUM(" + string.Join(", ", Enumerable.Repeat(argument, 200)) + ")";

        var result = Expr.Parse(expression);

        Assert.True(
            result.IsSuccess,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Атака_у_сто_тисяч_рівнів_відпрацьовує_на_стеку_256_КБ()
    {
        // ⛔ Саме те тіло запиту, яким валили сервер. Потік з 256 КБ стека —
        // чверть стандартного: якби сторож пропускав рекурсію бодай на частину
        // шляху, тут вона впала б раніше, ніж будь-де в продуктиві.
        //
        // ⚠ Виняток із потоку НЕ перехоплюється навмисно: перехоплення зробило
        // б тест зеленим і на падінні. Якщо розбір кине — тест червоний;
        // якщо переповнить стек — процес помре, і це теж не «зелено».
        ParseResult? result = null;
        var thread = new Thread(
            () => result = Expr.Parse(Build("parens", 100_000)),
            maxStackSize: 256 * 1024);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Розбір не завершився за 30 с.");

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.nestingTooDeep");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Запас_до_межі_щонайменше_восьмикратний_від_найглибшої_справжньої_формули()
    {
        // ⛔ «Межа має бути щедрою» — це твердження про КОРПУС, а не про смак.
        // Тому глибина кожної справжньої формули не оголошується, а МІРЯЄТЬСЯ
        // поведінкою самого сторожа (див. <see cref="MeasureDepth"/>).
        var corpus = CorpusExpressions();
        Assert.NotEmpty(corpus);

        var deepest = 0;
        var deepestText = string.Empty;

        foreach (var (text, dialect) in corpus)
        {
            var depth = MeasureDepth(text, dialect);
            if (depth > deepest)
            {
                deepest = depth;
                deepestText = text;
            }
        }

        Assert.True(deepest > 0);
        Assert.True(
            deepest * 8 <= Parser.MaxRecursionDepth,
            $"Запас менший за восьмикратний: найглибша формула корпусу коштує {deepest} спусків "
            + $"при межі {Parser.MaxRecursionDepth} — «{deepestText}».");
    }

    /// <summary>
    /// Глибина виразу, виміряна ПОВЕДІНКОЮ сторожа, а не оком.
    /// </summary>
    /// <remarks>
    /// ⚠ Прийом: зайва пара дужок коштує рівно 3 спуски, тому
    /// <c>d(E) = MaxRecursionDepth − 3k</c>, де <c>k</c> — найбільша кількість
    /// пар, з якими вираз ще розбирається. Це не оцінка «на око» і не другий
    /// лічильник поруч із бойовим: міряється саме той сторож, який стоїть у
    /// продуктиві, тож розійтися з ним вимірювання не може за побудовою.
    /// </remarks>
    private static int MeasureDepth(string expression, ExpressionDialect dialect)
    {
        var low = 0;
        var high = Parser.MaxRecursionDepth / 3;

        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var wrapped = new string('(', middle) + expression + new string(')', middle);

            if (Expr.Parse(wrapped, dialect).IsSuccess)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Parser.MaxRecursionDepth - (3 * low);
    }

    /// <summary>
    /// Кожен вираз обох фікстур — і той діалект, яким він справді розбирається.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік читається з ФАЙЛІВ фікстур, а не переписується в тест.
    /// Переписаний перелік старіє мовчки: формулу у фікстурі поглиблять, а
    /// твердження «запас восьмикратний» залишиться зеленим, бо міряло б копію.
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

                // ⚠ Діалект не вгадується: він визначається тим, який із двох
                // РОЗБИРАЄ вираз. Обидва діалекти живуть в одній фікстурі
                // (`@Jan + @Feb + @Mar` — методологія, `[Jan] + [Feb]` — шаблон),
                // і сплутати їх означало б міряти глибину нерозібраного тексту.
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

    /// <summary>Текст, вкладений рівно <paramref name="levels"/> разів заданим ребром граматики.</summary>
    private static string Build(string path, int levels) => path switch
    {
        "parens" => new string('(', levels) + "1" + new string(')', levels),
        "functionArgs" => Repeat("SUM(", levels) + "1" + new string(')', levels),
        "ternaryWhenTrue" => Repeat("1 > 0 ? ", levels) + "1" + Repeat(" : 0", levels),
        "ternaryWhenFalse" => Repeat("1 > 0 ? 0 : ", levels) + "1",
        "unaryMinus" => Repeat("- ", levels) + "1",
        "not" => Repeat("NOT ", levels) + "TRUE",
        "power" => "2" + Repeat(" ^ 2", levels),
        "predicate" => Predicate(levels),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Невідоме ребро рекурсії."),
    };

    private static string Repeat(string fragment, int times)
        => string.Concat(Enumerable.Repeat(fragment, times));

    /// <summary>Предикат у предикаті: <c>[Items].[WHERE … = 1].[Amount]</c>, вкладений сам у себе.</summary>
    private static string Predicate(int levels)
    {
        var text = "[Amount]";
        for (var i = 0; i < levels; i++)
        {
            text = $"[Items].[WHERE {text} = 1].[Amount]";
        }

        return text;
    }
}
