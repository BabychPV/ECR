// tests/Ecr.Infrastructure.Tests/Persistence/CalcErrorAuditOriginsTests.cs
using Ecr.Application.Documents;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ C-1 (D-475): події помилки розрахунку (<c>CalcError</c>/<c>CalcErrorResolved</c>) у <c>aud.CellChange</c> — НЕ
/// правка входу. Читач «останній запис рядка — машинний» (<see cref="RowStore.HasMachineOriginAsync"/>) пропускає їх
/// так само, як <c>Recalculation</c>: інакше подія після запису інтеграції відкривала б ручне видалення машинного рядка.
/// </summary>
/// <remarks>
/// Без C-1d (лише <c>Origin &lt;&gt; 'Recalculation'</c>) обидва тести червоні: останнім «не перерахунком» стає подія.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalcErrorAuditOriginsTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(CellChangeOrigins.CalcError)]
    [InlineData(CellChangeOrigins.CalcErrorResolved)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_помилки_після_інтеграції_не_знімає_машинного_походження_рядка(string eventOrigin)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202611);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        await InsertAsync(db, doc, CellChangeOrigins.Integration, T0);
        await InsertAsync(db, doc, eventOrigin, T0.AddMinutes(5));

        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(T0));

        Assert.True(await rows.HasMachineOriginAsync(doc.DocumentId, doc.PeriodKey, doc.RowIds[0], CancellationToken.None));
    }

    private static Task<int> InsertAsync(EcrDbContext db, TestDocument doc, string origin, DateTime at)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                        ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow)
            VALUES ({at}, {doc.PeriodKey.Value}, {doc.DocumentId}, {doc.RowIds[0]}, N'R1', {doc.ColumnDefIds[1]},
                    NULL, N'5', 0, {origin}, 0, 0);
            """);
}
