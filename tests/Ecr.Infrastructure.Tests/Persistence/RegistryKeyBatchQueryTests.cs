// tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeyBatchQueryTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Registries.Keys;
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
/// Аудит P9: ключі пакета записів довідника перераховуються СТАЛИМ числом звернень до бази —
/// на РЕАЛЬНОМУ SQL Server, справжньою службою ключів і справжнім сховищем.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти <c>RegistryKeyService.ApplyAsync</c> на кожен запис пакета робив 2 + K звернень
/// (значення, рядки ключів, по одному <c>UPDLOCK, HOLDLOCK</c> на ключ) в одній транзакції:
/// імпорт 50 тис. записів — сотні тисяч round-trip під замками діапазону.
/// </para>
/// <para>
/// ⛔ Мутаційні докази (перезбіркою): стара служба (поштучний цикл) — N=5 / N=60 дає 30 / 360
/// звернень проти 4 / 4, N=2500 — 10000 проти 16 → обидва тести числа звернень червоні;
/// прибрати виклик <c>LockHoldersAsync</c> зі служби — 22 / 242 і 5006; прибрати <c>HOLDLOCK</c>
/// у пакетному блокуванні сховища → вставка суперника проходить,
/// <see cref="Пакетне_блокування_тримає_вільний_ключ_до_кінця_транзакції"/> червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyBatchQueryTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Число_звернень_не_росте_з_розміром_пакета()
    {
        var five = await MeasureAsync(persisted: 5, added: 5);
        var sixty = await MeasureAsync(persisted: 60, added: 60);

        Assert.True(
            five.Commands == sixty.Commands,
            $"N=5 дав {five.Commands}, N=60 дав {sixty.Commands}.\nN=5:\n{five.Detail}\nN=60:\n{sixty.Detail}");

        // Значення + рядки ключів одним читанням кожне, по одному блокуванню на кожен із двох ключів.
        Assert.True(sixty.Commands == 4, $"очікувалося 4 звернення, а було {sixty.Commands}:\n{sixty.Detail}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Пакет_понад_2100_записів_не_впирається_в_ліміт_параметрів()
    {
        // 2500 збережених записів: 2500 Id у читанні значень і рядків ключів і 2500 хешів на
        // ключ у блокуванні — кожне з них одним IN (…) упало б на стелі SQL Server (2100).
        var result = await MeasureAsync(persisted: 2500, added: 0);

        // ⌈2500/1000⌉ = 3 на значення і 3 на рядки ключів, ⌈2500/500⌉ = 5 на кожен із двох ключів.
        Assert.True(result.Commands == 16, $"очікувалося 16 звернень, а було {result.Commands}:\n{result.Detail}");
        Assert.True(result.MaxParameters < 2100, result.Detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Пакетне_блокування_тримає_вільний_ключ_до_кінця_транзакції()
    {
        var setup = await ArrangeAsync();
        var entryId = (await SeedAsync(setup, count: 1)).Single();
        var free = Hash("FREE", "KEY");

        var counter = new DbCommandCounter();
        await using var checker = CountingContext(counter);
        await using var transaction = await checker.Database.BeginTransactionAsync();
        var store = new RegistryKeyStore(checker);

        await store.LockLiveHoldersAsync(setup.PrimaryKeyId, [free, Hash("S0", "C0")], CancellationToken.None);

        // Заблоковане віддається без бази — і тим самим змістом, що дав би поштучний запит.
        counter.Tally.Reset();
        Assert.Empty(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, free, 0, CancellationToken.None));
        var holder = Assert.Single(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, Hash("S0", "C0"), 0, CancellationToken.None));
        Assert.Equal(entryId, holder.EntryId);
        Assert.Null(holder.KeyHash);
        Assert.Empty(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, Hash("S0", "C0"), entryId, CancellationToken.None));
        Assert.Equal(0, counter.Tally.Snapshot().Total);

        // ⚠ Поштучний метод до перевірки суперника не кличеться: він сам бере HOLDLOCK на той
        // самий діапазон і замаскував би пакетне блокування без HOLDLOCK.
        await using var rival = sql.CreateContext();
        await rival.Database.OpenConnectionAsync();
        await rival.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1000");

        // Новий запис паралельної транзакції бере саме той ключ, що вільний, але заблокований пакетом.
        var rivalEntry = new RegistryEntry(setup.RegistryDefId, EcrCode.Create("RIVAL"), Text("Rival"), 0, Now);
        rival.RegistryEntries.Add(rivalEntry);
        rival.RegistryEntryKeys.Add(new RegistryEntryKey(rivalEntry, setup.PrimaryKeyId, free, "FREE · KEY"));

        // ⚠ 1222 (lock timeout) EF вважає транзієнтним і загортає — шукаємо сам SqlException.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => rival.SaveChangesAsync());
        var sqlError = Chain(error).OfType<SqlException>().FirstOrDefault();
        Assert.True(sqlError?.Number == 1222, $"Очікувався lock timeout (1222), а не: {error}");

        // Забуте — знову з бази.
        store.ForgetPreloaded();
        Assert.Empty(await store.FindLiveHoldersForUpdateAsync(setup.PrimaryKeyId, free, 0, CancellationToken.None));
        Assert.Equal(1, counter.Tally.Snapshot().Total);

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Пакетний_шлях_тримача_з_пакета_пропускає_а_тримача_поза_пакетом_ні()
    {
        var setup = await ArrangeAsync();
        var ids = await SeedAsync(setup, count: 2);

        // Обмін ключами: A бере ключ B, B — ключ A. Обидва тримачі — у пакеті, конфлікту немає.
        await using (var db = sql.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var (definition, keys, entries) = await LoadAsync(db, setup);
            await SetCaseAsync(db, entries[ids[0]], setup, "C1");
            await SetCaseAsync(db, entries[ids[1]], setup, "C0");
            await SetStreamAsync(db, entries[ids[0]], setup, "S1");
            await SetStreamAsync(db, entries[ids[1]], setup, "S0");

            await Service(db).ApplyAsync(definition, keys, [entries[ids[0]], entries[ids[1]]], CancellationToken.None);
            await transaction.RollbackAsync();
        }

        // Той самий A без B у пакеті: ключ тримає запис поза пакетом — 4092 keyTaken.
        await using (var db = sql.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var (definition, keys, entries) = await LoadAsync(db, setup);
            await SetCaseAsync(db, entries[ids[0]], setup, "C1");
            await SetStreamAsync(db, entries[ids[0]], setup, "S1");

            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Service(db).ApplyAsync(definition, keys, [entries[ids[0]]], CancellationToken.None));

            Assert.Equal("ECR-REG-4092", error.ErrorCode);
            Assert.Equal("err.ECR-REG-4092.keyTaken", error.Details!["messageKey"]);
            Assert.Equal(ids[1].ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details["entryId"]);
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// <paramref name="persisted"/> збережених записів змінюють CASE_NAME, <paramref name="added"/>
    /// нових додаються; рахуються звернення самого <c>ApplyAsync</c>, потім пакет зберігається і
    /// звіряються рядки ключів.
    /// </summary>
    private async Task<(int Commands, int MaxParameters, string Detail)> MeasureAsync(int persisted, int added)
    {
        var setup = await ArrangeAsync();
        var ids = await SeedAsync(setup, persisted);

        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var (definition, keys, entries) = await LoadAsync(db, setup);
        // Поле належить лише цьому довіднику — фільтр без переліку Id (їх може бути понад 2100).
        var caseValues = await db.RegistryValues
            .Where(v => v.RegistryFieldDefId == setup.CaseFieldId)
            .ToDictionaryAsync(v => v.RegistryEntryId);
        var batch = new List<RegistryEntry>();
        for (var i = 0; i < ids.Count; i++)
        {
            caseValues[ids[i]].Set(CellDataType.String, $"C{i}x", unitId: null);
            batch.Add(entries[ids[i]]);
        }

        for (var i = 0; i < added; i++)
        {
            var entry = new RegistryEntry(setup.RegistryDefId, EcrCode.Create($"N{i}"), Text($"N{i}"), 0, Now);
            db.RegistryEntries.Add(entry);
            AddValues(db, entry, setup, $"NS{i}", $"NC{i}");
            batch.Add(entry);
        }

        counter.Tally.Reset();
        await Service(db).ApplyAsync(definition, keys, batch, CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var read = sql.CreateContext();
        var texts = await read.RegistryEntryKeys
            .Where(k => k.RegistryKeyDefId == setup.PrimaryKeyId && k.IsLive)
            .Select(k => k.KeyText)
            .ToListAsync();
        Assert.Equal(persisted + added, texts.Count);
        Assert.Contains("S0 · C0x", texts);
        if (added > 0)
        {
            Assert.Contains("NS0 · NC0", texts);
        }

        return (seen.Total, seen.Categories.Max(c => c.MaxParameters), seen.Format());
    }

    /// <summary>
    /// Нетемпоральний довідник: первинний ключ (STREAM, CASE_NAME) і альтернативний (CASE_NAME).
    /// </summary>
    private async Task<Setup> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"KB_{tag}"), Text("Key batch probe"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, 1);
        var caseName = new RegistryFieldDef(registry.Id, EcrCode.Create("CASE_NAME"), Text("Case"), CellDataType.String, 2);
        stream.Update(Text("Stream"), 1, isRequired: true);
        caseName.Update(Text("Case"), 2, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, caseName);
        await db.SaveChangesAsync();

        var primary = new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Text("PK"), [stream, caseName], isPrimary: true, ignoreCase: true, 0, Now);
        db.RegistryKeyDefs.Add(primary);
        await db.SaveChangesAsync();

        var alternate = new RegistryKeyDef(
            registry.Id, EcrCode.Create("BY_CASE"), Text("By case"), [caseName], isPrimary: false, ignoreCase: true, 0, Now);
        db.RegistryKeyDefs.Add(alternate);
        await db.SaveChangesAsync();

        return new Setup(registry.Id, primary.Id, alternate.Id, stream.Id, caseName.Id);
    }

    /// <summary>Записи <c>E{i}</c> зі значеннями <c>S{i}</c>/<c>C{i}</c> і їхніми рядками ключів.</summary>
    private async Task<List<long>> SeedAsync(Setup setup, int count)
    {
        await using var db = sql.CreateContext();

        var entries = Enumerable.Range(0, count)
            .Select(i => new RegistryEntry(setup.RegistryDefId, EcrCode.Create($"E{i}"), Text($"E{i}"), 0, Now))
            .ToList();
        db.RegistryEntries.AddRange(entries);

        for (var i = 0; i < count; i++)
        {
            AddValues(db, entries[i], setup, $"S{i}", $"C{i}");
            db.RegistryEntryKeys.Add(new RegistryEntryKey(entries[i], setup.PrimaryKeyId, Hash($"S{i}", $"C{i}"), $"S{i} · C{i}"));
            db.RegistryEntryKeys.Add(new RegistryEntryKey(entries[i], setup.AlternateKeyId, Hash($"C{i}"), $"C{i}"));
        }

        await db.SaveChangesAsync();
        return [.. entries.Select(e => e.Id)];
    }

    private static async Task<(RegistryDef Definition, IReadOnlyList<RegistryKeyDef> Keys, Dictionary<long, RegistryEntry> Entries)> LoadAsync(
        EcrDbContext db, Setup setup)
    {
        var definition = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Id == setup.RegistryDefId);
        var keys = await new RegistryKeyStore(db).ListActiveKeysAsync(setup.RegistryDefId, CancellationToken.None);
        var entries = await db.RegistryEntries
            .Where(e => e.RegistryDefId == setup.RegistryDefId)
            .ToDictionaryAsync(e => e.Id);

        return (definition, keys, entries);
    }

    private static async Task SetCaseAsync(EcrDbContext db, RegistryEntry entry, Setup setup, string value)
        => (await db.RegistryValues.SingleAsync(v => v.RegistryEntryId == entry.Id && v.RegistryFieldDefId == setup.CaseFieldId))
            .Set(CellDataType.String, value, unitId: null);

    private static async Task SetStreamAsync(EcrDbContext db, RegistryEntry entry, Setup setup, string value)
        => (await db.RegistryValues.SingleAsync(v => v.RegistryEntryId == entry.Id && v.RegistryFieldDefId == setup.StreamFieldId))
            .Set(CellDataType.String, value, unitId: null);

    private static void AddValues(EcrDbContext db, RegistryEntry entry, Setup setup, string stream, string caseName)
    {
        var streamValue = new RegistryValue(entry, setup.StreamFieldId);
        streamValue.Set(CellDataType.String, stream, unitId: null);
        var caseValue = new RegistryValue(entry, setup.CaseFieldId);
        caseValue.Set(CellDataType.String, caseName, unitId: null);
        db.RegistryValues.AddRange(streamValue, caseValue);
    }

    private static RegistryKeyService Service(EcrDbContext db) => new(new RegistryKeyStore(db), new UnitOfWork(db));

    private EcrDbContext CountingContext(DbCommandCounter counter)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(counter))
            .Options);

    private static byte[] Hash(params string[] parts)
        => RegistryKeyNormalizer.Hash(RegistryKeyNormalizer.Canonical(
            [.. parts.Select(p => new RegistryKeyPart(CellDataType.String, p))])!);

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
