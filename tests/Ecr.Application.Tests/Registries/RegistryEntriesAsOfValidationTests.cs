// tests/Ecr.Application.Tests/Registries/RegistryEntriesAsOfValidationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Обов'язковість <c>asOf</c> залежить від <see cref="RegistryDef.IsTemporal"/>,
/// а не є безумовною (пряме рішення замовника, PR «FIX Lookup-піцкер
/// нетемпорального довідника»).
/// </summary>
/// <remarks>
/// ⛔ **Переписано.** До цього набір фіксував ЗВОРОТНЮ — і хибну — інваріанту:
/// «asOf обов'язковий для БУДЬ-ЯКОГО довідника, перевірка йде РАНІШЕ походу в
/// базу за визначенням» (`_registries.DidNotReceive().FindDefinitionAsync`).
/// Живий прогін показав наслідок: жоден клієнтський виклик `GET …/entries` не
/// передавав asOf НІКОЛИ, і Lookup-піцкер нетемпорального довідника
/// (наприклад, Substance — записи без вікна чинності, `ValidFrom`/`ValidTo`
/// завжди <c>null</c>) був непрацездатним ЗАВЖДИ: сервер відмовляв
/// `422 ECR-REQ-0422`, хоча жодна дата періоду для такого довідника нічого не
/// важить (`ValidityWindow.Contains` без меж — <c>true</c> для будь-якої
/// дати). Довідник без прапорця «Time-bound» не повинен вимагати дату — це і є
/// сенс цього прапорця.
/// <para>
/// Три сценарії нижче — це ТЕПЕРІШНЯ інваріанта: (а) нетемпоральний довідник
/// працює без <c>asOf</c> і повертає записи; (б) темпоральний без
/// <c>asOf</c> — усе ще <c>422</c> (правило не скасоване, лише звужене); (в)
/// темпоральний з <c>asOf</c> — фільтрує записи за вікном чинності РЕАЛЬНИМ
/// резолвером (`RegistryResolver`), а не заглушкою кешу.
/// </para>
/// </remarks>
public sealed class RegistryEntriesAsOfValidationTests
{
    private const int RegistryDefId = 100;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RegistryEntriesAsOfValidationTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }
                .Permission(GetRegistryEntriesHandler.Permission)
                .Build());

        // ⚠ Кеш — НАСКРІЗНИЙ виклик переданої фабрики, а не заглушка з готовим
        // переліком: (в) нижче доводить фільтрацію РЕЗОЛВЕРОМ, а заглушка, що
        // повертає фіксований список, довела б лише те, що метод не впав.
        _cache.GetOrAddAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
                callInfo.ArgAt<Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>>>(1)(default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нетемпоральний_довідник_повертає_записи_без_asOf()
    {
        StubDefinition(isTemporal: false, code: "SUBSTANCE");

        var entry = new RegistryEntry(RegistryDefId, EcrCode.Create("CO2"), Text("CO2"));
        SetId(entry, 1L);

        _registries.ListEntriesAsync(RegistryDefId, Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry> { entry });

        // `default(DateOnly)` — те саме, що надсилає клієнт, коли взагалі не
        // передає `asOf` (запит без параметра резолвиться в `default`).
        var result = await Handler().HandleAsync(
            "SUBSTANCE", default, parentEntryId: null, default);

        Assert.Single(result);
        Assert.Equal(1L, result[0].Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Темпоральний_довідник_без_asOf_відхиляється()
    {
        StubDefinition(isTemporal: true, code: "PERMIT");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync("PERMIT", default, parentEntryId: null, default));

        Assert.Equal(Ecr.Domain.Errors.ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Contains("asOf", error.Message, StringComparison.Ordinal);
        Assert.Equal("err.ECR-REQ-0422.asOfRequired", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Темпоральний_довідник_з_asOf_фільтрує_за_вікном_чинності()
    {
        StubDefinition(isTemporal: true, code: "PERMIT");

        var active = new RegistryEntry(RegistryDefId, EcrCode.Create("ACTIVE"), Text("Active"));
        SetId(active, 1L);
        active.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));

        var expired = new RegistryEntry(RegistryDefId, EcrCode.Create("EXPIRED"), Text("Expired"));
        SetId(expired, 2L);
        expired.SetValidity(new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1));

        _registries.ListEntriesAsync(RegistryDefId, Arg.Any<CancellationToken>())
            .Returns(new List<RegistryEntry> { active, expired });

        var result = await Handler().HandleAsync(
            "PERMIT", new DateOnly(2026, 6, 1), parentEntryId: null, default);

        Assert.Single(result);
        Assert.Equal(1L, result[0].Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "D1-06")]
    public async Task Перелік_довідників_D1_06_частина_під_темпоральним_батьком_вимагає_asOf_хоч_сама_нетемпоральна()
    {
        const int partId = 201;
        const int parentId = 202;
        const int plainId = 203;

        var parent = Registry(parentId, "CASE_TEMPORAL", isTemporal: true);
        var part = Registry(partId, "GAS_COMPOSITION", isTemporal: false);
        var field = new RegistryFieldDef(partId, EcrCode.Create("CASE"), Text("CASE"), CellDataType.Lookup, 1);
        SetId(field, 2010);
        field.PointTo(parentId);
        field.ComposeInto(ParentDeletePolicy.Cascade);
        part.AddField(field);
        var plain = Registry(plainId, "SUBSTANCE", isTemporal: false);

        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RegistryDef> { parent, part, plain });

        var list = await new ListRegistriesHandler(
                _registries, _access, _user, Substitute.For<Ecr.Domain.Abstractions.IClock>())
            .HandleAsync(default);

        // Темпоральний: так; частина за темпоральним батьком: так (саме тут клієнт не слав asOf і діставав 422);
        // довідник без темпоральності в ланцюжку: ні - контроль, що прапорець не «завжди true».
        Assert.True(list.Single(d => d.Code == "CASE_TEMPORAL").AsOfRequired);
        Assert.True(list.Single(d => d.Code == "GAS_COMPOSITION").AsOfRequired);
        Assert.False(list.Single(d => d.Code == "SUBSTANCE").AsOfRequired);
    }

    private GetRegistryEntriesHandler Handler()
        => new(_registries, new RegistryResolver(), _cache, _access, _user);

    private static RegistryDef Registry(int id, string code, bool isTemporal)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal);
        SetId(definition, id);
        return definition;
    }

    private void StubDefinition(bool isTemporal, string code)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text("Registry"), isTemporal);
        SetId(definition, RegistryDefId);

        _registries.FindDefinitionAsync(code, Arg.Any<CancellationToken>()).Returns(definition);
    }

    private static void SetId<T>(Entity<T> entity, T id) where T : IEquatable<T>
        => typeof(Entity<T>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Text(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
