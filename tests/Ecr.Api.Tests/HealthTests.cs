// tests/Ecr.Api.Tests/HealthTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Health-endpoint-и (tz/08 §8.5).</summary>
public sealed class HealthTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_режим_редакції()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_стан_RCSI()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_db_повідомляє_запас_партицій()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Health_live_не_звертається_до_БД()
        => Assert.Fail("not implemented");
}
