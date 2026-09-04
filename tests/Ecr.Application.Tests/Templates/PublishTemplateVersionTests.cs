using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Дванадцять перевірок публікації (02b §12).
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється **з переліком усіх
/// проблем**. Зупинка на першій помилці змусила б користувача виправляти їх
/// по одній, повторюючи публікацію десятки разів.
/// </remarks>
public sealed class PublishTemplateVersionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Коректна_версія_публікується()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Синтаксична_помилка_у_виразі_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_неіснуючу_колонку_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_у_графі_відхиляє_публікацію_із_шляхом_циклу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Несумісні_одиниці_без_CONVERT_відхиляють_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відповідь_містить_УСІ_проблеми_а_не_лише_першу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Публікація_зберігає_розкриті_діапазони_і_порядок_обчислення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публікація_записує_подію_в_аудит()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Невдала_публікація_не_лишає_часткових_змін()
        => Assert.Fail("not implemented");
}
