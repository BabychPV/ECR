using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.Scripting.Lexing;
using Ecr.Scripting.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// Стійкість до ворожого або помилкового тексту: сервер компілює текст користувача на кожну валідацію, тому жодний
/// текст не вправі валити процес (переповнення стека), їсти пам'ять без межі, рахуватися хвилинами або давати
/// відповідь, яку не можна серіалізувати.
/// </summary>
public sealed class RobustnessTests
{
    private const string R = "rows from T1;\n";

    /// <summary>Найбільший час компіляції тексту межі розміру; у нормі — частки секунди, межа лишає запас на навантажений CI.</summary>
    private const long BudgetMs = 3000;

    private static string Rep(string unit, int n) => new StringBuilder(unit.Length * n).Insert(0, unit, n).ToString();

    private static ScriptCompilation Timed(string text, out long ms, bool library = false)
    {
        var sw = Stopwatch.StartNew();
        var c = ScriptCompiler.Compile(text, new ScriptCompileOptions(NumericMode.Strict, library));
        ms = sw.ElapsedMilliseconds;
        return c;
    }

    private static string Describe(ScriptCompilation c, long ms)
        => $"{ms} мс; {T.Describe(c)}".Length > 500 ? $"{ms} мс; {c.Diagnostics.Count} діагностик" : $"{ms} мс; {T.Describe(c)}";

    private static void AssertFast(string text, ScriptCompilation c, long ms)
    {
        Assert.True(ms < BudgetMs, $"компіляція {text.Length} знаків зайняла {ms} мс ({c.Diagnostics.Count} діагностик)");
    }

    // ───── вкладеність: діагностика, а не падіння ─────

    public static TheoryData<string, string> DeepTexts() => new()
    {
        { "дужки", R + "var x = " + Rep("(", 90_000) + "1" + Rep(")", 90_000) + ";" },
        { "унарний мінус", R + "var x = " + Rep("-", 190_000) + "1;" },
        { "заперечення", R + "var x = " + Rep("!", 190_000) + "1;" },
        { "ланцюг тернарних у гілці", R + "var x = " + Rep("a ? b : ", 20_000) + "1;" },
        { "тернарні в умові", R + "var x = " + Rep("a ? ", 20_000) + "1" + Rep(" : 2", 20_000) + ";" },
        { "вкладені if", R + Rep("if (1) { ", 10_000) + Rep("}", 10_000) },
        { "ланцюг else if", R + "if (1) { }" + Rep(" else if (1) { }", 10_000) },
        { "вкладені мапи", "const M = " + Rep("{a:", 30_000) + "1" + Rep("}", 30_000) + ";" },
        { "заперечення в умові rows", "rows from T1 where " + Rep("!", 190_000) + "row.A == \"x\";" },
        { "дужки в умові rows", "rows from T1 where " + Rep("(", 90_000) + "row.A == \"x\"" + Rep(")", 90_000) + ";" },
        { "вкладені виклики", R + "var x = " + Rep("f(", 60_000) + "1" + Rep(")", 60_000) + ";" },
        { "вкладені індекси", R + "var x = " + Rep("a[", 60_000) + "1" + Rep("]", 60_000) + ";" },
        { "вкладені агрегати", R + "var x = " + Rep("Sum(", 20_000) + "1" + Rep(")", 20_000) + ";" },
        { "вкладені in", R + "var x = " + Rep("1 in (", 15_000) + "1" + Rep(")", 15_000) + ";" },
        { "вкладені foreach substance", R + Rep("foreach substance S in (\"a\") { ", 6_000) + Rep("}", 6_000) },
    };

