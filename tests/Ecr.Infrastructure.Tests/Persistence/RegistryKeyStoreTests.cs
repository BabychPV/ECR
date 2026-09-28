// tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeyStoreTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Сховище складених ключів довідника на РЕАЛЬНОМУ SQL Server (RT-10a, ФВ-8.15,
/// FEATURE-REGISTRY-TABLES §4.3): хто тримає ключ, блокування діапазону, поточні значення.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>HOLDLOCK</c> у
/// <see cref="RegistryKeyStore.FindLiveHoldersForUpdateAsync"/> → червоніє
/// <see cref="Блокування_тримає_вільний_ключ_до_кінця_транзакції"/> (вставка того самого ключа
/// паралельною транзакцією проходить, бо порожній результат без <c>HOLDLOCK</c> не блокує нічого).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Winter = Hash("1D-2", "370 Winter");
    private static readonly byte[] Summer = Hash("1D-2", "370 Summer");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Тримач_ключа_лише_живий_запис_того_самого_ключа_і_хешу_крім_себе()
    {
        var setup = await ArrangeAsync();
        long holderId;

        await using (var db = sql.CreateContext())
        {
            var holder = Entry(setup.RegistryDefId, "E1", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
            var deleted = Entry(setup.RegistryDefId, "E2");
            deleted.SoftDelete(userId: null, utcNow: Now);
            var otherKey = Entry(setup.RegistryDefId, "E3");
            var otherHash = Entry(setup.RegistryDefId, "E4");
            db.RegistryEntries.AddRange(holder, deleted, otherKey, otherHash);

            db.RegistryEntryKeys.AddRange(
                new RegistryEntryKey(holder, setup.PrimaryKeyId, Winter, "1D-2 · 370 Winter"),
                new RegistryEntryKey(deleted, setup.PrimaryKeyId, Winter, "1D-2 · 370 Winter"),
                new RegistryEntryKey(otherKey, setup.AlternateKeyId, Winter, "1D-2 · 370 Winter"),
                new RegistryEntryKey(otherHash, setup.PrimaryKeyId, Summer, "1D-2 · 370 Summer"));
            await db.SaveChangesAsync();
            holderId = holder.Id;
        }

        await using var read = sql.CreateContext();
        var store = new RegistryKeyStore(read);

        var found = Assert.Single(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, Winter, 0, CancellationToken.None));
        Assert.Equal(holderId, found.EntryId);
        Assert.Equal("E1", found.EntryCode);
        Assert.Equal("1D-2 · 370 Winter", found.KeyText);
        Assert.Equal(new ValidityWindow(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)), found.Window);

        // Запис, що зберігається, сам із собою не конфліктує.
        Assert.Empty(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, Winter, holderId, CancellationToken.None));

        // Вимкнений ключ у перевірку не йде; активні — з полями в порядку частин.
        var keys = await store.ListActiveKeysAsync(setup.RegistryDefId, CancellationToken.None);
        Assert.Equal([setup.PrimaryKeyId, setup.AlternateKeyId], keys.Select(k => k.Id));
        Assert.Equal(2, keys[0].Fields.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Блокування_тримає_вільний_ключ_до_кінця_транзакції()
    {
        var setup = await ArrangeAsync();
        long entryId;

        await using (var db = sql.CreateContext())
        {
            var entry = Entry(setup.RegistryDefId, "E1");
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            entryId = entry.Id;
        }

        await using var checker = sql.CreateContext();
        await using var transaction = await checker.Database.BeginTransactionAsync();

        // Ключ вільний — і саме тоді блокування мусить тримати діапазон, а не рядки.
        Assert.Empty(await new RegistryKeyStore(checker)
            .FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, Winter, 0, CancellationToken.None));

        await using var rival = sql.CreateContext();
        await rival.Database.OpenConnectionAsync();
        await rival.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1000");

        var rivalEntry = await rival.RegistryEntries.SingleAsync(e => e.Id == entryId);
        rival.RegistryEntryKeys.Add(new RegistryEntryKey(rivalEntry, setup.PrimaryKeyId, Winter, "1D-2 · 370 Winter"));

        // ⚠ 1222 (lock timeout) EF вважає транзієнтним і загортає у свій виняток — шукаємо сам
        // SqlException у ланцюгу, а не тип обгортки.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rival.SaveChangesAsync());
        var sqlError = Chain(error).OfType<SqlException>().FirstOrDefault();
        Assert.True(sqlError?.Number == 1222, $"Очікувався lock timeout (1222), а не: {error}");

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Поточні_значення_включають_додані_в_цій_одиниці_роботи()
    {
        var setup = await ArrangeAsync();
        long entryId;

        await using (var db = sql.CreateContext())
        {
            var entry = Entry(setup.RegistryDefId, "E1");
            db.RegistryEntries.Add(entry);
            var stream = new RegistryValue(entry, setup.StreamFieldId);
            stream.Set(CellDataType.String, "1D-2", unitId: null);
            db.RegistryValues.Add(stream);
            await db.SaveChangesAsync();
            entryId = entry.Id;
        }

        await using var work = sql.CreateContext();
        var store = new RegistryKeyStore(work);

        // Наявний запис: збережене значення з бази + нове поле, ще не збережене.
        var persisted = await work.RegistryEntries.SingleAsync(e => e.Id == entryId);
        var caseName = new RegistryValue(persisted, setup.CaseFieldId);
        caseName.Set(CellDataType.String, "370 Winter", unitId: null);
        work.RegistryValues.Add(caseName);

        var values = await store.ListCurrentValuesAsync(persisted, CancellationToken.None);
        Assert.Equal(
            new[] { setup.StreamFieldId, setup.CaseFieldId }.Order(),
            values.Select(v => v.RegistryFieldDefId).Order());

        // Новий запис: у бази його ще немає, значення — лише додані.
        var fresh = Entry(setup.RegistryDefId, "E2");
        work.RegistryEntries.Add(fresh);
        var freshStream = new RegistryValue(fresh, setup.StreamFieldId);
        freshStream.Set(CellDataType.String, "1D-3", unitId: null);
        work.RegistryValues.Add(freshStream);

        Assert.Same(freshStream, Assert.Single(await store.ListCurrentValuesAsync(fresh, CancellationToken.None)));
    }

    /// <summary>Довідник: первинний ключ (STREAM, CASE_NAME), альтернативний (STREAM), вимкнений.</summary>
    private async Task<Setup> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"KS_{tag}"), Text("Key store probe"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, 1);
        var caseName = new RegistryFieldDef(registry.Id, EcrCode.Create("CASE_NAME"), Text("Case"), CellDataType.String, 2);
        stream.Update(Text("Stream"), 1, isRequired: true);
        caseName.Update(Text("Case"), 2, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, caseName);
        await db.SaveChangesAsync();

        var primary = Key(registry.Id, "PK", [stream, caseName], isPrimary: true);
        db.RegistryKeyDefs.Add(primary);
        await db.SaveChangesAsync();

        var alternate = Key(registry.Id, "BY_STREAM", [stream], isPrimary: false);
        db.RegistryKeyDefs.Add(alternate);
        await db.SaveChangesAsync();

        var retired = Key(registry.Id, "OLD", [caseName], isPrimary: false);
        retired.SetActive(false);
        db.RegistryKeyDefs.Add(retired);
        await db.SaveChangesAsync();

        return new Setup(registry.Id, primary.Id, alternate.Id, stream.Id, caseName.Id);
    }

    private static RegistryEntry Entry(int registryDefId, string code, DateOnly? from = null, DateOnly? to = null)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code), 0, Now);
        entry.SetValidity(from, to);
        return entry;
    }

    private static RegistryKeyDef Key(int registryDefId, string code, RegistryFieldDef[] fields, bool isPrimary)
        => new(registryDefId, EcrCode.Create(code), Text(code), fields, isPrimary, ignoreCase: true, createdByUserId: 0, Now);

    private static byte[] Hash(string stream, string caseName)
        => RegistryKeyNormalizer.Hash(RegistryKeyNormalizer.Canonical(
            [new RegistryKeyPart(CellDataType.String, stream), new RegistryKeyPart(CellDataType.String, caseName)])!);

    private static IEnumerable<Exception> Chain(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            yield return error;
        }
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Setup(int RegistryDefId, int PrimaryKeyId, int AlternateKeyId, int StreamFieldId, int CaseFieldId);
}
