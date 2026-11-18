using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Calculations.Scripting;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>Порівняння версій за текстом скрипта, структура скрипта для редактора й заготовка (MS T3.2).</summary>
public sealed class ScriptDiffAndOutlineTests
{
    private static VersionContent Content(string? script) => new([], [], [], null, script);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-T3.2")]
    public void Зміна_тексту_скрипта_дає_запис_Script_із_полем_scriptText()
    {
        var items = CompareMethodologyVersionsHandler.Compare(Content("a"), Content("b"));

        var item = Assert.Single(items);
        Assert.Equal(MethodologyDiffItemKind.Script, item.Kind);
        Assert.Equal(CompareMethodologyVersionsHandler.ScriptCode, item.Code);
        Assert.Equal(MethodologyDiffChange.Changed, item.Change);
        Assert.Equal([MethodologyDiffFields.ScriptText], item.ChangedFields);
        Assert.Equal("a", item.Before);
        Assert.Equal("b", item.After);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-T3.2")]
    public void Різниця_лише_у_переводах_рядків_не_є_зміною_скрипта_а_поява_й_зникнення_тексту_є()
    {
        Assert.Empty(CompareMethodologyVersionsHandler.Compare(Content("a\r\nb"), Content("a\nb")));
        Assert.Empty(CompareMethodologyVersionsHandler.Compare(Content(null), Content("   ")));

        Assert.Equal(
            MethodologyDiffChange.Added,
            Assert.Single(CompareMethodologyVersionsHandler.Compare(Content(null), Content("x"))).Change);
        Assert.Equal(
            MethodologyDiffChange.Removed,
            Assert.Single(CompareMethodologyVersionsHandler.Compare(Content("x"), Content(null))).Change);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-T3.2")]
    public void Заготовка_нової_версії_компілюється_з_єдиною_діагностикою_missingRows()
    {
        var compilation = ScriptTestKit.Compile(ScriptStarter.Text);

        Assert.Equal([ScriptDiagnosticCodes.MissingRows], compilation.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MS-T3.2")]
    public void Структура_розрізняє_таблиці_rows_і_псевдоніми_table_та_віддає_змінні_й_функції()
    {
        var compilation = ScriptTestKit.Compile(
            """
            rows from TPL.T1, T2;
            table Src = T3;
            const k = 2;
            const M = { a: 1 };
            function twice(x, y) = x * y;
            var total = Sum(Src.Amount);
            var amount = row.Fuel? + 1;
            output O : t = twice(total, k) + M.a;
            """);
        Assert.Empty(compilation.Diagnostics);

        var outline = ScriptOutlineDto.From(compilation.Script);

        Assert.Equal(
            [("T1", "TPL", (string?)null), ("T2", null, null), ("T3", null, "Src")],
            outline.Tables.Select(t => (t.Code, t.TemplateCode, t.Alias)));
        Assert.Equal([("Fuel", true)], outline.Inputs.Select(i => (i.Name, i.Optional)));
        Assert.Equal([("k", "scalar", 3), ("M", "map", 4)], outline.Constants.Select(c => (c.Name, c.Kind, c.Line)));
        Assert.Equal([("total", 6), ("amount", 7)], outline.Variables.Select(v => (v.Name, v.Line)));
        var function = Assert.Single(outline.Functions);
        Assert.Equal(("twice", 5), (function.Name, function.Line));
        Assert.Equal(["x", "y"], function.Parameters);
        Assert.Equal([("O", "t", false)], outline.Outputs.Select(o => (o.Code, o.Unit, o.PerSubstance)));

        Assert.Same(ScriptOutlineDto.Empty, ScriptOutlineDto.From(null));
    }
}
