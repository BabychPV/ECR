// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneRemapMatchJsonTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>D1: чиста логіка переписування ключів предиката прив'язки при клоні версії шаблону.</summary>
public sealed class TemplateVersionCloneRemapMatchJsonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "D1")]
    public void Нечислові_ключі_і_порожній_предикат_лишаються_як_є_числові_переписуються_чужий_дає_null()
    {
        var map = new Dictionary<int, int> { [1] = 10 };

        Assert.Equal("{}", TemplateVersionStore.RemapMatchJson("{}", map));
        Assert.Equal("{\"Land_Status\":\"Running\"}", TemplateVersionStore.RemapMatchJson("{\"Land_Status\":\"Running\"}", map));
        Assert.Equal("{\"10\":\"a\"}", TemplateVersionStore.RemapMatchJson("{\"1\":\"a\"}", map));
        Assert.Null(TemplateVersionStore.RemapMatchJson("{\"1\":\"a\",\"2\":\"b\"}", map));
    }
}
