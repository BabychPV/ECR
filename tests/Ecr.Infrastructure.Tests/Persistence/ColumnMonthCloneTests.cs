using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PS-P1C: клон версії копіює місяць колонки (<c>IsMonthColumn</c>/<c>MonthNumber</c>) —
/// інакше нова версія мовчки втрачала б налаштовану автором прив'язку до місяця. На реальній базі.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnMonthCloneTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Клон_версії_копіює_місяць_колонки_а_колонки_без_місяця_лишає_без()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        await using (var setup = builder.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 9 WHERE Id = {0}", [doc.ColumnDefIds[0]], ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, "2.0.0.1", 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var source = await read.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == doc.ColumnDefIds[0], ct);
        var cloned = await read.ColumnDefs.AsNoTracking()
            .Where(c => read.TableDefs.Any(t => t.Id == c.TableDefId
                && read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId)))
            .ToListAsync(ct);

        Assert.True(source.IsMonthColumn);
        var clonedColumn = Assert.Single(cloned, c => c.Code == source.Code);
        Assert.True(clonedColumn.IsMonthColumn);
        Assert.Equal((byte)9, clonedColumn.MonthNumber);
        Assert.All(cloned.Where(c => c.Code != source.Code), c => { Assert.False(c.IsMonthColumn); Assert.Null(c.MonthNumber); });
    }
}
