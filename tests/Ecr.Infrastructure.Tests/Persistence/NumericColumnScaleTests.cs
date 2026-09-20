// tests/Ecr.Infrastructure.Tests/Persistence/NumericColumnScaleTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>D-148</c>: усі стовпці вимірюваних величин у РОЗГОРНУТІЙ базі мають
/// масштаб 16.
/// </summary>
/// <remarks>
/// ⛔ Це перевірка БАЗИ, а не моделі, і різниця не формальна. Модель EF
/// звіряється зі знімком міграцій (<c>PendingModelChangesWarning</c> робить
/// розбіжність помилкою вже на <c>Migrate</c>), але НІЩО не звіряє тіло
/// міграції з моделлю: звузити тип у самій міграції — і модель лишиться
/// «правильною», параметри поїдуть із масштабом 16, а SQL Server тихо
/// округлить їх до того, що вміщає стовпець.
///
/// ⚠ Перелік — таблицею, а не окремим тестом на колонку: додати колонку з
/// вимірюваною величиною й забути її тут має бути видно з одного місця.
///
/// ⚠ `uom.Unit`/`uom.Conversion` (коефіцієнти, <c>decimal(38,18)</c>) і
/// `cfg.StyleDef.FontSize` (<c>decimal(4,1)</c>) свідомо ПОЗА переліком:
/// перші вже точніші за 16 знаків і звузити їх означало б зіпсувати
/// конверсію, другий — розмір шрифту в пунктах, не вимірювання.
/// </remarks>
[Collection("SqlServer")]
public sealed class NumericColumnScaleTests(SqlServerFixture sql)
{
    /// <summary>
    /// Стовпці, кожен із яких несе вимірювану величину.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік росте разом із міграціями серії: тут ті, що переведені
    /// <c>D148CellValueScale16</c> і <c>D148CalculationScale16</c>. Четвірка
    /// <c>rpt.ReportRow</c>, <c>doc.DocumentIndexValue</c>,
    /// <c>ext.RawDataPoint</c>, <c>dic.RegistryValue</c> додається наступною
    /// міграцією — рядком у цю ж таблицю.
    /// </remarks>
    public static TheoryData<string, string> Columns() => new()
    {
        { "doc.CellValue", "ValueNumeric" },
        { "calc.CalculationResult", "Value" },
        { "calc.CalculationInput", "Value" },
        { "calc.CalculationStep", "Value" },
        { "calc.MethodologyConstant", "Value" },
        { "calc.TestCase", "Tolerance" },
        { "arc.CellValue", "ValueNumeric" },
        { "arc.CalculationResult", "Value" },
        { "arc.CalculationStep", "Value" },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Стовпець_вимірюваної_величини_має_тип_decimal_28_16(string table, string column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(TYPE_NAME(c.user_type_id), '(', c.precision, ',', c.scale, ')')
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column;
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);

        var actual = await command.ExecuteScalarAsync().ConfigureAwait(true);

        // Літералом: 16 — це вимога `D-148`, а не значення константи продукту.
        Assert.Equal("decimal(28,16)", actual);
    }
}
