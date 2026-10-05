using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-11: симуляція порівнює з версією, чинною на КІНЕЦЬ періоду за
/// календарем проєкту, а не на кінець «місяця» з номером періоду.
/// </summary>
/// <remarks>
/// ⛔ Що було. Ключ 202602 квартального проєкту (II квартал) ставав 28 лютого:
/// базою diff була версія, чинна в лютому, хоча продуктив рахує II квартал
/// версією, чинною на 30 червня.
/// </remarks>
public sealed class SimulatePeriodBoundsTests
{
    private const int OwnerId = 6;
    private const int JanuaryVersionId = 81;
    private const int AprilVersionId = 82;
    private const int DraftVersionId = 83;
    private const long DocumentId = 700;
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Квартальний_період_порівнюється_з_версією_на_кінець_кварталу()
    {
        var (module, handler) = Stand(new PeriodBounds(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)));

        var result = await handler.HandleAsync(DraftVersionId, 202602, CancellationToken.None);

        await module.Received().ExecuteAsync(
            Arg.Is<CalculationInput>(i => i.Methodology.MethodologyVersionId == AprilVersionId), Arg.Any<CancellationToken>());
        await module.DidNotReceive().ExecuteAsync(
            Arg.Is<CalculationInput>(i => i.Methodology.MethodologyVersionId == JanuaryVersionId), Arg.Any<CancellationToken>());
        Assert.Equal(1m, result.DiffWithPublished["tons"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Без_меж_періоду_ключ_читається_як_місяць()
    {
        var (module, handler) = Stand(bounds: null);

        await handler.HandleAsync(DraftVersionId, 202602, CancellationToken.None);

        await module.Received().ExecuteAsync(
            Arg.Is<CalculationInput>(i => i.Methodology.MethodologyVersionId == JanuaryVersionId), Arg.Any<CancellationToken>());
    }

    private static (ICalculationModule Module, SimulateMethodologyHandler Handler) Stand(PeriodBounds? bounds)
    {
        var methodology = new Methodology(EcrCode.Create("HSE301"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Flare" }));
        SetId(methodology, OwnerId);
        var january = Version(methodology, "1.0.0", JanuaryVersionId);
        var april = Version(methodology, "2.0.0", AprilVersionId);
        Version(methodology, "3.0.0", DraftVersionId);
        methodology.PublishVersion(january, 9, "initial", new DateOnly(2026, 1, 1), testsPassed: true, Now);
        methodology.PublishVersion(april, 9, "update", new DateOnly(2026, 4, 1), testsPassed: true, Now);

        var store = Substitute.For<IMethodologyStore>();
        store.FindByVersionAsync(DraftVersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        var input = new CalculationInput(
            new MethodologyDescriptor(
                OwnerId, DraftVersionId, "HSE301", "3.0.0", CalculationLevel.Configuration,
                NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
            DocumentId, TableInstanceId: 500, PeriodKey: new PeriodKey(202602), SourceRowKey: "E-1", Arguments: []);
        store.GetTestCasesAsync(DraftVersionId, Arg.Any<CancellationToken>()).Returns(
            new List<MethodologyTestCase> { new("golden", input, new Dictionary<string, decimal> { ["tons"] = 3m }, 0.000001m) });

        // Чернетка дає 3, квітнева версія 2, січнева 1: diff = 1 лише проти квітневої.
        var module = Substitute.For<ICalculationModule>();
        module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var versionId = call.Arg<CalculationInput>().Methodology.MethodologyVersionId;
                var tons = versionId switch { DraftVersionId => 3m, AprilVersionId => 2m, _ => 1m };
                return new CalculationOutput(DocumentId, "E-1", [new CalculationOutputValue(versionId, null, "tons", tons, 8)], []);
            });

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202602, Arg.Any<CancellationToken>()).Returns(bounds);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SimulateMethodologyHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return (module, new SimulateMethodologyHandler(module, store, access, user, periods));
    }

    private static MethodologyVersion Version(Methodology methodology, string number, int id)
    {
        var version = new MethodologyVersion(OwnerId, number, CalculationLevel.Configuration, 7, Now);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(version, id);
        methodology.AddVersion(version);
        return version;
    }

    private static void SetId<T>(T entity, int id)
        where T : Ecr.Domain.Abstractions.Entity<int>
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
