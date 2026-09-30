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
/// Пошук у довідниках: <c>REGFIND</c>, <c>REGONE</c>, <c>REGFIELD</c> зі шляхом
/// (FEATURE-REGISTRY-TABLES §5.4–§5.5, крок RT-20a; <c>D-159</c>…<c>D-162</c>).
/// </summary>
/// <remarks>
/// ⚠ Фікстура — зменшена модель FLERT (§5.6, §11.4): групи потоків, потоки
/// з <c>Lookup</c> на групу, кейси потоків зі складеним первинним ключем
/// <c>(STREAM, CASE_NAME)</c>, компоненти без первинного ключа. Невидимі на дату
/// знімка записи оголошено явно (<c>visible: false</c>) — лише щоб тест
/// читався як «запис закрито»; для рушія їх просто немає (контракт
/// <see cref="IRegistrySnapshot"/>).
/// </remarks>
public sealed class RegistryLookupFunctionTests
{
    private static InMemoryRegistrySnapshot Flert()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("STREAM_GROUP", ["TITLE"])
            .AddRegistry("STREAM", ["NAME", "GROUP"], lookupFields: ["GROUP"])
            .AddRegistry("STREAM_CASE", ["STREAM", "CASE_NAME", "T_C"], ["STREAM", "CASE_NAME"], ["STREAM"])
            .AddRegistry("COMPONENT", ["MW", "N_S"]);

        snapshot
            .AddEntry("STREAM_GROUP", 10, "FLARE", new() { ["TITLE"] = Text("Flare") }, ordinal: 1)
            .AddEntry("STREAM_GROUP", 99, "OLD", new() { ["TITLE"] = Text("Old") }, ordinal: 2, visible: false);

        snapshot
            .AddEntry("STREAM", 1, "1D-2", new() { ["NAME"] = Text("1D-2"), ["GROUP"] = Num(10) }, ordinal: 1)
            .AddEntry("STREAM", 2, "1D-3", new() { ["NAME"] = Text("1D-3") }, ordinal: 2)
            .AddEntry("STREAM", 3, "1D-4", new() { ["NAME"] = Text("1D-4"), ["GROUP"] = Num(99) }, ordinal: 3);

        snapshot
            .AddEntry("STREAM_CASE", 101, "C101", Case(1, "370 Winter", 12.5m), ordinal: 1)
            .AddEntry("STREAM_CASE", 102, "C102", Case(1, "370 Summer", 30m), ordinal: 2)
            // ⚠ Пошкоджені дані: два живі записи з тим самим ключем. У робочій
            // базі їх не пускає UX_RegistryEntryKey_Live (RT-01); REGFIND мусить
            // сказати #MULTI, а не взяти «перший».
            .AddEntry("STREAM_CASE", 103, "C103", Case(2, "Broken", 1m), ordinal: 3)
            .AddEntry("STREAM_CASE", 104, "C104", Case(2, "Broken", 2m), ordinal: 4);

        snapshot
            .AddEntry("COMPONENT", 201, "H2S", Component(34.08m, 1), ordinal: 1)
            .AddEntry("COMPONENT", 202, "CH4", Component(16.04m, 0), ordinal: 2)
            .AddEntry("COMPONENT", 203, "C2H6", Component(30.07m, 0), ordinal: 3)
            .AddEntry("COMPONENT", 204, "SO2", Component(64.07m, 1), ordinal: 4, visible: false);

