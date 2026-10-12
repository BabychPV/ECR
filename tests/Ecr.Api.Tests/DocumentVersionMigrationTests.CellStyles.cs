// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.CellStyles.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// CELL-STYLE L3b: користувацьке оформлення комірок (<c>doc.CellStyle</c>, без зовнішніх ключів) їде за колонкою
/// під час переносу документів на іншу версію шаблону: на колонці, що змапилась, — переїжджає разом із
/// кольором; на колонці без відповідника — знімається. Сирот на старих колонках не лишається.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Оформлення_комірок_їде_за_колонкою_а_без_відповідника_знімається()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var targetRows = await TargetRowDefsAsync(s).ConfigureAwait(true);
        var period = s.Doc.PeriodKey;
        var rowId = s.Doc.RowIds[0];
        var at = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            foreach (var (column, color) in new[]
                     {
                         (s.Doc.ColumnDefIds[0], "#FFFF00"),   // змапиться на C1
                         (s.Doc.ColumnDefIds[2], "#FF0000"),   // відповідника не матиме
                     })
            {
                var style = new CellStyle(new CellAddress(period, rowId, column), 1, at);
                style.Apply(color, null, true, 1, at);
                db.CellStyles.Add(style);
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var plan = new VersionMigrationPlan(
            Sheets: new Dictionary<int, int> { [s.Doc.SheetDefId] = s.TargetSheetDefId },
            Tables: new Dictionary<int, int> { [s.Doc.TableDefId] = s.TargetTableDefId },
            Columns:
            [
                new VersionMigrationColumn(s.Doc.ColumnDefIds[0], s.TargetColumns["C1"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[1], s.TargetColumns["C2"], s.TargetTableDefId),
            ],
            Rows: new Dictionary<int, int> { [s.Doc.RowDefIds[0]] = targetRows[0], [s.Doc.RowDefIds[1]] = targetRows[1] },
            HeaderFields: new Dictionary<int, int>(),
            NewRows: [],
            Items: [],
            TransferredValues: 0,
            LostValues: 0,
            GuardedValues: 0);

        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                         .UseSqlServer(sql.ConnectionString).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db)
                .ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var styles = await check.CellStyles.AsNoTracking()
            .Where(c => c.PeriodKeyValue == period.Value && c.TableRowId == rowId)
            .ToListAsync().ConfigureAwait(true);

        // Колір переїхав на нову колонку; на старій і на колонці без відповідника — нічого.
        var moved = Assert.Single(styles);
        Assert.Equal(s.TargetColumns["C1"], moved.ColumnDefId);
        Assert.Equal("#FFFF00", moved.BackgroundHex);
        Assert.True(moved.IsBold);
    }
}
