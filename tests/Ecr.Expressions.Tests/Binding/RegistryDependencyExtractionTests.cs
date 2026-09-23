using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Видобуток Registry-залежності (<c>DependsOnKind = 2</c>) для
/// <c>REGFIELD(lookup, "код")</c>.
/// </summary>
/// <remarks>
/// ⛔ Ця залежність — не косметика графа: без неї
/// <c>RecalculationService</c> не знає, який запис якого поля довідника
/// підвантажити перед обчисленням (<c>RegistryFieldRecalculationTests</c> у
/// <c>Ecr.Application.Tests</c> доводить це мутаційно — зламай видобуток тут,
/// і той тест почервоніє).
/// </remarks>
public sealed class RegistryDependencyExtractionTests
{
    private static readonly RangeExpander Expander = new();

    private static (TemplateBuilder Builder, TableDef Table, ColumnDef Lookup) Structure()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var lookup = builder.Column(table, "Permit", CellDataType.Lookup);
        builder.Column(table, "Code", CellDataType.String);
        builder.Column(table, "Result");
        builder.Row(table, "7001001", 1);
        return (builder, table, lookup);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_дає_ОБИДВІ_залежності_Cell_від_комірки_і_Registry_від_поля()
    {
        var (builder, table, lookup) = Structure();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("REGFIELD([Permit], 'Limit')").Expression!.Root;
        var dependencies = extractor.Extract(
            root, table.Id, "7001001", new Dictionary<int, TableDef> { [table.Id] = table });

        Assert.Equal(2, dependencies.Count);

        var cell = Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindCell);
        Assert.Equal(table.Id, cell.TableDefId);
        Assert.Equal("7001001", cell.RowKey);
        Assert.Equal(lookup.Id, cell.ColumnDefId);

        var registry = Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindRegistry);
        Assert.Equal(table.Id, registry.TableDefId);
        Assert.Equal("7001001", registry.RowKey);
        Assert.Equal(lookup.Id, registry.ColumnDefId);
        Assert.Equal("Limit", registry.FilterJson);
        Assert.Null(registry.PeriodOffset);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_у_колонковій_формулі_без_рядка_дає_RowKey_null()
    {
        // Колонкова формула ще не прив'язана до конкретного рядка — та сама
        // семантика, що вже описана для Cell-залежності (RowKey == null
        // означає «кожен рядок таблиці», `RecalculationPlanBuilder.AddCellEdge`).
        var (builder, table, lookup) = Structure();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("REGFIELD([Permit], 'Limit')").Expression!.Root;
        var dependencies = extractor.Extract(
            root, table.Id, currentRowKey: null, new Dictionary<int, TableDef> { [table.Id] = table });

        var registry = Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindRegistry);
        Assert.Null(registry.RowKey);
        Assert.Equal(lookup.Id, registry.ColumnDefId);
        Assert.Equal("Limit", registry.FilterJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Код_поля_обчислюваний_а_не_літерал_не_дає_Registry_залежності()
    {
        // `REGFIELD([Permit], [Code])` — код поля сам є посиланням на комірку:
        // статично невідомий, тож видобувати залежність нема на що. Cell-
        // залежності для ОБОХ аргументів лишаються — REGFIELD однаково
        // порахується в рантаймі.
        var (builder, table, lookup) = Structure();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("REGFIELD([Permit], [Code])").Expression!.Root;
        var dependencies = extractor.Extract(
            root, table.Id, "7001001", new Dictionary<int, TableDef> { [table.Id] = table });

        Assert.Equal(2, dependencies.Count);
        Assert.All(dependencies, d => Assert.Equal(DependencyExtractor.KindCell, d.DependsOnKind));
        Assert.Contains(dependencies, d => d.ColumnDefId == lookup.Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перший_аргумент_не_пряме_посилання_не_дає_Registry_залежності()
    {
        // `IF(TRUE, [Permit], [Permit])` — не CellReferenceNode: адреси
        // Lookup-комірки для Registry-ребра нема звідки статично взяти.
        var (builder, table, _) = Structure();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("REGFIELD(IF(TRUE, [Permit], [Permit]), 'Limit')").Expression!.Root;
        var dependencies = extractor.Extract(
            root, table.Id, "7001001", new Dictionary<int, TableDef> { [table.Id] = table });

        Assert.DoesNotContain(dependencies, d => d.DependsOnKind == DependencyExtractor.KindRegistry);
        // Обидва входження [Permit] всередині IF лишаються звичайними Cell.
        Assert.Equal(2, dependencies.Count(d => d.DependsOnKind == DependencyExtractor.KindCell));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Нерезолвлене_посилання_не_дає_Registry_залежності_і_не_падає()
    {
        var (builder, table, _) = Structure();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("REGFIELD([NoSuchColumn], 'Limit')").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(
            root, table.Id, "7001001", new Dictionary<int, TableDef> { [table.Id] = table }, diagnostics);

        Assert.Empty(dependencies);
        Assert.NotEmpty(diagnostics);
    }
}