    [Theory]
    [MemberData(nameof(DeepTexts))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Глибока_вкладеність_дає_tooDeep_а_не_падіння(string name, string text)
    {
        Assert.True(text.Length <= ScriptCompiler.MaxTextLength, name);

        var c = Timed(text, out var ms);

        Assert.True(c.Diagnostics.Any(d => d.Code == ScriptDiagnosticCodes.TooDeep), $"{name}: {Describe(c, ms)}");
        Assert.Null(c.Script);
        AssertFast(text, c, ms);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Межа_вкладеності_точна_64_проходить_65_ні()
    {
        var ok = T.WithRows("var x = " + Rep("(", 63) + "1" + Rep(")", 63) + ";");
        var tooDeep = T.WithRows("var x = " + Rep("(", 65) + "1" + Rep(")", 65) + ";");

        Assert.Empty(ok.Diagnostics);
        Assert.Contains(tooDeep.Diagnostics, d => d.Code == ScriptDiagnosticCodes.TooDeep && d.Params["max"] == "64");
    }

    // ───── глибина лівого ланцюга: межа не залежить від розміру стека ─────

    [Theory]
    [InlineData("+")]
    [InlineData("&&")]
    [InlineData("*")]
    [InlineData("||")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Довгий_лівий_ланцюг_має_детерміновану_межу_глибини(string op)
    {
        var long_ = R + "var x = 1" + Rep($" {op} 1", ScriptCompiler.MaxExpressionDepth + 10) + ";";
        var fine = R + "var x = 1" + Rep($" {op} 1", ScriptCompiler.MaxExpressionDepth - 10) + ";";

        // Потік із великим стеком: без детермінованої межі цей текст компілювався б (сторож стека мовчить).
        ScriptCompilation? tooDeep = null;
        ScriptCompilation? ok = null;
        var thread = new Thread(
            () =>
            {
                tooDeep = ScriptCompiler.Compile(long_, new ScriptCompileOptions(NumericMode.Strict, false));
                ok = ScriptCompiler.Compile(fine, new ScriptCompileOptions(NumericMode.Strict, false));
            },
            64 * 1024 * 1024);
        thread.Start();
        thread.Join();

        var d = Assert.Single(tooDeep!.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.TooDeep, d.Code);
        Assert.Equal(ScriptCompiler.MaxExpressionDepth.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Params["max"]);
        Assert.Null(tooDeep.Script);
        Assert.True(ok!.IsSuccess, T.Describe(ok));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дуже_довгий_ланцюг_і_ланцюг_доступів_не_валять_процес()
    {
        foreach (var text in new[]
                 {
                     R + "var x = 1" + Rep(" + 1", 49_000) + ";",
                     R + "var x = a" + Rep(".b", 90_000) + ";",
                     R + "var x = a" + Rep("[1]", 60_000) + ";",
                 })
        {
            var c = Timed(text, out var ms);
            AssertFast(text, c, ms);
            Assert.NotEmpty(c.Diagnostics);
        }
    }

    // ───── рекурсія функцій ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рекурсія_пряма_взаємна_і_кільце_позначаються_рівно_на_циклі()
    {
        var c = T.WithRows(
            "function self(a) = self(a);" +
            "function p(a) = q(a); function q(a) = r(a); function r(a) = p(a);" +
            "function top(a) = p(a);" +
            "function leaf(a) = a; function mid(a) = leaf(a); function up(a) = mid(a);" +
            "function two(a) = two(a) + 1;");

        var flagged = c.Diagnostics.Where(d => d.Code == ScriptDiagnosticCodes.RecursiveFunction)
            .Select(d => d.Params["name"]).Order().ToArray();

        Assert.Equal(["p", "q", "r", "self", "two"], flagged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рекурсія_через_виклики_у_різних_гілках_виразу_і_аргументах()
    {
        var c = T.WithRows(
            "function a1(x) = x > 0 ? b1(x) : 0; function b1(x) = Max(x, a1(x));");

        Assert.Equal(2, c.Diagnostics.Count(d => d.Code == ScriptDiagnosticCodes.RecursiveFunction));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кільце_і_довгий_ланцюг_функцій_перевіряються_за_лінійний_час()
    {
        const int n = 5_500;
        var chain = new StringBuilder(R);
        var ring = new StringBuilder(R);
        for (var i = 0; i < n; i++)
        {
            chain.Append("function f").Append(i).Append("(a) = ").Append(i == 0 ? "a" : $"f{i - 1}(a)").Append(";\n");
            ring.Append("function f").Append(i).Append("(a) = f").Append((i + 1) % n).Append("(a);\n");
        }

        var okChain = Timed(chain.ToString(), out var chainMs);
        var okRing = Timed(ring.ToString(), out var ringMs);

        Assert.Empty(okChain.Diagnostics);
        Assert.True(chainMs < BudgetMs, $"ланцюг: {chainMs} мс");
        Assert.All(okRing.Diagnostics, d => Assert.Equal(ScriptDiagnosticCodes.RecursiveFunction, d.Code));
        Assert.NotEmpty(okRing.Diagnostics);
        Assert.True(ringMs < BudgetMs, $"кільце: {ringMs} мс");
    }

    // ───── вибух розгортання foreach ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_із_великим_тілом_без_крапок_з_комою_обмежено_за_лексемами()
    {
        // Тіло без «;» рахується як один оператор, тож межа в 2000 операторів його не стримує.
        var values = string.Join(",", Enumerable.Range(1, 100));
        var text = R + "foreach X in (" + values + ") { " + Rep("a ", 5_000) + " }";

        var c = Timed(text, out var ms);

        var d = Assert.Single(c.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.ForeachTooLarge, d.Code);
        Assert.Equal(ScriptParser.MaxExpandedTokens.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Params["limit"]);
        Assert.Null(c.Script);
        AssertFast(text, c, ms);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_із_тілом_із_однією_крапкою_і_величезним_виразом_обмежено_за_лексемами()
    {
        var values = string.Join(",", Enumerable.Range(1, 20));
        var text = R + "foreach X in (" + values + ") { var v{X} = 1" + Rep(" + 1", 15_000) + "; }";

        var c = Timed(text, out var ms);

        Assert.Contains(c.Diagnostics, d => d.Code == ScriptDiagnosticCodes.ForeachTooLarge);
        Assert.Null(c.Script);
        AssertFast(text, c, ms);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вкладені_foreach_які_множаться_у_тисячі_обмежені()
    {
        var text = R + "foreach A in (1,2,3,4,5,6,7,8,9,10,11,12,13) { foreach B in (1,2,3,4,5,6,7,8,9,10,11,12,13) { foreach C in (1,2,3,4,5,6,7,8,9,10,11,12,13) { var v{A}_{B}_{C} = 1; } } }";

        var c = Timed(text, out var ms);

        Assert.Contains(c.Diagnostics, d => d.Code == ScriptDiagnosticCodes.ForeachTooLarge);
        Assert.True(ms < BudgetMs, $"{ms} мс");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помірне_розгортання_foreach_за_межами_не_зачіпається()
    {
        var c = T.WithRows("foreach A in (1,2,3,4,5,6,7,8,9,10,11,12) { foreach B in (1,2,3,4,5,6,7,8,9,10,11,12) { foreach C in (1,2,3,4,5,6,7,8,9,10,11,12) { var v{A}_{B}_{C} = 1; } } }");

        Assert.Empty(c.Diagnostics);
        Assert.Equal(1728, c.Script!.Statements.Count);
    }

    // ───── лексика: числа, рядки, коментарі, нульові байти, сурогати ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Величезні_числа_й_експоненти_дають_invalidNumber_швидко()
    {
        foreach (var literal in new[] { Rep("9", 190_000), "1e" + Rep("9", 190_000), "1e999999999", "79228162514264337593543950336", "1e29" })
        {
            var c = Timed(R + "var x = " + literal + ";", out var ms);

            Assert.Contains(c.Diagnostics, d => d.Code == ScriptDiagnosticCodes.InvalidNumber);
            Assert.True(ms < BudgetMs, $"{literal.Length}: {ms} мс");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Найбільше_decimal_приймається_а_одиниця_понад_ним_ні()
    {
        Assert.Empty(T.WithRows("var x = 79228162514264337593543950335;").Diagnostics);
        Assert.Contains(T.WithRows("var x = 79228162514264337593543950336;").Diagnostics, d => d.Code == ScriptDiagnosticCodes.InvalidNumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Незакриті_рядок_і_коментар_та_нульові_байти_не_валять_компіляцію()
    {
        foreach (var text in new[]
                 {
                     R + "var x = \"" + Rep("a", 190_000),
                     R + "var x = '" + Rep("\\", 190_000),
                     R + "/* " + Rep("/* ", 60_000),
                     R + Rep("\0", 190_000),
                     R + "var x = \"a\0b\";",
                 })
        {
            var c = Timed(text, out var ms);

            AssertFast(text, c, ms);
            Assert.NotNull(c);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сміття_дає_не_більше_межі_діагностик_і_швидко()
    {
        foreach (var text in new[]
                 {
                     R + Rep("^", 190_000),
                     R + Rep("\0", 190_000),
                     R + Rep("var ", 45_000),
                     R + Rep("};", 90_000),
                     R + Rep("foreach ", 22_000),
                     R + Rep("\ud800", 90_000),
                 })
        {
            var c = Timed(text, out var ms);

            Assert.InRange(c.Diagnostics.Count, 1, ScriptDiagnostic.MaxPerCompilation);
            Assert.Null(c.Script);
            AssertFast(text, c, ms);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Семантичні_діагностики_теж_обмежені_межею()
    {
        var text = R + Rep("var x = y;\n", 15_000);

        var c = Timed(text, out var ms);

        Assert.Equal(ScriptDiagnostic.MaxPerCompilation, c.Diagnostics.Count);
        AssertFast(text, c, ms);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діагностика_з_непарним_сурогатом_серіалізується_в_JSON()
    {
        foreach (var text in new[]
                 {
                     R + "var x = \ud800;",
                     R + "var \ud800 = 1;",
                     R + "var x = \"a\udc00\" \"b\udc00\";",
                     R + "var x = y\ud83d;",
                     R + "var x = 1 \udfff 2;",
                 })
        {
            var c = T.Compile(text);

            Assert.NotEmpty(c.Diagnostics);
            var json = JsonSerializer.Serialize(c.Diagnostics);
            Assert.DoesNotContain("\\ud800", json, StringComparison.OrdinalIgnoreCase);
            Assert.All(c.Diagnostics, d =>
            {
                _ = new UTF8Encoding(false, true).GetBytes(d.Message);
                Assert.All(d.Params.Values, v => _ = new UTF8Encoding(false, true).GetBytes(v));
            });
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Парний_сурогат_у_діагностиці_зберігається()
    {
        var c = T.WithRows("var x = \"😀\" + nothing;");

        var d = Assert.Single(c.Diagnostics);
        Assert.Equal("nothing", d.Params["name"]);
        Assert.Equal("a😀b", ScriptDiagnostic.Sanitize("a😀b", 10));
        Assert.Equal("a�b", ScriptDiagnostic.Sanitize("a\ud83db", 10));
        Assert.Equal("a�b", ScriptDiagnostic.Sanitize("a\ude00b", 10));
        Assert.Equal("a�", ScriptDiagnostic.Sanitize("a\ud83d", 10));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Довгий_ідентифікатор_скорочується_у_підстановці_діагностики()
    {
        var name = Rep("a", 190_000);
        var c = Timed(R + "var x = " + name + ";", out var ms);

        var d = Assert.Single(c.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.UnknownName, d.Code);
        Assert.True(d.Params["name"].Length <= ScriptDiagnostic.MaxTextChars + 1);
        Assert.True(d.Message.Length <= ScriptDiagnostic.MaxTextChars * 3 + 1);
        Assert.EndsWith("…", d.Params["name"], StringComparison.Ordinal);
        Assert.True(ms < BudgetMs);
    }

    // ───── лінійність на великих текстах ─────

    private static string Many(int limit, Func<int, string> line, string head = R)
    {
        var sb = new StringBuilder(head);
        for (var i = 0; sb.Length < limit; i++)
        {
            sb.Append(line(i));
        }

        return sb.ToString();
    }

    public static TheoryData<string, string> BigTexts() => new()
    {
        { "багато var у ланцюжку", Many(195_000, i => $"var v{i} = {(i == 0 ? "1" : "v" + (i - 1))} + 1;\n") },
        { "багато const", Many(195_000, i => $"const c{i} = {i};\n") },
        { "багато rows", Many(195_000, i => $"rows from Ta{i} where row.A == \"x\";\n", string.Empty) },
        { "багато полів row", Many(195_000, i => $"var v{i} = row.C{i};\n") },
        { "багато кодів довідників", Many(195_000, i => $"var v{i} = REGFIND(\"R{i}\", entry(\"R{i}\", \"c\"));\n") },
        { "багато table", Many(195_000, i => $"table T{i} = X{i};\n") },
        { "багато if із var у блоці", Many(195_000, i => $"if (1) {{ var q{i} = 1; }}\n") },
        { "багато output у циклі", "rows from T1;\nforeach substance S in (\"a\") {\n" + Many(195_000, i => $"output O{i} per S : t = 1;\n", string.Empty) + "}" },
        { "багато foreach для повторів", Many(195_000, i => $"foreach X in (1) {{ var z{i} = X; }}\n") },
        { "функція викликається багатьма var", R + "function big(a) = a" + Rep(" + a", 400) + ";\n" + Many(195_000, i => $"var v{i} = big({i});\n", string.Empty) },
        { "багато викликів власних функцій", R + "function g(a) = a;\n" + Many(195_000, i => $"var v{i} = g({i}) + g(1);\n", string.Empty) },
        { "багато агрегатів", R + "table G = T2;\n" + Many(195_000, i => $"var s{i} = Sum(G.A where G.B == {i});\n", string.Empty) },
    };

    [Theory]
    [MemberData(nameof(BigTexts))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Великий_текст_компілюється_за_секунди(string name, string text)
    {
        Assert.True(text.Length <= ScriptCompiler.MaxTextLength, name);

        var c = Timed(text, out var ms);

        Assert.True(ms < BudgetMs, $"{name}: {ms} мс ({c.Diagnostics.Count} діагностик)");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Лексер_і_парсер_лінійні_на_мільйоні_знаків()
    {
        var text = Rep("var abc1 = 1 + 2 * (x - y); // c\n", 30_000);
        Assert.True(text.Length > 900_000);

        var sw = Stopwatch.StartNew();
        var lex = ScriptLexer.Tokenize(text);
        var lexMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var parsed = ScriptParser.Parse(lex);
        var parseMs = sw.ElapsedMilliseconds;

        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(30_000, parsed.Program.Items.Count);
        Assert.True(lexMs < BudgetMs, $"лексер {lexMs} мс");
        Assert.True(parseMs < BudgetMs, $"парсер {parseMs} мс");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Лексер_і_парсер_на_мільйоні_знаків_сміття_швидкі_і_обмежують_діагностики()
    {
        var text = Rep("^", 1_000_000);

        var sw = Stopwatch.StartNew();
        var lex = ScriptLexer.Tokenize(text);
        var parsed = ScriptParser.Parse(lex);

        Assert.InRange(parsed.Diagnostics.Count, 1, ScriptDiagnostic.MaxPerCompilation + 1);
        Assert.True(sw.ElapsedMilliseconds < BudgetMs, $"{sw.ElapsedMilliseconds} мс");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Текст_понад_межу_відхиляється_одразу_без_розбору()
    {
        var text = R + Rep("var a = 1;\n", 100_000);

        var c = Timed(text, out var ms);

        var d = Assert.Single(c.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.TooLarge, d.Code);
        Assert.Null(c.Script);
        Assert.True(ms < 200, $"{ms} мс");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_і_переповнення_decimal_у_літералах_не_обчислюються_під_час_компіляції()
    {
        var c = T.WithRows(
            "var a = 1 / 0; var b = 79228162514264337593543950335 * 79228162514264337593543950335; var c = 5 % 0;"
            + "var d = Pow(10, 100000); var e = -79228162514264337593543950335 - 79228162514264337593543950335;");

        Assert.Empty(c.Diagnostics);
        Assert.Equal(5, c.Script!.Statements.Count);
    }
}
