using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Межа ланцюга міряє ВИСОТУ дерева, а не «ланки на шляху розбору» (L7-01, AN-72).
/// </summary>
/// <remarks>
/// ⛔ Аудит 2026-10-09: лічильник скидався при виході з вкладеного виразу, тож
/// ланцюг у дужках, що стоїть ЛІВИМ операндом іншого ланцюга, знову діставав
/// повні <see cref="Parser.MaxChainLinks"/> ланки — на кожному рівні дужок.
/// 63 рівні × 1024 ланки давали лівий гребінь глибиною ~64 тисячі зі ~130 КБ
/// тексту.
///
/// ⚠ Тут навмисно немає тесту на 63 рівні: на несправленому коді він не червоніє,
/// а валить хост тестів (StackOverflowException). Доводиться МЕЖА: два-три рівні
/// з глибиною, яку несправлений код ще витримує, але вже мусить відхилити.
/// </remarks>
public sealed class ParserChainDepthTests
{
    /// <summary>Ланцюг рівно з <paramref name="links"/> ланок: <c>1+1+…+1</c>.</summary>
    private static string Chain(int links) => "1" + Repeat("+1", links);

    private static string Repeat(string fragment, int times)
        => string.Concat(Enumerable.Repeat(fragment, times));

    private static void AssertChainTooLong(string expression)
    {
        var result = Expr.Parse(expression);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Expression);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.MessageKey == "expr.chainTooLong");
        Assert.Equal("1024", diagnostic.MessageParams!["max"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ліві_дужки_не_скидають_лічильник()
    {
        // 600 ланок у дужках — лівий операнд ще 600 ланок: гребінь 1200 > 1024.
        AssertChainTooLong("(" + Chain(600) + ")" + Repeat("+1", 600));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ліві_дужки_на_кількох_рівнях_додають_глибини()
    {
        // Той самий дефект у ступінчастому вигляді: кожен рівень у дужках — лівий операнд
        // наступного. Старий лічильник бачив на кожному рівні лише 400, а висота — 1200.
        var text = Chain(400);
        for (var level = 0; level < 2; level++)
        {
            text = "(" + text + ")" + Repeat("+1", 400);
        }

        AssertChainTooLong(text);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ліві_дужки_в_межі_розбираються()
    {
        // Висота рівно 1024: 512 + 512. Межа не стала суворішою, ніж треба.
        var result = Expr.Parse("(" + Chain(512) + ")" + Repeat("+1", 512));

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ранній_правий_операнд_у_дужках_додає_свою_глибину_до_ланок_після_нього()
    {
        // Правий операнд ПЕРШОЇ ланки: усі наступні ланки лежать над ним у дереві, тож
        // його глибина (600) додається до довжини гребеня (601), хоч на шляху розбору
        // під час його розбору була лише одна ланка.
        AssertChainTooLong("1+(" + Chain(600) + ")" + Repeat("+1", 600));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сусідні_піддерева_беруться_максимумом_а_не_сумою()
    {
        // Два ланцюги по 600 на різних гілках одного вузла: висота 601, а не 1201.
        var result = Expr.Parse("(" + Chain(600) + ")+(" + Chain(600) + ")");

        Assert.True(result.IsSuccess, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    public static TheoryData<string> Wrappers =>
    [
        "SUM({0})",
        "-({0})",
        "(({0}) > 0 ? 1 : 0)",
        "[T].[WHERE ({0}) > 0].[A]",
    ];

    [Theory]
    [MemberData(nameof(Wrappers))]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Висота_передається_крізь_вузли_що_ланками_не_є(string wrapper)
    {
        // Виклик, унарний мінус, умовний вираз, предикат: у кожного дитина-ланцюг 600, зверху
        // ще 600 ланок. Якщо вузол губить висоту дитини, межа мовчить.
        var inner = string.Format(System.Globalization.CultureInfo.InvariantCulture, wrapper, Chain(600));

        AssertChainTooLong(inner + Repeat("+1", 600));
    }
}
