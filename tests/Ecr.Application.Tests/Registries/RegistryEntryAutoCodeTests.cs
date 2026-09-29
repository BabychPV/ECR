// tests/Ecr.Application.Tests/Registries/RegistryEntryAutoCodeTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
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
/// Автоматичний код запису (RT-12, <c>D-157</c>, FEATURE-REGISTRY-TABLES §4.8): код нового запису
/// довідника з <c>CodeMode = Auto</c> — <c>E</c> + 9 цифр послідовності, однаково в пакеті writer'а
/// і в ручному upsert (CSV — <c>RegistryCompositionHttpTests</c>).
/// </summary>
/// <remarks>
/// Мутаційний доказ: <c>RegistryEntryWriter.ReserveAutoCodesAsync</c> повертає порожню чергу →
/// <see cref="Пакет_без_кодів_отримує_коди_послідовності"/> і
/// <see cref="Upsert_без_коду_отримує_код_послідовності"/> червоні.
/// </remarks>
public sealed class RegistryEntryAutoCodeTests
{
    private const int RegistryId = 44;
    private const int NameFieldId = 441;
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly List<RegistryEntry> _added = [];
    private readonly RegistryDef _definition;

    public RegistryEntryAutoCodeTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.EditData").Build());

        _definition = new RegistryDef(EcrCode.Create("GAS_COMPOSITION"), Text("Gas composition"), isTemporal: false);
        SetId(_definition, RegistryId);
        _definition.UseCodeMode(RegistryCodeMode.Auto);
        var name = new RegistryFieldDef(RegistryId, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        SetId(name, NameFieldId);
        _definition.AddField(name);

        _registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(_definition);
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryEntry>());
        _registries.When(r => r.Add(Arg.Any<RegistryEntry>())).Do(c => _added.Add(c.Arg<RegistryEntry>()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Пакет_без_кодів_отримує_коди_послідовності()
    {
        _registries.NextEntryCodesAsync(2, Arg.Any<CancellationToken>()).Returns(41L);

        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [Row(""), Row("")]), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal(["E000000041", "E000000042"], _added.Select(e => e.Code));

        // Одне звернення до послідовності на пакет, а не по одному на запис.
        await _registries.Received(1).NextEntryCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Пакет_із_власним_кодом_нового_запису_помилка_рядка()
    {
        var result = await Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [Row("MY1")]), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            [new RegistryEntryImportError(1, "MY1", null, RegistryEntryWriter.EntryCodeAutomaticKey)],
            result.Errors);
        Assert.Empty(_added);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task У_ручному_довіднику_порожній_код_лишається_помилкою_виклику()
    {
        _definition.UseCodeMode(RegistryCodeMode.Manual);

        await Assert.ThrowsAsync<ArgumentException>(() => Writer().WriteAsync(
            new RegistryEntryWriteBatch(RegistryId, [Row("")]), CancellationToken.None));

        await _registries.DidNotReceive().NextEntryCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Upsert_без_коду_отримує_код_послідовності()
    {
        _registries.NextEntryCodesAsync(1, Arg.Any<CancellationToken>()).Returns(7L);

        await Upsert().HandleAsync(Dto(code: string.Empty), CancellationToken.None);

        var entry = Assert.Single(_added);
        Assert.Equal("E000000007", entry.Code);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Upsert_з_власним_кодом_у_автоматичному_довіднику_422()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Upsert().HandleAsync(Dto(code: "MY1"), CancellationToken.None));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.entryCodeAutomatic", error.Details!["messageKey"]);
        Assert.Empty(_added);
        await _registries.DidNotReceive().NextEntryCodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private RegistryEntryWriter Writer() => new(_registries, _uow, _audit, _user, _clock);

    private UpsertRegistryEntryHandler Upsert() => new(_registries, _access, _user, Writer());

    private static RegistryEntryWrite Row(string code)
        => new(code, new Dictionary<string, object?> { ["NAME"] = "Methane" });

    private static RegistryEntryUpsertDto Dto(string code)
        => new(
            Id: null,
            RegistryDefId: RegistryId,
            Code: code,
            Display: Text("Methane"),
            ParentEntryId: null,
            Values: new Dictionary<string, object?> { ["NAME"] = "Methane" });

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
