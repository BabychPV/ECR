// tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeysSchemaTests.cs
using System.Security.Cryptography;
using System.Text;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Схема складених ключів довідника на РЕАЛЬНОМУ SQL Server (RT-01,
/// FEATURE-REGISTRY-TABLES §3.2): унікальність тримає база, а не обробник.
/// </summary>
/// <remarks>
/// ⛔ Перевіряється живий індекс, а не модель EF: фільтр і <c>UNIQUE</c>
/// можна оголосити в конфігурації й загубити в міграції (або навпаки), і
/// рефлексія по моделі цього не побачить.
///
/// Мутаційні докази (RT-01, §9.2): прибрати <c>filter</c> в
/// <c>UX_RegistryEntryKey_Live</c> у міграції — червоніє
/// <see cref="Видалений_запис_не_блокує_ключ"/>; прибрати <c>unique</c> —
/// червоніє <see cref="Дубль_живого_ключа_відхиляється"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeysSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-01")]
    public async Task Дубль_живого_ключа_відхиляється()
    {
        var setup = await ArrangeAsync(isTemporal: false);
        await using var db = sql.CreateContext();

        var first = await AddEntryAsync(db, setup.RegistryDefId, "E1");
        var second = await AddEntryAsync(db, setup.RegistryDefId, "E2");

        db.RegistryEntryKeys.Add(new RegistryEntryKey(first, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter"));
        await db.SaveChangesAsync();

        // Інший запис, той самий хеш — саме те, що мав би зробити переможений
        // гонки, який проскочив перевірку служби ключів (§4.3, крок 5).
        db.RegistryEntryKeys.Add(new RegistryEntryKey(second, setup.KeyDefId, Hash("S:370 WINTER"), "370 winter"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_RegistryEntryKey_Live", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-01")]
    public async Task Видалений_запис_не_блокує_ключ()
    {
        var setup = await ArrangeAsync(isTemporal: false);
        await using var db = sql.CreateContext();

        var deleted = await AddEntryAsync(db, setup.RegistryDefId, "E1");
        deleted.SoftDelete(userId: null, utcNow: Now);
        var replacement = await AddEntryAsync(db, setup.RegistryDefId, "E2");

        var released = new RegistryEntryKey(deleted, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter");
        Assert.False(released.IsLive);
        db.RegistryEntryKeys.Add(released);
        await db.SaveChangesAsync();

        // Живий запис бере ключ, звільнений видаленим. Без фільтра `IsLive = 1`
        // видалення блокувало б ключ назавжди.
        db.RegistryEntryKeys.Add(new RegistryEntryKey(replacement, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter"));
        await db.SaveChangesAsync();

        var rows = await db.RegistryEntryKeys.AsNoTracking()
                           .Where(k => k.RegistryKeyDefId == setup.KeyDefId)
                           .Select(k => new { k.RegistryEntryId, k.IsLive })
                           .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(replacement.Id, Assert.Single(rows, r => r.IsLive).RegistryEntryId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-01")]
    public async Task Темпоральний_ключ_розрізняє_початок_вікна_а_не_лише_хеш()
    {
        var setup = await ArrangeAsync(isTemporal: true);
        await using var db = sql.CreateContext();

        var winter = await AddEntryAsync(db, setup.RegistryDefId, "E1", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var nextWinter = await AddEntryAsync(db, setup.RegistryDefId, "E2", new DateOnly(2026, 1, 1), null);
        var sameStart = await AddEntryAsync(db, setup.RegistryDefId, "E3", new DateOnly(2026, 1, 1), null);

        db.RegistryEntryKeys.Add(new RegistryEntryKey(winter, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter"));
        db.RegistryEntryKeys.Add(new RegistryEntryKey(nextWinter, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter"));
        await db.SaveChangesAsync();

        // Точний збіг початку ловить індекс; перетин вікон з різним початком —
        // служба ключів під блокуванням (§4.4), не ця перевірка.
        db.RegistryEntryKeys.Add(new RegistryEntryKey(sameStart, setup.KeyDefId, Hash("S:370 WINTER"), "370 Winter"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_RegistryEntryKey_Live", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-01")]
    public async Task Другий_активний_первинний_ключ_довідника_відхиляється()
    {
        var setup = await ArrangeAsync(isTemporal: false);
        await using var db = sql.CreateContext();
        var fields = await db.RegistryFieldDefs.Where(f => f.RegistryDefId == setup.RegistryDefId)
                             .OrderBy(f => f.Ordinal).ToListAsync();

        // Альтернативних ключів — скільки завгодно; вимкнений первинний місця
        // не займає.
        db.RegistryKeyDefs.Add(Key(setup.RegistryDefId, "ALT_A", [fields[1]], isPrimary: false));
        db.RegistryKeyDefs.Add(Key(setup.RegistryDefId, "ALT_B", [fields[0]], isPrimary: false));
        var retired = Key(setup.RegistryDefId, "OLD_PK", [fields[1]], isPrimary: true);
        retired.SetActive(false);
        db.RegistryKeyDefs.Add(retired);
        await db.SaveChangesAsync();

        db.RegistryKeyDefs.Add(Key(setup.RegistryDefId, "PK2", [fields[1], fields[0]], isPrimary: true));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_RegistryKeyDef_Primary", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-01")]
    public async Task Ключ_зберігається_з_частинами_в_порядку_і_прапорцями_як_задано()
    {
        var setup = await ArrangeAsync(isTemporal: false);
        int keyId;
        int[] fieldIds;

        await using (var db = sql.CreateContext())
        {
            var fields = await db.RegistryFieldDefs.Where(f => f.RegistryDefId == setup.RegistryDefId)
                                 .OrderBy(f => f.Ordinal).ToListAsync();
            fieldIds = [fields[1].Id, fields[0].Id];

            // ⚠ `false` на bool із DEFAULT 1: якби EF вважав CLR-замовчування
            // «незаданим» (як для переліків, `EnumDefaultSentinelTests`), у
            // рядок ліг би DEFAULT — і ключ перевіряв би не так, як заведено.
            var key = new RegistryKeyDef(
                setup.RegistryDefId, EcrCode.Create("BY_CASE"), Text("By case"),
                [fields[1], fields[0]], isPrimary: false, ignoreCase: false, createdByUserId: 0, Now);
            key.SetActive(false);
            db.RegistryKeyDefs.Add(key);
            await db.SaveChangesAsync();
            keyId = key.Id;
        }

        await using var read = sql.CreateContext();
        var stored = await read.RegistryKeyDefs.AsNoTracking().Include(k => k.Fields).SingleAsync(k => k.Id == keyId);

        Assert.False(stored.IgnoreCase);
        Assert.False(stored.IsActive);
        Assert.False(stored.IsPrimary);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
        Assert.Equal(
            new[] { (1, fieldIds[0]), (2, fieldIds[1]) },
            stored.Fields.OrderBy(f => f.Ordinal).Select(f => ((int)f.Ordinal, f.RegistryFieldDefId)));
    }

    /// <summary>Довідник із двома обов'язковими полями й первинним ключем по обох.</summary>
    private async Task<(int RegistryDefId, int KeyDefId)> ArrangeAsync(bool isTemporal)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"RK_{tag}"), Text("Keys probe"), isTemporal);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, 1);
        var caseName = new RegistryFieldDef(registry.Id, EcrCode.Create("CASE_NAME"), Text("Case"), CellDataType.String, 2);
        stream.Update(Text("Stream"), 1, isRequired: true);
        caseName.Update(Text("Case"), 2, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, caseName);
        await db.SaveChangesAsync();

        var key = Key(registry.Id, "PK", [stream, caseName], isPrimary: true);
        db.RegistryKeyDefs.Add(key);
        await db.SaveChangesAsync();

        return (registry.Id, key.Id);
    }

    private static async Task<RegistryEntry> AddEntryAsync(
        EcrDbContext db, int registryDefId, string code, DateOnly? from = null, DateOnly? to = null)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code), 0, Now);
        entry.SetValidity(from, to);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static RegistryKeyDef Key(int registryDefId, string code, RegistryFieldDef[] fields, bool isPrimary)
        => new(registryDefId, EcrCode.Create(code), Text(code), fields, isPrimary, ignoreCase: true, createdByUserId: 0, Now);

    private static byte[] Hash(string canonical) => SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
