// tests/Ecr.Infrastructure.Tests/Persistence/NumericColumnScaleTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>D-148</c>: усі стовпці вимірюваних величин у РОЗГОРНУТІЙ базі мають
/// масштаб 16, а precision — ту, що названа поруч із кожним.
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
/// ✎ 2026-09-21: очікуваний тип переїхав із одного літерала в тілі тесту в
/// ТРЕТЮ колонку переліку. Причина не косметична: перехід на
/// <c>decimal(34,16)</c> іде трьома міграціями, по одній за коміт, і спільний
/// літерал змушував би або міняти всі тринадцять рядків разом із першою з них
/// (тобто червонити те, чого ще ніхто не чіпав), або тримати серію одним
/// комітом. Тепер кожен рядок червоніє рівно у своєму.
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
    /// ⚠ Перелік повний: усі тринадцять стовпців, які серія <c>D-148</c>
    /// перевела на 16 знаків трьома міграціями. Нова колонка з вимірюваною
    /// величиною має з'явитися саме тут — інакше її масштаб не стереже ніщо.
    /// </remarks>
    public static TheoryData<string, string, string> Columns() => new()
    {
        { "doc.CellValue", "ValueNumeric", "decimal(34,16)" },
        { "doc.DocumentIndexValue", "ValueNumeric", "decimal(28,16)" },
        { "rpt.ReportRow", "ValueNumeric", "decimal(28,16)" },
        { "ext.RawDataPoint", "ValueNumeric", "decimal(28,16)" },
        { "dic.RegistryValue", "ValueNumeric", "decimal(28,16)" },
        { "calc.CalculationResult", "Value", "decimal(34,16)" },
        { "calc.CalculationInput", "Value", "decimal(34,16)" },
        { "calc.CalculationStep", "Value", "decimal(34,16)" },
        { "calc.MethodologyConstant", "Value", "decimal(34,16)" },
        { "calc.TestCase", "Tolerance", "decimal(34,16)" },
        { "arc.CellValue", "ValueNumeric", "decimal(34,16)" },
        { "arc.CalculationResult", "Value", "decimal(34,16)" },
        { "arc.CalculationStep", "Value", "decimal(34,16)" },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Стовпець_вимірюваної_величини_має_оголошений_тип(
        string table, string column, string expected)
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

        // Літералом у переліку: і 16, і precision — вимога `D-148`, а не
        // значення константи продукту, тож твердження не може поїхати разом
        // із кодом, який воно стереже.
        Assert.Equal(expected, actual);
    }
}
