using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Наскрізні конвенції API.</summary>
public sealed class ApiConventionTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Списковий_ендпоінт_повертає_сторінку_а_не_весь_набір()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Розмір_сторінки_понад_максимум_відхиляється_400()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Курсор_наступної_сторінки_повертає_наступні_елементи_без_пропусків()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Довга_операція_повертає_202_із_ідентифікатором_задачі()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Числа_передаються_рядком_щоб_не_втратити_точність()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дати_передаються_в_UTC_за_ISO_8601()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Специфікація_OpenAPI_генерується_і_валідна()
        => Assert.Fail("not implemented");
}
