using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-234: клон версії копіює типову ширину колонки (<c>ColumnDef.WidthPx</c>) —
/// інакше нова версія мовчки втрачала б налаштований автором вигляд. На реальній базі.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnWidthCloneTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Клон_версії_копіює_ширину_колонки_а_колонки_без_ширини_лишає_без()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        await using (var setup = builder.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.ColumnDef SET WidthPx = 333 WHERE Id = {0}", [doc.ColumnDefIds[0]], ct);
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

        Assert.Equal(333, source.WidthPx);
        Assert.Equal(333, Assert.Single(cloned, c => c.Code == source.Code).WidthPx);
        Assert.All(cloned.Where(c => c.Code != source.Code), c => Assert.Null(c.WidthPx));
    }
}
