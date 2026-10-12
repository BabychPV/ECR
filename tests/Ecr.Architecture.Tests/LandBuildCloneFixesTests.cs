using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Build-LandTemplate.ps1 на чернетці-клоні опублікованої версії (оновлення наявного шаблону, перевірка 13.10 на копії
/// стенда): (1) один UPDATE презентації колонок на ~4,3 тис. рядків біг понад 300 с (тригер
/// <c>TR_ColumnDef_Immutable</c> порівнює inserted x deleted без індексів) і падав з Execution Timeout — тепер порціями;
/// (2) місячні колонки 7.0 у клоні лишались Decimal, а модель веде їх як Formula (формули комірок) — PUT formulas/cell
/// давав ECR-TMPL-4227, тож у чернетці тип переводиться в обчислюваний.
/// Живий прогін: Build + Verify на копії EcrLandUi, 0 розбіжностей.
/// </summary>
public sealed class LandBuildCloneFixesTests
{
    private static string Build()
        => File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1"));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Оновлення_презентації_колонок_іде_порціями_а_не_одним_UPDATE()
    {
        var text = Build();

        Assert.Contains("p.N > @lo AND p.N <= @hi", text);
        Assert.Contains("$lo += 200", text);
        Assert.DoesNotContain("FROM cfg.ColumnDef c JOIN #CP p ON p.Id = c.Id WHERE (p.StyleId IS NOT NULL", text);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонка_чернетки_клону_переводиться_з_Decimal_у_Formula_лише_в_обчислюваний_тип()
    {
        var text = Build();

        Assert.Contains("$retype", text);
        Assert.Contains("UPDATE cfg.ColumnDef SET DataType=@t, IsReadOnly=1 WHERE Id=@i", text);
        Assert.Contains("if ($hi.type -notin 2, 1) { continue }", text);
    }
}
