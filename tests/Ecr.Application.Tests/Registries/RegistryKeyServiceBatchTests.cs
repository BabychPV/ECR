// tests/Ecr.Application.Tests/Registries/RegistryKeyServiceBatchTests.cs
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
/// Нові точки служби ключів (RT-10b, FEATURE-REGISTRY-TABLES §4.3–4.6): пакет без збереження,
/// звільнення ключа видаленого запису, хеш ключа за значеннями в пам'яті.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати відсів тримачів пакета в <c>RequireFreeAsync</c> →
/// <see cref="Тримач_із_того_самого_пакета_не_конфліктує"/> червоний; <c>ReleaseAsync</c> без
/// <c>Retire</c> → <see cref="Звільнення_виводить_усі_рядки_ключів_запису"/> червоний.
/// </remarks>
public sealed class RegistryKeyServiceBatchTests
{
    private const int RegistryId = 8;
    private const int StreamId = 81;
    private const int CaseId = 82;
    private const int PrimaryKeyId = 800;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryKeyStore _store = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public RegistryKeyServiceBatchTests()
    {
        _store.ListEntryKeysAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryEntryKey>());
        _store.FindLiveHoldersForUpdateAsync(Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Тримач_із_того_самого_пакета_не_конфліктує()
    {
        var (definition, key) = Registry();
        var a = Persisted(10, "1D-2", "370 Winter");
        var b = Persisted(11, "1D-2", "370 Summer");

        // У базі ключ «Winter» ще тримає B (файл щойно віддав його A і змінив B): пакет переписує
        // обидва, тож перевірка проти бази B не рахує.
        _store.FindLiveHoldersForUpdateAsync(PrimaryKeyId, Arg.Any<byte[]>(), 10, Arg.Any<CancellationToken>())
            .Returns(new[] { new RegistryKeyHolder(11, "E11", "1D-2 · 370 Winter", ValidityWindow.Always) });

        await Service().ApplyAsync(definition, [key], [a.Entry, b.Entry], CancellationToken.None);

        // ⚠ Без пакета той самий тримач — конфлікт: відсів стосується лише записів пакета.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service().ApplyAsync(definition, [key], [a.Entry], CancellationToken.None));

        // Пакет не зберігає сам: збереження належить транзакції виклику.
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Звільнення_виводить_усі_рядки_ключів_запису()
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create("E90"), Text("Stored"), 1, Now);
        SetId(entry, 90L);
        var primary = new RegistryEntryKey(entry, PrimaryKeyId, new byte[32], "1D-2 · 370 Winter");
        var retiredKeyRow = new RegistryEntryKey(entry, PrimaryKeyId + 1, new byte[32], "1D-2");
        _store.ListEntryKeysAsync(90, Arg.Any<CancellationToken>()).Returns(new[] { primary, retiredKeyRow });

        entry.SoftDelete(userId: 1, utcNow: Now);
        await Service().ReleaseAsync(entry, CancellationToken.None);

        Assert.False(primary.IsLive);
        Assert.False(retiredKeyRow.IsLive);
        await _store.DidNotReceive().ListActiveKeysAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public void Хеш_за_значеннями_нормалізований_а_порожня_частина_дає_null()
    {
        var (definition, key) = Registry();

        var one = RegistryKeyService.HashOf(definition, key, Values("1D-2", "370 Winter"));
        var same = RegistryKeyService.HashOf(definition, key, Values(" 1d-2 ", "370  WINTER"));
        var other = RegistryKeyService.HashOf(definition, key, Values("1D-2", "370 Summer"));

        Assert.NotNull(one);
        Assert.Equal(one, same);
        Assert.NotEqual(one, other);
        Assert.Null(RegistryKeyService.HashOf(definition, key, Values("1D-2", caseName: null)));
    }

    private RegistryKeyService Service() => new(_store, _uow);

    private (RegistryEntry Entry, List<RegistryValue> Values) Persisted(long id, string stream, string caseName)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create($"E{id}"), Text("Stored"), 1, Now);
        SetId(entry, id);

        var values = Values(stream, caseName).Values
            .Select(v =>
            {
                var stored = new RegistryValue(id, v.RegistryFieldDefId);
                stored.Set(CellDataType.String, v.ValueString, unitId: null);
                return stored;
            })
            .ToList();
        _store.ListCurrentValuesAsync(entry, Arg.Any<CancellationToken>()).Returns(values);
        return (entry, values);
    }

    private static Dictionary<int, RegistryValue> Values(string stream, string? caseName)
    {
        var result = new Dictionary<int, RegistryValue>();
        var streamValue = new RegistryValue(0L, StreamId);
        streamValue.Set(CellDataType.String, stream, unitId: null);
        result[StreamId] = streamValue;

        if (caseName is not null)
        {
            var caseValue = new RegistryValue(0L, CaseId);
            caseValue.Set(CellDataType.String, caseName, unitId: null);
            result[CaseId] = caseValue;
        }

        return result;
    }

    private static (RegistryDef Definition, RegistryKeyDef Key) Registry()
    {
        var definition = new RegistryDef(EcrCode.Create("STREAM_CASE"), Text("Stream cases"), isTemporal: false);
        SetId(definition, RegistryId);

        var stream = Field(StreamId, "STREAM");
        var caseName = Field(CaseId, "CASE_NAME");
        definition.AddField(stream);
        definition.AddField(caseName);

        var key = new RegistryKeyDef(RegistryId, EcrCode.Create("PK"), Text("PK"), [stream, caseName], isPrimary: true, ignoreCase: true, 0, Now);
        SetId(key, PrimaryKeyId);
        return (definition, key);
    }

    private static RegistryFieldDef Field(int id, string code)
    {
        var field = new RegistryFieldDef(RegistryId, EcrCode.Create(code), Text(code), CellDataType.String, id);
        field.Update(Text(code), id, isRequired: true);
        SetId(field, id);
        return field;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static void SetId(RegistryEntry entity, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entity, id);
}
