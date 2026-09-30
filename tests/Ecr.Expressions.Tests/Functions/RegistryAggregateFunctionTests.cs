using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Агрегати довідника <c>REGSUM</c>/<c>REGAVG</c>/<c>REGMIN</c>/<c>REGMAX</c>/<c>REGCOUNT</c>
/// і область <c>ROW</c> (FEATURE-REGISTRY-TABLES §5.4, крок RT-20b; <c>D-159</c>, <c>D-162</c>).
/// </summary>
/// <remarks>
/// ⚠ Фікстура — зменшена модель FLERT: потоки, кейси потоків (складений ключ
/// <c>(STREAM, CASE_NAME)</c>, <c>Lookup</c> на потік), компоненти без
/// первинного ключа і склад газу — рядки з <c>Lookup</c> на кейс і на компонент.
/// Склад кейсу <c>1D-2 / 370 Winter</c> — СПРАВЖНІЙ (<c>hse301.xlsx</c>,
/// <c>AI_Int_SG_V8!C30:C63</c>; <c>Current_20250805</c>, рядок 217), молярні маси
/// — HYSYS, відновлені з того самого аркуша як <c>wt% · μ / об.%</c> (AC-1,
/// <c>component-mw-hysys.csv</c>). Решта складів — синтетичні й підписані як такі.
/// </remarks>
public sealed class RegistryAggregateFunctionTests
{
    private const long Stream1D2 = 1;
    private const long Winter = 101;
    private const long Summer = 102;
    private const long EmptyCase = 103;
    private const long ClosedComponent = 1999;

    /// <summary>
    /// Склад <c>1D-2 / 370 Winter</c>: код компонента, об.%, M (г/моль), атомів S.
    /// </summary>
    /// <remarks>
    /// ⚠ ТЕГ у складі з нулем і БЕЗ молярної маси — так у файлі (M не
    /// відновлюється з нульової частки): <c>0 · null</c> дає <c>null</c>, і
    /// агрегат його поглинає, а не падає й не рахує нуль «з нічого».
    /// </remarks>
    private static readonly (string Code, decimal MolPct, decimal? Mw, int Sulfur)[] WinterComposition =
    [
        ("N2", 1.4359774571349m, 28.013000488281794m, 0),
        ("CO2", 4.6822848999498m, 44.00970077514638m, 0),
        ("H2S", 12.4246685568197m, 34.07600021362303m, 1),
        ("CH4", 68.0556021130558m, 16.042900085449237m, 0),
        ("C2H6", 7.6219426101293m, 30.069900512695064m, 0),
        ("C3H8", 3.1053143341742m, 44.09700012207056m, 0),
        ("IC4", 0.4519165860107m, 58.11999893188711m, 0),
        ("NC4", 0.902793132269m, 58.11999893188226m, 0),
        ("IC5", 0.2624226431074m, 71.76000213624077m, 0),
        ("NC5", 0.265826242075m, 72.15000152588505m, 0),
        ("C6", 0.2412749847612m, 85.36000061033802m, 0),
        ("BENZENE", 0.0056350569835m, 78.11000060973538m, 0),
        ("C7", 0.1435652067024m, 99.08000183104694m, 0),
        ("TOLUENE", 0.0095255676476m, 92.14080047614132m, 0),
        ("C8", 0.0818982927185m, 113.23999786375096m, 0),
        ("PXYLENE", 0.0089821292097m, 106.16600036598548m, 0),
        ("EBENZENE", 0.0016524142844m, 106.16600036564849m, 0),
        ("C9", 0.0277375905548m, 125.19000244160426m, 0),
        ("C10", 0.0176178807172m, 137.83000183074282m, 0),
        ("C11", 0.0099150061294m, 149.00000000016496m, 0),
        ("C12", 0.0057411776812m, 162.99999999856334m, 0),
        ("C13", 0.0035587988088m, 175.9999999998319m, 0),
        ("C14", 0.0019372224797m, 190.99999999795804m, 0),
        ("CN1_35", 0.0025796589486m, 230.8500061062376m, 0),
        ("CN2_35", 0.0000101755511m, 325.3900147099979m, 0),
        ("CN3_16", 0.0000000015945m, 499.99061008932847m, 0),
        ("CH4S", 0.0040770803398m, 48.10680007977192m, 1),
        ("C2H6S", 0.0031350063593m, 62.13380050755737m, 1),
        ("C3H8S", 0.0014781344667m, 76.15000152376447m, 1),
        ("C4H10S", 0.0010456895648m, 90.18900299346983m, 1),
        ("CS2", 0.0002045618257m, 76.13050080442144m, 2),
        ("COS", 0.0016458127206m, 60.06990051316842m, 1),
        ("H2O", 0.2180339752248m, 18.015100479122548m, 0),
        ("TEG", 0m, null, 0),
    ];

