using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>MS Етап 7: версії рівня Module (HSE400, «місяці в колонках») → скрипт без <c>rows</c>.</summary>
public sealed class ModuleLevelConversionTests
{
    private static VersionSource Module(
        IEnumerable<SourceFormula> formulas,
        IEnumerable<SourceConstant>? constants = null,
        IEnumerable<SourceOutput>? outputs = null)
        => new()
        {
            MethodologyCode = "X400",
            IsModuleLevel = true,
            NumericMode = NumericMode.Legacy,
            Formulas = [.. formulas],
            Constants = [.. constants ?? []],
            Outputs = [.. outputs ?? []],
        };

    [Fact]
    public void Формули_стають_output_у_порядку_обчислення_а_GCV_читається_як_row_з_підкресленням()
    {
        var source = Module(
            [
                new SourceFormula("Common_K", "CST.Factor * 2", 1),
                new SourceFormula("X400_TA_Sum", "@GCV.X400_TA_Methane + @GCV_X400_TA_Nitrogen", 2),
                new SourceFormula("X400_TA_Ratio", "Round(!X400_TA_Sum / !Common_K, 2)", 3, UnitCode: "kg"),
            ],
            [SourceConstant.OfNumber("Factor", 1.5m, unitCode: "one")]);

        var result = ScriptConverter.Convert(source);

        Assert.NotNull(result.Text);
        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain("rows", result.Text, StringComparison.Ordinal);
        Assert.Contains("(module level)", result.Text, StringComparison.Ordinal);
        Assert.Contains("output Common_K : one = Factor * 2;", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "output X400_TA_Sum : one = row.GCV_X400_TA_Methane + row.GCV_X400_TA_Nitrogen;", result.Text, StringComparison.Ordinal);
        Assert.Contains("output X400_TA_Ratio : kg = Round(X400_TA_Sum / Common_K, 2);", result.Text, StringComparison.Ordinal);

        var compiled = ScriptCompiler.Compile(
            result.Text!, new ScriptCompileOptions(NumericMode.Legacy, false, null, IsModuleLevel: true));
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Diagnostics.Select(d => d.Code)));
        Assert.Equal(["Common_K", "X400_TA_Sum", "X400_TA_Ratio"], compiled.Script!.Outputs.Select(o => o.Code));
        Assert.Equal(["X400_TA_Sum", "Common_K"], compiled.Script.StepDependencies["X400_TA_Ratio"]);
    }

    [Fact]
    public void Модульна_версія_без_формул_і_з_речовинами_не_перетворюється()
    {
        Assert.Contains(
            ScriptConverter.Convert(Module([])).Issues, i => i is { Code: "moduleNoFormulas", Severity: IssueSeverity.Error });

        var withSubstances = Module([new SourceFormula("X400_TA_A", "1", 1)]) with { Substances = ["CH4"] };
        Assert.Contains(
            ScriptConverter.Convert(withSubstances).Issues, i => i is { Code: "moduleSubstances", Severity: IssueSeverity.Error });
    }

    [Fact]
    public void Звернення_вперед_у_модульній_версії_відхиляється()
    {
        var result = ScriptConverter.Convert(Module(
        [
            new SourceFormula("X400_TA_B", "!X400_TA_A + 1", 1),
            new SourceFormula("X400_TA_A", "@GCV.X400_TA_X", 2),
        ]));

        Assert.Null(result.Text);
        Assert.Contains(result.Issues, i => i.Code == "forwardReference");
    }

    [Fact]
    public void Відбиток_відрізняє_модульну_версію_від_звичайної()
    {
        var formulas = new[] { new SourceFormula("X400_TA_A", "1", 1) };
        var module = Module(formulas);
        var plain = module with { IsModuleLevel = false };

        Assert.NotEqual(Fingerprint.ComputeHex(module), Fingerprint.ComputeHex(plain));
    }
}
