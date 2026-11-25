// tests/Ecr.Application.Tests/Registries/FlaringRegistryLockTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.GasComposition;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// ✎ SGF AC-6/AC-7 (DESIGN-sg-flaring §5.7, C4, D4, D5): закриті місяці не змінюються і для довідників факелів, які пишуть
/// загальні редактори; право — <c>GasComposition.Edit</c>.
/// </summary>
/// <remarks>
/// Форми документа: РІЧНИЙ затверджений (вікно 2026-01-01…2026-12-31, ключ <c>202600</c>) і МІСЯЧНИЙ закритий
/// (2026-01-01…2026-01-31, <c>202601</c>). Мутаційні докази — у звіті лінії (правило 401).
/// </remarks>
public sealed class FlaringRegistryLockTests
{
    private const int UserId = 11;
    private const int SplitId = 801;
    private const int LocationId = 802;
    private const long SplitEntryId = 9001;
    private const long LocationEntryId = 9002;

    private static readonly GasLockedPeriod YearApproved =
        new(202600, GasLockedPeriod.Approved, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    private static readonly GasLockedPeriod JanuaryClosed =
        new(202601, GasLockedPeriod.Closed, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

    private readonly IGasCompositionStore _store = Substitute.For<IGasCompositionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));
    private readonly RegistryDef _split = Definition(SplitId, "LP_SOUR_SPLIT", temporal: true);
    private readonly RegistryDef _location = Definition(LocationId, "FLARE_LOCATION", temporal: false);
    private readonly RegistryEntry _splitEntry;
    private readonly RegistryEntry _locationEntry;

    public FlaringRegistryLockTests()
    {
        _user.UserId.Returns(UserId);
        Grant(GasCompositionPermissions.Edit, "Registry.EditData");
        _registries.FindDefinitionAsync("LP_SOUR_SPLIT", Arg.Any<CancellationToken>()).Returns(_split);
        _registries.FindDefinitionAsync("FLARE_LOCATION", Arg.Any<CancellationToken>()).Returns(_location);
        _registries.FindDefinitionByIdAsync(SplitId, Arg.Any<CancellationToken>()).Returns(_split);
        _registries.FindDefinitionByIdAsync(LocationId, Arg.Any<CancellationToken>()).Returns(_location);

        _splitEntry = Entry(SplitEntryId, SplitId, "S19");
        _splitEntry.SetValidity(new DateOnly(2026, 3, 1), null);
        _registries.FindEntryAsync(SplitEntryId, Arg.Any<CancellationToken>()).Returns(_splitEntry);

        _locationEntry = Entry(LocationEntryId, LocationId, "ONSHORE_HP");
        _registries.FindEntryAsync(LocationEntryId, Arg.Any<CancellationToken>()).Returns(_locationEntry);
    }

    // ───── AC-6: вікна з doc.Period (D4), старе/нове вікно, право ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Річний_затверджений_документ__нова_версія_з_березня__409()
    {
        Locked(YearApproved);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Lock().EnsureEditAllowedAsync(
            _split, [FlaringRegistryEdit.Insert(new ValidityWindow(new DateOnly(2026, 3, 1), null))], default));

