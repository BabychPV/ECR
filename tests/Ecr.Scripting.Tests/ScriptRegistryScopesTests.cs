using Ecr.Expressions.Binding;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// MS x B179: обсяг читання довідника за періодом для скрипта (<see cref="ScriptRegistryScopes"/>): місяць, рік, літеральний
/// місяць, об'єднання викликів і консервативне «усе» там, де обсяг невпевнений.
/// </summary>
/// <remarks>
/// Мутації (локально): <c>Collect</c> завжди повертає <c>All</c> - червоні всі тести, крім «усе»; не обходити тіла
/// функцій (<c>script.Functions</c>) - червоний тест про функцію; не обходити гілки <c>if</c> - тест про гілку; не
/// перевіряти <c>RegistryEntries</c> - тест про <c>entry</c> червоний; не перевіряти <c>Imports</c> - тест про імпорт червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class ScriptRegistryScopesTests
{
    private static IReadOnlyDictionary<string, PeriodScope> Scopes(string body, Func<string, CompiledScript?>? import = null)
    {
        var c = T.WithRows(body, import: import);
        Assert.True(c.Script is not null, T.Describe(c));
        return ScriptRegistryScopes.Collect(c.Script!);
    }

    [Fact]
    public void Рік_і_місяць_періоду_дають_Month()
    {
        var scopes = Scopes("output OUT : kg = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");");

        Assert.Equal(PeriodScope.Month, scopes["R"]);
        Assert.Equal(PeriodScope.Month, scopes["r"]);
    }

    [Fact]
    public void Лише_рік_дає_Year()
        => Assert.Equal(PeriodScope.Year, Scopes("var x = REGFIELD(REGFIND(\"R\", period.Year, \"a\"), \"V\");")["R"]);

    [Fact]
    public void Рік_з_літеральним_місяцем_дає_FixedMonth()
        => Assert.Equal(PeriodScope.FixedMonth(10), Scopes("var x = REGFIELD(REGFIND(\"R\", period.Year, 10, \"a\"), \"V\");")["R"]);

    [Fact]
    public void Без_періоду_в_ключах_усе()
        => Assert.Equal(PeriodScope.All, Scopes("var x = REGFIELD(REGFIND(\"R\", \"a\", \"b\"), \"V\");")["R"]);

    [Fact]
    public void Інший_рік_не_впізнається_і_дає_усе()
        => Assert.Equal(PeriodScope.All, Scopes("var x = REGFIELD(REGFIND(\"R\", period.Year - 1, period.Month, \"a\"), \"V\");")["R"]);

    [Fact]
    public void Ключ_через_змінну_консервативно_усе()
        => Assert.Equal(PeriodScope.All, Scopes("var y = period.Year; var x = REGFIELD(REGFIND(\"R\", y, period.Month, \"a\"), \"V\");")["R"]);

    [Fact]
    public void Кілька_викликів_одного_довідника_об_єднуються()
    {
        var scopes = Scopes(
            "var a = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");\n"
            + "var b = REGFIELD(REGFIND(\"R\", period.Year, 3, \"a\"), \"V\");");

        // Місяць ∪ літеральний місяць не виражається жодним із двох - весь рік.
        Assert.Equal(PeriodScope.Year, scopes["R"]);
    }

    [Fact]
    public void Різні_довідники_мають_власні_обсяги()
    {
        var scopes = Scopes(
            "var a = REGFIELD(REGFIND(\"R1\", period.Year, period.Month, \"a\"), \"V\");\n"
            + "var b = REGFIELD(REGFIND(\"R2\", \"a\"), \"V\");");

        Assert.Equal(PeriodScope.Month, scopes["R1"]);
        Assert.Equal(PeriodScope.All, scopes["R2"]);
    }

    [Fact]
    public void Читання_в_гілці_if_враховується()
    {
        var scopes = Scopes(
            "var a = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");\n"
            + "if (a > 0) { var b = REGFIELD(REGFIND(\"R\", \"a\"), \"V\"); }");

        Assert.Equal(PeriodScope.All, scopes["R"]);
    }

    [Fact]
    public void Читання_в_тілі_власної_функції_враховується()
    {
        var scopes = Scopes(
            "function f(k) = REGFIELD(REGFIND(\"R\", period.Year, period.Month, k), \"V\");\n"
            + "var a = f(\"a\");");

        Assert.Equal(PeriodScope.Month, scopes["R"]);
    }

    [Fact]
    public void Довідник_згаданий_ще_й_через_entry_дає_усе()
    {
        var scopes = Scopes(
            "var a = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");\n"
            + "var b = entry(\"R\", \"CODE\");");

        Assert.Equal(PeriodScope.All, scopes["R"]);
    }

    [Fact]
    public void Скрипт_з_імпортом_дає_усе_для_кожного_довідника()
    {
        var library = T.Lib("const K = 1;");
        var scopes = Scopes(
            "var a = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");",
            import: _ => library);
        Assert.Equal(PeriodScope.Month, scopes["R"]);

        var c = T.Compile("import Lib; rows from T1; var a = REGFIELD(REGFIND(\"R\", period.Year, period.Month, \"a\"), \"V\");", import: _ => library);
        Assert.True(c.Script is not null, T.Describe(c));
        Assert.Equal(PeriodScope.All, ScriptRegistryScopes.Collect(c.Script!)["R"]);
    }

    [Fact]
    public void Скрипт_без_довідників_дає_порожньо()
        => Assert.Empty(Scopes("var a = 1;"));
}
