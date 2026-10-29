// tests/Ecr.Adapters.Tests/Excel/ExcelExporterNoGroupsRegressionTests.cs
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;
using static Ecr.Adapters.Tests.Excel.ExcelExportHarness;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Групи заголовків (фаза 2): таблиця БЕЗ груп експортується рівно так, як до змін - клітинка в клітинку
/// (значення, типи, формати, стиль шапки, об'єднання, закріплення) і та сама карта книги разом з відбитками.
/// </summary>
/// <remarks>
/// Еталон знято з коду <c>origin/main</c> (до зміни експортера) на цьому ж наборі даних.
/// </remarks>
public sealed class ExcelExporterNoGroupsRegressionTests
{
    internal static readonly HarnessColumn[] Columns =
    [
        new("T", L("Tons", "Тонны"), CellDataType.Decimal),
        new("MG", L("Mg", "Мг"), CellDataType.Decimal),
        new("NOTE", L("Note", "Примечание"), CellDataType.String),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_без_груп_дає_ту_саму_книгу_що_й_до_змін()
    {
        using var workbook = await ExportAsync(Columns, []);
        Assert.Equal(Golden.Replace("\r\n", "\n"), Dump(workbook).TrimEnd('\n'));
    }

    private const string Golden = """
# Sheet visible=Visible
merged=
frozen=2
A1|Text|Table 1|B||Color Index: 64
A2|Text|Tons|B||FFDDE5F0
B2|Text|Mg|B||FFDDE5F0
C2|Text|Note|B||FFDDE5F0
D2|Text|Row|B||FFDDE5F0
A3|Number|100.5|-||Color Index: 64
B3|Number|101.5|-||Color Index: 64
C3|Text|s02|-|@|Color Index: 64
D3|Text|Row R1|-||Color Index: 64
A4|Number|200.5|-||Color Index: 64
B4|Number|201.5|-||Color Index: 64
C4|Text|s12|-|@|Color Index: 64
D4|Text|Row R2|-||Color Index: 64
A5|Number|300.5|-||Color Index: 64
B5|Number|301.5|-||Color Index: 64
C5|Text|s22|-|@|Color Index: 64
D5|Text|Row R3|-||Color Index: 64
# _ecr visible=Hidden
merged=
frozen=0
{"documentId":1,"periodKey":202609,"templateVersionId":1,"tables":[{"tableInstanceId":729,"tableDefId":1,"tableCode":"T1","sheetName":"Sheet","headerRow":2,"columns":[{"columnDefId":10,"code":"T","number":1,"isCalculated":false,"lookupRegistryDefId":null,"header":"bd39"},{"columnDefId":11,"code":"MG","number":2,"isCalculated":false,"lookupRegistryDefId":null,"header":"18f6"},{"columnDefId":12,"code":"NOTE","number":3,"isCalculated":false,"lookupRegistryDefId":null,"header":"a66d"}],"rows":[{"rowKey":"R1","number":3,"cells":"9ff5ce8d7cd2ed366e057883","label":"8521"},{"rowKey":"R2","number":4,"cells":"a5584c78a8caad4f6c0336c6","label":"99f8"},{"rowKey":"R3","number":5,"cells":"c61b6c07a3965b70fe0ad861","label":"9e0b"}]}]}
""";
}
