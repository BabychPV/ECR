// tests/Ecr.Application.Tests/Registries/RegistryExternalKeyHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Зовнішні ідентифікатори записів довідника (<c>ФВ-8.10</c>, FEATURE-REGISTRY-SYNC S2):
/// перелік, прив'язка, відв'язка.
/// </summary>
/// <remarks>
/// ⛔ До S2 <c>new RegistryExternalKey(</c> не траплявся в коді: синк читав зв'язки,
/// яких не було як завести.
/// </remarks>
public sealed class RegistryExternalKeyHandlersTests
{
    private const int UserId = 9;
    private const int RegistryId = 70;
    private const int OtherRegistryId = 71;
    private const int SourceId = 3;
    private const long EntryId = 500;
    private const long ForeignEntryId = 501;
    private const long KeyId = 900;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryExternalKeyStore _keys = Substitute.For<IRegistryExternalKeyStore>();
    private readonly IDataSourceStore _dataSources = Substitute.For<IDataSourceStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RegistryEntry _entry = Entry(EntryId, RegistryId, "FL01");
    private readonly List<StructureChangeRecord> _changes = [];

    public RegistryExternalKeyHandlersTests()
    {
        _user.UserId.Returns(UserId);
        Profile(b => b.Permission("Registry.EditData").Permission("Registry.View"));

        var registry = new RegistryDef(EcrCode.Create("Flares"), Name("Flares"), isTemporal: false);
        SetId(registry, RegistryId);
        _registries.FindDefinitionAsync("Flares", Arg.Any<CancellationToken>()).Returns(registry);
        _registries.FindEntryAsync(EntryId, Arg.Any<CancellationToken>()).Returns(_entry);
        _registries.FindEntryAsync(ForeignEntryId, Arg.Any<CancellationToken>())
            .Returns(Entry(ForeignEntryId, OtherRegistryId, "OTHER"));

        var source = new DataSource(
            EcrCode.Create("PI_MAIN"), Name("PI"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        SetId(source, SourceId);
        _dataSources.FindAsync(SourceId, Arg.Any<CancellationToken>()).Returns(source);

        _keys.AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                typeof(Entity<long>).GetProperty("Id")!.SetValue(call.Arg<RegistryExternalKey>(), KeyId);
                return Task.CompletedTask;
            });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _audit.WriteStructureChangeAsync(Arg.Do<StructureChangeRecord>(_changes.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _clock.UtcNow.Returns(new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc));
    }

    private BindRegistryExternalKeyHandler Bind()
        => new(_registries, _keys, _dataSources, _uow, _audit, _access, _user, _clock);

    private UnbindRegistryExternalKeyHandler Unbind()
        => new(_registries, _keys, _uow, _audit, _access, _user, _clock);

    private ListRegistryExternalKeysHandler List() => new(_registries, _keys, _access, _user);

    private static BindRegistryExternalKeyCommand Command(long entryId = EntryId, string? externalId = "  GUID-1 ")
        => new(entryId, SourceId, externalId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Прив_язка_пише_зв_язок_і_аудит_в_одній_транзакції()
    {
        var view = await Bind().HandleAsync("Flares", Command(), CancellationToken.None);

        Assert.Equal(KeyId, view.Id);
        Assert.Equal(EntryId, view.RegistryEntryId);
        Assert.Equal("FL01", view.EntryCode);
        Assert.Equal("PI_MAIN", view.DataSourceCode);
        Assert.Equal("GUID-1", view.ExternalId);

        await _keys.Received(1).AddAsync(
            Arg.Is<RegistryExternalKey>(k => k.RegistryEntryId == EntryId && k.ExternalId == "GUID-1"),
            Arg.Any<CancellationToken>());

        var change = Assert.Single(_changes);
        Assert.Equal("dic.RegistryExternalKey", change.EntityType);
        Assert.Equal((int)KeyId, change.EntityId);
        Assert.Equal("Bind", change.Operation);
        Assert.Equal(UserId, change.ChangedByUserId);
        Assert.Contains("GUID-1", change.NewJson, StringComparison.Ordinal);
        await _uow.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Дубль_пари_джерело_і_ідентифікатор_дає_409_і_не_пише()
    {
        // Мутація «прибрати перевірку FindByExternalIdAsync» → червоний (перевірено 2026-09-29).
        _keys.FindByExternalIdAsync(SourceId, "GUID-1", Arg.Any<CancellationToken>())
            .Returns(new RegistryExternalKeyView(1, 77, "FL77", SourceId, "PI_MAIN", "GUID-1", null, null, null));
        _registries.FindEntryAsync(77, Arg.Any<CancellationToken>()).Returns(Entry(77, RegistryId, "FL77"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Bind().HandleAsync("Flares", Command(), CancellationToken.None));

        Assert.Equal(ErrorCodes.RegistryEntryInUse, ex.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.externalKeyTaken", ex.Details!["messageKey"]);
        Assert.Equal("FL77", ex.Details!["code"]);
        await _keys.DidNotReceive().AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
        Assert.Empty(_changes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Пара_зайнята_записом_іншого_довідника_409_без_коду_цього_запису()
    {
        // ⛔ L1-14: код запису чужого довідника не віддається. Мутація «брати код без звірки
        // довідника» → червоний.
        _keys.FindByExternalIdAsync(SourceId, "GUID-1", Arg.Any<CancellationToken>())
            .Returns(new RegistryExternalKeyView(1, ForeignEntryId, "OTHER", SourceId, "PI_MAIN", "GUID-1", null, null, null));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Bind().HandleAsync("Flares", Command(), CancellationToken.None));

        Assert.Equal(ErrorCodes.RegistryEntryInUse, ex.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.externalKeyTakenElsewhere", ex.Details!["messageKey"]);
        Assert.False(ex.Details!.ContainsKey("code"));
        Assert.DoesNotContain("OTHER", ex.Message, StringComparison.Ordinal);
        await _keys.DidNotReceive().AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Запис_іншого_довідника_дає_404_навіть_із_грантом_на_довідник_шляху()
    {
        // ⛔ Грант лише на Flares: без звірки «запис належить довіднику шляху» він давав
        // би прив'язувати записи будь-якого довідника. Мутація → червоний (2026-09-29).
        Profile(b => b.Grant(ResourceKind.Registry, RegistryId, GrantLevel.Write));

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync("Flares", Command(entryId: ForeignEntryId), CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.registryEntry", ex.Details!["messageKey"]);
        await _keys.DidNotReceive().AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Видалений_або_відсутній_запис_дає_404()
    {
        _entry.SoftDelete(UserId, DateTime.UtcNow);

        var deleted = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync("Flares", Command(), CancellationToken.None));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync("Flares", Command(entryId: 12345), CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.registryEntry", deleted.Details!["messageKey"]);
        Assert.Equal("err.ECR-REG-0404.registryEntry", missing.Details!["messageKey"]);
        await _keys.DidNotReceive().AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("x201")]
    public async Task Порожній_чи_задовгий_ідентифікатор_дає_422(string? externalId)
    {
        var value = externalId == "x201" ? new string('x', BindRegistryExternalKeyHandler.MaxExternalIdLength + 1) : externalId;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Bind().HandleAsync("Flares", Command(externalId: value), CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.externalKeyInvalid", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Невідоме_джерело_й_невідомий_довідник_дають_404()
    {
        var source = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync("Flares", Command() with { DataSourceId = 999 }, CancellationToken.None));
        var registry = await Assert.ThrowsAsync<NotFoundException>(
            () => Bind().HandleAsync("Nope", Command(), CancellationToken.None));

        Assert.Equal("err.ECR-INT-0404.dataSource", source.Details!["messageKey"]);
        Assert.Equal("err.ECR-REG-0404.registry", registry.Details!["messageKey"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    [InlineData(null)]
    [InlineData(GrantLevel.Read)]
    public async Task Без_права_на_дані_довідника_прив_язка_і_відв_язка_відмовляють(GrantLevel? grant)
    {
        // Integration.Manage не рахується: зв'язок — властивість запису, право — на дані (S2).
        // Мутація «прибрати RegistryAccess.RequireAsync» → червоний (2026-09-29).
        Profile(b =>
        {
            b.Permission("Integration.Manage").Permission("Registry.View");
            if (grant is { } level)
            {
                b.Grant(ResourceKind.Registry, RegistryId, level);
            }
        });

        var bind = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Bind().HandleAsync("Flares", Command(), CancellationToken.None));
        var unbind = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Unbind().HandleAsync("Flares", KeyId, CancellationToken.None));

        Assert.Equal("Registry.EditData", bind.Details!["permission"]);
        Assert.Equal("Registry.EditData", unbind.Details!["permission"]);
        await _keys.DidNotReceive().AddAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
        await _keys.DidNotReceive().RemoveAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Грант_Write_на_довідник_достатній_для_прив_язки()
    {
        Profile(b => b.Grant(ResourceKind.Registry, RegistryId, GrantLevel.Write));

        var view = await Bind().HandleAsync("Flares", Command(), CancellationToken.None);

        Assert.Equal(KeyId, view.Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Відв_язка_видаляє_і_пише_аудит()
    {
        var key = Key(EntryId);
        _keys.FindAsync(KeyId, Arg.Any<CancellationToken>()).Returns(key);

        await Unbind().HandleAsync("Flares", KeyId, CancellationToken.None);

        await _keys.Received(1).RemoveAsync(Arg.Is<RegistryExternalKey>(k => ReferenceEquals(k, key)), Arg.Any<CancellationToken>());
        var change = Assert.Single(_changes);
        Assert.Equal("Unbind", change.Operation);
        Assert.Equal((int)KeyId, change.EntityId);
        Assert.Contains("GUID-1", change.OldJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Відв_язка_зв_язку_чужого_довідника_або_відсутнього_дає_404()
    {
        _keys.FindAsync(KeyId, Arg.Any<CancellationToken>()).Returns(Key(ForeignEntryId));

        var foreign = await Assert.ThrowsAsync<NotFoundException>(
            () => Unbind().HandleAsync("Flares", KeyId, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Unbind().HandleAsync("Flares", KeyId + 1, CancellationToken.None));

        Assert.Equal("err.ECR-REG-0404.externalKey", foreign.Details!["messageKey"]);
        Assert.Equal("err.ECR-REG-0404.externalKey", missing.Details!["messageKey"]);
        await _keys.DidNotReceive().RemoveAsync(Arg.Any<RegistryExternalKey>(), Arg.Any<CancellationToken>());
        Assert.Empty(_changes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Перелік_звужений_довідником_шляху_і_фільтрами()
    {
        var page = new PagedResult<RegistryExternalKeyView>([], null, null);
        _keys.ListAsync(Arg.Any<RegistryExternalKeyFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var result = await List().HandleAsync("Flares", EntryId, SourceId, new CursorRequest(20), CancellationToken.None);

        Assert.Same(page, result);
        await _keys.Received(1).ListAsync(
            new RegistryExternalKeyFilter(RegistryId, EntryId, SourceId),
            Arg.Is<CursorRequest>(p => p.Limit == 20),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Перелік_без_права_перегляду_відмовляє_а_розмір_поза_межами_дає_422()
    {
        var tooBig = await Assert.ThrowsAsync<BusinessRuleException>(
            () => List().HandleAsync("Flares", null, null, new CursorRequest(201), CancellationToken.None));
        Assert.Equal("err.ECR-REQ-0422.pageSizeOutOfRange", tooBig.Details!["messageKey"]);

        Profile(b => b.Permission("Integration.Manage"));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => List().HandleAsync("Flares", null, null, new CursorRequest(20), CancellationToken.None));

        Assert.Equal("Registry.View", denied.Details!["permission"]);
        await _keys.DidNotReceive().ListAsync(
            Arg.Any<RegistryExternalKeyFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    private void Profile(Action<AccessBuilder> configure)
    {
        var builder = new AccessBuilder { UserId = UserId };
        configure(builder);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static RegistryExternalKey Key(long entryId)
    {
        var key = new RegistryExternalKey(entryId, SourceId, "GUID-1");
        typeof(Entity<long>).GetProperty("Id")!.SetValue(key, KeyId);
        return key;
    }

    private static RegistryEntry Entry(long id, int registryDefId, string code)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Name(code));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static void SetId(Entity<int> entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
