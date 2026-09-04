using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Презентаційна правка «на льоту» — те, заради чого існує
/// <c>PresentationRevision</c> (ФВ-7.2).
/// </summary>
public sealed class PatchPresentationTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_підпису_опублікованої_версії_проходить()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_типу_даних_відхиляється_з_ECR_TMPL_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Змішаний_патч_із_однією_структурною_зміною_відхиляється_повністю()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Успішний_патч_інкрементує_ревізію_і_записує_аудит()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_Ordinal_не_впливає_на_результати_формул_із_діапазонами()
        => Assert.Fail("not implemented");
}
