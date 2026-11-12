// tests/Ecr.Infrastructure.Tests/Persistence/PermitHistoryStoreLockTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-1 (аудит 2026-11-10, L5): шкала історії дозволу рядка читається під <c>UPDLOCK, HOLDLOCK</c> і тримається до коміту —
/// друга сесія на тому самому рядку чекає (тут — тайм-аут 1222), на іншому рядку проходить одразу.
/// </summary>
/// <remarks>
/// ⚠ Справжній <c>PermitHistoryStore</c> і база. Ключі рядків «A» (сегмент), «B» (сегмент-проміжок) і «C» (сегмент):
/// діапазонний замок шкали «A» сідає на наступний ключ індексу («B»), тож «C» лежить поза ним — перевірка «інший
/// рядок проходить» детермінована.
///
/// ⛔ Мутація: прибрати <c>WITH (UPDLOCK, HOLDLOCK)</c> з <c>LockRowSegmentsAsync</c> — друга сесія читає одразу, 1222
/// немає, тести «блокує» червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitHistoryStoreLockTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task LockRowSegments_blocks_second_locker_until_commit()
    {
        var (builder, doc, column) = await ArrangeAsync();

        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var locked = await new PermitHistoryStore(holder)
            .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "A", column, CancellationToken.None);
        Assert.Single(locked);

        await using (var other = builder.CreateContext())
        await using (var otherTransaction = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 200");

            // Той самий рядок — чекає на першу сесію.
            var timeout = await Assert.ThrowsAsync<SqlException>(
                () => new PermitHistoryStore(other)
                    .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "A", column, CancellationToken.None));
            Assert.Equal(1222, timeout.Number);
        }

        await using (var other = builder.CreateContext())
        await using (var otherTransaction = await other.Database.BeginTransactionAsync())
        {
            await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 200");

            // Інший рядок — не чекає.
            Assert.Single(await new PermitHistoryStore(other)
                .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "C", column, CancellationToken.None));
        }

        await holderTransaction.RollbackAsync();

        // Замок звільнено — тепер береться.
        await using var after = builder.CreateContext();
        await using var afterTransaction = await after.Database.BeginTransactionAsync();
        Assert.Single(await new PermitHistoryStore(after)
            .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "A", column, CancellationToken.None));
    }

    /// <summary>Порожня шкала (ще жодного сегмента) теж блокується: інакше двоє однаково бачать «[]» і обидва пишуть.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task LockRowSegments_blocks_an_empty_timeline_too()
    {
        var (builder, doc, column) = await ArrangeAsync();

        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Empty(await new PermitHistoryStore(holder)
            .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "AA", column, CancellationToken.None));

        await using var other = builder.CreateContext();
        await using var otherTransaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 200");
        var timeout = await Assert.ThrowsAsync<SqlException>(
            () => new PermitHistoryStore(other)
                .LockRowSegmentsAsync(doc.DocumentId, doc.TableDefId, null, "AA", column, CancellationToken.None));
        Assert.Equal(1222, timeout.Number);
    }

    private async Task<(TestDocumentBuilder Builder, TestDocument Doc, int Column)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"PRM_LK{tag}"), Text("Permit"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var entry = new RegistryEntry(registry.Id, EcrCode.Create("P1"), Text("KZ-P1"));
        db.RegistryEntries.Add(entry);
        var user = new User($"prlk_{tag}", $"prlk_{tag}", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var column = doc.ColumnDefIds[0];
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        foreach (var rowKey in new[] { "A", "B", "C" })
        {
            db.PermitSegments.Add(PermitSegment.ForRow(
                doc.DocumentId, doc.TableDefId, null, rowKey, column, new DateOnly(2026, 2, 1), entry.Id, user.Id, now));
        }

        await db.SaveChangesAsync();
        return (builder, doc, column);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
