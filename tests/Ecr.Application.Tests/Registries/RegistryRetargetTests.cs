// tests/Ecr.Application.Tests/Registries/RegistryRetargetTests.cs
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
/// Зміна й зняття зв'язку наявного поля довідника (<c>ФВ-8.12</c>, порція 1).
/// </summary>
/// <remarks>
/// Мутаційні докази: без перевірки значень у <c>GuardLookupRetargetAsync</c> червоний
/// <see cref="Ціль_не_змінюється_поки_на_неї_посилаються_значення"/>; без <c>PointTo</c> —
/// <see cref="Ціль_змінюється_коли_значень_немає"/> і <see cref="Зв_язок_знімається_коли_значень_немає"/>;
/// без перевірки існування цілі — <see cref="Невідома_ціль_відхиляється"/>.
/// </remarks>
public sealed class RegistryRetargetTests
{
    private const int OwnerId = 41;
    private const int OldTargetId = 42;
    private const int NewTargetId = 43;
    private const int LinkFieldId = 411;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly RegistryDef _owner;
    private readonly RegistryFieldDef _link;

    public RegistryRetargetTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }
            .Permission("Registry.View").Permission("Registry.EditDefinition").Permission("Registry.Publish").Build());

        _owner = new RegistryDef(EcrCode.Create("OWNER"), Text("OWNER"), false);
        SetId(_owner, OwnerId);
        var name = new RegistryFieldDef(OwnerId, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 1);
        SetId(name, 410);
        name.MarkKey(true);
        _owner.AddField(name);
        _link = new RegistryFieldDef(OwnerId, EcrCode.Create("LINK"), Text("LINK"), CellDataType.Lookup, 2);
        SetId(_link, LinkFieldId);
        _link.PointTo(OldTargetId);
        _owner.AddField(_link);

        _registries.FindDefinitionAsync("OWNER", Arg.Any<CancellationToken>()).Returns(_owner);
        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>()).Returns([_owner]);
        _registries.FindDefinitionByIdAsync(NewTargetId, Arg.Any<CancellationToken>())
            .Returns(new RegistryDef(EcrCode.Create("NEW"), Text("NEW"), false));
        _registries.FindDefinitionByIdAsync(999, Arg.Any<CancellationToken>()).Returns((RegistryDef?)null);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Ціль_змінюється_коли_значень_немає()
    {
        await Save(NewTargetId);

        Assert.Equal(NewTargetId, _link.RefRegistryDefId);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Зв_язок_знімається_коли_значень_немає()
    {
        await Save(null);

        Assert.Null(_link.RefRegistryDefId);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    [InlineData(NewTargetId)]
    [InlineData(null)]
    public async Task Ціль_не_змінюється_поки_на_неї_посилаються_значення(int? target)
    {
        var entry = new RegistryEntry(OwnerId, EcrCode.Create("E1"), Text("E1"));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, 7L);
        _registries.ListEntriesAsync(OwnerId, Arg.Any<CancellationToken>()).Returns([entry]);
        var value = new RegistryValue(7L, LinkFieldId);
        value.Set(CellDataType.Lookup, 5L, null);
        _registries.ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns([value]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(target));

        Assert.Equal("err.ECR-REG-0422.lookupRetargetInUse", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Невідома_ціль_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(999));

        Assert.Equal("err.ECR-REG-0422.lookupTargetUnknown", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Композиція_не_перенацілюється()
    {
        _link.ComposeInto(ParentDeletePolicy.Restrict);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Save(NewTargetId));

        Assert.Equal("err.ECR-REG-0422.relationKindImmutable", error.Details!["messageKey"]);
        Assert.Equal(OldTargetId, _link.RefRegistryDefId);
    }

    private Task<int> Save(int? target)
    {
        var fields = _owner.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldSaveDto(
            f.Id, f.Code, f.NameL10n, f.DataType.ToString(), f.Ordinal, f.IsRequired, f.IsKey,
            f.Id == LinkFieldId ? target : f.RefRegistryDefId, f.UnitId)).ToList();
        var handler = new SaveRegistryDefinitionHandler(
            _registries, _uow, Substitute.For<IAuditWriter>(), _access, _user, Substitute.For<IClock>(),
            Substitute.For<IUnitCatalog>(), _keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(_keys, _uow));
        return handler.HandleAsync("OWNER", new SaveRegistryDefinitionDto(fields, [], "ФВ-8.12"), default);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}