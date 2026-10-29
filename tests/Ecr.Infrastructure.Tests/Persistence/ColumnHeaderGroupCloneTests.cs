using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Групи заголовків колонок: клон версії переносить дерево (коди стабільні, Id нові, батьки й колонки
/// перев'язані), а знімок метаданих несе групи таблиці. На реальній базі.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnHeaderGroupCloneTests(SqlServerFixture sql)
{
    private static LocalizedText L(string en) => new(new Dictionary<string, string> { ["en"] = en });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_версії_переносить_дерево_груп_і_призначення_колонок_а_знімок_їх_віддає()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: ct);

        int parentId, childId;
        await using (var setup = builder.CreateContext())
        {
            var parent = new ColumnHeaderGroupDef(doc.TableDefId, EcrCode.Create("Q1"), L("Q1"), 1);
            setup.ColumnHeaderGroupDefs.Add(parent);
            await setup.SaveChangesAsync(ct);
            var child = new ColumnHeaderGroupDef(doc.TableDefId, EcrCode.Create("JAN"), L("January"), 1, parent.Id);
            setup.ColumnHeaderGroupDefs.Add(child);
            await setup.SaveChangesAsync(ct);
            parentId = parent.Id;
            childId = child.Id;

            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.ColumnDef SET HeaderGroupId = {0} WHERE Id IN ({1}, {2})",
                [childId, doc.ColumnDefIds[0], doc.ColumnDefIds[1]], ct);
        }

        // Знімок джерела: групи є, колонка несе групу.
        await using (var read = builder.CreateContext())
        {
            var snapshot = await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), read)
                .GetAsync(doc.TemplateVersionId, ct);
            var table = snapshot.Sheets.SelectMany(s => s.Tables).Single(t => t.Id == doc.TableDefId);
            Assert.Equal(["JAN", "Q1"], table.HeaderGroups.Select(g => g.Code).Order().ToArray());
            Assert.Equal(childId, snapshot.ColumnsById[doc.ColumnDefIds[0]].HeaderGroupId);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, "2.0.0.7", 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var verify = builder.CreateContext();
        var cloneTables = await verify.TableDefs.AsNoTracking()
            .Where(t => verify.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId))
            .Select(t => t.Id).ToListAsync(ct);
        var groups = await verify.ColumnHeaderGroupDefs.AsNoTracking()
            .Where(g => cloneTables.Contains(g.TableDefId)).ToListAsync(ct);
        var columns = await verify.ColumnDefs.AsNoTracking()
            .Where(c => cloneTables.Contains(c.TableDefId)).ToListAsync(ct);
        var source = await verify.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).ToListAsync(ct);

        Assert.Equal(2, groups.Count);
        var clonedParent = Assert.Single(groups, g => g.Code == "Q1");
        var clonedChild = Assert.Single(groups, g => g.Code == "JAN");
        Assert.NotEqual(parentId, clonedParent.Id);
        Assert.Null(clonedParent.ParentGroupId);
        Assert.Equal(clonedParent.Id, clonedChild.ParentGroupId);
        Assert.Equal("January", clonedChild.HeaderL10n.Get("en"));

        var assigned = columns.Where(c => c.HeaderGroupId is not null).Select(c => c.Code).Order().ToArray();
        Assert.Equal(
            source.Where(c => c.HeaderGroupId is not null).Select(c => c.Code).Order().ToArray(), assigned);
        Assert.All(columns.Where(c => c.HeaderGroupId is not null), c => Assert.Equal(clonedChild.Id, c.HeaderGroupId));

        // Джерело не зачеплене.
        Assert.Equal(2, source.Count(c => c.HeaderGroupId == childId));
    }
}
