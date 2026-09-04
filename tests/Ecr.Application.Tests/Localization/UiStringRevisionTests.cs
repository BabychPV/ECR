// tests/Ecr.Application.Tests/Localization/UiStringRevisionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>Версія каталогу як `ETag` (ФВ-14.9c).</summary>
public sealed class UiStringRevisionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Будь_який_запис_інкрементує_Revision()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Збіг_If_None_Match_дає_304()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Два_одночасні_записи_дають_різні_версії()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Різні_області_мають_незалежні_ETag()
        => Assert.Fail("not implemented");
}
