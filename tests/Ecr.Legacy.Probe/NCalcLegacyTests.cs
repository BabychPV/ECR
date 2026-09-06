using System.Globalization;
using NCalc;
using Xunit;

namespace Ecr.Legacy.Probe;

/// <summary>
/// Поведінка обчислювача ЧИННОЇ системи — NCalc 1.3.8 (`H-24a`).
/// </summary>
/// <remarks>
/// ⛔ Це не «правильні значення». Це **те, що робить чинна система**, і
/// різниця принципова: коли завтра хтось вирішить, що банківське округлення
/// «неправильне», цей набір має нагадати, що воно **чинне**, і що змінити
/// його можна лише через <c>NumericMode.Strict</c> із нової <c>ValidFrom</c>.
///
/// ⛔ Виклик відтворює <c>Functions/Utilities.cs:42</c> дослівно:
/// <c>new Expression(key)</c> — **без** <c>EvaluateOptions</c>. Звідси і
/// банківське округлення, і чутливість імен функцій до регістру.
///
/// ⚠ П'ять очікувань директив №05 і №06 замір **не підтвердив**. Вони
/// виправлені тут за фактом і перелічені в <c>docs/legacy-ncalc-1.3.8.md</c>;
/// кожен такий тест несе позначку «✎ виправлено заміром».
/// </remarks>
public sealed class NCalcLegacyTests
{
    /// <summary>
    /// Обчислює так само, як чинна збірка.
    /// </summary>
    /// <remarks>
    /// ⚠ Параметри подаються <c>double</c> — саме так робить
    /// <c>PrepareArgumentsForCalculateExpression</c> (<c>Utilities.cs:188-215</c>):
    /// будь-яке число і будь-який рядок, що розібрався
    /// <c>double.TryParse(InvariantCulture)</c>, стають <c>double</c>.
    /// </remarks>
    private static object Eval(string expression, Dictionary<string, object>? parameters = null)
    {
        var e = new Expression(expression);

        if (parameters is not null)
        {
            // Словник параметрів чинної збірки — БЕЗ урахування регістру
            // (`Utilities.cs:186`), на відміну від імен функцій.
            e.Parameters = new Dictionary<string, object>(parameters, StringComparer.OrdinalIgnoreCase);
        }

        return e.Evaluate();
    }

    private static double Number(object value)
        => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    // ─────────────────────────────────────────────────────────────────────────
    // Округлення: банківське, бо `RoundAwayFromZero` не виставлений
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Round(2.5,0)", 2d)]
    [InlineData("Round(0.5,0)", 0d)]
    [InlineData("Round(1.5,0)", 2d)]
    [InlineData("Round(3.5,0)", 4d)]
    public void Round_банківське_бо_EvaluateOptions_не_передаються(string expression, double expected)
    {
        // ⛔ `Round(3.5,0) = 4` — контроль: він збігається за ОБОХ правил.
        // Якщо не збігається — зламано стенд, а не припущення.
        Assert.Equal(expected, Number(Eval(expression)));
    }

