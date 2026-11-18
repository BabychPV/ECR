using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>MS Етап 7: <see cref="CompiledScript.StepDependencies"/> і скрипт рівня Module без <c>rows</c>.</summary>
public sealed class StepDependenciesTests
{
    private static CompiledScript Module(string text)
    {
        var c = ScriptCompiler.Compile(text, new ScriptCompileOptions(NumericMode.Legacy, false, null, IsModuleLevel: true));
        Assert.True(c.Diagnostics.Count == 0, T.Describe(c));
        return c.Script!;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Залежності_кроків_лише_прямі_імена_верхнього_рівня()
    {
        var s = T.WithRows(
            "const K = 2;\n"
            + "var a = row.X * K;\n"
            + "var b = a + 1;\n"
            + "output c : one = b * a + row.Y;\n"
            + "visible var d : one = c;\n"
            + "output e : one = 5;\n").Script!;

        Assert.Equal(["a", "b", "c", "d", "e"], s.StepDependencies.Keys);
        Assert.Empty(s.StepDependencies["a"]);
        Assert.Equal(["a"], s.StepDependencies["b"]);
        Assert.Equal(["b", "a"], s.StepDependencies["c"]);
        Assert.Equal(["c"], s.StepDependencies["d"]);
        Assert.Empty(s.StepDependencies["e"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Залежності_проходять_крізь_власні_функції_і_умови()
    {
        var compiled = T.WithRows(
            "function twice(x) = x * 2;\n"
            + "var a = row.X;\n"
            + "var b = row.Y;\n"
            + "output c : one = twice(a) + (b > 0 ? 1 : 0);\n");
        Assert.True(compiled.Diagnostics.Count == 0, T.Describe(compiled));
        var s = compiled.Script!;

        Assert.Equal(["a", "b"], s.StepDependencies["c"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Залежності_проходять_крізь_значення_й_умову_агрегату()
    {
        var compiled = T.WithRows(
            "table G = T2;"
            + "var lim = row.X;"
            + "var k = row.Y;"
            + "var unused = 1;"
            + "output c : one = Sum(G.A * k where G.B > lim);");
        Assert.True(compiled.Diagnostics.Count == 0, T.Describe(compiled));
        var s = compiled.Script!;

        Assert.Equal(["k", "lim"], s.StepDependencies["c"].Order());
        Assert.DoesNotContain("unused", s.StepDependencies["c"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Скрипт_рівня_Module_компілюється_без_rows_а_звичайний_ні()
    {
        const string text = "var a = row.GCV_X_T_A * 2;\noutput X_T_B : one = a + 1;\n";

        var module = Module(text);
        Assert.Empty(module.Rows);
        Assert.Equal(["X_T_B"], module.Outputs.Select(o => o.Code));
        Assert.Equal(["a"], module.StepDependencies["X_T_B"]);

        var plain = T.Compile(text);
        Assert.Contains(ScriptDiagnosticCodes.MissingRows, T.Codes(plain));
    }
}
