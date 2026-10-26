// tests/Ecr.Application.Tests/Calculations/SimulateMethodologyNoDocumentValuesTests.cs
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
/// AN-54 (безпека): симуляція методології (<c>POST /methodologies/{id}/simulate</c>) НЕ читає значень комірок
/// документа — а отже не може віддати у <c>Trace</c>/<c>Outputs</c> значення зі схованої (Deny) колонки.
/// </summary>
/// <remarks>
/// Інваріант тримається двома пінами. (1) Склад портів обробника: жодного, що читає комірки
/// (<see cref="ICellStore"/>) чи документи, — новий порт змусить свідомо переглянути приховування
/// (<c>EditRules</c>/<c>AccessDecisionService</c>) ДО того, як значення документа потраплять у відповідь.
/// (2) Поведінка: у модуль розрахунку йдуть аргументи лише з тест-кейсу методології, а з документа
/// читаються тільки межі періоду (<see cref="IPeriodStore.FindPeriodBoundsAsync"/>).
/// ⚠ Ендпоінта <c>simulate-trace</c> у коді немає; найближчий — <c>/simulate</c> (Trace у його відповіді).
/// Мутаційний доказ (прогнано): додати в обробник необов'язковий порт <see cref="ICellStore"/> — червоніє
/// перший тест; підмінити <c>Arguments</c> у <c>testCase.Input with {…}</c> або кликати інший метод
/// <see cref="IPeriodStore"/> — червоніє другий.
/// </remarks>
public sealed class SimulateMethodologyNoDocumentValuesTests
{
    private const int VersionId = 71;
    private const int OwnerId = 6;
    private const long DocumentId = 700;
    private const decimal DocumentCellSentinel = 987654.321m;
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.5")]
    public void Обробник_симуляції_не_має_порту_читання_комірок_чи_документа()
    {
        var parameterTypes = typeof(SimulateMethodologyHandler).GetConstructors()
            .Single().GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Equal(
            new HashSet<Type>
            {
                typeof(ICalculationModule), typeof(IMethodologyStore), typeof(IAccessDecisionService),
                typeof(ICurrentUser), typeof(IPeriodStore),
            },
            parameterTypes);
        Assert.DoesNotContain(typeof(ICellStore), parameterTypes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.5")]
    public async Task Значення_комірок_документа_не_потрапляють_ні_у_вхід_модуля_ні_у_трейс_ні_у_результат()
    {
        var methodology = new Methodology(EcrCode.Create("HSE301"), Text("Flare"));
        SetId(methodology, OwnerId);
        var version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, 7, Now);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(version, VersionId);
        methodology.AddVersion(version);

        var testCaseInput = new CalculationInput(
            new MethodologyDescriptor(
                OwnerId, VersionId, "HSE301", "1.0.0", CalculationLevel.Configuration,
                NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
            DocumentId: DocumentId, TableInstanceId: 500, PeriodKey: new PeriodKey(202601),
            SourceRowKey: "E-1", Arguments: [new CalculationArgument("fuel", 2m, null, null)]);

        var store = Substitute.For<IMethodologyStore>();
        store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(
            new List<MethodologyTestCase>
            {
                new("golden", testCaseInput, new Dictionary<string, decimal> { ["tons"] = 2m }, Tolerance: 0.000001m),
            });

        // Модуль-«відлунь»: видає лише те, що отримав у аргументах (як рахує справжній — з входу, не з документа).
        var received = new List<CalculationInput>();
        var module = Substitute.For<ICalculationModule>();
        module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var input = call.Arg<CalculationInput>();
                received.Add(input);
                var arg = input.Arguments.Single();
                return new CalculationOutput(
                    input.DocumentId, input.SourceRowKey,
                    [new CalculationOutputValue(VersionId, null, "tons", arg.Value!.Value, 8)],
                    [new CalculationTraceStep(1, "tons", null, arg.Value, null)]);
            });

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SimulateMethodologyHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var result = await new SimulateMethodologyHandler(module, store, access, user, periods)
            .HandleAsync(VersionId, 202601, CancellationToken.None);

        // Контроль: симуляція відпрацювала й дала число з тест-кейсу.
        Assert.Equal(2m, result.Outputs["tons"]);
        Assert.NotEmpty(received);

        // З документа читаються ЛИШЕ межі періоду.
        var periodCalls = periods.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Distinct().ToList();
        Assert.Equal([nameof(IPeriodStore.FindPeriodBoundsAsync)], periodCalls);

        // У модуль іде тест-кейс як є (аргументи, адреса), відрізняється лише дескриптор методології.
        Assert.All(received, input => Assert.Equal(testCaseInput with { Methodology = input.Methodology }, input));

        // Значення комірки схованої колонки не з'являється ніде у відповіді.
        var sentinel = DocumentCellSentinel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain(result.Outputs.Values, v => v == DocumentCellSentinel);
        Assert.DoesNotContain(result.DiffWithPublished.Values, v => v == DocumentCellSentinel);
        Assert.DoesNotContain(result.Trace, line => line.Contains(sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.TestCases, v => v.ToString()!.Contains(sentinel, StringComparison.Ordinal));
    }

    private static void SetId(Ecr.Domain.Abstractions.Entity<int> entity, int id)
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty(nameof(Ecr.Domain.Abstractions.Entity<int>.Id))!
            .SetValue(entity, id);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
