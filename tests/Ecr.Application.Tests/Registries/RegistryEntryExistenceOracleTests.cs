// tests/Ecr.Application.Tests/Registries/RegistryEntryExistenceOracleTests.cs
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
/// S18, другий прогін: запис ЧУЖОГО довідника (зокрема схованого забороною) на шляхах зміни запису
/// за <c>{code}/entries/{id}</c> відповідає ТАК САМО, як неіснуючий запис: <c>404 registryEntry</c>.
/// </summary>
/// <remarks>
/// ⛔ Доти вікно чинності й видалення читали запис ДО права і питали право на довідник ЗАПИСУ, а код
/// з маршруту вікно чинності ігнорувало: запис схованого довідника давав <c>404 registryId</c> з його
/// <c>registryDefId</c> (власник глобального права) чи <c>403</c> (лише грант), неіснуючий —
/// <c>404 registryEntry</c>. Оновлення з <c>id</c> чужого запису давало <c>422 entryWrongRegistry</c> з
/// <c>ownerRegistryDefId</c>. Три різні відповіді — оракул існування й місця запису.
/// <para>
/// Мутаційні докази: повернути у вікні чинності право на довідник запису (<c>entry.RegistryDefId</c>) →
/// <see cref="Вікно_чинності__запис_схованого_довідника_через_відкритий_код__як_неіснуючий"/> червоний;
/// прибрати звірку <c>entry.RegistryDefId != definition.Id</c> у видаленні →
/// <see cref="Видалення__запис_схованого_довідника_через_відкритий_код__як_неіснуючий"/> червоний; повернути
/// <c>422 entryWrongRegistry</c> в оновленні → <see cref="Оновлення__id_запису_іншого_довідника__як_неіснуючий"/>
/// червоний; прибрати звірку коду маршруту в оновленні → <see cref="Оновлення__код_маршруту_не_того_довідника__404"/>
/// червоний.
/// </para>
/// </remarks>
public sealed class RegistryEntryExistenceOracleTests
{
    private const int UserId = 9;
    private const int HiddenId = 601;
    private const int OpenId = 602;
    private const string HiddenCode = "HIDDEN_REG";
    private const string OpenCode = "OPEN_REG";
    private const long HiddenEntryId = 7001;
    private const long MissingEntryId = 7999;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TestClock _clock = new(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));
    private readonly RegistryEntry _hiddenEntry;

    public RegistryEntryExistenceOracleTests()
    {
        _user.UserId.Returns(UserId);
        var hidden = Definition(HiddenId, HiddenCode);
        var open = Definition(OpenId, OpenCode);

        _registries.FindDefinitionAsync(HiddenCode, Arg.Any<CancellationToken>()).Returns(hidden);
        _registries.FindDefinitionAsync(OpenCode, Arg.Any<CancellationToken>()).Returns(open);
        _registries.FindDefinitionByIdAsync(HiddenId, Arg.Any<CancellationToken>()).Returns(hidden);
        _registries.FindDefinitionByIdAsync(OpenId, Arg.Any<CancellationToken>()).Returns(open);
        _hiddenEntry = Entry(HiddenEntryId, HiddenId);
        _registries.FindEntryAsync(HiddenEntryId, Arg.Any<CancellationToken>()).Returns(_hiddenEntry);
        _registries.FindEntryAsync(MissingEntryId, Arg.Any<CancellationToken>()).Returns((RegistryEntry?)null);
    }

    public static TheoryData<string> Profiles => new() { "global-deny", "grant-only" };

    [Theory]
    [MemberData(nameof(Profiles))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Вікно_чинності__запис_схованого_довідника_через_відкритий_код__як_неіснуючий(string profile)
    {
        Profile(profile);
        var handler = new SetEntryValidityHandler(
            _registries, Substitute.For<IOrphanScanner>(), _uow, _audit, _access, _user, _clock);

        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(OpenCode, HiddenEntryId, new DateOnly(2026, 1, 1), null, default));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(OpenCode, MissingEntryId, new DateOnly(2026, 1, 1), null, default));

        AssertSameShape(missing, hidden);
        Assert.Null(_hiddenEntry.ValidFrom);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Видалення__запис_схованого_довідника_через_відкритий_код__як_неіснуючий(string profile)
    {
        Profile(profile);
        var handler = new DeleteRegistryEntryHandler(_registries, _uow, _audit, _access, _user, _clock);

        var hidden = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(OpenCode, HiddenEntryId, default));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(OpenCode, MissingEntryId, default));

        AssertSameShape(missing, hidden);
        Assert.False(_hiddenEntry.IsDeleted);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Оновлення__id_запису_іншого_довідника__як_неіснуючий(string profile)
    {
        Profile(profile);
        var handler = new UpsertRegistryEntryHandler(_registries, _access, _user, Writer());

        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleWithWarningsAsync(Update(HiddenEntryId), OpenCode, default));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleWithWarningsAsync(Update(MissingEntryId), OpenCode, default));

        AssertSameShape(missing, hidden);
        Assert.False(hidden.Details!.ContainsKey("ownerRegistryDefId"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "S18")]
    public async Task Оновлення__код_маршруту_не_того_довідника__404()
    {
        Profile("global-deny");
        var handler = new UpsertRegistryEntryHandler(_registries, _access, _user, Writer());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleWithWarningsAsync(Update(MissingEntryId), "ELSEWHERE", default));

        Assert.Equal("err.ECR-REG-0404.registry", ex.Details!["messageKey"]);
        _registries.DidNotReceive().Add(Arg.Any<RegistryEntry>());
    }

    private static void AssertSameShape(NotFoundException expected, NotFoundException actual)
    {
        Assert.Equal(expected.ErrorCode, actual.ErrorCode);
        Assert.Equal("err.ECR-REG-0404.registryEntry", actual.Details!["messageKey"]);
        Assert.Equal(
            expected.Details!.Keys.Order(StringComparer.Ordinal),
            actual.Details.Keys.Order(StringComparer.Ordinal));
        Assert.False(actual.Details.ContainsKey("registryDefId"));
    }

    private static RegistryEntryUpsertDto Update(long id)
        => new(id, OpenId, "E1", Text("E1"), null, new Dictionary<string, object?>());

    /// <summary>
    /// <c>global-deny</c> — глобальне <c>Registry.EditData</c> і заборона на схований довідник;
    /// <c>grant-only</c> — лише грант <c>Write</c> на відкритий довідник.
    /// </summary>
    private void Profile(string kind)
    {
        var builder = new AccessBuilder { UserId = UserId };
        builder = kind == "global-deny"
            ? builder.Permission("Registry.EditData").Deny(ResourceKind.Registry, HiddenId)
            : builder.Grant(ResourceKind.Registry, OpenId, GrantLevel.Write);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private RegistryEntryWriter Writer() => new(_registries, _uow, _audit, _user, _clock);

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
