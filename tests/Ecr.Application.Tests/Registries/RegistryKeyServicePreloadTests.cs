// tests/Ecr.Application.Tests/Registries/RegistryKeyServicePreloadTests.cs
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
/// Аудит P9: пакет ключів читається наперед ОДНИМ викликом, тримачі блокуються ОДНИМ викликом на
/// ключ для всіх хешів пакета, а прочитане забувається й тоді, коли пакет упав на конфлікті.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>PreloadAsync</c> або <c>LockHoldersAsync</c> зі служби →
/// <see cref="Пакет_читається_наперед_і_блокується_одним_викликом_на_ключ"/> червоний; прибрати
/// <c>finally</c> з <c>ForgetPreloaded</c> →
/// <see cref="Прочитане_забувається_й_після_конфлікту"/> червоний. Звернення на справжньому SQL —
/// <c>RegistryKeyBatchQueryTests</c> (Infrastructure).
/// </remarks>
public sealed class RegistryKeyServicePreloadTests
{
    private const int RegistryId = 9;
    private const int StreamId = 91;
    private const int PrimaryKeyId = 900;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryKeyStore _store = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public RegistryKeyServicePreloadTests()
    {
        _store.ListEntryKeysAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryEntryKey>());
        _store.FindLiveHoldersForUpdateAsync(Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Пакет_читається_наперед_і_блокується_одним_викликом_на_ключ()
    {
        var (definition, key) = Registry();
        var entries = new[] { Persisted(10, "A"), Persisted(11, "B"), Persisted(12, "C") };

        await new RegistryKeyService(_store, _uow).ApplyAsync(definition, [key], entries, CancellationToken.None);

        await _store.Received(1).PreloadAsync(
            Arg.Is<IReadOnlyCollection<RegistryEntry>>(e => e.Count == 3), Arg.Any<CancellationToken>());
        await _store.Received(1).LockLiveHoldersAsync(
            PrimaryKeyId, Arg.Is<IReadOnlyCollection<byte[]>>(h => h.Count == 3), Arg.Any<CancellationToken>());
        await _store.Received(3).FindLiveHoldersForUpdateAsync(
            PrimaryKeyId, Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        _store.Received(1).ForgetPreloaded();
        _store.Received(3).Add(Arg.Any<RegistryEntryKey>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Прочитане_забувається_й_після_конфлікту()
    {
        var (definition, key) = Registry();
        _store.FindLiveHoldersForUpdateAsync(PrimaryKeyId, Arg.Any<byte[]>(), 10, Arg.Any<CancellationToken>())
            .Returns(new[] { new RegistryKeyHolder(55, "E55", "A", ValidityWindow.Always) });

        await Assert.ThrowsAsync<BusinessRuleException>(() => new RegistryKeyService(_store, _uow)
            .ApplyAsync(definition, [key], [Persisted(10, "A")], CancellationToken.None));

        _store.Received(1).ForgetPreloaded();
    }

    private RegistryEntry Persisted(long id, string stream)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create($"E{id}"), Text("Stored"), 1, Now);
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);

        var value = new RegistryValue(id, StreamId);
        value.Set(CellDataType.String, stream, unitId: null);
        _store.ListCurrentValuesAsync(entry, Arg.Any<CancellationToken>()).Returns(new[] { value });
        return entry;
    }

    private static (RegistryDef Definition, RegistryKeyDef Key) Registry()
    {
        var definition = new RegistryDef(EcrCode.Create("STREAMS"), Text("Streams"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryId);

        var stream = new RegistryFieldDef(RegistryId, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, StreamId);
        stream.Update(Text("Stream"), StreamId, isRequired: true);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(stream, StreamId);
        definition.AddField(stream);

        var key = new RegistryKeyDef(RegistryId, EcrCode.Create("PK"), Text("PK"), [stream], isPrimary: true, ignoreCase: true, 0, Now);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(key, PrimaryKeyId);
        return (definition, key);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
