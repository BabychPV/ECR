// tests/Ecr.Infrastructure.Tests/Persistence/RegistryStorePermitHistoryReferenceTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// C-13 (аудит 2026-11-10, L5): запис довідника, що є лише пізнішим дозволом з історії (сегмент) або дозволом частини
/// поділу — і в жодній комірці, — має посилання: м'яке видалення відмовляє (<c>references.Total &gt; 0</c>), а «де
/// використовується» показує документ.
/// </summary>
/// <remarks>
/// ⚠ Справжній <c>RegistryStore</c> і база. ⛔ Мутація: прибрати підрахунок <c>PermitSegments</c>/<c>CellPermitParts</c> з
/// <c>CountReferencesAsync</c> / <c>CountReferencesFromOutsideAsync</c> або гілку історії з <c>FindEntryDocumentsAsync</c> —
/// відповідна перевірка дає 0.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryStorePermitHistoryReferenceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C-13")]
    public async Task Дозвіл_лише_з_історії_дозволів_має_посилання_і_документ_використання()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1);

        long segmentOnly;
        long partOnly;
        long notInForce;
        await using (var db = builder.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var registry = new RegistryDef(EcrCode.Create($"PRM_RF{tag}"), Text("Permit"), isTemporal: true);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var b = new RegistryEntry(registry.Id, EcrCode.Create("B"), Text("KZ-B"));
            var c = new RegistryEntry(registry.Id, EcrCode.Create("C"), Text("KZ-C"));
            var later = new RegistryEntry(registry.Id, EcrCode.Create("D"), Text("KZ-D"));
            db.RegistryEntries.AddRange(b, c, later);
            var user = new User($"prrf_{tag}", $"prrf_{tag}", AuthProvider.Local);
            user.SetPassword("not-a-real-hash");
            db.Users.Add(user);
            await db.SaveChangesAsync();

            // ⛔ B56: справжній ключ рядка документа — історія рядка без живого рядка не є використанням.
            var rowKey = await db.TableRows.AsNoTracking()
                .Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();

            // B — лише пізніший сегмент історії рядка (чинний у 202601 — періоді документа); C — лише дозвіл частини поділу.
            // D — сегмент тієї ж цілі з 15.02: рядок живий лише в 202601, де D ще не чинний (B128c). У комірках немає жодного.
            db.PermitSegments.AddRange(
                PermitSegment.ForRow(
                    doc.DocumentId, doc.TableDefId, null, rowKey, doc.ColumnDefIds[0], new DateOnly(2026, 1, 15), b.Id, user.Id,
                    new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc)),
                PermitSegment.ForRow(
                    doc.DocumentId, doc.TableDefId, null, rowKey, doc.ColumnDefIds[0], new DateOnly(2026, 2, 15), later.Id, user.Id,
                    new DateTime(2026, 2, 15, 9, 0, 0, DateTimeKind.Utc)));
            db.CellPermitParts.Add(new CellPermitPart(
                doc.PeriodKey.Value, doc.DocumentId, doc.TableDefId, null, rowKey, doc.ColumnDefIds[1], 1, c.Id,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 14), 5m));
            await db.SaveChangesAsync();
            (segmentOnly, partOnly, notInForce) = (b.Id, c.Id, later.Id);
        }

        await using var read = builder.CreateContext();
        var store = new RegistryStore(read);

        Assert.Equal(1, (await store.CountReferencesAsync(segmentOnly, CancellationToken.None)).Total);
        Assert.Equal(1, (await store.CountReferencesAsync(partOnly, CancellationToken.None)).Total);
        Assert.Equal(2, (await store.CountReferencesFromOutsideAsync([segmentOnly, partOnly], CancellationToken.None)).Total);

        var usage = await store.FindEntryDocumentsAsync(segmentOnly, [doc.ProjectId], take: 10, CancellationToken.None);
        Assert.Equal(1, usage.Total);
        Assert.Equal(doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(usage.Items).Id);
        var partUsage = await store.FindEntryDocumentsAsync(partOnly, [doc.ProjectId], take: 10, CancellationToken.None);
        Assert.Equal(1, partUsage.Total);

        // ⛔ B128c: сегмент, чинний лише там, де рядка немає, — посилання (FK, видалення блокується), але не використання
        // документом. Мутація: повернути старе LiveSegments («рядок живий бодай в одному періоді») — Total 1.
        Assert.Equal(1, (await store.CountReferencesAsync(notInForce, CancellationToken.None)).Total);
        Assert.Equal(0, (await store.FindEntryDocumentsAsync(notInForce, [doc.ProjectId], take: 10, CancellationToken.None)).Total);

        // ⛔ B56: рядок видалено логічно — історія лишається в базі (FK-посилання, видалення запису й далі блокується), але
        // документ уже не «використовує» дозвіл. Мутація: прибрати вимогу живого рядка в PermitHistoryUsage — Total 1.
        await using (var db = builder.CreateContext())
        {
            var row = await db.TableRows.SingleAsync(r => r.Id == doc.RowIds[0]);
            row.SoftDelete(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var after = builder.CreateContext();
        var afterStore = new RegistryStore(after);
        Assert.Equal(0, (await afterStore.FindEntryDocumentsAsync(segmentOnly, [doc.ProjectId], take: 10, CancellationToken.None)).Total);
        Assert.Equal(0, (await afterStore.FindEntryDocumentsAsync(partOnly, [doc.ProjectId], take: 10, CancellationToken.None)).Total);
        Assert.Equal(1, (await afterStore.CountReferencesAsync(segmentOnly, CancellationToken.None)).Total);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
