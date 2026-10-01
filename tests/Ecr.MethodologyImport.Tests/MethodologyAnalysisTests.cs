using System.Xml;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reporting;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

public sealed class MethodologyAnalysisTests
{
    private static AnalysisReport Analyze(string xml)
        => AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml)).Report;

    /// <summary>Мінімальний набір: Common (формули + константи) і Flert, яка на нього посилається.</summary>
    private static AfXmlBuilder Baseline()
        => new AfXmlBuilder()
            .Formula("Common", "V1", "Common_WtCi_Benzene", "@CalculationDate;CST.k_Benzene_", "CST.k_Benzene_ * 2")
            .Constant("Common", "V1", "k_Benzene_", "0.5")
            .Constant("Common", "V1", "k_Shared")
            .Formula("Flert", "V1", "Density", "!Common_WtCi_Benzene;CST.k_Shared;CST.k_Own;@StreamID",
                "!Common_WtCi_Benzene * CST.k_Shared * CST.k_Own")
            .Constant("Flert", "V1", "k_Own", "3");

    [Fact]
    public void Trim_прибирає_пробіли_в_іменах_кодах_і_аргументах_і_посилання_резолвяться()
    {
        // У даних AF 133 з 291 посилання `!Common_…` мають пробіл у кінці, а частина `CST.…` — на початку.
        var xml = new AfXmlBuilder()
            .Formula("  Common ", "V1 ", " Common_X ", "CST.k1_ ", "1")
            .Constant("Common", "V1", " k1_ ")
            .Formula("Flert", "V1", "Uses", "!Common_X ; CST.k1_;  @Date ", "!Common_X + CST.k1_")
            .Build();

        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml));

        Assert.Empty(report.Unresolved);
        Assert.False(report.HasBlockers);
        Assert.Equal("Common_X", model.Formulas.First(f => f.Methodology == "Common").Name);
        Assert.Equal("Common", model.Formulas.First(f => f.Name == "Common_X").Methodology);
        Assert.Equal("V1", model.Formulas.First(f => f.Name == "Common_X").MethodologyVersion);
        Assert.All(model.Constants, c => Assert.Equal("k1_", c.Name));
        Assert.Equal("CST.k1_", model.Formulas.Single(f => f.Name == "Common_X").Arguments);
        Assert.True(report.TrimmedFields > 0);
        Assert.True(report.TrimmedArgumentTokens >= 3);
        Assert.Equal(1, report.FormulaReferences.Resolved);
        Assert.Equal(2, report.ConstantReferences.Resolved);
    }

    [Fact]
    public void Common_формули_й_константи_резолвяться_через_межу_методології()
    {
        var report = Analyze(Baseline().Build());

        Assert.Equal(2, report.Methodologies);
        Assert.Equal(2, report.Formulas);
        Assert.Equal(3, report.Constants);
        Assert.Equal(1, report.FormulaReferences.Total);
        Assert.Equal(1, report.FormulaReferences.Resolved);
        Assert.Equal(1, report.FormulaReferences.CrossMethodology);
        Assert.Equal(3, report.ConstantReferences.Total); // k_Benzene_ (Common), k_Shared (Common), k_Own (власна)
        Assert.Equal(3, report.ConstantReferences.Resolved);
        Assert.Equal(1, report.ConstantReferences.CrossMethodology); // лише k_Shared у Flert іде в бібліотеку; k_Benzene_ і k_Own — власні
        Assert.Equal(2, report.ParameterArguments); // @CalculationDate + @StreamID
        Assert.Empty(report.Blockers);
    }

    [Fact]
    public void Нерезолвна_формула_і_константа_є_блокером_із_місцем_і_токеном()
    {
        var xml = Baseline()
            .Formula("Flert", "V1", "Broken", "!Common_WtCi_DimethilSulfide;CST.k_Missing", "1")
            .Build();

        var report = Analyze(xml);

        Assert.True(report.HasBlockers);
        Assert.Contains(report.Blockers, b => b.StartsWith("UNRESOLVED_REFERENCES: 2", StringComparison.Ordinal));
        var formulaRef = Assert.Single(report.Unresolved, u => u.Kind == ReferenceKind.Formula);
        Assert.Equal("Common_WtCi_DimethilSulfide", formulaRef.Token);
        Assert.Equal("Flert", formulaRef.Methodology);
        Assert.Equal("Broken", formulaRef.Formula);
        var constantRef = Assert.Single(report.Unresolved, u => u.Kind == ReferenceKind.Constant);
        Assert.Equal("k_Missing", constantRef.Token);
        Assert.Equal(2, report.UnresolvedTokens.Sum(t => t.Count));
    }

    [Fact]
    public void Ціль_не_вгадується_регістр_і_чужа_методологія_лише_в_підказці()
    {
        var xml = Baseline()
            .Formula("Flert", "V1", "Casey", "!common_wtci_benzene;!Density_Other", "1")
            .Formula("Other", "V1", "Density_Other", "", "1")
            .Build();

        var report = Analyze(xml);

        Assert.Equal(2, report.Unresolved.Count);
        Assert.Contains(report.Unresolved, u => u.Token == "common_wtci_benzene" && u.Hint.Contains("регістром", StringComparison.Ordinal));
        Assert.Contains(report.Unresolved, u => u.Token == "Density_Other" && u.Hint.Contains("Other/V1", StringComparison.Ordinal));
    }

    [Fact]
    public void Цикл_між_формулами_і_самопосилання_є_блокером()
    {
        var xml = new AfXmlBuilder()
            .Formula("M", "V1", "A", "!B", "1")
            .Formula("M", "V1", "B", "!A", "1")
            .Formula("M", "V1", "Self", "!Self", "1")
            .Formula("M", "V1", "Leaf", "!A", "1") // веде в цикл, але сама в ньому не бере участі
            .Build();

        var report = Analyze(xml);

        Assert.Empty(report.Unresolved);
        Assert.Contains(report.Blockers, b => b.StartsWith("REFERENCE_CYCLES: 2", StringComparison.Ordinal));
        Assert.Contains(report.Cycles, c => c.Formulas.SequenceEqual(["M/V1/A/V1", "M/V1/B/V1"]));
        Assert.Contains(report.Cycles, c => c.Formulas.SequenceEqual(["M/V1/Self/V1"]));
    }

    [Fact]
    public void Ациклічний_ланцюжок_не_дає_циклів()
    {
        var xml = new AfXmlBuilder()
            .Formula("M", "V1", "A", "!B", "1")
            .Formula("M", "V1", "B", "!C", "1")
            .Formula("M", "V1", "C", "", "1")
            .Build();

        var report = Analyze(xml);

        Assert.Empty(report.Cycles);
        Assert.Empty(report.Blockers);
    }

    [Fact]
    public void Посилання_не_перетинають_версії_методології_без_імпорту()
    {
        var xml = new AfXmlBuilder()
            .Formula("HSE", "V1", "OnlyV1", "", "1")
            .Formula("HSE", "V2", "User", "!OnlyV1", "1")
            .Build();

        var report = Analyze(xml);

        var u = Assert.Single(report.Unresolved);
        Assert.Contains("HSE/V1", u.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Токен_у_тексті_без_запису_в_аргументах_лічиться_окремо_і_не_блокує()
    {
        var xml = new AfXmlBuilder()
            .Formula("M", "V1", "F", "", "!Ghost + CST.k_Ghost")
            .Build();

        var report = Analyze(xml);

        Assert.Empty(report.Blockers);
        Assert.Equal(2, report.UndeclaredInTextTotal);
    }

    [Fact]
    public void Вивід_детермінований_і_не_залежить_від_порядку_елементів()
    {
        var builder = Baseline().Formula("Flert", "V1", "Broken", "!Nope", "1");

        var forward = ReportWriter.ToJson(Analyze(builder.Build()));
        var backward = ReportWriter.ToJson(Analyze(builder.Build(reverse: true)));
        var again = ReportWriter.ToJson(Analyze(builder.Build()));

        Assert.Equal(forward, again);
        Assert.Equal(forward, backward);
        Assert.Contains("\"unresolved\"", forward, StringComparison.Ordinal);
    }

    [Fact]
    public void Кириличні_імена_переживають_json_без_екранування()
    {
        var xml = new AfXmlBuilder().Formula("Методологія", "V1", "Формула", "!Немає", "1").Build();

        var json = ReportWriter.ToJson(Analyze(xml));

        Assert.Contains("Немає", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Невідомі_теги_лічаться_а_не_валять_розбір()
    {
        // Відомі, але зайві теги (Description, VersionID, AFAnalysis…) мовчки пропускаються, невідомі — лічаться.
        var xml = Baseline()
            .Raw("<AFElement><Name>Odd</Name><AFAnalysisRule>r</AFAnalysisRule><AFAnalysisRule>r</AFAnalysisRule><Foo>1</Foo><AFAnalysis><Name>a</Name></AFAnalysis></AFElement>")
            .Build();

        var report = Analyze(xml);

        Assert.Equal(2, report.Reader.Unrecognized["AFAnalysisRule"]);
        Assert.Equal(1, report.Reader.Unrecognized["Foo"]);
        Assert.False(report.Reader.Unrecognized.ContainsKey("Description"));
        Assert.False(report.Reader.Unrecognized.ContainsKey("AFAnalysis"));
        Assert.Equal(2, report.Formulas);
    }

    [Fact]
    public void Контейнер_формули_не_є_формулою_а_Rules_і_корінь_методології_пропускаються()
    {
        // Реальна структура: Formulas\Ім'я (Formula_El, лише FInfo_Name/MInfo_*) і Formulas\Ім'я\V1 (тіло).
        var xml = new AfXmlBuilder()
            .Formula("M", "V1", "F", "", "1")
            .Raw(AfXmlBuilder.Element("Methodologies\\M\\V1\\Rules\\Rule_001\\V1", "EmissionCalculationWork_Rules_V", AfXmlBuilder.Value("RuleArg_Parameters", "a;b")))
            .Raw(AfXmlBuilder.Element("Air\\Plant", "Air_Area", AfXmlBuilder.Value("Name", "x")))
            .Build();

        var report = Analyze(xml);

        Assert.Equal(1, report.Formulas);
        Assert.Equal(1, report.Reader.FormulaVersionElements);
        Assert.Equal(1, report.Reader.FormulaContainers);
        Assert.Equal(1, report.Reader.SkippedMethodologyElements["Rules"]);
        Assert.Equal(3, report.Reader.SkippedMethodologyElements["(корінь методології)"]); // Methodologies, M, M\V1
        Assert.Equal(1, report.Reader.ElementsOutsideMethodologies);
    }

    [Fact]
    public void Нерезолвне_у_недоступній_формулі_лічиться_окремо_але_лишається_блокером()
    {
        var xml = new AfXmlBuilder()
            .Formula("M", "V1", "On", "!Nope1", "1")
            .Formula("M", "V1", "Off", "!Nope2", "1", available: "False")
            .Build();

        var report = Analyze(xml);

        Assert.Equal(2, report.Unresolved.Count);
        Assert.Equal(1, report.UnresolvedInAvailableFormulas);
        Assert.True(report.HasBlockers);
    }

    [Fact]
    public void Порожній_або_чужий_файл_без_методологій_є_блокером_а_не_тихим_нулем()
    {
        var report = Analyze("<AFDatabase><Name>x</Name><AFElement><Name>Other</Name></AFElement></AFDatabase>");

        Assert.Equal(0, report.Formulas);
        Assert.Contains(report.Blockers, b => b.StartsWith("NO_METHODOLOGY_DATA", StringComparison.Ordinal));
    }

    [Fact]
    public void Некоректний_xml_дає_XmlException()
    {
        Assert.Throws<XmlException>(() => Analyze("<AFDatabase><AFElement></AFDatabase>"));
    }

    [Fact]
    public void DOCTYPE_заборонений()
    {
        Assert.Throws<XmlException>(() => Analyze("<!DOCTYPE a [<!ENTITY x \"y\">]><AFDatabase/>"));
    }

    [Fact]
    public void Методологія_і_версія_розкриваються_з_ConfigString_і_зі_шляху()
    {
        // Вкладена методологія (ECW): ім'я — не другий сегмент шляху, а те, що дає MInfo_Name = "%..\..\..\..\Element%";.
        // FInfo_UniqueName ('MInfo_Name';_;'FInfo_Name') — обчислюваний String Builder: не використовується.
        var xml = new AfXmlBuilder()
            .Formula("EmissionCalculationWork\\ECW_C05_Stationary_Equipment\\ECW_C05_01", "V1", "ECW_EC_gsec", "", "1")
            .Constant("EmissionCalculationWork\\ECW_C05_Stationary_Equipment\\ECW_C05_01", "V1", "k1_")
            .Build();

        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml));

        var f = Assert.Single(model.Formulas);
        Assert.Equal("ECW_C05_01", f.Methodology);
        Assert.Equal("V1", f.MethodologyVersion);
        Assert.Equal("ECW_EC_gsec", f.Name);
        Assert.Equal("V1", f.Version);
        Assert.All(model.Constants, c => Assert.Equal("ECW_C05_01", c.Methodology));
        Assert.Equal("Common", model.Constants.Single(c => c.HasValue).Category);
        Assert.Equal(0, report.Reader.MethodologyFromPath);
        Assert.Equal(0, report.Reader.MethodologyPathMismatch);
        Assert.True(report.Reader.ResolvedFromConfigString > 0);
        Assert.Equal(0, report.Reader.ComputedConfigStrings); // потрібні поля розкрито; обчислюваний UniqueName ніхто не читає
        Assert.Equal(1, report.Methodologies);
    }

    [Fact]
    public void Вкладені_AFElement_нескладеного_експорту_теж_читаються()
    {
        const string xml = """
            <AF><AFDatabase><AFElement><Name>Methodologies</Name>
             <AFElement><Name>Common</Name><AFElement><Name>V1</Name><AFElement><Name>Formulas</Name>
              <AFElement><Name>Common_F</Name><AFElement><Name>V1</Name>
               <AFAttribute><Name>FInfo_Arguments</Name><Value>CST.k</Value></AFAttribute>
               <AFAttribute><Name>FInfo_Text</Name><Value>1</Value></AFAttribute>
               <AFAttribute><Name>MInfo_Name</Name><Value>Common</Value></AFAttribute>
               <AFAttribute><Name>MInfo_Version</Name><Value>V1</Value></AFAttribute>
              </AFElement></AFElement>
             </AFElement></AFElement></AFElement>
            </AFElement></AFDatabase></AF>
            """;

        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(xml));

        var f = Assert.Single(model.Formulas);
        Assert.Equal("Common/V1/Common_F/V1", f.Key);
        Assert.Equal(0, report.Reader.MethodologyFromPath);
    }

    [Fact]
    public void Літерал_String_Builder_розкривається_а_складений_вираз_ні()
    {
        var path = new[] { "Methodologies", "Common", "V1", "Formulas", "F", "V1" };

        Assert.Equal("V1", MethodologyModelBuilder.ResolveConfig("\"%Element%\";\r\n", path));
        Assert.Equal("F", MethodologyModelBuilder.ResolveConfig("\"%..\\Element%\";", path));
        Assert.Equal("Common", MethodologyModelBuilder.ResolveConfig("\"%..\\..\\..\\..\\Element%\";", path));
        Assert.Null(MethodologyModelBuilder.ResolveConfig("'CInfo_Parameter';_;'CInfo_Gas';", path));
        Assert.Null(MethodologyModelBuilder.ResolveConfig("\"%..\\..\\..\\..\\..\\..\\..\\Element%\";", path));
    }

    [Fact]
    public void Читач_потоковий_перший_елемент_віддається_до_прочитання_решти_файла()
    {
        // Мутація «XDocument.Load / читати все» зробила б позицію потоку при першому записі = довжині файла.
        var builder = new AfXmlBuilder();
        for (var i = 0; i < 5000; i++)
        {
            builder.Formula("M", "V1", "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "1");
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.Build());
        using var stream = new MemoryStream(bytes);
        long positionAtFirst = -1;
        var count = 0;
        Ecr.MethodologyImport.Reading.AfXmlReader.Read(stream, _ =>
        {
            if (positionAtFirst < 0)
            {
                positionAtFirst = stream.Position;
            }

            count++;
        });

        Assert.Equal(10_003, count); // 5000 × (контейнер + версія) + Methodologies, M, M\V1
        Assert.InRange(positionAtFirst, 1, bytes.Length / 10);
    }
}
