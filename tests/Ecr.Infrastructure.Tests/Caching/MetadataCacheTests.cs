using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна
/// правка створює новий ключ, а не псує старий (D-16).
/// </summary>
public sealed class MetadataCacheTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторне_читання_не_звертається_до_БД()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Після_презентаційної_правки_повертається_новий_знімок()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Старий_знімок_лишається_валідним_для_старого_ключа()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Знімок_містить_індекси_колонок_і_рядків_для_швидкого_доступу()
        => Assert.Fail("not implemented");
}
