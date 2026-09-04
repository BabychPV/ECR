using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контракт помилок: код стабільний, клієнт розрізняє причини **за кодом**,
/// а не за текстом.
/// </summary>
public sealed class ErrorContractTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Помилка_повертається_у_форматі_problem_json()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Тіло_помилки_містить_код_і_ідентифікатор_кореляції()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Конфлікт_повертає_409_із_переліком_розбіжностей()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відмова_в_доступі_повертає_403_із_ПРИЧИНОЮ_у_розширеннях()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Внутрішня_помилка_не_розкриває_стек_і_текст_винятку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ідентифікатор_кореляції_повертається_у_заголовку_відповіді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Усі_коди_з_каталогу_мають_унікальні_значення()
        => Assert.Fail("not implemented");
}