    private static InMemoryRegistrySnapshot Flert()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("STREAM", ["NAME"])
            .AddRegistry("STREAM_CASE", ["STREAM", "CASE_NAME", "T_C"], ["STREAM", "CASE_NAME"], ["STREAM"])
            .AddRegistry("COMPONENT", ["MW", "N_S"])
            .AddRegistry("GAS_COMPOSITION", ["CASE", "COMPONENT", "MOL_PCT"], lookupFields: ["CASE", "COMPONENT"]);

        snapshot
            .AddEntry("STREAM", Stream1D2, "1D-2", new() { ["NAME"] = Text("1D-2") }, ordinal: 1)
            .AddEntry("STREAM", 2, "1D-3", new() { ["NAME"] = Text("1D-3") }, ordinal: 2);

        snapshot
            .AddEntry("STREAM_CASE", Winter, "C101", Case(Stream1D2, "370 Winter", 49.9999977539011m), ordinal: 1)
            .AddEntry("STREAM_CASE", Summer, "C102", Case(Stream1D2, "370 Summer", 75m), ordinal: 2)
            .AddEntry("STREAM_CASE", EmptyCase, "C103", Case(2, "370 Winter", 50m), ordinal: 3);

        var componentId = 1000L;
        var rowId = 5000L;
        foreach (var (code, molPct, mw, sulfur) in WinterComposition)
        {
            var component = new Dictionary<string, ExpressionValue> { ["N_S"] = Num(sulfur) };
            if (mw is { } m)
            {
                component["MW"] = Num(m);
            }

            snapshot.AddEntry("COMPONENT", ++componentId, code, component, ordinal: (int)(componentId - 1000));
            snapshot.AddEntry("GAS_COMPOSITION", ++rowId, $"W{rowId}", Row(Winter, componentId, molPct), ordinal: (int)(rowId - 5000));
        }

        // ⚠ Закритий на дату знімка компонент: шлях ROW.COMPONENT.MW на нього — #REF.
        snapshot.AddEntry("COMPONENT", ClosedComponent, "OLD", new() { ["MW"] = Num(1m) }, visible: false);

        // Синтетичний літній склад — лише щоб фільтр мав що відсіювати.
        snapshot
            .AddEntry("GAS_COMPOSITION", 6001, "S1", Row(Summer, 1004, 90m), ordinal: 1)
            .AddEntry("GAS_COMPOSITION", 6002, "S2", Row(Summer, 1005, 10m), ordinal: 2);

