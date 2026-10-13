// tests/Ecr.Infrastructure.Tests/Persistence/CalculationBindingStoreLiveTablesTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-399: <see cref="CalculationBindingStore.ListLiveTableDefIdsAsync"/> — які з таблиць прив'язок звіряє
/// <c>ECR-CALC-0438</c>: таблиці ОСТАННЬОЇ опублікованої версії свого шаблону; чернетки й <c>Deprecated</c> не
/// рахуються; шаблон без прив'язок на живій версії лишається цілком (fallback).
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>ListLiveTableDefIdsAsync</c> прибрати фільтр <c>Status == Published</c> або вибір
/// найновішої версії — червоні відповідні кроки нижче.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationBindingStoreLiveTablesTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-399")]
    public async Task Жива_версія_це_остання_опублікована_чернетка_і_виведена_з_обігу_не_рахуються()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, ct: ct);
        var other = await builder.BuildAsync(columnCount: 2, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..4].GetHashCode() & 0xFFF;
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        // v1 опубліковано.
        await using (var db = builder.CreateContext())
        {
            var v1 = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId, ct);
            v1.Publish(1, t0);
            var o1 = await db.TemplateVersions.SingleAsync(v => v.Id == other.TemplateVersionId, ct);
            o1.Publish(1, t0);
            await db.SaveChangesAsync(ct);
        }

        // v2 — клон-чернетка (нова версія з додатковою колонкою).
        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"7.{tag}.0.1", 1, t0.AddDays(1), ct);
        }

        int cloneTableId;
        await using (var read = builder.CreateContext())
        {
            cloneTableId = await read.TableDefs.AsNoTracking()
                .Where(t => read.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == cloneId))
                .Select(t => t.Id)
                .SingleAsync(ct);
        }

        var all = new[] { doc.TableDefId, cloneTableId, other.TableDefId };

        // 1) Чернетка не жива: лише таблиця опублікованої v1 (і чужого шаблону).
        Assert.Equal(
            Sorted(doc.TableDefId, other.TableDefId),
            await LiveAsync(builder, all, ct));

        // 2) v2 опубліковано пізніше — жива вона; v1 більше не береться, чужий шаблон не зачеплено.
        await using (var db = builder.CreateContext())
        {
            var v2 = await db.TemplateVersions.SingleAsync(v => v.Id == cloneId, ct);
            v2.Publish(1, t0.AddDays(2));
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(
            Sorted(cloneTableId, other.TableDefId),
            await LiveAsync(builder, all, ct));

        // 3) Прив'язки лише на старій версії (на живій немає жодної) — шаблон лишається цілком (fallback).
        Assert.Equal(
            Sorted(doc.TableDefId),
            await LiveAsync(builder, [doc.TableDefId], ct));

        // 4) v2 виведено з обігу — жива знову v1.
        await using (var db = builder.CreateContext())
        {
            var v2 = await db.TemplateVersions.SingleAsync(v => v.Id == cloneId, ct);
            v2.Deprecate(1, t0.AddDays(3));
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(
            Sorted(doc.TableDefId, other.TableDefId),
            await LiveAsync(builder, all, ct));

        // 5) Порожній вхід — порожня відповідь.
        Assert.Empty(await LiveAsync(builder, [], ct));
    }

    private static async Task<int[]> LiveAsync(TestDocumentBuilder builder, int[] ids, CancellationToken ct)
    {
        await using var db = builder.CreateContext();
        return [.. await new CalculationBindingStore(db).ListLiveTableDefIdsAsync(ids, ct)];
    }

    private static int[] Sorted(params int[] ids) => [.. ids.Order()];
}
