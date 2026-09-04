using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Заборонені виклики, які ламають конкретні властивості системи.
/// </summary>
public sealed class ForbiddenApiTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void DateTime_Now_і_UtcNow_не_використовуються_поза_реалізацією_IClock()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void IExternalDataSink_не_існує_в_жодній_збірці()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_коді_немає_DDL_окрім_міграцій_і_генератора_вьюх()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Публічні_асинхронні_методи_приймають_CancellationToken()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Немає_async_void_окрім_обробників_подій()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Секрети_не_читаються_з_конфігурації_напряму_а_лише_за_іменем()
        => Assert.Fail("not implemented");
}
