// tests/Ecr.Application.Tests/Security/SimulationTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Симуляція «очима користувача» — **лише читання** (ФВ-6.16a, D-96).
/// </summary>
/// <remarks>
/// Два тести тут захищають від різних видів провалу: перший — від того, що
/// симуляція стане способом щось зробити за іншого; третій — від того, що
/// профіль суб'єкта витече справжньому користувачеві через кеш.
/// </remarks>
public sealed class SimulationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Запис_під_симуляцією_відхиляється_навіть_із_Manage()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Сеанс_потрапляє_в_аудит_до_видачі_профілю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_симуляції_не_кешується_і_не_витікає_справжньому_користувачу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Автором_дій_лишається_той_хто_симулює()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Симуляція_самого_себе_дає_ECR_SIM_0422()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Завершити_можна_лише_власний_сеанс()
        => Assert.Fail("not implemented");
}
