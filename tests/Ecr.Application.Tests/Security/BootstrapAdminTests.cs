// tests/Ecr.Application.Tests/Security/BootstrapAdminTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>Життєвий цикл bootstrap-адміністратора (ФВ-6.18, D-97, D-115).</summary>
public sealed class BootstrapAdminTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Створюється_лише_якщо_запису_ще_немає()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відсутня_змінна_середовища_дає_попередження_а_не_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Доки_MustChangePassword_інші_запити_дають_ECR_PWD_0428()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_пароля_знімає_прапорець_і_крутить_SecurityStamp()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поява_доменного_адміністратора_вимикає_запис_але_не_видаляє()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_потрапляє_в_лог()
        => Assert.Fail("not implemented");
}
