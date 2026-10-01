// tests/Ecr.Application.Tests/Registries/RecalculateImpactedHandlerTests.cs
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
/// <c>POST /registries/{code}/recalculate-impacted</c> (RT-25): причина, набір документів, права й постановка
/// ОДНІЄЇ батьківської задачі.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати перевірку «документ є в impact» → <see cref="Документ_поза_переліком_зачеплених_відмовляється_422"/>
/// червоний; замінити <c>EnqueueCoalescedAsync</c> на <c>EnqueueExclusiveAsync</c> → <see cref="Ставиться_одна_задача_злиттям_без_витіснення"/>
/// червоний; прибрати <c>RequireIn</c> → <see cref="Явний_документ_чужого_проєкту_це_403"/> червоний.
/// S18: передати сховищу <c>null</c> замість видимих проєктів →
/// <see cref="Сховище_отримує_видимі_проєкти_а_не_лише_ті_де_є_право"/> червоний; прибрати <c>SeesDocumentsOf</c>
/// з постфільтра → <see cref="Невидимий_документ_відсутній_навіть_коли_сховище_його_віддало"/> червоний.
/// </remarks>
public sealed class RecalculateImpactedHandlerTests
{
    private const int UserId = 9;
    private const int RegistryId = 601;
    private const string Code = "COMPONENT";
    private const int MyProject = 10;
    private const int OtherProject = 11;
    private const int HiddenProject = 12;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryImpactStore _impact = Substitute.For<IRegistryImpactStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RecalculateImpactedHandlerTests()
    {
        _user.UserId.Returns(UserId);

        var def = new RegistryDef(
            EcrCode.Create(Code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Component" }),
            isTemporal: false);
        typeof(RegistryDef).BaseType!.GetProperty("Id")!.SetValue(def, RegistryId);
        _registries.FindDefinitionAsync(Code, Arg.Any<CancellationToken>()).Returns(def);

        _jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        Profile();
        Rows(Row(1, MyProject), Row(2, MyProject), Row(3, OtherProject));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Без_причини_це_422(string? reason)
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest(null, reason), default));

        Assert.Equal("err.ECR-REQ-0422.impactReasonRequired", ex.Details!["messageKey"]);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Без_documentIds_беруться_лише_документи_з_правом_у_проєкті()
    {
        var jobId = await Handler().HandleAsync(Code, new RecalculateImpactedRequest(null, "  правка складу "), default);

        Assert.Equal("job-1", jobId);
        var request = Enqueued();
        Assert.Equal([1L, 2L], request.DocumentIds);
        Assert.Equal(RegistryId, request.RegistryDefId);
        Assert.Equal("правка складу", request.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Документ_поза_переліком_зачеплених_відмовляється_422()
    {
        // 99 — або закритий період, або не залежить від довідника: сервер не розрізняє.
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest([1, 99], "r"), default));

        Assert.Equal("ECR-REG-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.impactDocumentNotAffected", ex.Details!["messageKey"]);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Явний_документ_чужого_проєкту_це_403()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest([1, 3], "r"), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Порожній_набір_це_422()
    {
        Rows();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest(null, "r"), default));

