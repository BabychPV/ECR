using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Правило «вираз віддається, лише коли читач бачить усі посилання» на рівні предикатів: те, що
/// наскрізні тести недосяжні для справжнього рушія (ребро довідника з таблицею).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути <c>true</c> для <c>KindRegistry</c> у <c>DependencyVisible</c>
/// (як було) — червоніє тест про ребро довідника із забороненою таблицею; решта лишаються зеленими.
/// </remarks>
public sealed class ColumnExpressionVisibilityTests
{
    private const int DeniedTable = 41;
    private const int DeniedColumn = 42;
    private const int OpenTable = 51;
    private const int OpenColumn = 52;
    private const string Expression = "REGFIELD(1)";

    private static readonly TemplateVersionSnapshot Snapshot =
        new(2, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());

    private static bool CanReadTable(int id) => id != DeniedTable;

    private static bool CanReadColumn(int id) => id != DeniedColumn;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Ребро_довідника_з_забороненою_таблицею_ховає_вираз()
        => Assert.Null(Run(Registry(DeniedTable, OpenColumn)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Ребро_довідника_із_забороненою_колонкою_ховає_вираз()
        => Assert.Null(Run(Registry(OpenTable, DeniedColumn)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ребро_довідника_з_видимою_таблицею_і_колонкою_віддає_вираз()
        => Assert.Equal(Expression, Run(Registry(OpenTable, OpenColumn)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ребро_довідника_без_таблиці_віддає_вираз()
        => Assert.Equal(Expression, Run(new FormulaDependencyRef(DependencyExtractor.KindRegistry, null, "REG", null, "A.B", null, 0)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Без_рушія_вираз_не_віддається()
        => Assert.Null(Run(Registry(OpenTable, OpenColumn), withEngine: false));

    private static FormulaDependencyRef Registry(int tableId, int columnId)
        => new(DependencyExtractor.KindRegistry, tableId, "r1", columnId, null, null, 0);

    private static string? Run(FormulaDependencyRef edge, bool withEngine = true)
    {
        var engine = Substitute.For<IFormulaEngine>();
        engine.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
            .Returns(new RealFormulaEngine().Parse("1", ExpressionDialect.Template));
        engine.ExtractDependencies(Arg.Any<Ecr.Expressions.Parsing.ParsedExpression>(), Arg.Any<TemplateVersionSnapshot?>(), Arg.Any<DependencyContext>())
            .Returns(new DependencyExtraction([edge], []));

        var table = new TableDef(1, EcrCode.Create("Main"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        var formula = new FormulaDef(3, FormulaScope.Column, Expression, ExpressionDialect.Template);

        return ColumnExpressionVisibility.Visible(
            formula, table, 7, Snapshot, CanReadTable, CanReadColumn, withEngine ? engine : null);
    }
}
