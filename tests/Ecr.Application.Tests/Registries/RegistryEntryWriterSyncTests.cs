// tests/Ecr.Application.Tests/Registries/RegistryEntryWriterSyncTests.cs
using System.Text.Json;
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
/// Режими <see cref="RegistryEntryWriter"/> для синку довідника з AF (<c>D-212</c>, PR-5):
/// «лише створювати» (<see cref="RegistryEntryWriteBatch.CreateOnly"/>) з назвою
/// (<see cref="RegistryEntryWrite.DisplayName"/>) і вимкнення/увімкнення запису
/// (<see cref="RegistryEntryUpdate.IsActive"/>) з аудитом <see cref="RegistryEntryWriter.ActiveFieldCode"/>.
/// </summary>
/// <remarks>
/// ⚠ Мутаційний доказ НЕ проведено (2026-09-30): правку-мутацію перевірки зайнятого коду відхилив
/// класифікатор дозволів. Очікувано червоні: без перевірки <c>target.MustCreate &amp;&amp;
/// target.Existing is not null</c> у <c>WriteTargetsAsync</c> —
/// <see cref="CreateOnly_зайнятий_код_це_помилка_рядка_entryCodeTaken_і_нічого_не_записано"/>; без
/// зміни <c>@active</c> у <c>changes</c> — <see cref="IsActive_false_вимикає_запис_і_пише_аудит_active"/>
/// і <see cref="IsActive_true_вмикає_вимкнений_запис_і_пише_аудит_active"/>.
/// </remarks>
public sealed class RegistryEntryWriterSyncTests
{
    private const int RegistryId = 41;
    private const int CapId = 411;
    private const int UserId = 7;
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<RegistryEntry> _addedEntries = [];
    private readonly List<SecurityEventRecord> _events = [];
    private readonly RegistryDef _definition;

    public RegistryEntryWriterSyncTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _user.CorrelationId.Returns("corr-sync");

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
        _audit
            .When(a => a.WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>()))
            .Do(c => _events.AddRange(c.Arg<IReadOnlyList<SecurityEventRecord>>()));
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task CreateOnly_Manual_створює_запис_із_переданим_кодом_і_назвою()
    {
        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(
                RegistryId,
                [new RegistryEntryWrite("STACK_7", Values(3m)) { DisplayName = "  Stack #7 (AF)  " }])
            { CreateOnly = true },
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal((1, 0, 0), (result.Added, result.Updated, result.Unchanged));
        var created = Assert.Single(_addedEntries);
        Assert.Equal("STACK_7", created.Code);
        Assert.Equal("Stack #7 (AF)", created.DisplayL10n.Values["en"]);
        await _registries.DidNotReceive().NextEntryCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task CreateOnly_Auto_код_видає_послідовність_а_назва_з_DisplayName()
    {
        _definition.UseCodeMode(RegistryCodeMode.Auto);
        _registries.NextEntryCodesAsync(1, Arg.Any<CancellationToken>()).Returns(42L);

        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [new RegistryEntryWrite(string.Empty, Values(1m)) { DisplayName = "Boiler 2" }])
            { CreateOnly = true },
            CancellationToken.None);

        Assert.True(result.Applied);
        var created = Assert.Single(_addedEntries);
        Assert.Equal(RegistryEntryWriter.AutoCode(42L), created.Code);
        Assert.Equal("Boiler 2", created.DisplayL10n.Values["en"]);
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task Без_DisplayName_назва_нового_запису_дорівнює_коду_як_і_раніше()
    {
        await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [new RegistryEntryWrite("NEW1", Values(1m))]) { CreateOnly = true },
            CancellationToken.None);

        Assert.Equal("NEW1", Assert.Single(_addedEntries).DisplayL10n.Values["en"]);
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task CreateOnly_зайнятий_код_це_помилка_рядка_entryCodeTaken_і_нічого_не_записано()
    {
        var existing = Entry(501L, "TAKEN");
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existing });
        var revision = _definition.DataRevision;

        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(
                RegistryId,
                [new RegistryEntryWrite("taken", Values(9m)) { DisplayName = "Other" }, new RegistryEntryWrite("FREE", Values(1m))])
            { CreateOnly = true },
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal("err.ECR-REG-0409.entryCodeTaken", RegistryEntryWriter.EntryCodeTakenKey);
        Assert.Equal([new RegistryEntryImportError(1, "taken", null, RegistryEntryWriter.EntryCodeTakenKey)], result.Errors);
        _registries.DidNotReceive().AddValue(Arg.Is<RegistryValue>(v => v.RegistryEntryId == 501L));
        Assert.Equal("TAKEN", existing.DisplayL10n.Values["en"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(revision, _definition.DataRevision);
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task CreateOnly_разом_з_UpdateOnly_це_помилка_виклику()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [new RegistryEntryWrite("X1", Values(1m))]) { CreateOnly = true, UpdateOnly = true },
            CancellationToken.None));
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task IsActive_false_вимикає_запис_і_пише_аудит_active()
    {
        var entry = Entry(501L, "E1");
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(501L, new Dictionary<string, object?>()) { IsActive = false }]),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Unchanged));
        Assert.False(entry.IsActive);
        Assert.False(entry.IsDeleted);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        AssertActiveChange(Assert.Single(_events), oldValue: true, newValue: false);
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task IsActive_true_вмикає_вимкнений_запис_і_пише_аудит_active()
    {
        var entry = Entry(501L, "E1");
        entry.Deactivate();
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(501L, Values(5m)) { IsActive = true }]),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.True(entry.IsActive);
        var changes = Changes(Assert.Single(_events));
        Assert.Equal(["CAP", RegistryEntryWriter.ActiveFieldCode], changes.Select(c => c.GetProperty("field").GetString()));
        AssertActiveChange(_events[0], oldValue: false, newValue: true);
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task IsActive_без_фактичної_зміни_нічого_не_записує()
    {
        var entry = Entry(501L, "E1");
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(501L, new Dictionary<string, object?>()) { IsActive = true }]),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal((0, 0, 1), (result.Added, result.Updated, result.Unchanged));
        Assert.True(entry.IsActive);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "D-212")]
    public async Task IsActive_на_видаленому_записі_це_0404_а_не_Activate()
    {
        var entry = Entry(501L, "E1");
        entry.SoftDelete(1, Now);
        _registries.FindEntryAsync(501L, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(501L, new Dictionary<string, object?>()) { IsActive = true }]),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal([new RegistryEntryImportError(1, "501", null, "err.ECR-REG-0404.registryEntry")], result.Errors);
        Assert.False(entry.IsActive);
    }

    private static void AssertActiveChange(SecurityEventRecord record, bool oldValue, bool newValue)
    {
        Assert.Equal(RegistryEntryWriter.ValueChangedEventType, record.EventType);
        var active = Assert.Single(Changes(record), c => c.GetProperty("field").GetString() == RegistryEntryWriter.ActiveFieldCode);
        Assert.Equal(oldValue, active.GetProperty("oldValue").GetBoolean());
        Assert.Equal(newValue, active.GetProperty("newValue").GetBoolean());
    }

    private static List<JsonElement> Changes(SecurityEventRecord record)
    {
        using var json = JsonDocument.Parse(record.DetailsJson!);
        return [.. json.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.Clone())];
    }

    private RegistryEntryWriter Writer() => new(_registries, _uow, _audit, _user, _clock);

    private static Dictionary<string, object?> Values(decimal cap) => new() { ["CAP"] = cap };

    private static RegistryEntry Entry(long id, string code)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create(code), Text(code), 1, Now);
        SetId(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static void SetId(RegistryEntry entity, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entity, id);
}
