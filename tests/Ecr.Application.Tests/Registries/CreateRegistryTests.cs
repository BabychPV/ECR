// tests/Ecr.Application.Tests/Registries/CreateRegistryTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Заведення довідника-контейнера з нуля, без жодного поля (директива №11,
/// T4).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>POST /registries</c> не існувало: `RegistriesController` умів
/// лише читати перелік і правити опис НАЯВНОГО довідника, а сам довідник
/// заводив тільки офлайновий seed.
/// </remarks>
public sealed class CreateRegistryTests
{
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public CreateRegistryTests()
    {
        _user.UserId.Returns(9);
        Allow("Registry.EditDefinition");

        _registries.FindDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RegistryDef?)null);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Заводить_довідник_без_жодного_поля()
    {
        var created = await Handler().HandleAsync(
            "WASTE_GROUP",
            new Dictionary<string, string> { ["en"] = "Waste group" },
            isTemporal: true,
            default);

        Assert.Equal("WASTE_GROUP", created.Code);
        Assert.Equal("Waste group", created.NameL10n.Get("en"));
        Assert.True(created.IsTemporal);
        Assert.False(created.IsHierarchical);
        Assert.Empty(created.Fields);

        _registries.Received(1).AddDefinition(Arg.Is<RegistryDef>(d => d.Code == "WASTE_GROUP"));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Дублікат_коду_відхиляється_і_нічого_не_зберігається()
    {
        var clash = new RegistryDef(
            EcrCode.Create("WASTE_GROUP"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Existing" }),
            isTemporal: false);

        _registries.FindDefinitionAsync("WASTE_GROUP", Arg.Any<CancellationToken>()).Returns(clash);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            "WASTE_GROUP",
            new Dictionary<string, string> { ["en"] = "Waste group" },
            isTemporal: true,
            default));

        Assert.Equal("ECR-REG-4091", error.ErrorCode);

        _registries.DidNotReceive().AddDefinition(Arg.Any<RegistryDef>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_права_Registry_EditDefinition_довідник_не_заводиться()
    {
        // ⛔ D-134: мутація, що прибирає перевірку права, має ловитися саме
        // цим тестом — і рівно тому право задане ЗАМІНОЮ дозволеного набору,
        // а не додаванням до нього.
        Allow("Registry.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            "WASTE_GROUP",
            new Dictionary<string, string> { ["en"] = "Waste group" },
            isTemporal: true,
            default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);

        _registries.DidNotReceive().AddDefinition(Arg.Any<RegistryDef>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Анонімний_запит_відхиляється_до_перевірки_права()
    {
        _user.UserId.Returns((int?)null);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            "WASTE_GROUP",
            new Dictionary<string, string> { ["en"] = "Waste group" },
            isTemporal: true,
            default));

        Assert.Equal("ECR-AUTH-0401", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Недопустимий_код_відхиляється()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => Handler().HandleAsync(
            "waste-group",
            new Dictionary<string, string> { ["en"] = "Waste group" },
            isTemporal: true,
            default));

        Assert.Equal("ECR-CFG-0422", error.ErrorCode);
    }

    private CreateRegistryHandler Handler() => new(_registries, _uow, _access, _user);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}