        Assert.Equal("err.ECR-REG-0422.impactNothingToRecalculate", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Ставиться_одна_задача_злиттям_без_витіснення()
    {
        await Handler().HandleAsync(Code, new RecalculateImpactedRequest([2, 1, 2], "r"), default);

        await _jobs.Received(1).EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
            RegistryImpactRecalculationTarget.Of(RegistryId, [1, 2]),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), UserId);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(default!, default, default);
        Assert.Equal([1L, 2L], Enqueued().DocumentIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Ціль_злиття_залежить_від_набору_а_не_від_порядку()
    {
        Assert.Equal(
            RegistryImpactRecalculationTarget.Of(5, [3, 1, 2]),
            RegistryImpactRecalculationTarget.Of(5, [1, 2, 3, 3]));
        Assert.NotEqual(
            RegistryImpactRecalculationTarget.Of(5, [1, 2]),
            RegistryImpactRecalculationTarget.Of(5, [1, 2, 3]));
        Assert.NotEqual(
            RegistryImpactRecalculationTarget.Of(5, [1]),
            RegistryImpactRecalculationTarget.Of(6, [1]));
    }

    private RegistryImpactRecalculationRequest Enqueued()
    {
        var call = _jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync));
        return (RegistryImpactRecalculationRequest)call.GetArguments()[1]!;
    }

    /// <summary>
    /// S18: документ проєкту, якого викликач не бачить, для нього відсутній — та сама 422 «не зачеплений»,
    /// що й на неіснуючий, а не 403, яка підтверджувала б, що документ є й залежить від довідника.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Явний_невидимий_документ_це_та_сама_422_що_й_неіснуючий()
    {
        GlobalRoleProfile();
        Rows(Row(1, MyProject), Row(3, HiddenProject));

        var hidden = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest([3], "r"), default));
        var missing = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest([99], "r"), default));

        Assert.Equal(missing.ErrorCode, hidden.ErrorCode);
        Assert.Equal(missing.Details!["messageKey"], hidden.Details!["messageKey"]);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Без_documentIds_невидимі_проєкти_не_беруться_навіть_з_правом_без_області()
    {
        GlobalRoleProfile();
        Rows(Row(1, MyProject), Row(3, HiddenProject));

        await Handler().HandleAsync(Code, new RecalculateImpactedRequest(null, "r"), default);

        Assert.Equal([1L], Enqueued().DocumentIds);
    }

    /// <summary>
    /// Видимий без права проєкт іде у вибірку (явний документ з нього — 403 з причиною), невидимий — ні.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Сховище_отримує_видимі_проєкти_а_не_лише_ті_де_є_право()
    {
        await Handler().HandleAsync(Code, new RecalculateImpactedRequest(null, "r"), default);

        await _impact.Received(1).ListImpactedAsync(
            RegistryId,
            Arg.Is<IReadOnlyCollection<int>?>(p => p != null && p.Order().SequenceEqual(new[] { MyProject, OtherProject })),
            IRegistryImpactStore.MaxRows,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Невидимий_документ_відсутній_навіть_коли_сховище_його_віддало()
    {
        GlobalRoleProfile();
        _impact.ListImpactedAsync(
                RegistryId, Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Row(1, MyProject), Row(3, HiddenProject)]);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Code, new RecalculateImpactedRequest([3], "r"), default));

        Assert.Equal("err.ECR-REG-0422.impactDocumentNotAffected", ex.Details!["messageKey"]);
    }

    private RecalculateImpactedHandler Handler() => new(_registries, _impact, _jobs, _access, _user);

    /// <summary>Сховище, що поводиться як справжнє: фільтр проєктів — до стелі.</summary>
    private void Rows(params RegistryImpactRow[] rows)
        => _impact.ListImpactedAsync(
                RegistryId, Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<RegistryImpactRow>>(
                [.. rows.Where(r => ci.ArgAt<IReadOnlyCollection<int>?>(1) is not { } p || p.Contains(r.ProjectId))]));

    /// <summary>Обидва права ролями без області, грант читання — лише на <see cref="MyProject"/>.</summary>
    private void GlobalRoleProfile()
        => _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }
                .Permission("Registry.View")
                .Permission("Calculation.Recalculate")
                .Grant(ResourceKind.Project, MyProject, GrantLevel.Read)
                .Build());

    private static RegistryImpactRow Row(long doc, int project)
        => new(doc, $"DOC{doc}", project, 202601, PeriodState.Open, "HSE301");

    /// <summary><c>Registry.View</c> глобально, <c>Calculation.Recalculate</c> — лише в одному проєкті.</summary>
    private void Profile()
    {
        var basic = new AccessBuilder()
            .Permission("Registry.View")
            .Grant(ResourceKind.Project, MyProject, GrantLevel.Read)
            .Grant(ResourceKind.Project, OtherProject, GrantLevel.Read)
            .Build();
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
                [MyProject] = new(
                    new Dictionary<string, GrantLevel>(),
                    new HashSet<string>(),
                    new HashSet<int>(),
                    new HashSet<string> { "Calculation.Recalculate" }),
            },
        };
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(scoped);
    }
}
