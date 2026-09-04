// tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Темпоральність запису довідника (ФВ-8.5). Межі вікна **включні** з обох
/// боків — саме на цьому найлегше помилитися на день.
/// </summary>
public sealed class RegistryEntryTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("2026-01-01", true)]   // рівно ValidFrom
    [InlineData("2026-06-30", true)]   // рівно ValidTo
    [InlineData("2025-12-31", false)]  // день до
    [InlineData("2026-07-01", false)]  // день після
    public void IsValidOn_включає_обидві_межі(string date, bool expected)
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidFrom_означає_чинність_від_початку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidTo_означає_чинність_без_обмеження()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SoftDelete_не_видаляє_запис_фізично()
        => Assert.Fail("not implemented");
}
