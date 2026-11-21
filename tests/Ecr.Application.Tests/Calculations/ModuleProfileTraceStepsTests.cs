// tests/Ecr.Application.Tests/Calculations/ModuleProfileTraceStepsTests.cs
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// ✎ D-565 (PERF-03): факт прогону «чиї кроки трейсу записано» у <c>ModulesProfileJson</c> (ключ <c>traceSteps</c>) —
/// пишеться, зводиться між проходами й читається назад; методологія в обох множинах — «не записано».
/// </summary>
/// <remarks>
/// ⛔ Мутації: не писати ключ — червоний перший; у <c>Merge</c> не зводити факт — червоний другий; лишати методологію в
/// «записано», коли вона ж у «не записано», — червоний другий.
/// </remarks>
public sealed class ModuleProfileTraceStepsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-565")]
    public void Факт_пишеться_й_читається_назад()
    {
        var profile = new ModuleProfile();
        profile.RecordTraceSteps([7], [3, 5]);

        var fact = ModuleProfile.ReadTraceSteps(profile.ToJson());

        Assert.NotNull(fact);
        Assert.True(fact.StepsRecorded(7));
        Assert.False(fact.StepsRecorded(3));
        Assert.False(fact.StepsRecorded(5));
        Assert.False(fact.StepsRecorded(99));
        Assert.Contains("\"traceSteps\":{\"recorded\":[7],\"notRecorded\":[3,5]}", profile.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-565")]
    public void Зведення_проходів_обережне_методологія_в_обох_множинах_не_записана()
    {
        var first = new ModuleProfile();
        first.RecordTraceSteps([1, 2], []);
        var second = new ModuleProfile();
        second.RecordTraceSteps([3], [2]);

        var merged = new ModuleProfile();
        merged.Merge(first);
        merged.Merge(second);
        var fact = ModuleProfile.ReadTraceSteps(merged.ToJson())!;

        Assert.Equal([1, 3], fact.Recorded.Order());
        Assert.Equal([2], fact.NotRecorded.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""[{"module":"Generic"}]""")]
    [InlineData("""{"modules":[]}""")]
    [InlineData("{not json")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-565")]
    public void Без_факту_прогін_до_D565_або_зіпсований_профіль_null(string? json)
    {
        Assert.Null(ModuleProfile.ReadTraceSteps(json));
        Assert.DoesNotContain("traceSteps", new ModuleProfile().ToJson(), StringComparison.Ordinal);
    }
}
