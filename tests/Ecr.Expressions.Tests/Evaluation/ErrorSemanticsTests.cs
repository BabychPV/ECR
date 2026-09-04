using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Помилки — **значення**, а не винятки: одна зіпсована комірка не має валити
/// перерахунок усієї таблиці (02b §6.4).
/// </summary>
public sealed class ErrorSemanticsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_помилку_а_не_виняток()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_null_дає_помилку_ділення_на_нуль()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помилка_поширюється_через_арифметику()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_перехоплює_помилку()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_НЕ_перехоплює_null_бо_це_не_помилка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірка_з_помилкою_зберігається_видимою_а_не_як_порожня()
        => Assert.Fail("not implemented");
}
