using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// T3-07: один код (<c>ECR-TMPL-0409</c>) покриває різні стани, і спільний заголовок
/// «The template version is published» брехав для дубліката коду зв'язку (версія — чернетка).
/// Кидок може мати власний заголовок: ключ <c>&lt;messageKey&gt;.title</c> бере верх над
/// <c>err.&lt;код&gt;</c>; без нього — заголовок по коду, як і раніше.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати гілку <c>messageKey + ".title"</c> у
/// <c>LocalizedTitleAsync</c> — перший тест червоний.
/// </remarks>
public sealed class ErrorTitleByMessageKeyTests
{
    private static FakeUiStringCatalog Catalog() => new FakeUiStringCatalog()
        .Add("en", "err.ECR-TMPL-0409", "The template version is published", UiStringScope.Public)
        .Add("en", "err.ECR-TMPL-0409.relationCodeTaken.title", "Relation code is already in use", UiStringScope.Public);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "T3-07")]
    public async Task Дублікат_коду_зв_язку_має_власний_заголовок_а_не_заголовок_коду()
    {
        var problem = await ProblemAsync(Catalog(), () => throw new ConcurrencyConflictException(
            "ECR-TMPL-0409",
            "Зв'язок із кодом «CHK1» уже існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0409.relationCodeTaken",
                ["relationCode"] = "CHK1",
            }));

        Assert.Equal("Relation code is already in use", problem.GetProperty("title").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "T3-07")]
    public async Task Інший_messageKey_без_власного_заголовка_лишає_заголовок_коду()
    {
        var problem = await ProblemAsync(Catalog(), () => throw new ConcurrencyConflictException(
            "ECR-TMPL-0409",
            "Версію опубліковано.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-TMPL-0409.structurallyFrozen" }));

        Assert.Equal("The template version is published", problem.GetProperty("title").GetString());
    }

    private static async Task<JsonElement> ProblemAsync(IUiStringCatalog catalog, Func<Task> throwing)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns("en");

        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton(currentUser);

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throwing(), NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
