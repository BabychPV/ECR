// tests/Ecr.Application.Tests/Integration/SourceEventCatalogHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Каталог шаблонів подій і проба подій (HSE301 A6): права, відмови адаптера, вікно й стеля проби.
/// </summary>
/// <remarks>
/// ⚠ Обробники без бази: підроблені лише порти (адаптер, сховище джерел). Наскрізне — на реальній базі й HTTP —
/// в <c>SourceEventsApiTests</c> (Ecr.Api.Tests).
/// </remarks>
public sealed class SourceEventCatalogHandlersTests
{
    private const int Actor = 11;
    private const int SourceId = 3;
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly IExternalDataSource _adapter = Substitute.For<IExternalDataSource>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SourceEventCatalogHandlersTests()
    {
        _user.UserId.Returns(Actor);
        _clock.UtcNow.Returns(Now);
        Allow("Integration.Manage");

        var source = new DataSource(
            EcrCode.Create("PI_MAIN"), new LocalizedText(new Dictionary<string, string> { ["en"] = "PI" }),
            ExternalTransport.PiSqlClient, "Driver={PI SQL}", "DataSource.PI_MAIN");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, SourceId);
        _store.FindAsync(SourceId, Arg.Any<CancellationToken>()).Returns(source);
        _adapter.Transport.Returns(ExternalTransport.PiSqlClient);
    }

    [Theory]
    [InlineData("Integration.View", true)]
    [InlineData("Integration.Manage", true)]
    [InlineData("System.ViewHealth", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Каталог_шаблонів_доступний_View_і_Manage_і_відсортований(string permission, bool allowed)
    {
        Allow(permission);
        _adapter.DiscoverEventTemplatesAsync(SourceId, Arg.Any<CancellationToken>()).Returns(
        [
            new SourceEventTemplate("Zeta", []),
            new SourceEventTemplate("alpha", [new SourceEventAttributeDescriptor("Category", SourceEventAttributeScope.Event, null, "String")]),
        ]);

        if (!allowed)
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => Templates().HandleAsync(SourceId, default));
            await _adapter.DidNotReceiveWithAnyArgs().DiscoverEventTemplatesAsync(default, default);
            return;
        }

        var templates = await Templates().HandleAsync(SourceId, default);

        Assert.Equal(["alpha", "Zeta"], templates.Select(t => t.TemplateName));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Відмова_адаптера_запит_подій_не_налаштовано_проходить_як_є_а_збій_транспорту_це_503()
    {
        var notConfigured = IExternalDataSource.QueryKindNotSupported(
            IExternalDataSource.EventTemplateQueryKind, ExternalTransport.PiSqlClient);
        _adapter.DiscoverEventTemplatesAsync(SourceId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SourceEventTemplate>>(_ => throw notConfigured);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Templates().HandleAsync(SourceId, default));
        Assert.Equal("ECR-INT-0422", refused.ErrorCode);

        _adapter.DiscoverEventTemplatesAsync(SourceId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<SourceEventTemplate>>(_ => throw new HttpRequestException("connection refused"));

        var unavailable = await Assert.ThrowsAsync<BusinessRuleException>(() => Templates().HandleAsync(SourceId, default));
        Assert.Equal("ECR-INT-0503", unavailable.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.catalogUnavailable", unavailable.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Джерело_що_мовчить_дає_503_таймаут_а_не_висить()
    {
        _adapter.ReadEventsAsync(Arg.Any<SourceEventQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => Hang(call.Arg<CancellationToken>()));
        var handler = new ProbeSourceEventsHandler(
            _store, [_adapter], new SourceCatalogPolicy(TimeSpan.FromMilliseconds(200)), _access, _user, _clock);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(SourceId, new SourceEventProbeRequest("FlareEvent", null, null, null, null), default)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("err.ECR-INT-0503.probeTimeout", refused.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Проба_подій_лише_Manage_з_типовими_вікном_30_днів_і_стелею_20()
    {
        var events = new[]
        {
            new SourceEvent("E1", "FlareEvent", "Flaring", Now.AddDays(-2), Now.AddDays(-2).AddMinutes(15), null, null, null, []),
        };
        _adapter.ReadEventsAsync(Arg.Any<SourceEventQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SourceEventResult(events, true, null));

        var result = await Probe().HandleAsync(
            SourceId,
            new SourceEventProbeRequest(
                "  FlareEvent  ", null, null, [new SourceEventProbeAttribute(" Category ", SourceEventAttributeScope.PrimaryElement)], null),
            default);

        Assert.Equal(Now.AddDays(-30), result.FromUtc);
        Assert.Equal(Now, result.ToUtc);
        Assert.True(result.Truncated);
        Assert.Single(result.Events);

        var query = Assert.Single(_adapter.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IExternalDataSource.ReadEventsAsync))
            .Select(c => (SourceEventQuery)c.GetArguments()[0]!));
        Assert.Equal(("FlareEvent", SourceId, 20), (query.Template, query.DataSourceId, query.MaxEvents));
        Assert.Equal(new SourceEventAttributeRef("Category", SourceEventAttributeScope.PrimaryElement), Assert.Single(query.Attributes));

        // Право — лише Manage: View читає каталог, але не ходить у джерело за подіями.
        Allow("Integration.View");
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Probe().HandleAsync(SourceId, new SourceEventProbeRequest("FlareEvent", null, null, null, null), default));
    }

    [Theory]
    [InlineData(null, 30, 20)]
    [InlineData("  ", 30, 20)]
    [InlineData("FlareEvent", 93, 20)]
    [InlineData("FlareEvent", 30, 0)]
    [InlineData("FlareEvent", 30, 101)]
    [InlineData("FlareEvent", -1, 20)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Проба_подій_із_некоректним_запитом_422_і_адаптер_не_викликається(string? template, int windowDays, int max)
    {
        // windowDays < 0 — перевернуте вікно (початок після кінця).
        var from = Now.AddDays(-windowDays);
        var request = new SourceEventProbeRequest(template, from, Now, null, max);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Probe().HandleAsync(SourceId, request, default));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.probeEventsInvalid", refused.Details!["messageKey"]);
        await _adapter.DidNotReceiveWithAnyArgs().ReadEventsAsync(default!, default);
    }

    private ListEventTemplatesHandler Templates()
        => new(_store, [_adapter], new SourceCatalogPolicy(TimeSpan.FromMilliseconds(200)), _access, _user);

    private ProbeSourceEventsHandler Probe()
        => new(_store, [_adapter], new SourceCatalogPolicy(TimeSpan.FromSeconds(10)), _access, _user, _clock);

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());

    private static async Task<SourceEventResult> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);

        return new SourceEventResult([], false, null);
    }
}
