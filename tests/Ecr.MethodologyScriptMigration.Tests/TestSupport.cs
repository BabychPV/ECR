using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

internal static class Sup
{
    public static readonly SourceRowsClause Rows = new(
        ["T1"], [new SourceRule([])], []);

    public static VersionSource Version(
        IEnumerable<SourceFormula> formulas,
        IEnumerable<SourceConstant>? constants = null,
        IEnumerable<SourceOutput>? outputs = null,
        string? categoryRule = null,
        IEnumerable<string>? categories = null,
        IEnumerable<string>? substances = null,
        IEnumerable<SourceImport>? imports = null,
        DateOnly? next = null)
        => new()
        {
            MethodologyCode = "M1",
            Formulas = [.. formulas],
            Constants = [.. constants ?? []],
            Outputs = [.. outputs ?? []],
            CategoryRule = categoryRule,
            Categories = [.. categories ?? []],
            Substances = [.. substances ?? []],
            Imports = [.. imports ?? []],
            RowsClauses = [Rows],
            NextEffectiveFrom = next,
        };

    public static SourceFormula F(
        string code, string expr, int order, MethodologyFormulaScope scope = MethodologyFormulaScope.Substance,
        bool visible = false, string? unit = null)
        => new(code, expr, order, scope, visible, FormulaResultType.Number, unit);

    public static ConversionResult ConvertOk(VersionSource source)
    {
        var result = ScriptConverter.Convert(source);
        Assert.True(
            result.Text is not null && !result.HasErrors,
            "Errors: " + string.Join(" | ", result.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code + ": " + i.Message)));
        return result;
    }

    public static CompiledScript Compile(
        string text, bool library = false, Func<string, CompiledScript?>? import = null)
    {
        var compiled = ScriptCompiler.Compile(text, new ScriptCompileOptions(NumericMode.Legacy, library, import));
        Assert.True(
            compiled.Diagnostics.Count == 0,
            string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code}@{d.Line}:{d.Column} {d.Message}")) + "\n" + text);
        return compiled.Script!;
    }

    public static bool HasIssue(ConversionResult result, string code, IssueSeverity? severity = null)
        => result.Issues.Any(i => i.Code == code && (severity is null || i.Severity == severity));
}
