// tests/Ecr.Application.Tests/Registries/RegistryKeyServiceLockOrderTests.cs
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
/// Пакетне блокування тримачів іде ключ за ключем у порядку <c>RegistryKeyDefId</c> — старшої
/// частини індексу <c>IX_RegistryEntryKey_Hash</c>, — а не в порядку переліку ключів.
/// </summary>
/// <remarks>
/// Мутаційний доказ: цикл <c>for (i = 0; i &lt; keys.Count; i++)</c> замість упорядкованого за
/// <c>Id</c> → <see cref="Ключі_блокуються_в_порядку_їхніх_ідентифікаторів"/> червоний
/// (900 раніше за 700). Порядок хешів усередині ключа тримає сховище —
/// <c>RegistryKeyLockOrderTests</c> (Infrastructure).
/// </remarks>
public sealed class RegistryKeyServiceLockOrderTests
{
    private const int RegistryId = 9;
    private const int StreamId = 91;
    private const int HighKeyId = 900;
    private const int LowKeyId = 700;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryKeyStore _store = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public RegistryKeyServiceLockOrderTests()
    {
        _store.ListEntryKeysAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryEntryKey>());
        _store.FindLiveHoldersForUpdateAsync(Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    [Trait("Finding", "P9")]
    public async Task Ключі_блокуються_в_порядку_їхніх_ідентифікаторів()
    {
        var (definition, high, low) = Registry();
        var locked = new List<int>();
        _store.LockLiveHoldersAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => locked.Add(call.ArgAt<int>(0)));

        // Перелік — навпаки від порядку Id: служба не має на нього спиратися.
        await new RegistryKeyService(_store, _uow).ApplyAsync(
            definition, [high, low], [Persisted(10, "A"), Persisted(11, "B")], CancellationToken.None);

        Assert.Equal([LowKeyId, HighKeyId], locked);
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

    private static (RegistryDef Definition, RegistryKeyDef High, RegistryKeyDef Low) Registry()
    {
        var definition = new RegistryDef(EcrCode.Create("STREAMS"), Text("Streams"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryId);

        var stream = new RegistryFieldDef(RegistryId, EcrCode.Create("STREAM"), Text("Stream"), CellDataType.String, StreamId);
        stream.Update(Text("Stream"), StreamId, isRequired: true);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(stream, StreamId);
        definition.AddField(stream);

        var high = new RegistryKeyDef(RegistryId, EcrCode.Create("PK"), Text("PK"), [stream], isPrimary: true, ignoreCase: true, 0, Now);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(high, HighKeyId);

        var low = new RegistryKeyDef(RegistryId, EcrCode.Create("BY_STREAM_CS"), Text("By stream"), [stream], isPrimary: false, ignoreCase: false, 0, Now);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(low, LowKeyId);
        return (definition, high, low);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
