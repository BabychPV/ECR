// tests/Ecr.Application.Tests/Registries/RegistryDenyOverridesGlobalRightTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Rows;
using Ecr.Application.Search;
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
/// S18 (ENT-AUDIT): явна ЗАБОРОНА на довідник (<c>ResourceGrant.IsDeny</c>,
/// <see cref="ResourceKind.Registry"/>) виграє над ГЛОБАЛЬНИМ <c>Registry.View</c>/<c>Registry.EditData</c>
/// — на кожному шляху до даних і опису довідника.
/// </summary>
/// <remarks>
/// ⛔ <b>Доказ червоного.</b> До S18 <c>RegistryAccess.RequireAsync</c> повертав профіль, щойно
/// <c>profile.Has(permission)</c>, ще до будь-якої перевірки заборони, а обробники опису/переліку/пошуку
/// не питали заборон узагалі: кожен тест «заборонений довідник → 404» тут давав 200/запис. Набір
/// запускався на чистій вершині <c>dev/integration</c> без виправлення — червоний; з ним — зелений.
/// <para>
/// Відповідь на заборонений довідник — <c>404</c>, ТА САМА, що й на неіснуючий (як невидимий документ,
/// B-08): різниця з <c>403</c> розкривала б, що довідник є.
/// </para>
/// </remarks>
public sealed class RegistryDenyOverridesGlobalRightTests
{
    private const int UserId = 9;
    private const int DeniedId = 601;
    private const int OtherId = 602;
    private const string DeniedCode = "DENIED_REG";
    private const long DeniedEntryId = 7001;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IRegistryRowsQuery _rows = Substitute.For<IRegistryRowsQuery>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TestClock _clock = new(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));
    private readonly RegistryDef _denied;
    private readonly RegistryDef _other;

    public RegistryDenyOverridesGlobalRightTests()
    {
        _user.UserId.Returns(UserId);
        _denied = Definition(DeniedId, DeniedCode);
        _other = Definition(OtherId, "OTHER_REG");

        _registries.FindDefinitionAsync(DeniedCode, Arg.Any<CancellationToken>()).Returns(_denied);
        _registries.FindDefinitionAsync("OTHER_REG", Arg.Any<CancellationToken>()).Returns(_other);
        _registries.FindDefinitionByIdAsync(DeniedId, Arg.Any<CancellationToken>()).Returns(_denied);
        _registries.FindDefinitionByIdAsync(OtherId, Arg.Any<CancellationToken>()).Returns(_other);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RegistryDef> { _denied, _other });
        _registries.FindEntryAsync(DeniedEntryId, Arg.Any<CancellationToken>())
            .Returns(Entry(DeniedEntryId, DeniedId));

        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry>());
    }

    // ---- RegistryAccess: єдине місце рішення ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Глобальне_право_і_заборона_на_довідник__404_як_неіснуючий__а_не_доступ()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => RegistryAccess.RequireAsync(
            _access, _user, "Registry.View", GrantLevel.Read, DeniedId, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Глобальне_право_без_заборони_і_заборона_на_ІНШИЙ_довідник__доступ_як_раніше()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, OtherId));

        var profile = await RegistryAccess.RequireAsync(
            _access, _user, "Registry.View", GrantLevel.Read, DeniedId, default);

        Assert.NotNull(profile);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грант_і_заборона_на_той_самий_довідник__заборона_виграє__403()
    {
        Profile(b => b.Grant(ResourceKind.Registry, DeniedId, GrantLevel.Write).Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => RegistryAccess.RequireAsync(
            _access, _user, "Registry.View", GrantLevel.Read, DeniedId, default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void DeniedIds_і_IsDenied__читають_лише_заборони_на_довідники()
    {
        var profile = new AccessBuilder { UserId = UserId }
            .Deny(ResourceKind.Registry, DeniedId)
            .Deny(ResourceKind.Project, OtherId)
            .Deny(ResourceKind.Table, 3)
            .Build();

        Assert.Equal([DeniedId], RegistryAccess.DeniedIds(profile));
        Assert.True(RegistryAccess.IsDenied(profile, DeniedId));
        Assert.False(RegistryAccess.IsDenied(profile, OtherId));
    }

    // ---- читання даних: записи (пікер), запис, рядки, зовнішні ключі ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Записи_пікера__глобальне_View_і_заборона__404_ідентичний_неіснуючому()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var handler = new GetRegistryEntriesHandler(_registries, new RegistryResolver(), _cache, _access, _user);

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DeniedCode, new DateOnly(2026, 9, 30), null, default));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync("NO_SUCH", new DateOnly(2026, 9, 30), null, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registry", denied.Details!["messageKey"]);
        Assert.Equal(DeniedCode, denied.Details["registryCode"]);
        Assert.Equal(missing.ErrorCode, denied.ErrorCode);
        Assert.Equal(missing.Details!["messageKey"], denied.Details["messageKey"]);
        await _cache.DidNotReceiveWithAnyArgs().GetOrAddAsync(default!, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Записи_пікера__глобальне_View_без_заборон__не_платить_зайвим_запитом_довідника()
    {
        // Ледача резолюція — контракт: без заборон на довідники профіль не змушує читати довідник у RegistryAccess.
        Profile(b => b.Permission("Registry.View"));
        var handler = new GetRegistryEntriesHandler(_registries, new RegistryResolver(), _cache, _access, _user);

        await handler.HandleAsync(DeniedCode, new DateOnly(2026, 9, 30), null, default);

        // Один запит — власний у обробнику (перевірка asOf), а не другий від RegistryAccess.
        await _registries.Received(1).FindDefinitionAsync(DeniedCode, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Запис__глобальне_View_і_заборона__404()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => new GetRegistryEntryHandler(_registries, _access, _user).HandleAsync(DeniedCode, DeniedEntryId, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await _registries.DidNotReceive().ListValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Рядки_сітки__глобальне_View_і_заборона__404_і_жодного_читання_рядків()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var handler = new GetRegistryRowsHandler(_registries, _rows, new RegistryResolver(), _access, _user);
        var request = new RegistryRowsRequest(
            DeniedCode, new DateOnly(2026, 9, 30), null, null, null, new Dictionary<string, string>(), new CursorRequest(50, null));

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(request, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await _registries.DidNotReceive().ListEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Історія_запису__глобальне_View_і_заборона__404_і_жодного_читання_версій()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var handler = new GetRegistryEntryHistoryHandler(_registries, _rows, _access, _user);

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DeniedCode, DeniedEntryId, new CursorRequest(50, null), default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await _rows.DidNotReceive().ReadEntryHistoryAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Експорт__глобальне_View_і_заборона__404_без_файлу_й_без_події()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var workbooks = Substitute.For<IRegistryWorkbookWriter>();
        var handler = new Ecr.Application.Registries.Export.ExportRegistryHandler(
            _registries,
            new GetRegistryRowsHandler(_registries, _rows, new RegistryResolver(), _access, _user),
            Substitute.For<IRegistryKeyStore>(),
            workbooks,
            _audit,
            _access,
            _user,
            _clock);

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DeniedCode, "xlsx", null, 10, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await _registries.DidNotReceive().ListEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await workbooks.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<RegistryWorkbook>>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteIndependentSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зовнішні_ключі_перелік__глобальне_View_і_заборона__404()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var keys = Substitute.For<IRegistryExternalKeyStore>();

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new ListRegistryExternalKeysHandler(
                _registries, keys, _access, _user)
            .HandleAsync(DeniedCode, null, null, new CursorRequest(50, null), default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await keys.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default);
    }

    // ---- запис: upsert, вікно чинності, видалення, пакет, CSV, зовнішні ключі, сутність збору ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Upsert__глобальне_EditData_і_заборона__404_і_нічого_не_записано()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new UpsertRegistryEntryHandler(
                _registries, _access, _user, Writer())
            .HandleAsync(
                new Ecr.Application.Registries.Dto.RegistryEntryUpsertDto(
                    null, DeniedId, "E1", Text("E1"), null, new Dictionary<string, object?>()),
                default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registryId", denied.Details!["messageKey"]);
        _registries.DidNotReceive().Add(Arg.Any<RegistryEntry>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Вікно_чинності__глобальне_EditData_і_заборона__404_і_запис_не_змінено()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));
        var entry = Entry(DeniedEntryId, DeniedId);
        _registries.FindEntryAsync(DeniedEntryId, Arg.Any<CancellationToken>()).Returns(entry);

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new SetEntryValidityHandler(
                _registries, Substitute.For<IOrphanScanner>(), _uow, _audit, _access, _user, _clock)
            .HandleAsync(DeniedCode, DeniedEntryId, new DateOnly(2026, 1, 1), null, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        Assert.Null(entry.ValidFrom);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Видалення__глобальне_EditData_і_заборона__404_і_запис_живий()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));
        var entry = Entry(DeniedEntryId, DeniedId);
        _registries.FindEntryAsync(DeniedEntryId, Arg.Any<CancellationToken>()).Returns(entry);

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => Deleter().HandleAsync(DeniedCode, DeniedEntryId, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        Assert.False(entry.IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Пакет_сітки__глобальне_EditData_і_заборона__404_навіть_dryRun()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));
        var handler = new RegistryBatchHandler(
            _registries, _rows, _uow, Writer(), Deleter(), _access, _user, Substitute.For<Ecr.Application.Registries.Rules.IRegistryRuleEngine>());

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(
            DeniedCode,
            new RegistryBatchRequest([new RegistryBatchItemDto("c1", "upsert", null, "X", null, new Dictionary<string, object?>())]),
            dryRun: true,
            default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await _uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default(Func<CancellationToken, Task>)!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Імпорт_CSV__глобальне_EditData_і_заборона__404_навіть_dryRun()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));
        var handler = new ImportRegistryEntriesHandler(_registries, _audit, _access, _user, _clock, Writer());

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DeniedCode, "code\nE1\n", 8, 1024, dryRun: true, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        _registries.DidNotReceive().Add(Arg.Any<RegistryEntry>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Прив_язка_і_відв_язка_зовнішнього_ключа__глобальне_EditData_і_заборона__404()
    {
        Profile(b => b.Permission("Registry.EditData").Deny(ResourceKind.Registry, DeniedId));
        var keys = Substitute.For<IRegistryExternalKeyStore>();

        var bind = await Assert.ThrowsAsync<NotFoundException>(() => new BindRegistryExternalKeyHandler(
                _registries, keys, Substitute.For<IDataSourceStore>(), _uow, _audit, _access, _user, _clock)
            .HandleAsync(DeniedCode, new BindRegistryExternalKeyCommand(DeniedEntryId, 1, "GUID"), default));
        var unbind = await Assert.ThrowsAsync<NotFoundException>(() => new UnbindRegistryExternalKeyHandler(
                _registries, keys, _uow, _audit, _access, _user, _clock)
            .HandleAsync(DeniedCode, 5, default));

        Assert.Equal("ECR-REG-0404", bind.ErrorCode);
        Assert.Equal("ECR-REG-0404", unbind.ErrorCode);
        await keys.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await keys.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
    }

    // ---- опис, історія, чернетка, перелік, пошук ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Опис_довідника__глобальне_View_і_заборона__404()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new GetRegistryDefinitionHandler(
                _registries, Substitute.For<IRegistryKeyStore>(), _access, _user)
            .HandleAsync(DeniedCode, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registry", denied.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Історія_довідника__глобальне_View_і_заборона__404_і_журнал_не_читано()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var audit = Substitute.For<IAuditReader>();

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new GetRegistryHistoryHandler(
                _registries, audit, _access, _user)
            .HandleAsync(DeniedCode, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await audit.DidNotReceiveWithAnyArgs().ReadStructureChangesAsync(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Чернетка_опису__глобальне_View_і_заборона__404()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var drafts = Substitute.For<IRegistryDraftStore>();

        var denied = await Assert.ThrowsAsync<NotFoundException>(() => new GetRegistryDefinitionDraftHandler(
                _registries, drafts, _access, _user)
            .HandleAsync(DeniedCode, default));

        Assert.Equal("ECR-REG-0404", denied.ErrorCode);
        await drafts.DidNotReceiveWithAnyArgs().FindAsync(default, default);
    }

    // ---- S18, другий прогін: шляхи ЗМІНИ опису і «де використовується» теж поважають заборону ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Використання_довідника__EditDefinition_і_заборона__404_і_посилання_не_рахувались()
    {
        Profile(b => b.Permission("Registry.EditDefinition").Deny(ResourceKind.Registry, DeniedId));

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => new GetRegistryUsageHandler(_registries, _access, _user).HandleAsync(DeniedCode, default));

        Assert.Equal("err.ECR-REG-0404.registry", denied.Details!["messageKey"]);
        await _registries.DidNotReceiveWithAnyArgs().FindDefinitionUsageAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Зміна_опису_чернетка_публікація__EditDefinition_і_заборона__404_і_нічого_не_змінено()
    {
        Profile(b => b
            .Permission("Registry.EditDefinition")
            .Permission("Registry.Publish")
            .Deny(ResourceKind.Registry, DeniedId));
        var drafts = Substitute.For<IRegistryDraftStore>();
        var keys = Substitute.For<IRegistryKeyStore>();
        var units = Substitute.For<IUnitCatalog>();
        var save = new SaveRegistryDefinitionHandler(
            _registries, _uow, _audit, _access, _user, _clock, units, keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(keys, _uow));

        var direct = await Assert.ThrowsAsync<NotFoundException>(() => save.HandleAsync(
            DeniedCode, new Ecr.Application.Registries.Dto.SaveRegistryDefinitionDto([], [], "r"), default));
        var saveDraft = await Assert.ThrowsAsync<NotFoundException>(() => new SaveRegistryDefinitionDraftHandler(
                _registries, drafts, _uow, _audit, _access, _user, _clock)
            .HandleAsync(DeniedCode, new Ecr.Application.Registries.Dto.SaveRegistryDefinitionDraftRequest([], [], "r", null), default));
        var discard = await Assert.ThrowsAsync<NotFoundException>(() => new DiscardRegistryDefinitionDraftHandler(
                _registries, drafts, _uow, _audit, _access, _user, _clock)
            .HandleAsync(DeniedCode, null, default));
        var publish = await Assert.ThrowsAsync<NotFoundException>(() => new PublishRegistryDefinitionHandler(
                _registries, drafts, save, _access, _user)
            .HandleAsync(DeniedCode, new Ecr.Application.Registries.Dto.PublishRegistryDefinitionRequest("AA=="), default));

        foreach (var ex in new[] { direct, saveDraft, discard, publish })
        {
            Assert.Equal("err.ECR-REG-0404.registry", ex.Details!["messageKey"]);
        }

        await drafts.DidNotReceiveWithAnyArgs().FindAsync(default, default);
        await _uow.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default!, default);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Перелік_довідників__заборонений_не_повертається__решта_так()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));

        var list = await new ListRegistriesHandler(_registries, _access, _user).HandleAsync(default);

        Assert.Equal(["OTHER_REG"], list.Select(d => d.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Перелік_довідників__без_заборон__повертає_усі()
    {
        Profile(b => b.Permission("Registry.View"));

        var list = await new ListRegistriesHandler(_registries, _access, _user).HandleAsync(default);

        Assert.Equal(["DENIED_REG", "OTHER_REG"], list.Select(d => d.Code).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Пошук__заборонені_довідники_передаються_сховищу_як_виключені()
    {
        Profile(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, DeniedId));
        var store = Substitute.For<ISearchStore>();
        SearchScope? seen = null;
        store.SearchAsync(Arg.Any<string>(), Arg.Do<SearchScope>(s => seen = s), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchRow>());

        await new SearchHandler(store, _access, _user).HandleAsync("reg", 10, default);

        Assert.NotNull(seen);
        Assert.True(seen.Registries);
        Assert.Equal([DeniedId], seen.DeniedRegistries);
    }

    // ---- helpers ----

    private RegistryEntryWriter Writer() => new(_registries, _uow, _audit, _user, _clock);

    private DeleteRegistryEntryHandler Deleter() => new(_registries, _uow, _audit, _access, _user, _clock);

    private void Profile(Func<AccessBuilder, AccessBuilder> configure)
        => _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(configure(new AccessBuilder { UserId = UserId }).Build());

    private static RegistryDef Definition(int id, string code)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(definition, id);
        return definition;
    }

    private static RegistryEntry Entry(long id, int registryDefId)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create("E" + id), Text("E"));
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
