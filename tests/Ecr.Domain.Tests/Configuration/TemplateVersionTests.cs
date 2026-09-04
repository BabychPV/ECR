using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Інваріанти публікації — найважливіші в системі (ФВ-7.1, ФВ-7.2).
/// </summary>
public sealed class TemplateVersionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нова_версія_створюється_у_стані_Draft_з_нульовою_ревізією()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публікація_фіксує_автора_і_момент()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторна_публікація_відхиляється()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Опублікована_версія_структурно_заморожена()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Структурна_зміна_опублікованої_версії_кидає_ECR_TMPL_0409()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_кешу_містить_і_версію_і_ревізію_презентації()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Презентаційна_ревізія_приймається_лише_як_наступна_за_поточною()
        => Assert.Fail("not implemented");
}
