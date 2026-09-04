using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Перевірка одиниць при публікації — **головна цінність механізму одиниць**:
/// помилка ловиться до продуктиву, а не на звірці через місяць (ФВ-16.7).
/// </summary>
public sealed class UnitCheckerTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_величин_у_різних_одиницях_відхиляється_з_ECR_TMPL_4223()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Додавання_після_явного_CONVERT_проходить()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Ділення_маси_на_час_дає_похідну_одиницю_масової_витрати()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результат_несумісний_з_оголошеною_одиницею_колонки_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Агрегація_колонки_з_одиницею_на_рядок_без_приведення_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Неявної_конверсії_не_відбувається_навіть_коли_вона_очевидна()
        => Assert.Fail("not implemented");
}
