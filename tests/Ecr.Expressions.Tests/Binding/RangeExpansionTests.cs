using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Діапазони розкриваються **при публікації**, а не в рантаймі.
/// </summary>
/// <remarks>
/// Тест <c>Зміна_Ordinal_після_публікації_не_змінює_результат</c> — головний
/// у цьому файлі. Якби діапазон обчислювався за <c>Ordinal</c> у рантаймі,
/// презентаційна правка мовчки змінювала б числа: даних не зіпсовано,
/// а результат інший — найгірший клас помилок.
/// </remarks>
public sealed class RangeExpansionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_розкривається_у_список_ключів_за_Ordinal()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зміна_Ordinal_після_публікації_НЕ_змінює_результат_формули()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Розкритий_діапазон_зберігається_із_порядковим_номером_кожного_рядка()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Неіснуюча_межа_діапазону_дає_ECR_TMPL_4222_при_публікації()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Видалені_рядки_не_потрапляють_у_розкритий_діапазон()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Для_динамічної_таблиці_діапазон_записується_предикатом_а_не_переліком()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкретний_RowKey_для_динамічної_таблиці_відхиляється()
        => Assert.Fail("not implemented");
}
