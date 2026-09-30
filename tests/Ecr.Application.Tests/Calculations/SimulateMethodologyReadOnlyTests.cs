// tests/Ecr.Application.Tests/Calculations/SimulateMethodologyReadOnlyTests.cs
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
/// ФВ-13.5: симуляція методології нічого не пише. Обробник має лише два
/// порти — сховище методологій і модуль розрахунку; записом є будь-який виклик
/// сховища, що не починається з <c>Find</c>/<c>Get</c>/<c>Resolve</c>/<c>List</c>,
/// і будь-який виклик модуля, крім <c>ExecuteAsync</c> (чистий прогін).
/// </summary>
/// <remarks>
/// Мутаційний доказ (прогнано): у <c>SimulateMethodologyHandler.HandleAsync</c>
/// додати <c>await methodologies.ReplaceDependenciesAsync(...)</c> → тест
/// червоний із назвою недозволеного виклику.
/// </remarks>
public sealed class SimulateMethodologyReadOnlyTests
{
    private const int VersionId = 71;
    private const int OwnerId = 6;
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] ReadPrefixes = ["Find", "Get", "Resolve", "List"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.5")]
    public async Task Симуляція_не_викликає_жодного_записуючого_методу_портів()
    {
        var methodology = new Methodology(EcrCode.Create("HSE301"), Text("Flare"));
        SetId(methodology, OwnerId);
        var version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, 7, Now);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(version, VersionId);
        methodology.AddVersion(version);

        var store = Substitute.For<IMethodologyStore>();
        store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        var input = new CalculationInput(
            new MethodologyDescriptor(
                OwnerId, VersionId, "HSE301", "1.0.0", CalculationLevel.Configuration,
                NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
            DocumentId: 700, TableInstanceId: 500, PeriodKey: new PeriodKey(202601),
            SourceRowKey: "E-1", Arguments: []);
        store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
            new List<MethodologyTestCase>
            {
                new("golden", input, new Dictionary<string, decimal> { ["tons"] = 1m }, Tolerance: 0.000001m),
            });

        var module = Substitute.For<ICalculationModule>();
        module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
            .Returns(call => new CalculationOutput(
                call.Arg<CalculationInput>().DocumentId, call.Arg<CalculationInput>().SourceRowKey,
                [new CalculationOutputValue(VersionId, null, "tons", 1m, 8)], []));

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SimulateMethodologyHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var result = await new SimulateMethodologyHandler(module, store, access, user)
            .HandleAsync(VersionId, 202601, CancellationToken.None);

        // Контроль: симуляція справді відпрацювала, а не вийшла до обчислень.
        Assert.Equal(1m, result.Outputs["tons"]);
        Assert.Single(module.ReceivedCalls());

        var forbiddenStore = store.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Where(n => !ReadPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
        Assert.True(forbiddenStore.Count == 0, $"Симуляція викликала запис у сховищі: {string.Join(", ", forbiddenStore)}");

        var forbiddenModule = module.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Where(n => n != nameof(ICalculationModule.ExecuteAsync))
            .ToList();
        Assert.True(forbiddenModule.Count == 0, $"Симуляція викликала модуль не лише на читання: {string.Join(", ", forbiddenModule)}");
    }

    private static void SetId(Ecr.Domain.Abstractions.Entity<int> entity, int id)
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty(nameof(Ecr.Domain.Abstractions.Entity<int>.Id))!
            .SetValue(entity, id);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
