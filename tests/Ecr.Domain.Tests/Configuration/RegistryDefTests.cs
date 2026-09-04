// tests/Ecr.Domain.Tests/Configuration/RegistryDefTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Визначення довідника: версійність даних і зміна master-джерела.</summary>
public sealed class RegistryDefTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Зміна_запису_інкрементує_DataRevision()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void DataRevision_не_змінюється_від_читання()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Перемикання_SourceKind_змінює_master()
        => Assert.Fail("not implemented");
}
