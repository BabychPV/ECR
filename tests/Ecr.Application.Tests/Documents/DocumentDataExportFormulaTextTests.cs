using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// P2: текстове значення Formula/Calculated-колонки (результат моста, «No - Нет») не губиться в
/// експорті — раніше віддавався лише <c>ValueNumeric</c>.
/// </summary>
public sealed class DocumentDataExportFormulaTextTests
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> NoLookups
        = new Dictionary<int, IReadOnlyDictionary<long, string>>();

    private static ColumnDef Column(CellDataType type, string code = "C")
        => new(1, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), 1, type);

    /// <remarks>
    /// Мутація: у <c>FormatValue</c> повернути гілку Formula/Calculated до «лише ValueNumeric» —
    /// текстовий результат знову порожній.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(CellDataType.Formula)]
    [InlineData(CellDataType.Calculated)]
    public void Текстовий_результат_формули_віддається_текстом(CellDataType type)
        => Assert.Equal(
            "No - Нет",
            DocumentDataExporter.FormatValue(
                Column(type), new CellValueData { ValueString = "No - Нет" }, NoLookups));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(CellDataType.Formula)]
    [InlineData(CellDataType.Calculated)]
    public void Числовий_результат_формули_без_змін(CellDataType type)
    {
        var column = Column(type);
        Assert.Equal("12.5", DocumentDataExporter.FormatValue(
            column, new CellValueData { ValueNumeric = 12.5m }, NoLookups));

        // Є і число, і рядок: число має пріоритет.
        Assert.Equal("7", DocumentDataExporter.FormatValue(
            column, new CellValueData { ValueNumeric = 7m, ValueString = "x" }, NoLookups));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Порожня_формула_лишається_порожньою()
        => Assert.Null(DocumentDataExporter.FormatValue(
            Column(CellDataType.Formula), new CellValueData(), NoLookups));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Csv_текст_формули_екранується_а_число_лишається_як_є()
    {
        var formula = Column(CellDataType.Formula, "F");
        var table = new ExportTable(
            "S", "T", [formula],
            [
                ("R1", new Dictionary<string, string> { ["F"] = "No - Нет" }),
                ("R2", new Dictionary<string, string> { ["F"] = "-5" }),
                ("R3", new Dictionary<string, string> { ["F"] = "=1+1" }),
            ],
            new Dictionary<string, string>());

        var lines = DocumentDataExporter.Csv(table)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("R1,No - Нет", lines);
        Assert.Contains("R2,-5", lines);
        Assert.DoesNotContain("R3,=1+1", lines); // формула-ін'єкція не проходить сирою
        Assert.Equal("R3,'=1+1", lines.Single(l => l.StartsWith("R3,", StringComparison.Ordinal)));
    }
}