    [Fact]
    public void Round_2_675_дає_2_68_а_не_2_67()
    {
        // ✎ **Виправлено заміром.** Директива №06 §7 очікувала `2.67` із
        // міркування «`double`, а не `decimal`». Міркування правильне,
        // висновок — ні: подвійне подання `2.675` дорівнює
        // 2.67500000000000026645…, тобто трохи БІЛЬШЕ за 2.675, і банківське
        // округлення відправляє його вгору.
        //
        // ⚠ Значення лишається доказом того, що рахунок іде в `double`:
        // у `decimal` 2.675 — це рівно 2.675, і `ToEven` дало б 2.68 теж,
        // але через інше правило. Тому цей тест доводить не тип, а число.
        Assert.Equal(2.68d, Number(Eval("Round(2.675,2)")));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ділення: цілочисельного НЕМАЄ ВЗАГАЛІ
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12/163", 0.0736196319018405d)]
    [InlineData("365/31", 11.774193548387096d)]
    [InlineData("13/176", 0.07386363636363637d)]
    [InlineData("3747/10000", 0.3747d)]
    [InlineData("30/100", 0.3d)]
    [InlineData("1813100/100", 18131d)]
    public void Ділення_цілих_ЛІТЕРАЛІВ_теж_дробове(string expression, double expected)
    {
        // ✎ **Виправлено заміром — і це скасовує цілу пастку.**
        //
        // ⛔ Директива №05 §7 (пастка 1) стверджує: «`365/31 = 11`. Обидва
        // операнди цілі → цілочисельне ділення», і §11 крок 5 вимагає
        // відтворити це в `Legacy`. Замір каже інше: **11.774193548387096**.
        //
        // NCalc 1.3.8 ділить у `double` ЗАВЖДИ. Директива №05 §2-ter закрила
        // половину пастки (константи йдуть параметрами як `double`); замір
        // закриває другу — літерали діляться так само. Цілочисельного ділення
        // в чинному рушії **не існує в жодному вигляді**.
        //
        // ⚠ Наслідок: валідатор «ціле/ціле двома літералами» попереджав би
        // про поведінку, якої немає, — тобто вчив би обходити неіснуючу ваду.
        var actual = Number(Eval(expression));

        Assert.Equal(expected, actual, 12);
    }

    [Fact]
    public void Ділення_параметрів_дробове_бо_вони_double()
    {
        // Закриття `V-1a`: побоювання про нульові вагові частки
        // C12+/C13+/C14+ у чинній звітності **не підтвердилося**.
        var result = Eval("a/b", new Dictionary<string, object> { ["a"] = 12d, ["b"] = 163d });

        Assert.Equal(0.073619631901840496d, Number(result), 12);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Оператори
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Дашка_це_XOR_а_не_степінь()
    {
        // ⛔ Через це `^` заборонений у нашому діалекті B: вираз, який
        // виглядає як степінь, мовчки рахував би побітове виключне «або».
        Assert.Equal(1d, Number(Eval("2^3")));
    }

    [Fact]
    public void Оператора_степеня_НЕМАЄ()
    {
        // ✎ **Виправлено заміром.** Директива №05 §11 крок 2 вимагає
        // `2**3 = 8`. У NCalc 1.3.8 `**` — синтаксична помилка; степінь є
        // рівно одна — `Pow(a, b)`.
        Assert.ThrowsAny<Exception>(() => Eval("2**3"));
    }

    [Fact]
    public void Степінь_це_Pow()
        => Assert.Equal(8d, Number(Eval("Pow(2,3)")));

    [Theory]
    [InlineData("5%3", 2d)]
    [InlineData("1 and 1", 1d)]
    [InlineData("1 && 1", 1d)]
    public void Оператори_корпусу_працюють(string expression, double expected)
    {
        // `and`/`&&`, `<>`/`!=`, `=`/`==` — обидві форми, як у корпусі.
        Assert.Equal(expected, Number(Eval(expression)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Каталог: що в 1.3.8 Є, а чого немає
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Abs(-1)")]
    [InlineData("Acos(1)")]
    [InlineData("Asin(0)")]
    [InlineData("Atan(0)")]
    [InlineData("Ceiling(1.2)")]
    [InlineData("Cos(0)")]
    [InlineData("Exp(0)")]
    [InlineData("Floor(1.8)")]
    [InlineData("IEEERemainder(5,3)")]
    [InlineData("Log(8,2)")]
    [InlineData("Log10(100)")]
    [InlineData("Max(1,2)")]
    [InlineData("Min(1,2)")]
    [InlineData("Pow(2,3)")]
    [InlineData("Round(1.5,0)")]
    [InlineData("Sign(-5)")]
    [InlineData("Sin(0)")]
    [InlineData("Sqrt(4)")]
    [InlineData("Tan(0)")]
    [InlineData("Truncate(1.9)")]
    [InlineData("in(1,1,2)")]
    [InlineData("if(true,1,2)")]
    public void Двадцять_дві_функції_є(string expression)
        => Assert.NotNull(Eval(expression));

    [Theory]
    [InlineData("Ln(1)")]
    [InlineData("ifs(true,1,2)")]
    public void Двох_функцій_каталогу_в_рушії_НЕМАЄ(string expression)
    {
        // ✎ **Виправлено заміром.** Каталог із 24 у директиві №05 §3 — це
        // набір редактора **PI Vision**, а не перелік того, що вміє NCalc
        // 1.3.8. Сама директива §2-bis це передбачала: «у версії 1.3.8 `ifs`
        // майже напевно немає, `Ln` — під питанням. Спершу поміряй».
        //
        // Поміряно: обох немає. Отже чинний набір — **22 функції**.
        // Натуральний логарифм у чинній системі недосяжний: `Log` строго
        // двоаргументний, а `Ln` не оголошений.
        var error = Assert.ThrowsAny<Exception>(() => Eval(expression));

        Assert.Contains("Function not found", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ceiling(1.5,0.5)")]
    [InlineData("Floor(1.5,0.5)")]
    [InlineData("Truncate(1.55,1)")]
    [InlineData("Max(1,2,3)")]
    [InlineData("Log(8)")]
    [InlineData("Round(1.5)")]
    public void Арність_перевіряється_суворо(string expression)
    {
        // `Ceiling`/`Floor`/`Truncate` — рівно один аргумент;
        // `Max`/`Min`/`Log`/`Round` — рівно два. Наш каталог дозволяв
        // `CEILING(a, significance)` і варіативний `MAX` — обидва вигадані.
        Assert.ThrowsAny<Exception>(() => Eval(expression));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Регістр: функції чутливі, параметри — ні
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POW(2,3)")]
    [InlineData("ROUND(2.5,0)")]
    [InlineData("abs(1)")]
    public void Імена_функцій_чутливі_до_регістру(string expression)
        => Assert.ThrowsAny<Exception>(() => Eval(expression));

    [Fact]
    public void Імена_параметрів_НЕ_чутливі_до_регістру()
    {
        // ⚠ Асиметрія навмисна і переноситься буквально: у корпусі `Total` і
        // `@Total` вживаються в одній формулі.
        var result = Eval("Total", new Dictionary<string, object> { ["total"] = 42d });

        Assert.Equal(42d, Number(result));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Текст — діалект B не суто числовий
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Формула_може_повертати_текст()
    {
        // Саме тому `FormulaDef` потрібен `ResultType { Number, Text }`:
        // `'В пределе норматива'` / `'Сверхнорматив'` — результати формул.
        Assert.Equal("так", Eval("if(1=1,'так','ні')"));
    }

    [Fact]
    public void Порівняння_рядків_з_урахуванням_регістру()
    {
        Assert.True((bool)Eval("'a' = 'a'"));
        Assert.False((bool)Eval("'a' = 'A'"));
    }

    [Fact]
    public void Кирилиця_в_літералі_розбирається()
    {
        // У корпусі: `if(@Land_Measure_ReleaseColdVent = 'No - Нет', 0, …)`.
        var result = Eval(
            "if(X = 'No - Нет', 0, 1)",
            new Dictionary<string, object> { ["X"] = "No - Нет" });

        Assert.Equal(0d, Number(result));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Збої, які чинна система перетворює на нуль (`H-24d-1`)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1/0", double.PositiveInfinity)]
    [InlineData("12/0", double.PositiveInfinity)]
    public void Ділення_на_нуль_дає_нескінченність_а_не_виняток(string expression, double expected)
    {
        // ✎ **Виправлено заміром.** Директива №06 §7 очікувала
        // `DivideByZeroException`, яку `Utilities.cs:102` ловить і повертає
        // `"0"`. Насправді NCalc ділить у `double`, тож виходить `+∞` —
        // і нуль з'являється **іншою гілкою**: `Utilities.cs:58`
        // (`double.IsInfinity(d) → d = 0.0`).
        //
        // ⛔ Для нас це не косметика: причина в ознаці `MaskedZero` має бути
        // `Infinity`, а не `DivideByZero`. Той `catch` у чинному коді для
        // ділення на нуль **не спрацьовує ніколи**.
        Assert.Equal(expected, Number(Eval(expression)));
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("Sqrt(-1)")]
    public void NaN_повертається_значенням_а_не_винятком(string expression)
    {
        // `Utilities.cs:56-71` перетворює на `0.0` — мовчки. Це і є найтихіше
        // місце чинної системи: різні збої дають нуль, невідрізненний від
        // справжнього нуля.
        Assert.True(double.IsNaN(Number(Eval(expression))));
    }

    [Fact]
    public void Відсутній_параметр_кидає()
    {
        // `Utilities.cs:106-131` ловить і розрізняє `CST_*` та `Land_*` за іменем.
        Assert.ThrowsAny<Exception>(() => Eval("НемаТакого + 1"));
    }
}