        return snapshot;
    }

    // ——— золотий приклад ———

    /// <remarks>
    /// AC-1: μ = 23.0544679165262 (<c>AI_Int_SG_V8!C26</c>), допуск 1e-9
    /// відносно — жорсткіший за ±1e-7 з DoD кроку. Формула — §5.6 дослівно.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Золотий_приклад_молярна_маса_складу_1D2_370_Winter()
    {
        var snapshot = Flert();
        var inner = new TestEvaluationContext();
        inner.Arguments["Stream"] = Num(Stream1D2);
        inner.Arguments["HmbCase"] = Text("370 Winter");
        var context = snapshot.Attach(inner);

        var mu = Expr.Eval(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', @Stream, @HmbCase), ROW.MOL_PCT * ROW.COMPONENT.MW) / 100",
            context,
            ExpressionDialect.Methodology);

        AssertClose(23.0544679165262m, mu, 1e-9m);

        // ⚠ Перебрано лише 34 рядки складу кейсу — індекс за ROW.CASE (§5.4).
        Assert.Equal(WinterComposition.Length, snapshot.RowsHandedOut);

        // S, мас.% (C27) — той самий склад, перехід ROW.COMPONENT.N_S, M_S = 32.064.
        var sulfur = Expr.Eval(
            "32.064 * REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', @Stream, @HmbCase), ROW.COMPONENT.N_S * ROW.MOL_PCT)"
            + " / (REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', @Stream, @HmbCase), ROW.MOL_PCT * ROW.COMPONENT.MW) / 100)",
            context,
            ExpressionDialect.Methodology);

        AssertClose(17.2965446772043m, sulfur, 1e-9m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Золотий_приклад_у_формулі_шаблону_з_Lookup_коміркою()
    {
        var mu = Expr.Eval(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = [HmbCase], ROW.MOL_PCT * ROW.COMPONENT.MW) / 100",
            Cells(Flert(), Num(Winter)));

        AssertClose(23.0544679165262m, mu, 1e-9m);
    }

    // ——— порожня множина ———

    /// <remarks>
    /// Мутація: у <c>Accumulator.Outcome</c> рахувати порожнє <c>REGAVG</c> як 0 —
    /// тест червоний.
    /// </remarks>
    [Theory]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.CASE = 103, ROW.MOL_PCT)", "0")]
    [InlineData("REGCOUNT('GAS_COMPOSITION', ROW.CASE = 103)", "0")]
    [InlineData("REGAVG('GAS_COMPOSITION', ROW.CASE = 103, ROW.MOL_PCT)", null)]
    [InlineData("REGMIN('GAS_COMPOSITION', ROW.CASE = 103, ROW.MOL_PCT)", null)]
    [InlineData("REGMAX('GAS_COMPOSITION', ROW.CASE = 103, ROW.MOL_PCT)", null)]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.MOL_PCT > 1000, ROW.MOL_PCT)", "0")]
    [InlineData("REGAVG('GAS_COMPOSITION', ROW.MOL_PCT > 1000, ROW.MOL_PCT)", null)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порожня_множина_сума_й_кількість_нуль_решта_null(string expression, string? expected)
    {
        // ⛔ Сума й кількість «ні з чого» — нуль (як SUM, 02b §6.1); середнє,
        // мінімум і максимум — null: нуль там стверджував би виміряне значення.
        var value = Expr.Eval(expression, Cells(Flert()));

        if (expected is null)
        {
            Assert.True(value.IsNull, $"Очікувався null, отримано {value.Type} {value.Value} {value.ErrorCode}");
        }
        else
        {
            Assert.Equal(ExpressionValueType.Number, value.Type);
            Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.AsNumber());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Довідник_без_жодного_запису_теж_порожня_множина()
    {
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("EMPTY", ["X"]);

        Assert.Equal(0m, Expr.Eval("REGSUM('EMPTY', TRUE, ROW.X)", Cells(snapshot)).AsNumber());
        Assert.Equal(0m, Expr.Eval("REGCOUNT('EMPTY', TRUE)", Cells(snapshot)).AsNumber());
        Assert.True(Expr.Eval("REGMAX('EMPTY', TRUE, ROW.X)", Cells(snapshot)).IsNull);
    }

    // ——— null поглинаються ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_виразу_поглинається_а_не_дає_нуль_у_дільнику()
    {
        var snapshot = Samples();
        var context = Cells(snapshot);

        // Значення 10, null, 20: null не входить ані в суму, ані в дільник.
        Assert.Equal(30m, Expr.Eval("REGSUM('SAMPLE', ROW.GROUP = 1, ROW.VALUE)", context).AsNumber());
        Assert.Equal(15m, Expr.Eval("REGAVG('SAMPLE', ROW.GROUP = 1, ROW.VALUE)", context).AsNumber());
        Assert.Equal(10m, Expr.Eval("REGMIN('SAMPLE', ROW.GROUP = 1, ROW.VALUE)", context).AsNumber());
        Assert.Equal(20m, Expr.Eval("REGMAX('SAMPLE', ROW.GROUP = 1, ROW.VALUE)", context).AsNumber());

        // REGCOUNT рахує рядки за фільтром — рядок із порожнім значенням теж.
        Assert.Equal(3m, Expr.Eval("REGCOUNT('SAMPLE', ROW.GROUP = 1)", context).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Фільтр_null_рядок_не_бере()
    {
        // Рядок 3 без VALUE: `null > 5` дає null — рядок не входить, а не #VALUE.
        Assert.Equal(2m, Expr.Eval("REGCOUNT('SAMPLE', ROW.VALUE > 5)", Cells(Samples())).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Незаповнене_поле_на_шляху_поглинається()
    {
        // ТЕГ без молярної маси: `ROW.COMPONENT.MW > 0` дає null — рядок не
        // входить; у золотому прикладі так само 0 · null = null поглинається.
        var snapshot = Flert();
        var withTeg = Expr.Eval("REGCOUNT('GAS_COMPOSITION', ROW.CASE = 101)", Cells(snapshot));
        var withMass = Expr.Eval(
            "REGCOUNT('GAS_COMPOSITION', ROW.CASE = 101 AND ROW.COMPONENT.MW > 0)", Cells(snapshot));

        Assert.Equal(34m, withTeg.AsNumber());
        Assert.Equal(33m, withMass.AsNumber());
    }

    // ——— мін./макс. ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGMIN_і_REGMAX_приймають_дати()
    {
        var context = Cells(Samples());

        Assert.Equal(new DateTime(2026, 1, 5), Expr.Eval("REGMIN('SAMPLE', TRUE, ROW.TAKEN)", context).Value);
        Assert.Equal(new DateTime(2026, 3, 1), Expr.Eval("REGMAX('SAMPLE', TRUE, ROW.TAKEN)", context).Value);
    }

    [Theory]
    [InlineData("REGMAX('SAMPLE', TRUE, ROW.LABEL)")]
    [InlineData("REGSUM('SAMPLE', TRUE, ROW.LABEL)")]
    [InlineData("REGAVG('SAMPLE', TRUE, ROW.TAKEN)")]
    [InlineData("REGMIN('SAMPLE', ROW.GROUP = 1, IF(ROW.VALUE = 10, ROW.TAKEN, ROW.VALUE))")]
    [InlineData("REGSUM('SAMPLE', ROW.VALUE, ROW.VALUE)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Не_той_тип_значення_чи_фільтра_дає_VALUE(string expression)
    {
        // Текст не підсумовується; дата не усереднюється; «менше» між числом і
        // датою не визначене; фільтр — не булевий. Публікацію обійшли — #VALUE.
        AssertError(ExpressionErrors.BadValue, Expr.Eval(expression, Cells(Samples())));
    }

    // ——— помилки ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Повертається_перша_помилка_за_порядком_рядків()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("COMPONENT", ["MW"])
            .AddRegistry("ROWS", ["COMPONENT", "X"], lookupFields: ["COMPONENT"])
            .AddEntry("COMPONENT", 1, "A", new() { ["MW"] = Num(10m) })
            .AddEntry("COMPONENT", 2, "OLD", new() { ["MW"] = Num(20m) }, visible: false)
            .AddEntry("ROWS", 11, "R1", new() { ["COMPONENT"] = Num(1), ["X"] = Num(4m) }, ordinal: 1)
            .AddEntry("ROWS", 12, "R2", new() { ["COMPONENT"] = Num(1), ["X"] = Num(0m) }, ordinal: 2)
            .AddEntry("ROWS", 13, "R3", new() { ["COMPONENT"] = Num(2), ["X"] = Num(1m) }, ordinal: 3);
        var context = Cells(snapshot);

        // Рядок 2 дає #DIV/0, рядок 3 — #REF (компонент закритий). Перша — #DIV/0.
        AssertError(
            ExpressionErrors.DivideByZero,
            Expr.Eval("REGSUM('ROWS', TRUE, ROW.COMPONENT.MW / ROW.X)", context));

        // Той самий #REF без рядка 2 — щоб «перша» не була збігом виду помилки.
        AssertError(
            ExpressionErrors.BadReference,
            Expr.Eval("REGSUM('ROWS', ROW.X <> 0, ROW.COMPONENT.MW / ROW.X)", context));

        // Помилка фільтра — теж перша, навіть для REGCOUNT без виразу.
        AssertError(ExpressionErrors.DivideByZero, Expr.Eval("REGCOUNT('ROWS', 1 / ROW.X > 0)", context));
    }

    [Theory]
    [InlineData("REGSUM('NOPE', TRUE, ROW.X)", ExpressionErrors.BadReference)]
    [InlineData("REGCOUNT(1, TRUE)", ExpressionErrors.BadValue)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Невідомий_довідник_дає_REF_а_код_не_текст_VALUE(string expression, string expected)
    {
        AssertError(expected, Expr.Eval(expression, Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_знімка_агрегати_дають_REF()
    {
        AssertError(
            ExpressionErrors.BadReference,
            Expr.Eval("REGSUM('GAS_COMPOSITION', TRUE, ROW.MOL_PCT)", new TestEvaluationContext()));
    }

    // ——— області ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вкладений_агрегат_затіняє_зовнішній_ROW_і_повертає_його()
    {
        // Зовнішній ROW — кейс, внутрішній — рядок складу. Після внутрішнього
        // агрегату ROW.T_C знову читає КЕЙС: у складу поля T_C немає (#REF).
        var value = Expr.Eval(
            "REGCOUNT('STREAM_CASE', REGCOUNT('GAS_COMPOSITION', ROW.MOL_PCT > 50) > 0 AND ROW.T_C < 60)",
            Cells(Flert()));

        // Внутрішній: рядків > 50 два (CH4 зими, 90 літа) — умова істинна для
        // кожного кейсу; T_C < 60 — кейси 101 і 103.
        Assert.Equal(2m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вираз_з_вкладеним_агрегатом_в_індексі_не_читає_поточний_рядок()
    {
        // Права сторона рівності — REGONE зі СВОЇМ ROW; це вираз без поточного
        // ROW, тож іде індексний шлях (перебрано лише 2 рядки літа).
        var snapshot = Flert();

        var value = Expr.Eval(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = REGONE('STREAM_CASE', ROW.CASE_NAME = '370 Summer'), ROW.MOL_PCT)",
            Cells(snapshot));

        Assert.Equal(100m, value.AsNumber());

        // 3 кейси REGONE для цілі індексу + 2 рядки літа + на КОЖНОМУ з двох
        // рядків умова рахується повністю (індекс звужує, а не замінює
        // перевірку), тобто ще двічі по 3 кейси. Повний перегляд дав би 36 + 36·3.
        Assert.Equal(3 + 2 + (2 * 3), snapshot.RowsHandedOut);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Області_рядка_в_парсері_й_обчислювачі_збігаються()
    {
        // ⚠ Два переліки (граматика `Parser.RowScopeFunctions` і
        // `RegistryForms.RowScopeNames`) мусять збігатися: інакше індексний шлях
        // прийняв би ROW. вкладеної функції за «чужу» область, або навпаки.
        foreach (var name in FunctionRegistry.Names.Where(n => n.StartsWith("REG", StringComparison.Ordinal)))
        {
            var parsed = Expr.Parse($"{name}('R', ROW.X = 1, ROW.Y)");
            var outside = parsed.Diagnostics.Any(d => d.MessageKey == "expr.rowReferenceOutsideScope");

            Assert.True(
                RegistryForms.RowScopeNames.Contains(name) != outside,
                $"{name}: у парсері {(outside ? "не " : string.Empty)}відкриває область, в обчислювачі — навпаки");
        }

        Assert.Equal(6, RegistryForms.RowScopeNames.Count);
    }

    // ——— індексний шлях і бюджет ———

    /// <remarks>
    /// Мутація: у <c>RegistryForms.Rows</c> завжди повертати
    /// <c>snapshot.GetEntries(code)</c> — тест червоний (перебрано всі 36 рядків
    /// складу замість 2).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Індексний_шлях_перебирає_лише_дітей()
    {
        var snapshot = Flert();

        var value = Expr.Eval("REGSUM('GAS_COMPOSITION', ROW.CASE = 102, ROW.MOL_PCT)", Cells(snapshot));

        Assert.Equal(100m, value.AsNumber());
        Assert.Equal(2, snapshot.RowsHandedOut);

        // Ціль зліва — те саме; REGONE іде тим самим шляхом.
        snapshot.ResetCounters();
        Assert.Equal(100m, Expr.Eval("REGSUM('GAS_COMPOSITION', 102 = ROW.CASE, ROW.MOL_PCT)", Cells(snapshot)).AsNumber());
        Assert.Equal(2, snapshot.RowsHandedOut);

        snapshot.ResetCounters();
        var one = Expr.Eval("REGONE('GAS_COMPOSITION', ROW.CASE = 102 AND ROW.MOL_PCT > 50)", Cells(snapshot));
        Assert.Equal(6001m, one.AsNumber());
        Assert.Equal(2, snapshot.RowsHandedOut);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Індекс_дає_ту_саму_відповідь_що_й_повний_перегляд()
    {
        var snapshot = Flert();
        var context = Cells(snapshot);

        // `ROW.CASE + 0 = 101` — та сама умова, але не індексна форма.
        var indexed = Expr.Eval("REGSUM('GAS_COMPOSITION', ROW.CASE = 101, ROW.MOL_PCT * ROW.COMPONENT.MW)", context);
        var indexedRows = snapshot.RowsHandedOut;
        snapshot.ResetCounters();
        var scanned = Expr.Eval("REGSUM('GAS_COMPOSITION', ROW.CASE + 0 = 101, ROW.MOL_PCT * ROW.COMPONENT.MW)", context);

        Assert.Equal(scanned.AsNumber(), indexed.AsNumber());
        Assert.Equal(34, indexedRows);
        Assert.Equal(36, snapshot.RowsHandedOut);
    }

    [Theory]
    [InlineData("REGCOUNT('SAMPLE', ROW.GROUP = [HmbCase])", 1)]
    [InlineData("REGCOUNT('SAMPLE', ROW.GROUP = 1.5)", 0)]
    [InlineData("REGCOUNT('SAMPLE', ROW.LABEL = 'b')", 1)]
    [InlineData("REGCOUNT('SAMPLE', ROW.GROUP = 1 OR ROW.GROUP = 2)", 4)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Коли_індекс_неможливий_іде_повний_перегляд(string expression, int expected)
    {
        // null-ціль: `ROW.GROUP = null` істинне саме для НЕзаповнених, а їх індекс
        // не тримає; дробова ціль — не id; поле не Lookup; OR — не верхній
        // кон'юнкт. Скрізь — усі 5 рядків і правильна відповідь.
        var snapshot = Samples();

        Assert.Equal(expected, Expr.Eval(expression, Cells(snapshot)).AsNumber());
        Assert.Equal(5, snapshot.RowsHandedOut);
    }

    /// <remarks>
    /// Мутація: вимкнути індексний шлях — друга половина червона (#BUDGET).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Повний_перегляд_30_тисяч_рядків_дає_BUDGET_а_індексний_ні()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("PARENT", ["NAME"])
            .AddRegistry("BIG", ["PARENT", "X"], lookupFields: ["PARENT"])
            .AddEntry("PARENT", 7, "P7");

        for (var id = 1; id <= 30_000; id++)
        {
            snapshot.AddEntry(
                "BIG", 100_000 + id, $"B{id}",
                new() { ["PARENT"] = Num(1 + (id % 10_000)), ["X"] = Num(id) },
                ordinal: id);
        }

        AssertError(ExpressionErrors.BudgetExceeded, Expr.Eval("REGSUM('BIG', ROW.X >= 0, ROW.X)", Cells(snapshot)));
        AssertError(ExpressionErrors.BudgetExceeded, Expr.Eval("REGCOUNT('BIG', TRUE)", Cells(snapshot)));

        // Діти P7 — рядки 6, 10 006, 20 006.
        snapshot.ResetCounters();
        var indexed = Expr.Eval("REGSUM('BIG', ROW.PARENT = 7, ROW.X)", Cells(snapshot));

        Assert.Equal(30_018m, indexed.AsNumber());
        Assert.Equal(3, snapshot.RowsHandedOut);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void BUDGET_не_перехоплюється_IFERROR_навколо_агрегату()
    {
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("BIG", ["X"]);
        for (var id = 1; id <= 30_000; id++)
        {
            snapshot.AddEntry("BIG", id, $"B{id}", ordinal: id);
        }

        AssertError(ExpressionErrors.BudgetExceeded, Expr.Eval("IFERROR(REGCOUNT('BIG', TRUE), 0)", Cells(snapshot)));
    }

    // ——— діалекти й каталоги ———

    [Theory]
    [InlineData("REGSUM")]
    [InlineData("REGAVG")]
    [InlineData("REGMIN")]
    [InlineData("REGMAX")]
    [InlineData("REGCOUNT")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегати_це_розширення_недоступні_в_Legacy(string name)
    {
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf(name));
        Assert.False(DialectCatalog.IsAllowedIn(name, NumericMode.Legacy));
        Assert.True(DialectCatalog.IsAllowedIn(name, NumericMode.Strict));
        Assert.True(RegistryForms.Handles(name, ExpressionDialect.Methodology));
        Assert.True(RegistryForms.Handles(name.ToLowerInvariant(), ExpressionDialect.Template));
        Assert.False(RegistryForms.Handles(name.ToLowerInvariant(), ExpressionDialect.Methodology));
        Assert.False(RegistryForms.Handles(name, ExpressionDialect.Report));
    }

    [Theory]
    [InlineData("REGSUM('R', TRUE)")]
    [InlineData("REGCOUNT('R', TRUE, 1)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Неправильна_кількість_аргументів_відхиляється_розбором(string expression)
    {
        Assert.False(Expr.Parse(expression).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Каталог_шаблонів_не_обчислює_агрегат_в_обхід_обчислювача()
    {
        Assert.Throws<InvalidOperationException>(
            () => new FunctionRegistry().Invoke("REGSUM", [ExpressionValue.Text("R")], new TestEvaluationContext()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дерево_з_кешу_розбору_рахується_з_різними_знімками()
    {
        // ⛔ CAL-05: дерево в кеші спільне; стан перебору живе в області рядка,
        // а не у вузлах.
        var parser = new Parser();
        const string Text = "REGSUM('GAS_COMPOSITION', ROW.CASE = 102, ROW.MOL_PCT)";
        var first = parser.Parse(Text, ExpressionDialect.Template);
        Assert.Same(first, parser.Parse(Text, ExpressionDialect.Template));

        var other = new InMemoryRegistrySnapshot()
            .AddRegistry("GAS_COMPOSITION", ["CASE", "MOL_PCT"], lookupFields: ["CASE"])
            .AddEntry("GAS_COMPOSITION", 1, "X", new() { ["CASE"] = Num(102), ["MOL_PCT"] = Num(7m) });

        var evaluator = new Evaluator(new FunctionRegistry());
        var root = first.Expression!.Root;

        Assert.Equal(100m, evaluator.Evaluate(root, Cells(Flert()), ExpressionDialect.Template).AsNumber());
        Assert.Equal(7m, evaluator.Evaluate(root, Cells(other), ExpressionDialect.Template).AsNumber());
        Assert.Equal(100m, evaluator.Evaluate(root, Cells(Flert()), ExpressionDialect.Template).AsNumber());
    }

    // ——— помічники ———

    /// <summary>
    /// Синтетичний довідник проб: група 1 — значення 10, null, 20; група 2 — 5;
    /// один рядок без групи.
    /// </summary>
    private static InMemoryRegistrySnapshot Samples()
        => new InMemoryRegistrySnapshot()
            .AddRegistry("SAMPLE_GROUP", ["NAME"])
            .AddRegistry("SAMPLE", ["GROUP", "VALUE", "TAKEN", "LABEL"], lookupFields: ["GROUP"])
            .AddEntry("SAMPLE_GROUP", 1, "G1")
            .AddEntry("SAMPLE_GROUP", 2, "G2")
            .AddEntry("SAMPLE", 11, "S1", Sample(1, 10m, new DateTime(2026, 2, 1), "a"), ordinal: 1)
            .AddEntry("SAMPLE", 12, "S2", Sample(1, null, new DateTime(2026, 1, 5), "b"), ordinal: 2)
            .AddEntry("SAMPLE", 13, "S3", Sample(1, 20m, new DateTime(2026, 3, 1), "c"), ordinal: 3)
            .AddEntry("SAMPLE", 14, "S4", Sample(2, 5m, new DateTime(2026, 2, 2), "d"), ordinal: 4)
            .AddEntry("SAMPLE", 15, "S5", Sample(null, 1m, new DateTime(2026, 2, 3), "e"), ordinal: 5);

    private static Dictionary<string, ExpressionValue> Sample(long? group, decimal? value, DateTime taken, string label)
    {
        var values = new Dictionary<string, ExpressionValue>
        {
            ["TAKEN"] = ExpressionValue.Date(taken),
            ["LABEL"] = Text(label),
        };

        if (group is { } g)
        {
            values["GROUP"] = Num(g);
        }

        if (value is { } v)
        {
            values["VALUE"] = Num(v);
        }

        return values;
    }

    private static IEvaluationContext Cells(InMemoryRegistrySnapshot snapshot, ExpressionValue? hmbCase = null)
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "HmbCase", hmbCase ?? ExpressionValue.Null);
        return snapshot.Attach(context);
    }

    private static Dictionary<string, ExpressionValue> Case(long stream, string name, decimal temperature)
        => new() { ["STREAM"] = Num(stream), ["CASE_NAME"] = Text(name), ["T_C"] = Num(temperature) };

    private static Dictionary<string, ExpressionValue> Row(long caseId, long componentId, decimal molPct)
        => new() { ["CASE"] = Num(caseId), ["COMPONENT"] = Num(componentId), ["MOL_PCT"] = Num(molPct) };

    private static ExpressionValue Num(decimal value) => ExpressionValue.Number(value);

    private static ExpressionValue Text(string value) => ExpressionValue.Text(value);

    private static void AssertClose(decimal expected, ExpressionValue actual, decimal relative)
    {
        Assert.True(
            actual.Type == ExpressionValueType.Number,
            $"Очікувалось число {expected}, отримано {actual.Type} {actual.ErrorCode}");

        var delta = Math.Abs(actual.AsNumber()!.Value - expected);
        Assert.True(delta <= Math.Abs(expected) * relative, $"{actual.AsNumber()} ≠ {expected} (Δ = {delta})");
    }

    private static void AssertError(string expected, ExpressionValue actual)
    {
        Assert.True(actual.IsError, $"Очікувалась помилка {expected}, отримано {actual.Type} {actual.Value}");
        Assert.Equal(expected, actual.ErrorCode);
    }
}
