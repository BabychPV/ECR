// tests/Ecr.Application.Tests/Registries/RegistryKeyServiceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Keys;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Служба складених ключів довідника (RT-10a, ФВ-8.15, FEATURE-REGISTRY-TABLES §4.3–4.4,
/// <c>D-151…D-153</c>): що перевіряється, що пишеться, що виводиться з унікальності.
/// </summary>
/// <remarks>
/// Блокування й справжня транзакція — у <c>RegistryKeyStoreTests</c> (SQL) і
/// <c>RegistryKeyConflictHttpTests</c> (HTTP); тут — рішення служби на підроблених портах.
/// </remarks>
public sealed class RegistryKeyServiceTests
{
    private const int RegistryId = 7;
    private const int StreamId = 71;
    private const int CaseId = 72;
    private const int NoteId = 73;
    private const int PrimaryKeyId = 700;
    private const int AlternateKeyId = 701;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryKeyStore _store = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly List<RegistryValue> _values = [];

    public RegistryKeyServiceTests()
    {
        // Без цього NSubstitute ніколи не викликає замикання — перевірки не виконались би.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _store.ListCurrentValuesAsync(Arg.Any<RegistryEntry>(), Arg.Any<CancellationToken>()).Returns(_ => _values);
        _store.ListEntryKeysAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryEntryKey>());
        _store.FindLiveHoldersForUpdateAsync(Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Довідник_без_ключів_зберігається_як_досі_без_транзакції()
    {
        var (definition, _) = Registry(isTemporal: false);
        _store.ListActiveKeysAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryKeyDef>());

        await Service().SaveAsync(definition, NewEntry(), CancellationToken.None);

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Ключ_зайнятий_іншим_записом_дає_4092_і_нічого_не_зберігає()
    {
        var (definition, fields) = Registry(isTemporal: false);
        Keys(Primary(fields));
        var entry = NewEntry();
        Value(entry, StreamId, CellDataType.String, " 1d-2 ");
        Value(entry, CaseId, CellDataType.String, "370  winter");
        Holders(PrimaryKeyId, new RegistryKeyHolder(55, "E000000055", "1D-2 · 370 Winter", ValidityWindow.Always));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service().SaveAsync(definition, entry, CancellationToken.None));

        Assert.Equal("ECR-REG-4092", error.ErrorCode);
        Assert.Equal("err.ECR-REG-4092.keyTaken", error.Details!["messageKey"]);
        Assert.Equal("PK", error.Details["key"]);
        Assert.Equal("1D-2 · 370 Winter", error.Details["keyText"]);
        Assert.Equal("55", error.Details["entryId"]);
        Assert.Equal("E000000055", error.Details["entryCode"]);

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _store.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Темпоральний_дубль_у_вікні_що_не_перетинається_дає_рядок_ключа()
    {
        var (definition, fields) = Registry(isTemporal: true);
        Keys(Primary(fields));
        var entry = NewEntry();
        entry.SetValidity(new DateOnly(2026, 1, 1), null);
        Value(entry, StreamId, CellDataType.String, "1D-2");
        Value(entry, CaseId, CellDataType.String, " 370 Winter");
        Holders(PrimaryKeyId, new RegistryKeyHolder(
            55, "W25", "1D-2 · 370 Winter", new ValidityWindow(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))));

        RegistryEntryKey? added = null;
        _store.When(s => s.Add(Arg.Any<RegistryEntryKey>())).Do(call => added = call.Arg<RegistryEntryKey>());

        await Service().SaveAsync(definition, entry, CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(PrimaryKeyId, added!.RegistryKeyDefId);
        Assert.Equal("1D-2 · 370 Winter", added.KeyText);
        Assert.Equal(new DateOnly(2026, 1, 1), added.ValidFromKey);
        Assert.True(added.IsLive);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Темпоральний_дубль_у_перетинному_вікні_дає_keyWindowOverlap()
    {
        var (definition, fields) = Registry(isTemporal: true);
        Keys(Primary(fields));
        var entry = NewEntry();
        entry.SetValidity(new DateOnly(2025, 12, 31), null);
        Value(entry, StreamId, CellDataType.String, "1D-2");
        Value(entry, CaseId, CellDataType.String, "370 Winter");
        Holders(PrimaryKeyId, new RegistryKeyHolder(
            55, "W25", "1D-2 · 370 Winter", new ValidityWindow(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1))));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service().SaveAsync(definition, entry, CancellationToken.None));

        Assert.Equal("err.ECR-REG-4092.keyWindowOverlap", error.Details!["messageKey"]);
        Assert.Equal("W25", error.Details["entryCode"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Порожня_частина_альтернативного_ключа_не_перевіряється_а_наявний_рядок_виводиться()
    {
        var (definition, fields) = Registry(isTemporal: false);
        var alternate = Key(AlternateKeyId, "BY_NOTE", [fields[NoteId]], isPrimary: false);
        Keys(alternate);
        var entry = PersistedEntry(90);
        Value(entry, NoteId, CellDataType.String, "   ");

        var row = new RegistryEntryKey(entry, AlternateKeyId, new byte[32], "old note");
        _store.ListEntryKeysAsync(90, Arg.Any<CancellationToken>()).Returns(new[] { row });

        await Service().SaveAsync(definition, entry, CancellationToken.None);

        // D-153: як UNIQUE з різними NULL — рядок із порожньою частиною не бере участі.
        Assert.False(row.IsLive);
        await _store.DidNotReceive().FindLiveHoldersForUpdateAsync(
            Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _store.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Наявний_рядок_ключа_перераховується_на_місці_і_запис_не_конфліктує_сам_із_собою()
    {
        var (definition, fields) = Registry(isTemporal: false);
        Keys(Primary(fields));
        var entry = PersistedEntry(90);
        Value(entry, StreamId, CellDataType.String, "1D-2");
        Value(entry, CaseId, CellDataType.String, "370 Summer");

        var row = new RegistryEntryKey(entry, PrimaryKeyId, new byte[32], "1D-2 · 370 Winter");
        _store.ListEntryKeysAsync(90, Arg.Any<CancellationToken>()).Returns(new[] { row });

        await Service().SaveAsync(definition, entry, CancellationToken.None);

        Assert.Equal("1D-2 · 370 Summer", row.KeyText);
        Assert.NotEqual(new byte[32], row.KeyHash);
        _store.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
        await _store.Received(1).FindLiveHoldersForUpdateAsync(
            PrimaryKeyId, Arg.Any<byte[]>(), 90, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Текст_ключа_Lookup_це_код_цілі_а_число_без_хвостових_нулів()
    {
        var definition = new RegistryDef(EcrCode.Create("STREAM_CASE"), Text("Stream cases"), isTemporal: false);
        SetId(definition, RegistryId);
        var stream = Field(StreamId, "STREAM", CellDataType.Lookup, required: true);
        var temperature = Field(CaseId, "T_C", CellDataType.Decimal, required: true);
        definition.AddField(stream);
        definition.AddField(temperature);
        Keys(Key(PrimaryKeyId, "PK", [stream, temperature], isPrimary: true));

        var entry = NewEntry();
        Value(entry, StreamId, CellDataType.Lookup, 162L);
        Value(entry, CaseId, CellDataType.Decimal, 49.9999977539011000m);
        _store.FindEntryCodesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [162] = "HP_SEP_GAS" });

        RegistryEntryKey? added = null;
        _store.When(s => s.Add(Arg.Any<RegistryEntryKey>())).Do(call => added = call.Arg<RegistryEntryKey>());

        await Service().SaveAsync(definition, entry, CancellationToken.None);

        Assert.Equal("HP_SEP_GAS · 49.9999977539011", added!.KeyText);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public void Перетин_вікон_напівінтервальний_а_порожнє_вікно_не_перетинається_ні_з_чим()
    {
        var year2025 = new ValidityWindow(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));

        Assert.False(RegistryKeyService.Overlaps(year2025, new ValidityWindow(new DateOnly(2026, 1, 1), null)));
        Assert.True(RegistryKeyService.Overlaps(year2025, new ValidityWindow(new DateOnly(2025, 12, 31), null)));
        Assert.True(RegistryKeyService.Overlaps(year2025, ValidityWindow.Always));
        Assert.True(RegistryKeyService.Overlaps(ValidityWindow.Always, year2025));
        Assert.False(RegistryKeyService.Overlaps(new ValidityWindow(null, new DateOnly(2025, 1, 1)), year2025));
        Assert.False(RegistryKeyService.Overlaps(
            ValidityWindow.Always, new ValidityWindow(new DateOnly(2025, 5, 1), new DateOnly(2025, 5, 1))));
    }

    private RegistryKeyService Service() => new(_store, _uow);

    private void Keys(params RegistryKeyDef[] keys)
        => _store.ListActiveKeysAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(keys);

    private void Holders(int keyDefId, params RegistryKeyHolder[] holders)
        => _store.FindLiveHoldersForUpdateAsync(keyDefId, Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(holders);

    private void Value(RegistryEntry entry, int fieldId, CellDataType dataType, object? value)
    {
        var stored = entry.IsPersisted ? new RegistryValue(entry.Id, fieldId) : new RegistryValue(entry, fieldId);
        stored.Set(dataType, value, unitId: null);
        _values.Add(stored);
    }

    private static (RegistryDef Definition, Dictionary<int, RegistryFieldDef> Fields) Registry(bool isTemporal)
    {
        var definition = new RegistryDef(EcrCode.Create("STREAM_CASE"), Text("Stream cases"), isTemporal);
        SetId(definition, RegistryId);

        var fields = new[]
        {
            Field(StreamId, "STREAM", CellDataType.String, required: true),
            Field(CaseId, "CASE_NAME", CellDataType.String, required: true),
            Field(NoteId, "NOTE", CellDataType.String, required: false),
        };

        foreach (var field in fields)
        {
            definition.AddField(field);
        }

        return (definition, fields.ToDictionary(f => f.Id));
    }

    private static RegistryKeyDef Primary(Dictionary<int, RegistryFieldDef> fields)
        => Key(PrimaryKeyId, "PK", [fields[StreamId], fields[CaseId]], isPrimary: true);

    private static RegistryFieldDef Field(int id, string code, CellDataType dataType, bool required)
    {
        var field = new RegistryFieldDef(RegistryId, EcrCode.Create(code), Text(code), dataType, id);
        field.Update(Text(code), id, required);
        SetId(field, id);
        return field;
    }

    private static RegistryKeyDef Key(int id, string code, RegistryFieldDef[] fields, bool isPrimary)
    {
        var key = new RegistryKeyDef(RegistryId, EcrCode.Create(code), Text(code), fields, isPrimary, ignoreCase: true, 0, Now);
        SetId(key, id);
        return key;
    }

    private static RegistryEntry NewEntry()
        => new(RegistryId, EcrCode.Create("E_NEW"), Text("New"), 1, Now);

    private static RegistryEntry PersistedEntry(long id)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create($"E{id}"), Text("Stored"), 1, Now);
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
