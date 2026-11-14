// tests/Ecr.Infrastructure.Tests/Persistence/MonthInputAliasMetadataCloneTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ LAND-YEAR L0a: псевдоніми місячних входів (<c>cfg.MonthInputAlias</c>) — у знімку метаданих версії
/// «рік, місяці колонками» (і лише в ній), у переліку для перевірки публікації й у клоні версії (на колонки клону).
/// </summary>
[Collection("SqlServer")]
public sealed class MonthInputAliasMetadataCloneTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Знімок_річної_версії_несе_псевдоніми_за_таблицею_й_місяцем_а_версія_копія_на_місяць_ні()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var year = await builder.BuildAsync(columnCount: 3, ct: ct);
        var copies = await builder.BuildAsync(columnCount: 3, ct: ct);

        await using (var setup = builder.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.TemplateVersion SET Layout = 1 WHERE Id = {0}", year.TemplateVersionId);
            setup.MonthInputAliases.AddRange(
                new MonthInputAlias(year.TableDefId, "Gas", 1, year.ColumnDefIds[1]),
                new MonthInputAlias(year.TableDefId, "Gas", 2, year.ColumnDefIds[2]),
                new MonthInputAlias(year.TableDefId, "Oil", 2, year.ColumnDefIds[2]),
                new MonthInputAlias(copies.TableDefId, "Gas", 1, copies.ColumnDefIds[1]));
            await setup.SaveChangesAsync(ct);
        }

        await using var db = builder.CreateContext();
        var snapshot = await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), db)
            .GetAsync(year.TemplateVersionId, ct);

        Assert.Equal(["Gas"], snapshot.MonthInputAliasesFor(year.TableDefId, 1).Select(a => a.Alias));
        Assert.Equal(year.ColumnDefIds[1], snapshot.MonthInputAliasesFor(year.TableDefId, 1)[0].ColumnDefId);
        Assert.Equal(["Gas", "Oil"], snapshot.MonthInputAliasesFor(year.TableDefId, 2).Select(a => a.Alias));
        Assert.Empty(snapshot.MonthInputAliasesFor(year.TableDefId, 3));

        // Версія «копія на місяць» псевдонімів не читає (і не платить запиту), навіть якщо рядки є.
        var copiesSnapshot = await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), db)
            .GetAsync(copies.TemplateVersionId, ct);
        Assert.Empty(copiesSnapshot.MonthInputAliases);

        // Перелік для перевірки публікації — лише псевдоніми таблиць цієї версії.
        var listed = await new TemplateVersionStore(db).ListMonthInputAliasesAsync(year.TemplateVersionId, ct);
        Assert.Equal(3, listed.Count);
        Assert.All(listed, a => Assert.Equal(year.TableDefId, a.TableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Клон_версії_копіює_псевдоніми_на_таблицю_й_колонки_клону()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using (var setup = builder.CreateContext())
        {
            setup.MonthInputAliases.AddRange(
                new MonthInputAlias(doc.TableDefId, "Gas", 1, doc.ColumnDefIds[1]),
                new MonthInputAlias(doc.TableDefId, "Gas", 2, doc.ColumnDefIds[2]));
            await setup.SaveChangesAsync(ct);
        }

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"7.{tag[..4].GetHashCode() & 0xFFF}.0.2", 1,
                new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), ct);
        }

        await using var read = builder.CreateContext();
        var cloneTable = await read.TableDefs.AsNoTracking()
            .SingleAsync(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId), ct);
        var cloneByCode = await read.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == cloneTable.Id).ToDictionaryAsync(c => c.Code, c => c.Id, ct);
        var sourceCodes = await read.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var cloned = await read.MonthInputAliases.AsNoTracking()
            .Where(a => a.TableDefId == cloneTable.Id).OrderBy(a => a.MonthNumber).ToListAsync(ct);

        Assert.Equal(2, cloned.Count);
        Assert.Equal(("Gas", (byte)1, cloneByCode[sourceCodes[doc.ColumnDefIds[1]]]), (cloned[0].Alias, cloned[0].MonthNumber, cloned[0].ColumnDefId));
        Assert.Equal(("Gas", (byte)2, cloneByCode[sourceCodes[doc.ColumnDefIds[2]]]), (cloned[1].Alias, cloned[1].MonthNumber, cloned[1].ColumnDefId));

        // Джерело не зачеплене.
        Assert.Equal(2, await read.MonthInputAliases.AsNoTracking().CountAsync(a => a.TableDefId == doc.TableDefId, ct));
    }
}
