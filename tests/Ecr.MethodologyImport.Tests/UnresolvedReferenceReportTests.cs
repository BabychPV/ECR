using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Reporting;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Звіт нерезолвних посилань з рекомендацією. Фрагменти синтетичні, у стилі <c>ECR_01_Air.xml</c>
/// (структура — <see cref="AfXmlBuilder"/>); реальні 17 посилань з файла на пристрої користувача сюди не скопійовано.
/// </summary>
public sealed class UnresolvedReferenceReportTests
{
    private static IReadOnlyList<UnresolvedRecommendation> Recommend(AfXmlBuilder builder)
    {
        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(builder.Build()));
        return UnresolvedReferenceReport.Recommend(model, report);
    }

    private static AfXmlBuilder Library()
        => new AfXmlBuilder()
            .Formula("Common", "V1", "Common_WtCi_DimethylSulfide", "", "1")
            .Formula("Common", "V1", "Common_WtCi_Benzene", "", "1")
            .Constant("Common", "V1", "k_Benzene_", "0.5", parameter: "BenzeneFactor");

    private static UnresolvedRecommendation Single(IReadOnlyList<UnresolvedRecommendation> items, string token)
        => Assert.Single(items, i => i.Reference.Token == token);

    [Fact]
    public void Регістр_дає_механічну_заміну_без_методолога()
    {
        var items = Recommend(Library().Formula("Flert", "V1", "F", "!common_wtci_benzene", "1"));

        var r = Single(items, "common_wtci_benzene");
        Assert.Equal(UnresolvedCategory.CaseMismatch, r.Category);
        Assert.Equal("Common_WtCi_Benzene", r.Candidate);
        Assert.False(r.NeedsMethodologist);
    }

    [Fact]
    public void Описка_в_одну_літеру_дає_кандидата_але_рішення_за_методологом()
    {
        // «Benzine» замість «Benzene» — саме такий вигляд мають описки в аргументах AF.
        var items = Recommend(Library().Formula("Flert", "V1", "F", "!Common_WtCi_Benzine", "1"));

        var r = Single(items, "Common_WtCi_Benzine");
        Assert.Equal(UnresolvedCategory.LikelyTypo, r.Category);
        Assert.Equal("Common_WtCi_Benzene", r.Candidate);
        Assert.True(r.NeedsMethodologist);
    }

    [Fact]
    public void Два_рівновіддалені_кандидати_не_вгадуються()
    {
        var items = Recommend(new AfXmlBuilder()
            .Formula("Common", "V1", "Flow_A1", "", "1")
            .Formula("Common", "V1", "Flow_A2", "", "1")
            .Formula("Flert", "V1", "F", "!Flow_A3", "1"));

        var r = Single(items, "Flow_A3");
        Assert.Equal(UnresolvedCategory.Missing, r.Category);
        Assert.Null(r.Candidate);
    }

    [Fact]
    public void Далеке_ім_я_не_вважається_опискою()
    {
        var items = Recommend(Library().Formula("Flert", "V1", "F", "!Common_WtCi_Toluene", "1"));

        var r = Single(items, "Common_WtCi_Toluene");
        Assert.Equal(UnresolvedCategory.Missing, r.Category);
        Assert.Null(r.Candidate);
        Assert.True(r.BlocksCalculation);
    }

    [Fact]
    public void Визначення_в_чужій_методології_названо_з_місцем()
    {
        var items = Recommend(Library()
            .Formula("Other", "V2", "Density_Other", "", "1")
            .Constant("Other", "V2", "k_Foreign")
            .Formula("Flert", "V1", "F", "!Density_Other;CST.k_Foreign", "1"));

        Assert.All(items, i => Assert.Equal(UnresolvedCategory.DefinedElsewhere, i.Category));
        Assert.Contains("Other/V2", Single(items, "Density_Other").Action, StringComparison.Ordinal);
        Assert.Contains("Other/V2", Single(items, "k_Foreign").Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Константа_назване_параметром_отримує_справжнє_ім_я()
    {
        var items = Recommend(Library().Formula("Flert", "V1", "F", "CST.BenzeneFactor", "1"));

        var r = Single(items, "BenzeneFactor");
        Assert.Equal(UnresolvedCategory.ParameterNameUsed, r.Category);
        Assert.Equal("k_Benzene_", r.Candidate);
    }

    [Fact]
    public void Некоректний_токен_не_отримує_кандидата()
    {
        var items = Recommend(Library().Formula("Flert", "V1", "F", "!Common_WtCi-Benzene", "1"));

        var r = Assert.Single(items);
        Assert.Equal(UnresolvedCategory.InvalidToken, r.Category);
        Assert.Null(r.Candidate);
    }

    [Fact]
    public void Вимкнена_формула_позначена_як_така_що_не_блокує_розрахунок()
    {
        var items = Recommend(Library().Formula("Flert", "V1", "Off", "!Nope_Missing", "1", available: "False"));

        var r = Single(items, "Nope_Missing");
        Assert.False(r.BlocksCalculation);
        Assert.Contains("IsAvailable=False", r.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_має_рядок_на_кожне_посилання_підсумок_і_детермінований()
    {
        var builder = Library()
            .Formula("Flert", "V1", "F", "!common_wtci_benzene;!Common_WtCi_NoSuchFormula;CST.k_None", "1");

        var first = UnresolvedReferenceReport.ToMarkdown(Recommend(builder));
        var second = UnresolvedReferenceReport.ToMarkdown(Recommend(builder));

        Assert.Equal(first, second);
        Assert.Contains("Усього: 3;", first, StringComparison.Ordinal);
        Assert.Contains("`!common_wtci_benzene`", first, StringComparison.Ordinal);
        Assert.Contains("`CST.k_None`", first, StringComparison.Ordinal);
        Assert.Equal(3, first.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\| \d+ \|")));
    }

    [Fact]
    public void Без_нерезолвних_звіт_порожній_але_валідний()
    {
        var md = UnresolvedReferenceReport.ToMarkdown(Recommend(Library().Formula("Flert", "V1", "F", "!Common_WtCi_Benzene", "1")));

        Assert.Contains("Усього: 0;", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Abc", null)]           // коротке ім'я: кандидатів не шукаємо
    [InlineData("Flow_X", "Flow_Y")]    // 6 символів: відстань 1
    [InlineData("Flow_XZ", null)]       // відстань 2 при межі 1
    [InlineData("Stream_Rate_X", "Stream_Rate_YZ")] // 13 символів: відстань 2
    public void Межа_відстані_залежить_від_довжини(string token, string? expected)
        => Assert.Equal(expected, UnresolvedReferenceReport.Nearest(token, ["Abd", "Flow_Y", "Stream_Rate_YZ"]));
}