        Assert.Equal("ECR-REG-0409", error.ErrorCode);
        Assert.Equal(GasCompositionRules.LockedKey, error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Місячний_закритий_січень__нова_версія_з_лютого__дозволено()
    {
        Locked(JanuaryClosed);

        await Lock().EnsureEditAllowedAsync(
            _split, [FlaringRegistryEdit.Insert(new ValidityWindow(new DateOnly(2026, 2, 1), null))], default);

        await _store.Received(1).ListLockedPeriodsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == SplitId), Arg.Any<CancellationToken>());
    }

    /// <summary>D4: вікно — з <c>doc.Period</c>, а не з ключа: квартальний ключ <c>202602</c> — це квітень–червень, не лютий.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Квартальний_період__вікно_з_doc_Period_а_не_з_ключа()
    {
        Locked(new GasLockedPeriod(202602, GasLockedPeriod.Closed, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)));

        // Травень — у кварталі (за ключем «лютий» пройшло б).
        await Assert.ThrowsAsync<BusinessRuleException>(() => Lock().EnsureEditAllowedAsync(
            _split, [FlaringRegistryEdit.Insert(new ValidityWindow(new DateOnly(2026, 5, 1), null))], default));

        // Березень — до кварталу (за ключем «лютий» — відмова).
        await Lock().EnsureEditAllowedAsync(
            _split, [FlaringRegistryEdit.Insert(new ValidityWindow(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)))], default);
    }

    /// <summary>C4(b): перевіряється і СТАРЕ вікно — звуження версії, що читалась у замкненому місяці, теж 409.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Старе_вікно_в_замкненому_періоді__409_навіть_якщо_нове_відкрите()
    {
        Locked(JanuaryClosed);

        await Assert.ThrowsAsync<BusinessRuleException>(() => Lock().EnsureEditAllowedAsync(
            _split,
            [new FlaringRegistryEdit(new ValidityWindow(new DateOnly(2026, 1, 1), null), new ValidityWindow(new DateOnly(2026, 2, 1), null))],
            default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Без_GasComposition_Edit__403_навіть_з_Registry_EditData()
    {
        Grant("Registry.EditData");

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => Lock().EnsureEditAllowedAsync(
            _split, [FlaringRegistryEdit.Insert(new ValidityWindow(new DateOnly(2027, 1, 1), null))], default));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.Equal(GasCompositionPermissions.Edit, error.Details!["permission"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Інший_довідник__запобіжник_нічого_не_робить_і_не_вимагає_права()
    {
        Grant();
        var other = Definition(803, "EMISSION_FACTORS", temporal: true);

        await ExternalRegistryGuard.EnsureFlaringEditAllowedAsync(
            other, () => throw new InvalidOperationException("зміни не обчислюються"), Lock(), default);
        await ExternalRegistryGuard.EnsureFlaringEditAllowedAsync(
            other, () => throw new InvalidOperationException("зміни не обчислюються"), flaring: null, default);
        await _store.DidNotReceiveWithAnyArgs().ListLockedPeriodsAsync(Arg.Any<IReadOnlyCollection<int>>(), default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Обробник_без_запобіжника__для_довідника_факелів_відмова_а_не_пропуск()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalRegistryGuard.EnsureFlaringEditAllowedAsync(
            _split, () => Task.FromResult<IReadOnlyCollection<FlaringRegistryEdit>>([]), flaring: null, default));

    /// <summary>Шлях писача: вікно чинності (старе й нове) — через справжній <see cref="SetEntryValidityHandler"/>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Писач_вікна_чинності__перенесення_в_замкнений_рік__409_і_нічого_не_змінено()
    {
        Locked(YearApproved);
        var handler = new SetEntryValidityHandler(
            _registries, Substitute.For<IOrphanScanner>(), _uow, _audit, _access, _user, _clock, keys: null, flaring: Lock());

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync("LP_SOUR_SPLIT", SplitEntryId, new DateOnly(2026, 6, 1), null, default));

        Assert.Equal(new DateOnly(2026, 3, 1), _splitEntry.ValidFrom);
        await _uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Писач_вікна_чинності__відкритий_рік__дозволено()
    {
        Locked(JanuaryClosed);
        _splitEntry.SetValidity(new DateOnly(2026, 3, 1), null);
        var handler = new SetEntryValidityHandler(
            _registries, Substitute.For<IOrphanScanner>(), _uow, _audit, _access, _user, _clock, keys: null, flaring: Lock());

        await handler.HandleAsync("LP_SOUR_SPLIT", SplitEntryId, new DateOnly(2026, 4, 1), null, default);

        Assert.Equal(new DateOnly(2026, 4, 1), _splitEntry.ValidFrom);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-6")]
    public async Task Писач_видалення__запис_що_читав_замкнений_рік__409()
    {
        Locked(YearApproved);
        var handler = new DeleteRegistryEntryHandler(_registries, _uow, _audit, _access, _user, _clock, keys: null, flaring: Lock());

        await Assert.ThrowsAsync<BusinessRuleException>(() => handler.HandleAsync("LP_SOUR_SPLIT", SplitEntryId, default));

        Assert.False(_splitEntry.IsDeleted);
    }

    // ───── AC-7: нетемпоральний довідник (D5) ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-7")]
    public async Task Нетемпоральний__вставка_нового_коду_після_закритого_періоду__дозволена()
    {
        Locked(JanuaryClosed, YearApproved);

        await Lock().EnsureEditAllowedAsync(_location, [FlaringRegistryEdit.Insert(null)], default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-7")]
    public async Task Нетемпоральний__зміна_наявного_коду_що_читали__409()
    {
        Locked(JanuaryClosed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Lock().EnsureEditAllowedAsync(
            _location, [FlaringRegistryEdit.Change(_locationEntry.Window)], default));

        Assert.Equal(GasCompositionRules.LockedKey, error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-7")]
    public async Task Нетемпоральний__без_замкнених_періодів__зміна_дозволена()
    {
        Locked();

        await Lock().EnsureEditAllowedAsync(_location, [FlaringRegistryEdit.Change(_locationEntry.Window)], default);
    }

    /// <summary>Шлях писача upsert: наявний код FLARE_LOCATION — 409 ДО будь-якої зміни.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-7")]
    public async Task Писач_upsert__наявний_код_FLARE_LOCATION__409()
    {
        Locked(JanuaryClosed);
        var handler = Upsert();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => handler.HandleAsync(
            new RegistryEntryUpsertDto(LocationEntryId, LocationId, "ONSHORE_HP", Text("Onshore HP"), null, new Dictionary<string, object?>()),
            default));

        Assert.Equal(GasCompositionRules.LockedKey, error.Details!["messageKey"]);
    }

    /// <summary>Шлях писача upsert: НОВИЙ код FLARE_LOCATION проходить запобіжник (D5).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-7")]
    public async Task Писач_upsert__новий_код_FLARE_LOCATION__проходить_запобіжник()
    {
        Locked(JanuaryClosed);
        _registries.FindEntryByCodeAsync(LocationId, "E_ISLAND_HP", Arg.Any<CancellationToken>()).Returns((RegistryEntry?)null);
        var handler = Upsert();

        var exception = await Record.ExceptionAsync(() => handler.HandleAsync(
            new RegistryEntryUpsertDto(null, LocationId, "E_ISLAND_HP", Text("E Island - HP Flare"), null, new Dictionary<string, object?>()),
            default));

        Assert.False(
            exception is BusinessRuleException { Details: { } d } && Equals(d.GetValueOrDefault("messageKey"), GasCompositionRules.LockedKey),
            "новий код відмовлено запобіжником: " + exception?.Message);
        Assert.IsNotType<AccessDeniedException>(exception);
    }

    // ───── AC-10: зміна опису довідника факелів ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-10")]
    public async Task Опис_довідника_факелів__замкнений_період__409()
    {
        Locked(JanuaryClosed);
        Grant(GasCompositionPermissions.Edit, SaveRegistryDefinitionHandler.Permission, PublishRegistryDefinitionHandler.Permission);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveDefinitionAsync("FLARE_LOCATION"));

        Assert.Equal(GasCompositionRules.LockedKey, error.Details!["messageKey"]);
        await _uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-10")]
    public async Task Опис_довідника_факелів__без_GasComposition_Edit__403()
    {
        Locked();
        Grant(SaveRegistryDefinitionHandler.Permission, PublishRegistryDefinitionHandler.Permission);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => SaveDefinitionAsync("LP_SOUR_SPLIT"));

        Assert.Equal(GasCompositionPermissions.Edit, error.Details!["permission"]);
    }

    private Task<int> SaveDefinitionAsync(string code)
    {
        var keys = Substitute.For<IRegistryKeyStore>();
        return new SaveRegistryDefinitionHandler(
                _registries, _uow, _audit, _access, _user, _clock, Substitute.For<IUnitCatalog>(), keys,
                new Ecr.Application.Registries.Keys.RegistryKeyService(keys, _uow), flaring: Lock())
            .HandleAsync(code, new SaveRegistryDefinitionDto([], [], "SGF AC-10", [], null), default, "\"1\"");
    }

    // ───── допоміжне ─────

    private FlaringRegistryLock Lock() => new(_store, _access, _user);

    private UpsertRegistryEntryHandler Upsert()
        => new(_registries, _access, _user, new RegistryEntryWriter(_registries, _uow, _audit, _user, _clock), flaring: Lock());

    private void Locked(params GasLockedPeriod[] periods)
        => _store.ListLockedPeriodsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GasLockedPeriod>)periods);

    private void Grant(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = UserId };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static RegistryDef Definition(int id, string code, bool temporal)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: temporal);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(definition, id);
        return definition;
    }

    private static RegistryEntry Entry(long id, int registryDefId, string code)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code));
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
