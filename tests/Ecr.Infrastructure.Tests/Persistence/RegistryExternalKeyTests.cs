// tests/Ecr.Infrastructure.Tests/Persistence/RegistryExternalKeyTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Зовнішні ідентифікатори записів довідника — іменовані поля
/// <c>dic.RegistryExternalKey</c>, а не GUID у клітинці конфігурації
/// (<c>ФВ-8.10</c>).
/// </summary>
/// <remarks>
/// ⚠ Перевіряється лише те, що в продукті Є: схема й сутність. Шляху API/UI,
/// який писав би або читав ці ключі, у продукті поки немає — це прогалина
/// вимоги, а не цього тесту.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryExternalKeyTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Довідник тесту; заводиться першим записом.</summary>
    private int? _registryId;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Зовнішній_ідентифікатор_зберігається_іменованим_полем_запису_разом_зі_шляхом()
    {
        var (entryId, afId, _) = await ArrangeAsync();
        var guid = Guid.NewGuid().ToString("D");

        await using (var db = Context())
        {
            var key = new RegistryExternalKey(entryId, afId, guid);
            key.MarkSynced(@"\\Server\Db\Stack1", new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc));
            db.RegistryExternalKeys.Add(key);
            await db.SaveChangesAsync();

            // Елемент AF перенесли в іншу гілку: GUID той самий, шлях новий.
            key.MarkSynced(@"\\Server\Db\Site2\Stack1", new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc));
            await db.SaveChangesAsync();
        }

        await using var read = Context();

        // ⛔ Запис знаходиться за ПАРОЮ (джерело, зовнішній Id) — саме так його
        // шукатиме синхронізація, — і приводить до свого запису довідника.
        var stored = await read.RegistryExternalKeys
            .AsNoTracking()
            .SingleAsync(k => k.DataSourceId == afId && k.ExternalId == guid);

        Assert.Equal(entryId, stored.RegistryEntryId);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: у `RegistryExternalKey.MarkSynced` перестати
        // оновлювати `ExternalPath` → тут лишається старий шлях.
        Assert.Equal(@"\\Server\Db\Site2\Stack1", stored.ExternalPath);
        Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), stored.LastSyncedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Той_самий_GUID_у_тому_самому_джерелі_не_вказує_на_два_записи()
    {
        var (entryId, afId, otherId) = await ArrangeAsync();
        var secondEntry = await AddEntryAsync("P2");
        var guid = Guid.NewGuid().ToString("D");

        await using (var db = Context())
        {
            // Один запис легально має ключі в КІЛЬКОХ системах.
            db.RegistryExternalKeys.AddRange(
                new RegistryExternalKey(entryId, afId, guid),
                new RegistryExternalKey(entryId, otherId, guid));
            await db.SaveChangesAsync();
        }

        // ⛔ Але той самий GUID у тій самій системі на ІНШИЙ запис — відмова
        // бази (`UQ_RegistryExternalKey`). Мовчазне прийняття означало б, що
        // синхронізація за цим GUID писала б у два записи навмання.
        // МУТАЦІЙНИЙ ДОКАЗ: `unique: false` для `UQ_RegistryExternalKey` у
        // міграції `Stage4Dictionaries` → вставка проходить, тест червоний.
        await using var conflict = Context();
        conflict.RegistryExternalKeys.Add(new RegistryExternalKey(secondEntry, afId, guid));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => conflict.SaveChangesAsync());
        Assert.Contains("UQ_RegistryExternalKey", error.InnerException?.Message, StringComparison.Ordinal);

        await using var read = Context();
        Assert.Equal(
            [entryId, entryId],
            await read.RegistryExternalKeys.AsNoTracking()
                .Where(k => k.ExternalId == guid)
                .Select(k => k.RegistryEntryId)
                .ToListAsync());
    }

    /// <summary>Довідник з одним записом і два джерела.</summary>
    private async Task<(long EntryId, int AfId, int OtherId)> ArrangeAsync()
    {
        var entryId = await AddEntryAsync("P1");

        await using var db = Context();

        var af = new DataSource(
            EcrCode.Create($"AF_{_tag}"), Text("PI AF"), ExternalTransport.PiWebApi, "https://af.test", "secret");
        var other = new DataSource(
            EcrCode.Create($"FL_{_tag}"), Text("FLERT"), ExternalTransport.PiWebApi, "https://fl.test", "secret");
        db.DataSources.AddRange(af, other);
        await db.SaveChangesAsync();

        return (entryId, af.Id, other.Id);
    }

    private async Task<long> AddEntryAsync(string code)
    {
        await using var db = Context();

        if (_registryId is null)
        {
            var registry = new RegistryDef(EcrCode.Create($"EXT_{_tag}"), Text("Дозволи"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            _registryId = registry.Id;
        }

        var entry = new RegistryEntry(_registryId.Value, EcrCode.Create(code), Text(code));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();

        return entry.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
