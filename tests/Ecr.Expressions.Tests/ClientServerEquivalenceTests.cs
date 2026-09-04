using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Тест еквівалентності клієнт/сервер.
/// </summary>
/// <remarks>
/// Клієнтський обчислювач (<c>formulajs</c>) — **лише підказка** під час
/// введення; збережене значення завжди рахує сервер (D-20). Але якщо підказка
/// систематично розходиться з результатом, користувач перестає їй вірити —
/// і саме тому набір спільних випадків має збігатися.
///
/// Реалізація: набір виразів і очікувань зберігається у спільному JSON, який
/// читають і цей тест, і vitest-тест на клієнті (див. `06e`).
/// </remarks>
public sealed class ClientServerEquivalenceTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спільний_набір_виразів_дає_однакові_результати_на_сервері()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Набір_покриває_усі_одинадцять_функцій_діалекту_шаблонів()
        => Assert.Fail("not implemented");
}
