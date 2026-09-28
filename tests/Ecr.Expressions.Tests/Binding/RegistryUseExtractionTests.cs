// tests/Ecr.Expressions.Tests/Binding/RegistryUseExtractionTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Ребра «довідник/шлях поля» функцій довідників (FEATURE-REGISTRY-TABLES §5.8,
/// крок RT-21) — на них публікація методології й шаблону перепише
/// <c>cfg.RegistryUse</c> (RT-23b, RT-24), а на тому стоять «де використано» і
/// позначка застарілості.
/// </summary>
/// <remarks>
/// Форма ребра: <c>DependsOnKind = KindRegistry</c>, <c>TableDefId = null</c>,
/// <c>RowKey</c> — код довідника, <c>FilterJson</c> — шлях (<c>null</c> — сам
/// довідник). Шаблонне ребро <c>REGFIELD([Lookup], 'поле')</c> з адресою комірки —
/// <c>RegistryDependencyExtractionTests</c>, воно не змінилося.
/// </remarks>
public sealed class RegistryUseExtractionTests
{
    private static readonly RangeExpander Expander = new();

    /// <remarks>
    /// Мутація: прибрати гілку <c>RowFieldNode</c> у <c>DependencyExtractor.Visit</c> —
    /// тест червоний (немає ребер <c>MOL_PCT</c> і <c>COMPONENT.MW</c>).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_дає_довідник_і_шляхи_полів_рядка()
    {
        var edges = RegistryEdges(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', [Stream], 'W'), "
            + "ROW.MOL_PCT * ROW.COMPONENT.MW + ROW.MOL_PCT)");

        Assert.Equal(
            [
                ("GAS_COMPOSITION", null),
                ("GAS_COMPOSITION", "CASE"),
                ("STREAM_CASE", null),
                ("GAS_COMPOSITION", "MOL_PCT"),
                ("GAS_COMPOSITION", "COMPONENT.MW"),
            ],
            edges);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вкладений_агрегат_затіняє_ROW_зовнішнього()
    {
        var edges = RegistryEdges(
            "REGSUM('GAS_COMPOSITION', ROW.COMPONENT = REGONE('COMPONENT', ROW.N_S > 0), ROW.MOL_PCT)");

        Assert.Equal(
            [
                ("GAS_COMPOSITION", null),
                ("GAS_COMPOSITION", "COMPONENT"),
                ("COMPONENT", null),
                ("COMPONENT", "N_S"),
                ("GAS_COMPOSITION", "MOL_PCT"),
            ],
            edges);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_від_знайденого_запису_дає_шлях_поля()
    {
        Assert.Equal(
            [("STREAM_CASE", "T_C"), ("STREAM_CASE", null)],
            RegistryEdges("REGFIELD(REGFIND('STREAM_CASE', [Stream], 'W'), 'T_C')"));

        // Від поля рядка — шлях продовжується: ROW.COMPONENT + 'MW'.
        Assert.Contains(
            ("GAS_COMPOSITION", "COMPONENT.MW"),
            RegistryEdges("REGSUM('GAS_COMPOSITION', TRUE, REGFIELD(ROW.COMPONENT, 'MW'))"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Методологія_дає_ті_самі_ребра()
    {
        var root = Expr.Parse(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', @Stream, @HmbCase), ROW.MOL_PCT)",
            ExpressionDialect.Methodology).Expression!.Root;

        var (extractor, table) = Extractor();
        var edges = extractor.Extract(root, table.Id, null)
            .Where(d => d.DependsOnKind == DependencyExtractor.KindRegistry)
            .Select(d => (d.RowKey, d.FilterJson))
            .ToList();

        Assert.Contains(("GAS_COMPOSITION", "MOL_PCT"), edges);
        Assert.Contains(("STREAM_CASE", (string?)null), edges);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обчислений_код_довідника_ребра_не_дає_а_ROW_під_ним_не_чіпляється_до_зовнішнього()
    {
        var edges = RegistryEdges("REGSUM('GAS_COMPOSITION', REGCOUNT([Code], ROW.X > 0) > 0, ROW.MOL_PCT)");

        Assert.Equal([("GAS_COMPOSITION", null), ("GAS_COMPOSITION", "MOL_PCT")], edges);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ребро_довідника_не_має_таблиці_і_не_плутається_з_REGFIELD_комірки()
    {
        var (extractor, table) = Extractor();
        var root = Expr.Parse("REGSUM('GAS_COMPOSITION', ROW.CASE = [Stream], ROW.MOL_PCT)").Expression!.Root;

        var dependencies = extractor.Extract(root, table.Id, "R1", new Dictionary<int, TableDef> { [table.Id] = table });

        // Комірка [Stream] — звичайна Cell-залежність; ребра довідника — без таблиці.
        Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindCell && d.TableDefId == table.Id);
        Assert.All(
            dependencies.Where(d => d.DependsOnKind == DependencyExtractor.KindRegistry),
            d =>
            {
                Assert.Null(d.TableDefId);
                Assert.Null(d.ColumnDefId);
                Assert.Null(d.PeriodOffset);
            });
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static (DependencyExtractor Extractor, TableDef Table) Extractor()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Flare");
        var table = builder.Table(sheet, "Events");
        builder.Column(table, "Stream", CellDataType.Lookup);
        builder.Column(table, "Code", CellDataType.String);
        builder.Row(table, "R1", 1);
        return (new DependencyExtractor(new ReferenceResolver(builder.Build()), Expander), table);
    }

    private static List<(string? Registry, string? Path)> RegistryEdges(string text)
    {
        var (extractor, table) = Extractor();
        var root = Expr.Parse(text).Expression!.Root;

        return extractor.Extract(root, table.Id, "R1", new Dictionary<int, TableDef> { [table.Id] = table })
            .Where(d => d.DependsOnKind == DependencyExtractor.KindRegistry)
            .Select(d => (d.RowKey, d.FilterJson))
            .ToList();
    }
}
