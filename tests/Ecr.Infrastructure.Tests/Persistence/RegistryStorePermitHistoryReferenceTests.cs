// tests/Ecr.Infrastructure.Tests/Persistence/RegistryStorePermitHistoryReferenceTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
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
        await using (var db = builder.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var registry = new RegistryDef(EcrCode.Create($"PRM_RF{tag}"), Text("Permit"), isTemporal: true);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var b = new RegistryEntry(registry.Id, EcrCode.Create("B"), Text("KZ-B"));
            var c = new RegistryEntry(registry.Id, EcrCode.Create("C"), Text("KZ-C"));
            db.RegistryEntries.AddRange(b, c);
            var user = new User($"prrf_{tag}", $"prrf_{tag}", AuthProvider.Local);
            user.SetPassword("not-a-real-hash");
            db.Users.Add(user);
            await db.SaveChangesAsync();

            // B — лише пізніший сегмент історії рядка; C — лише дозвіл частини поділу. У комірках немає жодного.
            db.PermitSegments.Add(PermitSegment.ForRow(
                doc.DocumentId, doc.TableDefId, null, "R1", doc.ColumnDefIds[0], new DateOnly(2026, 2, 15), b.Id, user.Id,
                new DateTime(2026, 2, 15, 9, 0, 0, DateTimeKind.Utc)));
            db.CellPermitParts.Add(new CellPermitPart(
                202601, doc.DocumentId, doc.TableDefId, null, "R1", doc.ColumnDefIds[1], 1, c.Id,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 14), 5m));
            await db.SaveChangesAsync();
            (segmentOnly, partOnly) = (b.Id, c.Id);
        }

        await using var read = builder.CreateContext();
        var store = new RegistryStore(read);

        Assert.Equal(1, (await store.CountReferencesAsync(segmentOnly, CancellationToken.None)).Total);
        Assert.Equal(1, (await store.CountReferencesAsync(partOnly, CancellationToken.None)).Total);
        Assert.Equal(2, (await store.CountReferencesFromOutsideAsync([segmentOnly, partOnly], CancellationToken.None)).Total);

        var usage = await store.FindEntryDocumentsAsync(segmentOnly, [doc.ProjectId], take: 10, CancellationToken.None);
        Assert.Equal(1, usage.Total);
        Assert.Equal(doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(usage.Items).Id);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
