// tests/Ecr.Application.Tests/Validation/ValidationEngineTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// Валідація. **Блокує збереження лише комірковий `Error`** (D-90): заборона
/// зберегти проміжний стан зробила б роботу з великою таблицею неможливою.
/// </summary>
public sealed class ValidationEngineTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірковий_Error_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Error_рівня_документа_блокує_Submit_але_не_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зламане_правило_дає_Warning_про_правило_а_не_Error_даних()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_не_залежить_від_порядку_правил()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Результат_валідації_переживає_перезавантаження()
        => Assert.Fail("not implemented");
}
