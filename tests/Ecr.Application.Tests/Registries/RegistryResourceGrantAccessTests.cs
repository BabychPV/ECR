// tests/Ecr.Application.Tests/Registries/RegistryResourceGrantAccessTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
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
/// Ресурсний грант на КОНКРЕТНИЙ довідник (<c>A7-58</c>, <see cref="ResourceKind.Registry"/>)
/// — додатковий, вужчий шлях доступу до даних довідника для користувача БЕЗ
/// глобального <c>Registry.View</c>/<c>Registry.EditData</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Мутаційний доказ цього набору.</b> Прибрати
/// <c>profile.LevelFor(ResourceKind.Registry, registryDefId) &gt;= minLevel</c>
/// з <see cref="RegistryAccess.RequireAsync(IAccessDecisionService, ICurrentUser, string, GrantLevel, Func{CancellationToken, Task{int?}}, CancellationToken)"/>
/// (лишити тільки <c>profile.Has(permission)</c>) — і
/// <see cref="Грант_на_свій_довідник_дозволяє_редагувати_без_глобального_права"/>
/// та <see cref="Грант_на_свій_довідник_дозволяє_читати_без_глобального_права"/>
/// стають червоними: користувач БЕЗ глобального права і З грантом рівно на
/// цей довідник дістає <c>ECR-AUTH-0403</c> замість дозволу.
/// </remarks>
public sealed class RegistryResourceGrantAccessTests
{
    private const int OwnRegistryId = 501;
    private const int OtherRegistryId = 502;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TestClock _clock = new(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc));

    public RegistryResourceGrantAccessTests()
    {
        _user.UserId.Returns(9);

        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry>());
    }

    // ---- UpsertRegistryEntryHandler (право на ЗМІНУ даних: GrantLevel.Write) ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грант_на_свій_довідник_дозволяє_редагувати_без_глобального_права()
    {
        ProfileWithGrant(GrantLevel.Write, OwnRegistryId);
        StubDefinition(OwnRegistryId);

        _registries.FindEntryByCodeAsync(OwnRegistryId, "E1", Arg.Any<CancellationToken>())
            .Returns((RegistryEntry?)null);

        var id = await UpsertHandler().HandleAsync(
            new RegistryEntryUpsertDto(
                Id: null,
                RegistryDefId: OwnRegistryId,
                Code: "E1",
                Display: Text("Entry"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?>()),
            default);

        Assert.True(id >= 0);
        _registries.Received(1).Add(Arg.Is<RegistryEntry>(e => e.RegistryDefId == OwnRegistryId));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грант_на_чужий_довідник_не_дає_редагувати()
    {
        // Грант виданий на OtherRegistryId, а зміна йде в OwnRegistryId —
        // вужчий шлях доступу не поширюється сам собою на сусідній довідник.
        ProfileWithGrant(GrantLevel.Write, OtherRegistryId);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => UpsertHandler().HandleAsync(
            new RegistryEntryUpsertDto(
                Id: null,
                RegistryDefId: OwnRegistryId,
                Code: "E1",
                Display: Text("Entry"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?>()),
            default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        _registries.DidNotReceive().Add(Arg.Any<RegistryEntry>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грант_рівня_Read_не_дає_редагувати()
    {
        // Читання не тягне за собою запис: Read < Write.
        ProfileWithGrant(GrantLevel.Read, OwnRegistryId);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => UpsertHandler().HandleAsync(
            new RegistryEntryUpsertDto(
                Id: null,
                RegistryDefId: OwnRegistryId,
                Code: "E1",
                Display: Text("Entry"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?>()),
            default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Глобальне_право_EditData_діє_як_раніше_без_жодного_гранту()
    {
        // Регресія: користувач із глобальним правом і БЕЗ жодного гранту —
        // як до A7-58, бачить/редагує все.
        ProfileWithPermission(UpsertRegistryEntryHandler.Permission);
        StubDefinition(OwnRegistryId);

        _registries.FindEntryByCodeAsync(OwnRegistryId, "E1", Arg.Any<CancellationToken>())
            .Returns((RegistryEntry?)null);

        await UpsertHandler().HandleAsync(
            new RegistryEntryUpsertDto(
                Id: null,
                RegistryDefId: OwnRegistryId,
                Code: "E1",
                Display: Text("Entry"),
                ParentEntryId: null,
                Values: new Dictionary<string, object?>()),
            default);

        _registries.Received(1).Add(Arg.Any<RegistryEntry>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- GetRegistryEntriesHandler (право на ЧИТАННЯ: GrantLevel.Read) ----

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грант_на_свій_довідник_дозволяє_читати_без_глобального_права()
    {
        ProfileWithGrant(GrantLevel.Read, OwnRegistryId);
        StubDefinition(OwnRegistryId, code: "OWN");

        var result = await GetHandler().HandleAsync(
            "OWN", new DateOnly(2026, 9, 23), parentEntryId: null, default);

        Assert.NotNull(result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_гранту_і_без_глобального_права_читання_дає_403()
    {
        ProfileWithGrant(GrantLevel.Read, OtherRegistryId);
        StubDefinition(OwnRegistryId, code: "OWN");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => GetHandler().HandleAsync(
            "OWN", new DateOnly(2026, 9, 23), parentEntryId: null, default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    // ---- helpers ----

    private UpsertRegistryEntryHandler UpsertHandler()
        => new(_registries, _uow, _audit, _access, _user, _clock);

    private GetRegistryEntriesHandler GetHandler()
        => new(_registries, new RegistryResolver(), _cache, _access, _user);

    private void StubDefinition(int registryDefId, string code = "REG")
    {
        var definition = new RegistryDef(
            Ecr.Domain.ValueObjects.EcrCode.Create(code), Text("Registry"), isTemporal: false);

        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(definition, registryDefId);

        _registries.FindDefinitionByIdAsync(registryDefId, Arg.Any<CancellationToken>()).Returns(definition);
        _registries.FindDefinitionAsync(code, Arg.Any<CancellationToken>()).Returns(definition);
    }

    private void ProfileWithGrant(GrantLevel level, int registryDefId)
        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }.Grant(ResourceKind.Registry, registryDefId, level).Build());

    private void ProfileWithPermission(string permission)
        => _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }.Permission(permission).Build());

    private static LocalizedText Text(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
