// tests/Ecr.Application.Tests/Registries/RegistryEntryWriterModeTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Режими <see cref="RegistryEntryWriter"/> для синку довідника (FEATURE-REGISTRY-SYNC S7,
/// <c>ФВ-8.11</c>): «лише оновлювати» (<see cref="RegistryEntryWriteBatch.UpdateOnly"/>) і адресація
/// за <c>RegistryEntryId</c> (<see cref="RegistryEntryWriter.UpdateAsync"/>).
/// </summary>
/// <remarks>
/// Поведінка за замовчуванням (немає запису — створюється) тримає <c>RegistryEntryWriterTests</c> без
/// змін. Мутаційні докази (2026-09-28, власний worktree): у <c>WriteTargetsAsync</c> прибрати перевірку
/// <c>!target.MayCreate &amp;&amp; target.Existing is null</c> →
/// <see cref="UpdateOnly_за_кодом_відсутній_запис_це_помилка_рядка_а_не_створення"/> червоний (запис
/// створено); в <c>UpdateAsync</c> прибрати умову <c>entry.RegistryDefId != definition.Id</c> →
/// <see cref="UpdateAsync_запис_чужого_або_видаленого_довідника_це_помилка_рядка"/> червоний.
/// </remarks>
public sealed class RegistryEntryWriterModeTests
{
    private const int RegistryId = 31;
    private const int CapId = 311;
    private const int UserId = 7;
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<RegistryEntry> _addedEntries = [];
    private readonly RegistryDef _definition;

    public RegistryEntryWriterModeTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _user.CorrelationId.Returns("corr-mode");

        _definition = new RegistryDef(EcrCode.Create("STACKS"), Text("Stacks"), isTemporal: false);
        SetId(_definition, RegistryId);
        var cap = new RegistryFieldDef(RegistryId, EcrCode.Create("CAP"), Text("CAP"), CellDataType.Decimal, 1);
        SetId(cap, CapId);
        _definition.AddField(cap);

        _registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(_definition);
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryEntry>());
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryValue>());
        _registries.When(r => r.Add(Arg.Any<RegistryEntry>())).Do(c => _addedEntries.Add(c.Arg<RegistryEntry>()));
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task UpdateAsync_оновлює_наявний_запис_за_Id_без_створення()
    {
        var e1 = Entry(501L, "E1");
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(e1);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Stored(501L, 10m) });
        var revision = _definition.DataRevision;

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(501L, Values(12.5m))]),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Unchanged));
        Assert.Empty(_addedEntries);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(revision + 1, _definition.DataRevision);
        await _audit.Received(1).WriteSecurityEventsAsync(
            Arg.Is<IReadOnlyList<SecurityEventRecord>>(r => r.Count == 1 && r[0].ChangedByUserId == UserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task UpdateAsync_відсутній_запис_це_помилка_рядка_і_пакет_не_записано()
    {
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(Entry(501L, "E1"));
        _registries.FindEntryAsync(999L, Arg.Any<CancellationToken>()).Returns((RegistryEntry?)null);
        var revision = _definition.DataRevision;

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(
                RegistryId,
                [new RegistryEntryUpdate(501L, Values(12.5m)), new RegistryEntryUpdate(999L, Values(3m))]),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            [new RegistryEntryImportError(2, "999", null, "err.ECR-REG-0404.registryEntry", new Dictionary<string, string> {["entryId"] = "999"})],
            result.Errors);
        Assert.Empty(_addedEntries);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(revision, _definition.DataRevision);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task UpdateAsync_запис_чужого_або_видаленого_довідника_це_помилка_рядка()
    {
        var foreign = new RegistryEntry(RegistryId + 1, EcrCode.Create("X1"), Text("X1"), 1, Now);
        SetId(foreign, 601L);
        var deleted = Entry(602L, "D1");
        deleted.SoftDelete(1, Now);
        _registries.FindEntryAsync(601L, Arg.Any<CancellationToken>()).Returns(foreign);
        _registries.FindEntryAsync(602L, Arg.Any<CancellationToken>()).Returns(deleted);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(
                RegistryId,
                [new RegistryEntryUpdate(601L, Values(1m)), new RegistryEntryUpdate(602L, Values(2m))]),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            [
                new RegistryEntryImportError(1, "601", null, "err.ECR-REG-0404.registryEntry", new Dictionary<string, string> {["entryId"] = "601"}),
                new RegistryEntryImportError(2, "602", null, "err.ECR-REG-0404.registryEntry", new Dictionary<string, string> {["entryId"] = "602"}),
            ],
            result.Errors);
        _registries.DidNotReceive().AddValue(Arg.Any<RegistryValue>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task UpdateOnly_за_кодом_відсутній_запис_це_помилка_рядка_а_не_створення()
    {
        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [new RegistryEntryWrite("NEW1", Values(1m))]) { UpdateOnly = true },
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal([new RegistryEntryImportError(1, "NEW1", null, "err.ECR-REG-0404.registryEntry", new Dictionary<string, string> {["entryId"] = "NEW1"})], result.Errors);
        Assert.Empty(_addedEntries);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Без_UpdateOnly_той_самий_пакет_створює_запис_як_і_раніше()
    {
        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [new RegistryEntryWrite("NEW1", Values(1m))]),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(1, result.Added);
        Assert.Equal("NEW1", Assert.Single(_addedEntries).Code);
    }

    [Fact]
    public async Task UpdateAsync_повторений_Id_це_помилка_виклику()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(
                RegistryId,
                [new RegistryEntryUpdate(501L, Values(1m)), new RegistryEntryUpdate(501L, Values(2m))]),
            CancellationToken.None));
    }

    private RegistryEntryWriter Writer() => new(_registries, _uow, _audit, _user, _clock);

    private static Dictionary<string, object?> Values(decimal cap) => new() { ["CAP"] = cap };

    private static RegistryEntry Entry(long id, string code)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create(code), Text(code), 1, Now);
        SetId(entry, id);
        return entry;
    }

    private static RegistryValue Stored(long entryId, decimal value)
    {
        var stored = new RegistryValue(entryId, CapId);
        stored.Set(CellDataType.Decimal, value, unitId: null);
        return stored;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static void SetId(RegistryEntry entity, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entity, id);
}
