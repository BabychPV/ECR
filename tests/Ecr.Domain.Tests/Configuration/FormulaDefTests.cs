// tests/Ecr.Domain.Tests/Configuration/FormulaDefTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Порядок обчислення формул — **обчислюваний**, не введений (ФВ-9.4).
/// Дозволити задати його руками означало б, що додана формула тихо зміщує
/// решту.
/// </summary>
public sealed class FormulaDefTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void EvaluationOrder_встановлюється_лише_при_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спроба_задати_EvaluationOrder_ззовні_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Токен_поза_оголошеним_списком_аргументів_дає_помилку_публікації()
        => Assert.Fail("not implemented");
}
