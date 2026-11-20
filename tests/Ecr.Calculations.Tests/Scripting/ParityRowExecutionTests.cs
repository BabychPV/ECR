using Ecr.Application.Ports;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// Як <see cref="ParityComparer"/> зводить виконання модуля в порівнювані дані (MS, план §8.4): виняток шляху — це
/// розбіжність з кодом і ключем, відхилення рядка правилом категорії — окремий прапорець, відсутній вхід — відсутній
/// результат (а не збіг), а <c>StepError</c> і відсутні аргументи доходять до порівняння.
/// </summary>
public sealed class ParityRowExecutionTests
{
    private static readonly MethodologyDescriptor Descriptor = new(
        1, 10, "M1", "V1", CalculationLevel.Configuration, NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off);

    private sealed class FakeModule(Func<CalculationInput, CalculationOutput> run) : ICalculationModule
    {
        public string Code => "fake";

        public CalculationLevel Level => CalculationLevel.Configuration;

        public bool CanHandle(MethodologyDescriptor methodology) => true;

        public Task<CalculationBindingContext> PrepareAsync(
            MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
            => Task.FromResult(run(input));

        public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
            => Task.FromResult(run(input));
    }

    private static CalculationInput Input(string rowKey)
        => new(Descriptor, 5, 900, new PeriodKey(209901), rowKey, []);

    private static Dictionary<string, CalculationInput> Inputs(params string[] rowKeys)
        => rowKeys.ToDictionary(k => k, Input, StringComparer.Ordinal);

    private static Task<Dictionary<string, ParityRowOutcome>> RunAsync(
        FakeModule module, string[] rowKeys, Dictionary<string, CalculationInput>? inputs = null)
        => ParityComparer.ExecuteRowsAsync(rowKeys, inputs ?? Inputs(rowKeys), module, null!, false, CancellationToken.None);

    private static CalculationOutput Output(string rowKey, params CalculationOutputValue[] values)
        => new(5, rowKey, values, []);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Значення_відсутні_аргументи_і_StepError_доходять_до_порівняння()
    {
        var module = new FakeModule(input => Output(
            input.SourceRowKey!, new CalculationOutputValue(10, 7, "Em", 1.5m, 3, CalculationResultKind.Output))
            with
            {
                MissingArguments = ["Qty"],
                StepError = new CalculationStepError("Em", "#VALUE", 1),
            });

        var outcome = (await RunAsync(module, ["R1"]))["R1"];

        var value = Assert.Single(outcome.Values);
        Assert.Equal(("Em", 7, 1.5m, 3), (value.OutputCode, value.SubstanceEntryId, value.Value, value.UnitId));
        Assert.Equal(["Qty"], outcome.MissingArguments);
        Assert.True(outcome.HasStepError);
        Assert.False(outcome.Rejected);
        Assert.Null(outcome.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Виняток_модуля_лишає_код_і_ключ_а_не_порожній_результат()
    {
        var module = new FakeModule(_ => throw new DomainException(
            "ECR-CALC-0500",
            "boom",
            new Dictionary<string, object?> { ["messageKey"] = "err.boom" }));

        var outcome = (await RunAsync(module, ["R1"]))["R1"];

        Assert.Equal("ECR-CALC-0500: err.boom", outcome.Error);
        Assert.False(outcome.Rejected);
        Assert.Empty(outcome.Values);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відхилення_рядка_правилом_категорії_це_Rejected_а_не_Error()
    {
        var module = new FakeModule(_ => throw new DomainException(
            "ECR-CALC-0422",
            "category",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.categoryRuleFailed" }));

        var outcome = (await RunAsync(module, ["R1"]))["R1"];

        Assert.True(outcome.Rejected);
        Assert.Null(outcome.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Інша_помилка_з_кодом_0422_не_вважається_відхиленням_рядка()
    {
        var module = new FakeModule(_ => throw new DomainException(
            "ECR-CALC-0422",
            "other",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.somethingElse" }));

        var outcome = (await RunAsync(module, ["R1"]))["R1"];

        Assert.False(outcome.Rejected);
        Assert.Equal("ECR-CALC-0422: err.ECR-CALC-0422.somethingElse", outcome.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_входу_не_дає_результату_і_не_стає_порожнім_збігом()
    {
        var module = new FakeModule(input => Output(input.SourceRowKey!));

        var outcomes = await RunAsync(module, ["R1", "R2"], Inputs("R1"));

        Assert.True(outcomes.ContainsKey("R1"));
        Assert.False(outcomes.ContainsKey("R2"));
    }
}
