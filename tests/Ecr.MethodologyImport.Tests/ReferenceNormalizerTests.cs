using Ecr.MethodologyImport.Analysis;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Таблиця відповідностей посилань AF → ECR: кожне правило (параметри факела, DimethilSulfide, дві константи),
/// «інші !-імена не чіпаються», порядок і «нерезолвних стає менше». Дані — синтетичні, не реальний файл.
/// </summary>
public sealed class ReferenceNormalizerTests
{
    private static AfXmlBuilder Library()
        => new AfXmlBuilder()
            .Formula("Common", "V1", "Common_WtCi_C2H6S", "", "1")
            .Constant("ECW_C11_11_02", "V1", "MMh2s_ECW_C11_11_02_", "34")
            .Constant("ECW_C11_11_05", "V1", "k3_ECW_C11_11_05", "3600");

    private static (Ecr.MethodologyImport.Model.MethodologyModel Model, AnalysisReport Report) Run(AfXmlBuilder b, bool normalize = true)
        => AnalyzeCommand.Run(AfXmlBuilder.ToStream(b.Build()), normalize: normalize);

    [Theory]
    [InlineData("!Total", "@Total")]
    [InlineData("!Duration", "@Duration")]
    [InlineData("!FlareUnitMode", "@FlareUnitMode")]
    public void Параметри_факела_стають_параметрами_в_аргументах_і_тексті(string from, string to)
    {
        var b = Library().Formula("Flert", "V1", "F", $"{from};CST.nothing_here_", $"{from} * 2 + {from}");

        var (model, report) = Run(b);

        var f = model.Formulas.Single(x => x.Name == "F");
        Assert.Equal($"{to};CST.nothing_here_", f.Arguments);
        Assert.Equal($"{to} * 2 + {to}", f.Text);
        var applied = Assert.Single(report.Normalizations!);
        Assert.Equal(from, applied.From);
        Assert.Equal((1, 2), (applied.InArguments, applied.InTexts));
        Assert.DoesNotContain(report.Unresolved, u => u.Token == from[1..]);
        Assert.Equal(1, report.ParameterArguments);
    }

    [Fact]
    public void DimethilSulfide_стає_Common_WtCi_C2H6S_і_резолвиться()
    {
        var b = Library().Formula("HSE400", "V1", "G", "!Common_WtCi_DimethilSulfide", "!Common_WtCi_DimethilSulfide * 3");

        var (model, report) = Run(b);

        Assert.Equal("!Common_WtCi_C2H6S", model.Formulas.Single(x => x.Name == "G").Arguments);
        Assert.Equal("!Common_WtCi_C2H6S * 3", model.Formulas.Single(x => x.Name == "G").Text);
        Assert.Empty(report.Unresolved);
        Assert.Equal(1, report.FormulaReferences.Resolved);
    }

    [Theory]
    [InlineData("CST.Mh2s_ECW_C11_11_02_", "CST.MMh2s_ECW_C11_11_02_", "ECW_C11_11_02")]
    [InlineData("CST.k3_ECW_C11_11_05_", "CST.k3_ECW_C11_11_05", "ECW_C11_11_05")]
    public void Константи_перейменовуються_і_резолвяться_у_власній_методології(string from, string to, string methodology)
    {
        var b = Library().Formula(methodology, "V1", "F", from, $"{from} / 2");

        var (model, report) = Run(b);

        var f = model.Formulas.Single(x => x.Name == "F");
        Assert.Equal(to, f.Arguments);
        Assert.Equal($"{to} / 2", f.Text);
        Assert.Empty(report.Unresolved);
        Assert.Equal(1, report.ConstantReferences.Resolved);
    }

    [Fact]
    public void Інші_знакові_імена_не_чіпаються_ні_префікси_ні_довші_імена()
    {
        var b = Library().Formula(
            "Flert", "V1", "F",
            "!TotalX;!Common_WtCi_DimethilSulfideX;!Other;CST.Mh2s_ECW_C11_11_02_2;@Total",
            "!TotalX + !Total_2 + x!Total + !Other + CST.Mh2s_ECW_C11_11_02_2 + 'a.CST.Mh2s_ECW_C11_11_02_'");

        var (model, report) = Run(b);

        var f = model.Formulas.Single(x => x.Name == "F");
        Assert.Equal("!TotalX;!Common_WtCi_DimethilSulfideX;!Other;CST.Mh2s_ECW_C11_11_02_2;@Total", f.Arguments);
        Assert.Equal("!TotalX + !Total_2 + x!Total + !Other + CST.Mh2s_ECW_C11_11_02_2 + 'a.CST.Mh2s_ECW_C11_11_02_'", f.Text);
        Assert.Empty(report.Normalizations!);
        Assert.Equal(4, report.Unresolved.Count); // TotalX, ...DimethilSulfideX, Other, CST...02_2 — нічого не вгадано
    }

    [Fact]
    public void Нерезолвних_стає_менше_а_без_нормалізації_лишається_як_було()
    {
        var b = Library()
            .Formula("Flert", "V1", "F1", "!Total;!Duration;!FlareUnitMode", "1")
            .Formula("HSE400", "V1", "F2", "!Common_WtCi_DimethilSulfide", "1")
            .Formula("ECW_C11_11_02", "V1", "F3", "CST.Mh2s_ECW_C11_11_02_", "1")
            .Formula("ECW_C11_11_05", "V1", "F4", "CST.k3_ECW_C11_11_05_", "1")
            .Formula("Flert", "V1", "F5", "!Genuinely_Missing", "1");

        var before = Run(b, normalize: false).Report;
        var after = Run(b).Report;

        Assert.Equal(7, before.Unresolved.Count);
        Assert.Equal("Genuinely_Missing", Assert.Single(after.Unresolved).Token);
        Assert.Equal(6, after.Normalizations!.Count);
        Assert.Empty(before.Normalizations!);
    }

    [Fact]
    public void Таблиця_не_має_дублів_джерела_і_цілі_не_збігаються_з_джерелами()
    {
        var rules = ReferenceNormalizer.Rules;
        Assert.Equal(rules.Count, rules.Select(r => r.From).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(rules.Select(r => r.To).Intersect(rules.Select(r => r.From), StringComparer.Ordinal));
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }
}
