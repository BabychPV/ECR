// tests/Ecr.Infrastructure.Tests/Persistence/CellValueRegistryEntryFkTests.cs
using Ecr.Domain.ValueObjects;
using Ecr.Domain.Entities.Documents;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Директива registry-lookup, PR A1: <c>doc.CellValue.ValueRegistryEntryId</c>
/// не мав реального зовнішнього ключа на <c>dic.RegistryEntry</c> (Q-222) —
/// комірка Lookup-типу могла посилатися на запис довідника, якого не існує
/// взагалі, і жодна перевірка (ні БД, ні код) цього не ловила.
/// </summary>
/// <remarks>
/// ⛔ Корінь — розбіжність CLR-типів по обидва боки зв'язку:
/// <c>ValueRegistryEntryId</c> був голим <c>int?</c>, а <c>RegistryEntry.Id</c>
/// — <c>long</c> (конвертований у <c>int</c> лише для зберігання,
/// <c>RegistryEntryConfiguration.cs:41</c>). EF звіряє сумісність CLR-типів
/// ДО конвертації, тож FK додати було неможливо, попри те що фізично обидва
/// зберігаються як <c>int</c>. Фікс — той самий прийом, що вже working для
/// <c>FK_MC_Substance</c> (<c>MethodologyConstant.SubstanceEntryId</c>):
/// <c>long?</c> у CLR + <c>HasConversion&lt;int?&gt;()</c> у зберіганні.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellValueRegistryEntryFkTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Комірка_з_неіснуючим_записом_довідника_відхиляється_на_рівні_БД()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = Context();

        // ⛔ Жодного запису довідника з таким Id не існує в базі взагалі —
        // не «видалений», а НІКОЛИ не створювався. До фіксу цей рядок
        // проходив SaveChangesAsync мовчки.
        const long nonExistentEntryId = 999_999_999;

        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[0]);
        var cell = new CellValue(address, doc.TableDefId, new CellValueData
        {
            ValueRegistryEntryId = nonExistentEntryId,
        });
        db.CellValues.Add(cell);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(CancellationToken.None));

        // ⛔ Помилка порушення FK, не якась інша (типу, довжини тощо) —
        // SQL Server 547 = "The INSERT statement conflicted with the FOREIGN
        // KEY constraint".
        var sqlError = Assert.IsType<SqlException>(thrown.InnerException);
        Assert.Equal(547, sqlError.Number);
        Assert.Contains("FK_CellValue_Entry", sqlError.Message, StringComparison.Ordinal);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