        return snapshot;
    }

    // ——— REGFIND ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_знаходить_запис_за_складеним_первинним_ключем()
    {
        var value = Expr.Eval("REGFIND('STREAM_CASE', [Stream], [HmbCase])", Cells(Flert()));

        AssertEntry(101, value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_без_первинного_ключа_шукає_за_кодом_запису()
    {
        AssertEntry(201, Expr.Eval("REGFIND('COMPONENT', 'H2S')", Cells(Flert())));
    }

    [Theory]
    [InlineData("REGFIND('STREAM_CASE', 1, '999 Nowhere')")]
    [InlineData("REGFIND('COMPONENT', 'SO2')")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_не_знайшов_дає_NA(string expression)
    {
        // ⛔ #N/A, а не #REF: опис довідника цілий, у ДАНИХ немає запису
        // (другий випадок — запис є, але закритий на дату знімка). Виправляють
        // довідник, а не формулу, і код мусить це розрізняти.
        AssertError(ExpressionErrors.NotAvailable, Expr.Eval(expression, Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_дубль_ключа_в_пошкоджених_даних_дає_MULTI()
    {
        AssertError(ExpressionErrors.MultipleMatches, Expr.Eval("REGFIND('STREAM_CASE', 2, 'Broken')", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_з_порожньою_частиною_ключа_дає_null()
    {
        var context = Cells(Flert(), hmbCase: ExpressionValue.Null);

        var value = Expr.Eval("REGFIND('STREAM_CASE', [Stream], [HmbCase])", context);

        // Ключ не заповнено — легітимна порожнеча (§5.4), а не «не знайдено».
        Assert.True(value.IsNull, $"Очікувався null, отримано {value.Type} {value.ErrorCode}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_помилка_частини_перемагає_порожню_частину()
    {
        var context = Cells(Flert(), hmbCase: ExpressionValue.Null);

        var value = Expr.Eval("REGFIND('STREAM_CASE', [HmbCase], 1 / 0)", context);

        AssertError(ExpressionErrors.DivideByZero, value);
    }

    [Theory]
    [InlineData("REGFIND('NOPE', 'X')")]
    [InlineData("REGFIND('STREAM_CASE', 1)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_невідомий_довідник_чи_інша_кількість_частин_дає_REF(string expression)
    {
        // Опис змінили після публікації — це #REF, як будь-яке нерезолвлене посилання.
        AssertError(ExpressionErrors.BadReference, Expr.Eval(expression, Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_код_довідника_не_текст_дає_VALUE()
    {
        AssertError(ExpressionErrors.BadValue, Expr.Eval("REGFIND(1, 'H2S')", Cells(Flert())));
    }

    [Theory]
    [InlineData("REGFIND('COMPONENT', 'H2S')")]
    [InlineData("REGONE('COMPONENT', ROW.N_S = 1)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_знімка_функції_довідників_дають_REF(string expression)
    {
        // Контекст без знімка (методологія до RT-23a, діалект звітів): одна
        // формула не сміє зірвати прогін винятком.
        AssertError(ExpressionErrors.BadReference, Expr.Eval(expression, new TestEvaluationContext()));
    }

    // ——— REGONE ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_знаходить_єдиний_видимий_запис_за_умовою()
    {
        // SO2 теж має N_S = 1, але закритий на дату знімка — у перегляд не входить.
        AssertEntry(201, Expr.Eval("REGONE('COMPONENT', ROW.N_S = 1)", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_жодного_запису_дає_NA()
    {
        AssertError(ExpressionErrors.NotAvailable, Expr.Eval("REGONE('COMPONENT', ROW.MW > 100)", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_більше_одного_запису_дає_MULTI()
    {
        AssertError(ExpressionErrors.MultipleMatches, Expr.Eval("REGONE('COMPONENT', ROW.N_S = 0)", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_умова_null_рядок_не_бере()
    {
        // 1D-3 групи не має: `null < 50` дає null, і рядок просто не входить.
        // Без цього правила відповідь була б #VALUE — умова «не булева».
        AssertEntry(1, Expr.Eval("REGONE('STREAM', ROW.GROUP < 50)", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_помилка_умови_перемагає_MULTI()
    {
        // H2S дає TRUE, CH4 — #DIV/0, C2H6 — теж #DIV/0. Помилка на будь-якому
        // рядку сильніша за кількість збігів: інакше відповідь залежала б від
        // того, де в порядку (Ordinal, Id) стоїть зіпсований рядок.
        AssertError(ExpressionErrors.DivideByZero, Expr.Eval("REGONE('COMPONENT', 1 / ROW.N_S > 0)", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_умова_йде_шляхом_через_Lookup()
    {
        var value = Expr.Eval(
            "REGONE('STREAM_CASE', ROW.STREAM.NAME = '1D-2' AND ROW.T_C < 20)", Cells(Flert()));

        AssertEntry(101, value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_шлях_на_невидимий_запис_дає_REF()
    {
        // 1D-4 посилається на групу OLD, закриту на дату знімка. Тихо
        // пропустити рядок гірше, ніж показати помилку (§5.4).
        AssertError(
            ExpressionErrors.BadReference,
            Expr.Eval("REGONE('STREAM', ROW.GROUP.TITLE = 'Flare')", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вкладений_REGONE_затіняє_зовнішній_ROW()
    {
        // Внутрішній ROW.NAME — поле ПОТОКУ. Якби він читав зовнішній рядок
        // (кейс), поля NAME там немає — і відповіддю був би #REF.
        var value = Expr.Eval(
            "REGONE('STREAM_CASE', ROW.STREAM = REGONE('STREAM', ROW.NAME = '1D-2') AND ROW.CASE_NAME = '370 Summer')",
            Cells(Flert()));

        AssertEntry(102, value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGONE_кожен_переглянутий_рядок_коштує_крок_бюджету()
    {
        // ⚠ Умова — один вузол (`FALSE`), тож 12 000 рядків коштують 12 000
        // кроків за вузлами — менше за межу 20 000. Лише власний крок на рядок
        // (разом 24 000) доводить, що повний перегляд бачить бюджет (§5.4).
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("BIG", ["X"]);
        for (var id = 1; id <= 12_000; id++)
        {
            snapshot.AddEntry("BIG", id, $"E{id}", ordinal: id);
        }

        AssertError(ExpressionErrors.BudgetExceeded, Expr.Eval("REGONE('BIG', FALSE)", Cells(snapshot)));
    }

    // ——— REGFIELD зі шляхом ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_шлях_проходить_через_Lookup()
    {
        Assert.Equal("Flare", Expr.Eval("REGFIELD([Stream], 'GROUP.TITLE')", Cells(Flert())).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_від_REGFIND_читає_поле_й_шлях_запису()
    {
        var context = Cells(Flert());

        Assert.Equal(12.5m, Expr.Eval("REGFIELD(REGFIND('STREAM_CASE', 1, '370 Winter'), 'T_C')", context).AsNumber());
        Assert.Equal(
            "Flare",
            Expr.Eval("REGFIELD(REGFIND('STREAM_CASE', 1, '370 Winter'), 'STREAM.GROUP.TITLE')", context).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_незаповнений_Lookup_на_шляху_дає_null()
    {
        var value = Expr.Eval("REGFIELD(2, 'GROUP.TITLE')", Cells(Flert()));

        Assert.True(value.IsNull, $"Очікувався null, отримано {value.Type} {value.ErrorCode}");
    }

    [Theory]
    [InlineData("REGFIELD(3, 'GROUP.TITLE')")]
    [InlineData("REGFIELD(99, 'TITLE')")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_невидимий_запис_дає_REF(string expression)
    {
        AssertError(ExpressionErrors.BadReference, Expr.Eval(expression, Cells(Flert())));
    }

    [Theory]
    [InlineData("GROUP.NOPE")]
    [InlineData("NAME.TITLE")]
    [InlineData("NOPE")]
    [InlineData("GROUP..TITLE")]
    [InlineData("GROUP.")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_немає_поля_чи_сегмент_не_Lookup_дає_REF(string path)
    {
        AssertError(ExpressionErrors.BadReference, Expr.Eval($"REGFIELD(1, '{path}')", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_порожній_запис_дає_null()
    {
        var context = Cells(Flert(), hmbCase: ExpressionValue.Null);

        var value = Expr.Eval("REGFIELD(REGFIND('STREAM_CASE', 1, [HmbCase]), 'T_C')", context);

        Assert.True(value.IsNull, $"Очікувався null, отримано {value.Type} {value.ErrorCode}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_без_знімка_проходить_шлях_тим_самим_джерелом_що_й_раніше()
    {
        // ⚠ Шаблонні контексти знімка ще не мають (RT-24): і однокрокова
        // форма, і шлях читають через GetRegistryField, як до кроку.
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Stream", Num(1));
        context.SetRegistryField(1, "GROUP", Num(10));
        context.SetRegistryField(10, "TITLE", Text("Flare"));

        Assert.Equal("Flare", Expr.Eval("REGFIELD([Stream], 'GROUP.TITLE')", context).Value);
        Assert.Equal(10m, Expr.Eval("REGFIELD([Stream], 'GROUP')", context).AsNumber());
    }

    // ——— діалекти ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void У_методології_функції_довідників_рахуються_так_само()
    {
        var inner = new TestEvaluationContext();
        inner.Arguments["Stream"] = Num(1);
        inner.Arguments["HmbCase"] = Text("370 Winter");
        var context = Flert().Attach(inner);

        Assert.Equal(
            12.5m,
            Expr.Eval("REGFIELD(REGFIND('STREAM_CASE', @Stream, @HmbCase), 'T_C')", context, ExpressionDialect.Methodology)
                .AsNumber());
        Assert.Equal(
            "Flare",
            Expr.Eval("REGFIELD(REGFIND('STREAM_CASE', @Stream, @HmbCase), 'STREAM.GROUP.TITLE')", context, ExpressionDialect.Methodology)
                .Value);
        AssertEntry(201, Expr.Eval("REGONE('COMPONENT', ROW.N_S = 1)", context, ExpressionDialect.Methodology));
        AssertError(
            ExpressionErrors.NotAvailable,
            Expr.Eval("REGFIND('COMPONENT', 'SO2')", context, ExpressionDialect.Methodology));
    }

    [Theory]
    [InlineData("REGFIND")]
    [InlineData("REGONE")]
    [InlineData("REGFIELD")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIND_у_Legacy_недозволена(string name)
    {
        // Чинний рушій довідників не бачив: у Legacy-версії відтворювати нічого
        // (ECR-CALC-0433, перевірка 21 02b §12).
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf(name));
        Assert.False(DialectCatalog.IsAllowedIn(name, NumericMode.Legacy));
        Assert.True(DialectCatalog.IsAllowedIn(name, NumericMode.Strict));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void У_методології_регістр_імені_значущий_а_в_шаблоні_ні()
    {
        Assert.False(Expr.Parse("regfind('COMPONENT', 'H2S')", ExpressionDialect.Methodology).IsSuccess);
        Assert.True(Expr.Parse("REGFIND('COMPONENT', 'H2S')", ExpressionDialect.Methodology).IsSuccess);

        AssertEntry(201, Expr.Eval("regfind('COMPONENT', 'H2S')", Cells(Flert())));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діалект_звітів_функцій_довідників_не_має()
    {
        Assert.False(Expr.Parse("REGFIND('COMPONENT', 'H2S')", ExpressionDialect.Report).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дерево_з_кешу_розбору_не_тримає_стану_знімка()
    {
        // ⛔ CAL-05: дерево в кеші спільне для всіх прогонів. Обчислення з
        // іншим знімком мусить дати іншу відповідь — жодного стану у вузлах.
        var parser = new Parser();
        var first = parser.Parse("REGONE('COMPONENT', ROW.N_S = 1)", ExpressionDialect.Template);
        var second = parser.Parse("REGONE('COMPONENT', ROW.N_S = 1)", ExpressionDialect.Template);
        Assert.Same(first, second);

        var other = new InMemoryRegistrySnapshot()
            .AddRegistry("COMPONENT", ["MW", "N_S"])
            .AddEntry("COMPONENT", 777, "H2S", Component(34.08m, 1));

        var evaluator = new Evaluator(new FunctionRegistry());
        var root = first.Expression!.Root;

        AssertEntry(201, evaluator.Evaluate(root, Cells(Flert()), ExpressionDialect.Template));
        AssertEntry(777, evaluator.Evaluate(root, Cells(other), ExpressionDialect.Template));
        AssertEntry(201, evaluator.Evaluate(root, Cells(Flert()), ExpressionDialect.Template));
    }

    // ——— помічники ———

    private static IEvaluationContext Cells(InMemoryRegistrySnapshot snapshot, ExpressionValue? hmbCase = null)
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Stream", Num(1));
        context.SetCell("S", "T", string.Empty, "HmbCase", hmbCase ?? Text("370 Winter"));
        return snapshot.Attach(context);
    }

    private static Dictionary<string, ExpressionValue> Case(long stream, string name, decimal temperature)
        => new() { ["STREAM"] = Num(stream), ["CASE_NAME"] = Text(name), ["T_C"] = Num(temperature) };

    private static Dictionary<string, ExpressionValue> Component(decimal mw, int sulfur)
        => new() { ["MW"] = Num(mw), ["N_S"] = Num(sulfur) };

    private static ExpressionValue Num(decimal value) => ExpressionValue.Number(value);

    private static ExpressionValue Text(string value) => ExpressionValue.Text(value);

    private static void AssertEntry(long expected, ExpressionValue actual)
    {
        Assert.True(
            actual.Type == ExpressionValueType.Number,
            $"Очікувався запис {expected}, отримано {actual.Type} {actual.ErrorCode}");
        Assert.Equal(expected, actual.AsNumber());
    }

    private static void AssertError(string expected, ExpressionValue actual)
    {
        Assert.True(actual.IsError, $"Очікувалась помилка {expected}, отримано {actual.Type} {actual.Value}");
        Assert.Equal(expected, actual.ErrorCode);
    }
}
