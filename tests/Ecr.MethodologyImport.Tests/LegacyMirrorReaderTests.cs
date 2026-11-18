using Ecr.MethodologyImport.Export;
using Ecr.MethodologyImport.Reading;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Джерело «дзеркала SQL старої системи» (AF_Formulas / AF_Constants): синтетичні рядки, без БД і без реальних даних.
/// Головна вимога — та сама модель і той самий пакет, що й з AF XML.
/// </summary>
public sealed class LegacyMirrorReaderTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById(LegacyMirrorReader.DefaultTimeZone);

    private static readonly DateTime Start = new(2024, 1, 1);
    private static readonly DateTime FormulaEnd = new(9999, 2, 20);

    private static LegacyFormulaRow Formula(string m, string name, string args, string text, string version = "V1", bool available = true)
        => new(m, "V1", name, version, args, text, Start, FormulaEnd, available, "", Guid.NewGuid().ToString());

    private static LegacyConstantRow Constant(
        string m, string name, string value, string category = "Common", string version = "V1", string? unit = null)
        => new(m, "V1", name, category, version, value, "p", Start, null, unit, Guid.NewGuid().ToString());

    private static MethodologyPackage Package(
        IEnumerable<LegacyFormulaRow> formulas, IEnumerable<LegacyConstantRow> constants, string? only = null)
    {
        var (model, readStats, _) = LegacyMirrorReader.Build(formulas, constants, Atyrau, only);
        var (analysed, report) = AnalyzeCommand.Analyze(model, readStats);
        return MethodologyPackage.From(analysed, report, "Common");
    }

    [Fact]
    public void Пакет_із_дзеркал_збігається_з_пакетом_з_AF_XML_для_тих_самих_даних()
    {
        var xml = new AfXmlBuilder()
            .Formula("Common", "V1", "Common_A", "CST.k1_ ; @Date", "CST.k1_ * 2")
            .Constant("Common", "V1", "k1_", "0.5")
            .Constant("Common", "V1", "k1_", "0.7", category: "Loc_B")
            .Formula("Flert", "V1", "Uses", "!Common_A;CST.k1_", "!Common_A")
            .Build();
        var (xmlModel, xmlReport) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml));
        var fromXml = MethodologyPackage.From(xmlModel, xmlReport, "Common");

        var fromMirror = Package(
            [
                Formula("Common", "Common_A", "CST.k1_ ; @Date", "CST.k1_ * 2"),
                Formula("Flert", "Uses", "!Common_A;CST.k1_", "!Common_A"),
            ],
            [
                Constant("Common", "k1_", "0.5"),
                Constant("Common", "k1_", "0.7", "Loc_B"),
            ]);

        Assert.Equal(fromXml.ToJson(), fromMirror.ToJson());
    }

    [Fact]
    public void Локальні_дати_дзеркала_стають_UTC_рядками_AF_з_сентинелами()
    {
        var package = Package(
            [Formula("Common", "Common_A", "CST.k1_", "CST.k1_")],
            [Constant("Common", "k1_", "1")]);

        var formula = Assert.Single(package.Methodologies[0].Versions[0].Formulas);
        Assert.Equal("2023-12-31T19:00:00Z", formula.StartDate);   // 2024-01-01 00:00 за Атирау (UTC+5)
        Assert.Equal("9999-02-19T19:00:00Z", formula.EndDate);
        var value = Assert.Single(Assert.Single(package.Methodologies[0].Versions[0].Constants).Values);
        Assert.Equal("2023-12-31T19:00:00Z", value.StartDate);
        Assert.Equal(string.Empty, value.EndDate);
    }

    [Fact]
    public void Поля_обрізаються_а_порожні_ключі_пропускаються_і_лічаться()
    {
        var rows = new[]
        {
            Formula(" Common ", " Common_A ", " CST.k1_ ", " CST.k1_ "),
            Formula("", "NoMethodology", "1", "1"),
        };

        var (model, _, stats) = LegacyMirrorReader.Build(rows, [Constant(" Common ", " k1_ ", " 0.5 ")], Atyrau);

        var formula = Assert.Single(model.Formulas);
        Assert.Equal(("Common", "Common_A", "CST.k1_", "CST.k1_"), (formula.Methodology, formula.Name, formula.Arguments, formula.Text));
        Assert.Equal("0.5", Assert.Single(model.Constants).Value);
        Assert.Equal(2, stats.FormulaRows);
        Assert.Equal(1, stats.SkippedRows);
        Assert.True(stats.TrimmedFields >= 5);
    }

    [Fact]
    public void Фільтр_методології_лишає_лише_її_рядки()
    {
        var package = Package(
            [Formula("Common", "Common_A", "1", "1"), Formula("Flert", "F", "1", "1")],
            [Constant("Common", "k1_", "1"), Constant("Flert", "k2_", "2")],
            only: "Flert");

        var methodology = Assert.Single(package.Methodologies);
        Assert.Equal("Flert", methodology.Name);
        Assert.Equal("k2_", Assert.Single(methodology.Versions[0].Constants).Name);
    }

    [Fact]
    public void Недоступна_формула_і_кілька_версій_констант_зберігаються()
    {
        var package = Package(
            [Formula("Common", "Common_A", "CST.k1_", "CST.k1_", "V1", available: false), Formula("Common", "Common_A", "CST.k1_", "CST.k1_ * 2", "V2")],
            [Constant("Common", "k1_", "1"), Constant("Common", "k1_", "2", version: "V2")]);

        var version = Assert.Single(package.Methodologies[0].Versions);
        Assert.Equal([false, true], version.Formulas.Select(f => f.IsAvailable));
        Assert.Equal(["1", "2"], Assert.Single(version.Constants).Values.Select(v => v.Value));
    }

    [Fact]
    public void Нерезолвне_посилання_у_дзеркалі_дає_блокер_як_і_в_XML()
    {
        var (model, readStats, _) = LegacyMirrorReader.Build(
            [Formula("Flert", "Uses", "!Missing_F", "!Missing_F")], [], Atyrau);

        var (_, report) = AnalyzeCommand.Analyze(model, readStats);

        Assert.True(report.HasBlockers);
    }
}
