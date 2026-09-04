// tests/Ecr.Domain.Tests/Calculations/MethodologyVersionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Публікація версії методології — найнебезпечніша операція в системі, бо
/// змінює вже подані числа. Тому правило чотирьох очей перевіряється
/// **системно** (D-40), а не інструкцією.
/// </summary>
public sealed class MethodologyVersionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_автором_останньої_правки_відхиляється()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_ChangeReason_неможлива()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_зеленого_тесту_неможлива()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вікна_дії_опублікованих_версій_не_перетинаються()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Опублікована_версія_не_редагується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void NumericMode_за_замовчуванням_Legacy()
        => Assert.Fail("not implemented");
}
