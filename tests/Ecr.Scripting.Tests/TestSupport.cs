using Xunit;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Scripting;

namespace Ecr.Scripting.Tests;

internal static class T
{
    public const string Rows = "rows from T1;\n";

    public static ScriptCompilation Compile(
        string text,
        bool library = false,
        NumericMode mode = NumericMode.Strict,
        Func<string, CompiledScript?>? import = null)
        => ScriptCompiler.Compile(text, new ScriptCompileOptions(mode, library, import));

    /// <summary>Компілює скрипт з обовязковим rows попереду.</summary>
    public static ScriptCompilation WithRows(string body, NumericMode mode = NumericMode.Strict, Func<string, CompiledScript?>? import = null)
        => Compile(Rows + body, false, mode, import);

    public static string[] Codes(ScriptCompilation c) => [.. c.Diagnostics.Select(d => d.Code)];

    public static string Describe(ScriptCompilation c)
        => string.Join("; ", c.Diagnostics.Select(d => $"{d.Code}@{d.Line}:{d.Column}"));

    /// <summary>Друк виразу останнього var скрипта.</summary>
    public static string Expr(string prelude, string expr, NumericMode mode = NumericMode.Strict)
    {
        var c = WithRows(prelude + "var x = " + expr + ";", mode);
        Assert.True(c.Diagnostics.Count == 0, Describe(c));
        var stmt = c.Script!.Statements.OfType<VarStatement>().Last();
        return AstPrinter.Print(stmt.Expr);
    }

    public static CompiledScript Lib(string text)
    {
        var c = Compile(text, library: true);
        Assert.True(c.Diagnostics.Count == 0, Describe(c));
        return c.Script!;
    }
}
