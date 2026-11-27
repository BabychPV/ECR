using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// ✎ Борги 27.11 (п. 4, D-578 / огляд M3): проблема рядка <c>MissingInputs</c> скрипта називає лише порожні поля, які скрипт
/// СПРАВДІ прочитав за прогін рядка (гілка, що виконалась), — а не всі порожні поля рядка, які скрипт читає будь-де.
/// </summary>
/// <remarks>
/// Мутація (локально): у <c>ScriptCalculationModule.MissingArgumentsOf</c> прибрати умову <c>!read.Contains(field.Name)</c> —
/// червоні обидва тести «гілки» (названо й поле невиконаної гілки). Друга: у <c>ScriptEvaluationContext.RowField</c> не
/// записувати читання — червоні всі (нічого не названо).
/// </remarks>
public sealed class ScriptMissingInputsTests
{
    private const string Script =
        "rows from T1;\n"
        + "if (row.A > 5) { output Hi: kg = row.B * 2; } else { output Lo: kg = row.C * 3; }";

    private static ScriptStand Stand(TraceLevel trace = TraceLevel.Full)
    {
        var stand = new ScriptStand(Script) { Trace = trace };
        stand.AddTable("T1").Column("A").Column("B").Column("C");
        return stand;
    }

    [Theory]
    [InlineData(TraceLevel.Full)]
    [InlineData(TraceLevel.Off)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-578")]
    public async Task Гілка_then_називає_лише_прочитане_порожнє_поле_B(TraceLevel trace)
    {
        var stand = Stand(trace);
        stand.Main.Row("R1", ("A", 9m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Empty(output.Values);
        Assert.Equal(["B"], output.MissingArguments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-578")]
    public async Task Гілка_else_називає_лише_прочитане_порожнє_поле_C()
    {
        var stand = Stand();
        stand.Main.Row("R1", ("A", 1m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(["C"], output.MissingArguments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-578")]
    public async Task Порожня_умова_називає_поле_умови_а_не_поля_гілок()
    {
        var stand = Stand();
        stand.Main.Row("R1", ("B", 4m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(["A"], output.MissingArguments);
    }
}
