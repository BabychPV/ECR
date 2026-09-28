// tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeyRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Keys;
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
/// Гонка двох записів того самого складеного ключа на РЕАЛЬНОМУ SQL Server, двома з'єднаннями
/// (RT-10b, Д-3, FEATURE-REGISTRY-TABLES §4.3 крок 5, AC-2).
/// </summary>
/// <remarks>
/// <para>
/// Два рівні захисту, і тест на кожен. Перший — блокування <c>UPDLOCK, HOLDLOCK</c> у службі
/// ключів: одночасні записи серіалізуються, другий бачить тримача й отримує 409 <c>keyTaken</c>
/// (<see cref="Одночасні_записи_того_самого_ключа_рівно_один_успішний_другий_409"/>). Другий —
/// індекс <c>UX_RegistryEntryKey_Live</c> для гонки, яку блокування не закрило: її мапить
/// <c>UnitOfWork.TryMapDuplicateKey</c> у 409 <c>keyTakenConcurrently</c>.
/// </para>
/// <para>
/// ⚠ Справжнім паралелізмом другого рівня не досягти: блокування першого рівня якраз і не дає
/// двом транзакціям обом пройти перевірку. Тому «обидві пройшли перевірку» тут моделює сховище,
/// що перевірки не бачить (<see cref="BlindKeyStore"/>), — усе інше справжнє: служба, одиниця
/// роботи, два з'єднання, індекс бази.
/// </para>
/// <para>
/// Мутаційний доказ: прибрати гілку <c>RegistryEntryKey</c> у <c>TryMapDuplicateKey</c> →
/// <see cref="Гонка_двох_записів_дає_409_а_не_500"/> і
/// <see cref="Гонка_при_зміні_ключа_наявного_запису_теж_keyTakenConcurrently"/> червоні
/// (<c>DbUpdateException</c>, тобто 500).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryKeyRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ConflictKeys =
        ["err.ECR-REG-4092.keyTaken", "err.ECR-REG-4092.keyTakenConcurrently"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Гонка_двох_записів_дає_409_а_не_500()
    {
        var setup = await ArrangeAsync();

        await using var first = sql.CreateContext();
        await using var second = sql.CreateContext();

        // Обидва «пройшли перевірку» до того, як перший записав: другий дізнається лише від індексу.
        await SaveNewAsync(first, setup, "E1", "1D-2", "370 Winter", blind: true);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => SaveNewAsync(second, setup, "E2", " 1d-2 ", "370  WINTER", blind: true));

        Assert.Equal("ECR-REG-4092", error.ErrorCode);
        Assert.Equal("err.ECR-REG-4092.keyTakenConcurrently", error.Details!["messageKey"]);
        Assert.Equal("1d-2 · 370  WINTER", error.Details["keyText"]);
        Assert.Equal("E2", error.Details["code"]);

        // Переможений не лишив нічого: ні запису, ні рядка ключа.
        await using var read = sql.CreateContext();
        Assert.Equal(["E1"], await read.RegistryEntries.Where(e => e.RegistryDefId == setup.RegistryDefId).Select(e => e.Code).ToListAsync());
        Assert.Equal(1, await read.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == setup.KeyDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Гонка_при_зміні_ключа_наявного_запису_теж_keyTakenConcurrently()
    {
        var setup = await ArrangeAsync();

        await using (var db = sql.CreateContext())
        {
            await SaveNewAsync(db, setup, "E1", "1D-2", "370 Winter", blind: false);
        }

        await using (var db = sql.CreateContext())
        {
            await SaveNewAsync(db, setup, "E2", "1D-2", "370 Summer", blind: false);
        }

        // Наявний E2 змінює ключ на ключ E1. У пакеті команд поруч із рядком ключа — ЗМІНЕНИЙ
        // запис (назва): гілка «гонка за кодом запису» не мусить його перехопити.
        await using var work = sql.CreateContext();
        var definition = await DefinitionAsync(work, setup);
        var entry = await work.RegistryEntries.SingleAsync(e => e.RegistryDefId == setup.RegistryDefId && e.Code == "E2");
        entry.Rename(Text("E2 renamed"));
        var caseValue = await work.RegistryValues.SingleAsync(v => v.RegistryEntryId == entry.Id && v.RegistryFieldDefId == setup.CaseFieldId);
        caseValue.Set(CellDataType.String, "370 Winter", unitId: null);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Service(work, blind: true).SaveAsync(definition, entry, CancellationToken.None));

        Assert.Equal("ECR-REG-4092", error.ErrorCode);
        Assert.Equal("err.ECR-REG-4092.keyTakenConcurrently", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Одночасні_записи_того_самого_ключа_рівно_один_успішний_другий_409()
    {
        var setup = await ArrangeAsync();

        await using var a = sql.CreateContext();
        await using var b = sql.CreateContext();

        // Справжня служба і справжнє сховище, два з'єднання, запуск одночасно.
        var outcomes = await Task.WhenAll(
            Outcome(() => SaveNewAsync(a, setup, "A", "1D-2", "370 Winter", blind: false)),
            Outcome(() => SaveNewAsync(b, setup, "B", "1d-2", "370 winter", blind: false)));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var failure = Assert.Single(outcomes, o => o is not null);

        // 409, а не 500: який із двох рівнів спрацював, залежить від планувальника, код — ні.
        var conflict = Assert.IsAssignableFrom<EcrException>(failure);
        Assert.Equal("ECR-REG-4092", conflict.ErrorCode);
        Assert.Contains(conflict.Details!["messageKey"] as string, ConflictKeys);

        await using var read = sql.CreateContext();
        Assert.Equal(1, await read.RegistryEntries.CountAsync(e => e.RegistryDefId == setup.RegistryDefId));
        Assert.Equal(1, await read.RegistryEntryKeys.CountAsync(k => k.RegistryKeyDefId == setup.KeyDefId && k.IsLive));
    }

    private static async Task<Exception?> Outcome(Func<Task> action)
    {
        await Task.Yield();
        try
        {
            await action();
            return null;
        }
        catch (Exception ex) when (ex is EcrException or DbUpdateException or InvalidOperationException)
        {
            return ex;
        }
    }

    /// <summary>Новий запис зі значеннями ключа — через службу ключів, як це робить upsert.</summary>
    private static async Task SaveNewAsync(
        EcrDbContext db, Setup setup, string code, string stream, string caseName, bool blind)
    {
        var definition = await DefinitionAsync(db, setup);

        var entry = new RegistryEntry(setup.RegistryDefId, EcrCode.Create(code), Text(code), 0, Now);
        db.RegistryEntries.Add(entry);

        var streamValue = new RegistryValue(entry, setup.StreamFieldId);
        streamValue.Set(CellDataType.String, stream, unitId: null);
        var caseValue = new RegistryValue(entry, setup.CaseFieldId);
        caseValue.Set(CellDataType.String, caseName, unitId: null);
        db.RegistryValues.AddRange(streamValue, caseValue);

        await Service(db, blind).SaveAsync(definition, entry, CancellationToken.None);
    }

    private static Task<RegistryDef> DefinitionAsync(EcrDbContext db, Setup setup)
        => db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Id == setup.RegistryDefId);

    private static RegistryKeyService Service(EcrDbContext db, bool blind)
    {
        var store = new RegistryKeyStore(db);
        return new RegistryKeyService(blind ? new BlindKeyStore(store) : store, new UnitOfWork(db));
    }

    /// <summary>Нетемпоральний довідник: первинний ключ (STREAM, CASE_NAME).</summary>
    private async Task<Setup> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"KR_{tag}"), Text("Key race probe"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var stream = new RegistryFieldDef(registry.Id, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, 1);
        var caseName = new RegistryFieldDef(registry.Id, EcrCode.Create("CASE_NAME"), Text("Case"), CellDataType.String, 2);
        stream.Update(Text("Stream"), 1, isRequired: true);
        caseName.Update(Text("Case"), 2, isRequired: true);
        db.RegistryFieldDefs.AddRange(stream, caseName);
        await db.SaveChangesAsync();

        var key = new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Text("PK"), [stream, caseName],
            isPrimary: true, ignoreCase: true, createdByUserId: 0, Now);
        db.RegistryKeyDefs.Add(key);
        await db.SaveChangesAsync();

        return new Setup(registry.Id, key.Id, stream.Id, caseName.Id);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Setup(int RegistryDefId, int KeyDefId, int StreamFieldId, int CaseFieldId);

    /// <summary>
    /// Справжнє сховище, що не бачить тримачів: модель двох транзакцій, які обидві пройшли
    /// перевірку до того, як перша записала.
    /// </summary>
    private sealed class BlindKeyStore(IRegistryKeyStore inner) : IRegistryKeyStore
    {
        public Task<IReadOnlyList<RegistryKeyDef>> ListActiveKeysAsync(int registryDefId, CancellationToken ct)
            => inner.ListActiveKeysAsync(registryDefId, ct);

        public Task<IReadOnlyList<RegistryValue>> ListCurrentValuesAsync(RegistryEntry entry, CancellationToken ct)
            => inner.ListCurrentValuesAsync(entry, ct);

        public Task<IReadOnlyList<RegistryEntryKey>> ListEntryKeysAsync(long registryEntryId, CancellationToken ct)
            => inner.ListEntryKeysAsync(registryEntryId, ct);

        public Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersForUpdateAsync(
            int registryKeyDefId, byte[] keyHash, long exceptEntryId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RegistryKeyHolder>>([]);

        public Task<IReadOnlyList<RegistryKeyHolder>> FindLiveHoldersAsync(
            int registryKeyDefId, IReadOnlyCollection<byte[]> keyHashes, CancellationToken ct)
            => inner.FindLiveHoldersAsync(registryKeyDefId, keyHashes, ct);

        public Task<IReadOnlyDictionary<long, string>> FindEntryCodesAsync(
            IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
            => inner.FindEntryCodesAsync(registryEntryIds, ct);

        public Task<IReadOnlyDictionary<int, string>> FindUnitCodesAsync(IReadOnlyCollection<int> unitIds, CancellationToken ct)
            => inner.FindUnitCodesAsync(unitIds, ct);

        public void Add(RegistryEntryKey key) => inner.Add(key);
    }
}
