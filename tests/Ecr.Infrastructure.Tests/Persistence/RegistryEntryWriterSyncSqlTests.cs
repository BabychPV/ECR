// tests/Ecr.Infrastructure.Tests/Persistence/RegistryEntryWriterSyncSqlTests.cs
using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <see cref="RegistryEntryWriter"/> для синку з AF (<c>D-212</c>, PR-5) на справжній базі: вимкнення
/// запису доходить до <c>dic.RegistryEntry.IsActive</c>, аудит <c>@active</c> — до
/// <c>aud.SecurityEvent</c>; «лише створювати» бачить код логічно видаленого запису як зайнятий
/// (<c>UQ_RegistryEntry</c> тримає і його).
/// </summary>
/// <remarks>
/// ⚠ Writer викликається всередині транзакції виклику (PR-6): <c>ExecuteInTransactionAsync</c>
/// приєднується до відкритої — тест <see cref="У_транзакції_виклику_відкат_прибирає_і_стан_і_аудит"/>
/// тримає, що writer не комітить сам.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryWriterSyncSqlTests(SqlServerFixture sql)
{
    private const int UserId = 9;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task IsActive_false_зберігається_і_аудит_active_у_журналі()
    {
        var (defId, entryId) = await StandAsync();

        await using (var db = Context())
        {
            var result = await Writer(db).UpdateAsync(
                new RegistryEntryUpdateBatch(defId, [new RegistryEntryUpdate(entryId, new Dictionary<string, object?>()) { IsActive = false }]),
                CancellationToken.None);
            Assert.True(result.Applied);
        }

        await using var check = Context();
        Assert.False(await check.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.IsActive).SingleAsync());
        Assert.Equal(1, await ActiveAuditCountAsync(check, entryId));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task CreateOnly_код_видаленого_запису_зайнятий()
    {
        var (defId, entryId) = await StandAsync();
        await using (var db = Context())
        {
            var entry = await db.RegistryEntries.SingleAsync(e => e.Id == entryId);
            entry.SoftDelete(UserId, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var write = Context();
        var result = await Writer(write).WriteAsync(
            new RegistryEntryWriteBatch(defId, [new RegistryEntryWrite("E1", new Dictionary<string, object?>()) { DisplayName = "New" }])
            { CreateOnly = true },
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal([new RegistryEntryImportError(1, "E1", null, RegistryEntryWriter.EntryCodeTakenKey)], result.Errors);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-212")]
    public async Task У_транзакції_виклику_відкат_прибирає_і_стан_і_аудит()
    {
        var (defId, entryId) = await StandAsync();

        await using (var db = Context())
        {
            var uow = new UnitOfWork(db);
            await using (await uow.BeginTransactionAsync(CancellationToken.None))
            {
                var result = await Writer(db).WriteAsync(
                    new RegistryEntryWriteBatch(defId, [new RegistryEntryWrite("E2", new Dictionary<string, object?>()) { DisplayName = "Two" }])
                    { CreateOnly = true },
                    CancellationToken.None);
                Assert.True(result.Applied);

                var update = await Writer(db).UpdateAsync(
                    new RegistryEntryUpdateBatch(defId, [new RegistryEntryUpdate(entryId, new Dictionary<string, object?>()) { IsActive = false }]),
                    CancellationToken.None);
                Assert.True(update.Applied);

                // Без Commit: DisposeAsync відкочує.
            }
        }

        await using var check = Context();
        Assert.False(await check.RegistryEntries.AsNoTracking().AnyAsync(e => e.RegistryDefId == defId && e.Code == "E2"));
        Assert.True(await check.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.IsActive).SingleAsync());
        Assert.Equal(0, await ActiveAuditCountAsync(check, entryId));
    }

    private async Task<(int DefId, long EntryId)> StandAsync()
    {
        await using var db = Context();
        var def = new RegistryDef(EcrCode.Create($"SYNC_{_tag}"), Text("Sync"), isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(def.Id, EcrCode.Create("E1"), Text("E1"), UserId, DateTime.UtcNow);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return (def.Id, entry.Id);
    }

    private static Task<int> ActiveAuditCountAsync(EcrDbContext db, long entryId)
    {
        var type = RegistryEntryWriter.ValueChangedEventType;
        var entryPattern = $"%\"entryId\":{entryId},%";
        var fieldPattern = $"%\"field\":\"{RegistryEntryWriter.ActiveFieldCode}\"%";
        return db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM aud.SecurityEvent WHERE EventType = {type} AND DetailsJson LIKE {entryPattern} AND DetailsJson LIKE {fieldPattern}")
            .SingleAsync();
    }

    private static RegistryEntryWriter Writer(EcrDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        return new RegistryEntryWriter(new RegistryStore(db), new UnitOfWork(db, clock, user), new AuditWriter(db), user, clock);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
