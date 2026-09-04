// tests/Ecr.Expressions.Tests/Binding/TypeCheckerTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Типізація при публікації. Мета — щоб несумісність типів була **помилкою
/// публікації**, а не дивним числом у звіті через місяць.
/// </summary>
public sealed class TypeCheckerTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_плюс_текст_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різниця_дат_дає_число()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Boolean_в_арифметиці_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_числа_з_текстом_дає_помилку_публікації()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Несумісні_одиниці_без_CONVERT_дають_ECR_TMPL_4223()
        => Assert.Fail("not implemented");
}
