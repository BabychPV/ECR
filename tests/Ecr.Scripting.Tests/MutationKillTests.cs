using Ecr.Expressions.Ast;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>Тести, додані після мутаційної перевірки: кожен вбиває мутанта, якого не ловили попередні тести.</summary>
public sealed class MutationKillTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Числовий_ключ_мапи_у_літералі_порівнюється_без_хвостових_нулів()
    {
        var c = T.WithRows("const M = { 1: 10, 1.5: 20 }; var a = M[1.0]; var b = M[1.50]; var c = M[1];");

        Assert.Empty(c.Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Той_самий_вихід_у_гілках_з_різними_одиницями_або_per_є_duplicateName()
    {
        var units = T.WithRows("var c = row.A > 0; if (c) { output X : t = 1; } else { output X : g = 2; }");
        var perMismatch = T.WithRows(
            "foreach substance S in (\"1\") { if (true) { output X per S : t = 1; } else { output X : t = 2; } }");
        var ok = T.WithRows("var c = row.A > 0; if (c) { output X : t = 1; } else { output X : t = 2; }");

        Assert.Contains(units.Diagnostics, d => d.Code == ScriptDiagnosticCodes.DuplicateName);
        Assert.Contains(perMismatch.Diagnostics, d => d.Code == ScriptDiagnosticCodes.DuplicateName);
        Assert.Empty(ok.Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void duplicateTable_не_залежить_від_регістру_коду()
    {
        Assert.Contains(T.Compile("rows from T1; rows from t1;").Diagnostics, d => d.Code == ScriptDiagnosticCodes.DuplicateTable);
        Assert.Contains(T.Compile("rows from Ab, aB;").Diagnostics, d => d.Code == ScriptDiagnosticCodes.DuplicateTable);
        Assert.Contains(T.Compile("rows from X.T1; rows from x.t1;").Diagnostics, d => d.Code == ScriptDiagnosticCodes.DuplicateTable);
        Assert.Empty(T.Compile("rows from X.T1; rows from Y.T1;").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_від_ємного_літерала_підставляє_мінус()
    {
        var c = T.WithRows("foreach X in (-5, 7) { var v_a = X; }");

        // Імена збігаються після підстановки, тож дивимось на значення: перший var має -5, другий (дубль) — 7.
        var values = c.Script!.Statements.OfType<VarStatement>().Select(v => ((LiteralNode)v.Expr).Value).ToArray();
        Assert.Equal([-5m, 7m], values);
    }
}
