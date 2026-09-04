// tests/Ecr.Infrastructure.Tests/Security/PasswordHasherTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>Хешування паролів локальних облікових записів (ФВ-6.5).</summary>
public sealed class PasswordHasherTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Однаковий_пароль_дає_різні_хеші_через_різні_солі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перевірка_виконується_за_сталий_час()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void NeedsRehash_істинний_після_зміни_параметрів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пароль_не_потрапляє_в_текст_винятку()
        => Assert.Fail("not implemented");
}
