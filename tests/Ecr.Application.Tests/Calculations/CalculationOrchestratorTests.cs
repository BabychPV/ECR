// tests/Ecr.Application.Tests/Calculations/CalculationOrchestratorTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Оркестрація прогону. Профіль по модулях заповнюється **завжди**: без нього
/// невідомо, звідки брати різницю між 20 і 10 хвилинами (ПРД-13, `J-1`).
/// </summary>
public sealed class CalculationOrchestratorTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Незалежні_гілки_графа_рахуються_паралельно()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Профіль_по_модулях_заповнюється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Закритий_період_не_перераховується_автоматично()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Поданий_зріз_не_перераховується_взагалі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void IsCurrent_перемикається_однією_транзакцією()
        => Assert.Fail("not implemented");
}
