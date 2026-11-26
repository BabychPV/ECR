// tests/Ecr.Scripting.Tests/RegistryRowFieldCompileTests.cs
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// ✎ D-580 (SGF-flaring, рішення людини 26.11, варіант A): <c>ROW.&lt;поле&gt;</c> у скрипті — лише в умові/виразі
/// <c>REGONE</c>/<c>REGSUM</c>/<c>REGAVG</c>/<c>REGMIN</c>/<c>REGMAX</c>/<c>REGCOUNT</c>, тим самим вузлом, що в діалекті формул.
/// </summary>
public sealed class RegistryRowFieldCompileTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-FLARING-D580")]
    public void ROW_у_REGSUM_лягає_у_поле_рядка_довідника_як_у_формулах()
    {
        var printed = T.Expr(
            "var c = 1;\n",
            "REGSUM(\"GAS_COMPOSITION\", ROW.CASE == c && ROW.COMPONENT.CODE == \"H2S\", ROW.MOL_PCT)",
            NumericMode.Legacy);

        Assert.Contains("ROW.CASE", printed, StringComparison.Ordinal);
        Assert.Contains("ROW.COMPONENT.CODE", printed, StringComparison.Ordinal);
        Assert.Contains("ROW.MOL_PCT", printed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REGONE(\"STREAM\", ROW.LEGACY_ID == row.ID)")]
    [InlineData("REGCOUNT(\"STREAM\", ROW.IS_ONSHORE == true)")]
    [InlineData("REGAVG(\"STREAM\", true, ROW.LEGACY_ID)")]
    [InlineData("REGMIN(\"STREAM\", true, ROW.LEGACY_ID)")]
    [InlineData("REGMAX(\"STREAM\", true, ROW.LEGACY_ID)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-FLARING-D580")]
    public void ROW_дозволено_в_усіх_функціях_області_рядка_і_довідник_у_RegistryCodes(string expr)
    {
        var c = T.WithRows("var x = " + expr + ";\n", NumericMode.Legacy);

        Assert.True(c.IsSuccess, T.Describe(c));
        Assert.Equal(["STREAM"], c.Script!.RegistryCodes);
    }

    [Theory]
    [InlineData("var x = ROW.LEGACY_ID;")]
    [InlineData("var x = REGFIND(\"STREAM\", ROW.CODE);")]
    [InlineData("var x = REGFIELD(REGFIND(\"STREAM\", \"S1\"), ROW.NAME);")]
    [InlineData("var x = REGSUM(ROW.CODE, true, 1);")]
    [InlineData("var x = Abs(ROW.LEGACY_ID);")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-FLARING-D580")]
    public void ROW_поза_умовою_чи_виразом_REG__помилка_rowOutsideRegistryFunction(string body)
    {
        var c = T.WithRows(body + "\n", NumericMode.Legacy);

        var d = Assert.Single(c.Diagnostics, d => d.Code == ScriptDiagnosticCodes.RowOutsideRegistryFunction);
        Assert.Equal("script.rowOutsideRegistryFunction", d.MessageKey);
        Assert.False(c.IsSuccess);
    }

    /// <summary>Мала літера — як і раніше колонка рядка документа, не довідника.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-FLARING-D580")]
    public void row_мала_літера_лишається_колонкою_документа()
    {
        var c = T.WithRows("var x = REGSUM(\"STREAM\", ROW.LEGACY_ID == row.ID, ROW.LEGACY_ID);\n", NumericMode.Legacy);

        Assert.True(c.IsSuccess, T.Describe(c));
        Assert.Contains(c.Script!.RowFields, f => f.Name == "ID");
        Assert.DoesNotContain(c.Script!.RowFields, f => f.Name == "LEGACY_ID");
    }
}
