using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// <c>SourceRule.For</c> (D-556, коло 4): правило старої системи має таблицю, тож правило однієї таблиці не відбирає рядки
/// іншої. Синтетичні коди таблиць і колонок.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>SourceRule.For</c> замінити фільтр на «усі правила» (<c>true</c>) — червоніє
/// <c>Правило_таблиці_не_діє_для_іншої_таблиці</c>; замінити
/// <c>Contains(tableCode)</c> на <c>Any()</c> — червоніє <c>Правило_таблиці_не_діє_для_іншої_таблиці</c>; прибрати гілку
/// <c>null or { Count: 0 }</c> — червоніє <c>Правило_без_таблиці_діє_для_кожної</c>.
/// </remarks>
public sealed class SourceRuleScopeTests
{
    private static SourceMatchPair Pair(string column, string value) => new(column, SourceMatchValue.OfText(value));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Правило_таблиці_не_діє_для_іншої_таблиці()
    {
        var ruleA = new SourceRule([Pair("Kind", "x")], ["TA"]);
        var ruleB = new SourceRule([Pair("Kind", "y")], ["TB"]);

        Assert.Equal([ruleA], SourceRule.For("TA", [ruleA, ruleB]));
        Assert.Equal([ruleB], SourceRule.For("TB", [ruleA, ruleB]));
        Assert.Empty(SourceRule.For("TC", [ruleA, ruleB]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Правило_без_таблиці_діє_для_кожної()
    {
        var any = new SourceRule([]);
        var anyEmpty = new SourceRule([], []);
        var ruleA = new SourceRule([Pair("Kind", "x")], ["TA"]);

        Assert.Equal([any, anyEmpty, ruleA], SourceRule.For("TA", [any, anyEmpty, ruleA]));
        Assert.Equal([any, anyEmpty], SourceRule.For("TB", [any, anyEmpty, ruleA]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Правило_кількох_таблиць_діє_для_кожної_з_них()
    {
        var multi = new SourceRule([Pair("Kind", "x")], ["TA", "TB"]);

        Assert.Equal([multi], SourceRule.For("TB", [multi]));
        Assert.Empty(SourceRule.For("TC", [multi]));
    }
}
