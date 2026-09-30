// tests/Ecr.Application.Tests/Registries/RegistryImpactHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Impact;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// <c>GET /registries/{code}/impact</c> (RT-25): права двох рівнів і форма відповіді.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>profile.Has(CalculationPermission, row.ProjectId)</c> →
/// <see cref="Документ_чужого_проєкту_не_потрапляє_в_перелік"/> червоний; прибрати групування за
/// «документ × період» → <see cref="Два_методи_одного_документа_дають_один_рядок_з_двома_via"/> червоний.
/// </remarks>
public sealed class RegistryImpactHandlerTests
{
    private const int UserId = 9;
    private const int RegistryId = 601;
    private const string Code = "COMPONENT";
    private const int MyProject = 10;
    private const int OtherProject = 11;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryImpactStore _impact = Substitute.For<IRegistryImpactStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RegistryImpactHandlerTests()
    {
        _user.UserId.Returns(UserId);

        var def = new RegistryDef(
            EcrCode.Create(Code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Component" }),
            isTemporal: false);
        typeof(RegistryDef).BaseType!.GetProperty("Id")!.SetValue(def, RegistryId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(def);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Документ_чужого_проєкту_не_потрапляє_в_перелік()
    {
        Profile(scopedProject: MyProject);
        Rows(
            Row(1, MyProject, 202601, "HSE301"),
            Row(2, OtherProject, 202601, "HSE301"));

        var result = await Handler().HandleAsync(Code, default);

        Assert.Equal([1L], result.Items.Select(i => i.DocumentId));
        Assert.Equal(1, result.Total);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Два_методи_одного_документа_дають_один_рядок_з_двома_via()
    {
        Profile(scopedProject: MyProject);
        Rows(
            Row(1, MyProject, 202601, "ZED"),
            Row(1, MyProject, 202601, "HSE301"),
            Row(1, MyProject, 202602, "HSE301"));

        var result = await Handler().HandleAsync(Code, default);

        Assert.Equal(2, result.Total);
        Assert.Equal(["methodology:HSE301", "methodology:ZED"], result.Items[0].Via);
        Assert.Equal("Open", result.Items[0].PeriodState);
        Assert.Equal(["methodology:HSE301"], result.Items[1].Via);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Без_права_на_розрахунки_ніде_це_403_а_без_права_на_довідник_теж()
    {
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder().Permission("Registry.View").Build());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(Code, default));

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder().Permission("Calculation.View").Build());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(Code, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Невідомий_довідник_це_404()
    {
        Profile(scopedProject: MyProject);
        _registries.FindDefinitionAsync("NOPE", Arg.Any<CancellationToken>()).Returns((RegistryDef?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync("NOPE", default));

        Assert.Equal("ECR-REG-0404", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Вибірка_на_стелі_позначається_обрізаною()
    {
        Profile(scopedProject: MyProject);
        Rows([.. Enumerable.Range(1, IRegistryImpactStore.MaxRows).Select(i => Row(i, MyProject, 202601, "HSE301"))]);

        var result = await Handler().HandleAsync(Code, default);

        Assert.True(result.Truncated);
        Assert.Equal(IRegistryImpactStore.MaxRows, result.Total);
        Assert.Equal(RegistryImpactResponse.PageSize, result.Items.Count);
    }

    private GetRegistryImpactHandler Handler() => new(_registries, _impact, _access, _user);

    private void Rows(params RegistryImpactRow[] rows)
        => _impact.ListImpactedAsync(RegistryId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);

    private static RegistryImpactRow Row(long doc, int project, int period, string methodology)
        => new(doc, $"DOC{doc}", project, period, PeriodState.Open, methodology);

    /// <summary><c>Registry.View</c> глобально, <c>Calculation.View</c> — лише в одному проєкті (роль з областю).</summary>
    private void Profile(int scopedProject)
    {
        var basic = new AccessBuilder().Permission("Registry.View").Build();
        var scoped = new AccessProfile
        {
            CacheKey = basic.CacheKey,
            UserId = UserId,
            SecurityStamp = basic.SecurityStamp,
            Permissions = basic.Permissions,
            Grants = basic.Grants,
            Denies = basic.Denies,
            RoleIds = basic.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [scopedProject] = new(
                    new Dictionary<string, GrantLevel>(),
                    new HashSet<string>(),
                    new HashSet<int>(),
                    new HashSet<string> { "Calculation.View" }),
            },
        };
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(scoped);
    }
}
