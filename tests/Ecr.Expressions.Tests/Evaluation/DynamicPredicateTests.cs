// tests/Ecr.Expressions.Tests/Evaluation/DynamicPredicateTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Предикатні діапазони для динамічних таблиць. На відміну від звичайних
/// діапазонів, які матеріалізуються при публікації (ФВ-2.9), предикат
/// **обчислюється в рантаймі** — рядків на момент публікації ще немає.
/// </summary>
public sealed class DynamicPredicateTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_обчислюється_в_рантаймі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_на_порожній_таблиці_дає_порожню_множину()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_у_предикаті_відхиляється_при_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_періодне_посилання_у_предикаті_відхиляється()
        => Assert.Fail("not implemented");
}
