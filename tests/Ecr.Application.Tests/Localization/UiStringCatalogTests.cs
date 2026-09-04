// tests/Ecr.Application.Tests/Localization/UiStringCatalogTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// Каталог рядків інтерфейсу (ФВ-14.9). **Порожнеча не повертається ніколи**:
/// одна забута локалізація не має ламати екран.
/// </summary>
public sealed class UiStringCatalogTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відсутній_переклад_підмінюється_мовою_за_замовчуванням()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Ключ_якого_немає_ніде_повертається_як_сам_ключ()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Тексти_помилок_резолвляться_з_того_самого_каталогу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Публічна_область_не_містить_адміністративних_підписів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Анонімний_запит_приватної_області_відхиляється()
        => Assert.Fail("not implemented");
}
