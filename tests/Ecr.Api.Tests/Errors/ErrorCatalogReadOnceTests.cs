// tests/Ecr.Api.Tests/Errors/ErrorCatalogReadOnceTests.cs
using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// X5-02: обробник помилок читає каталог рядків не більше ОДНОГО разу на відповідь, а на
/// тимчасовому збої БД — жодного.
/// </summary>
/// <remarks>
/// ⛔ Було: заголовок і подробиця читали каталог окремо — два нові з'єднання й два
/// <c>SELECT Revision</c> на кожну відмову (кеш стоїть ПІСЛЯ них). На 503 <c>databaseBusy</c>
/// каталог живе в тій самій недоступній БД: кожне читання чекало <c>Connect Timeout</c>
/// (до ~2×15 с), брало з'єднання з вичерпаного пулу і все одно давало код заголовком та
/// українське речення подробицею для en/ru/kz.
/// </remarks>
public sealed class ErrorCatalogReadOnceTests
{
    private const string Code = "ECR-USR-0409";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "X5-02")]
    public async Task Звичайна_відмова_з_ключем_читає_каталог_один_раз()
    {
        var strings = new FakeUiStringCatalog()
            .Add("en", "err." + Code, "User name already in use", UiStringScope.Private)
            .Add("en", "err.ECR-USR-0409.userNameTaken", "A user named \"{userName}\" already exists.", UiStringScope.Private);
        var catalog = Counting(strings);

        var problem = await ProblemAsync(
            catalog,
            new BusinessRuleException(Code, "Користувач з іменем «ivan» уже існує.", new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-USR-0409.userNameTaken",
                ["userName"] = "ivan",
            }));

        Assert.Equal("User name already in use", problem.GetProperty("title").GetString());
        Assert.Equal("A user named \"ivan\" already exists.", problem.GetProperty("detail").GetString());

        // ⛔ Мутація: повернути окреме читання в LocalizedDetailAsync — два виклики.
        await catalog.Received(1).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "X5-02")]
    public async Task Тимчасовий_збій_БД_не_звертається_до_каталогу_і_не_шле_українського_речення()
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<UiStringCatalog>>(_ => throw new InvalidOperationException("Timeout expired."));

        var (problem, context) = await RunAsync(catalog, new RetryLimitExceededException("retries exhausted"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter.ToString());

        // ⛔ Мутація: прибрати `skip` у CatalogOnce — один-два виклики каталогу.
        await catalog.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Ключ лишається — за ним клієнт бере текст мовою користувача зі свого каталогу.
        Assert.Equal("ECR-SYS-0503", problem.GetProperty("title").GetString());
        Assert.Equal("err.ECR-SYS-0503.databaseBusy", problem.GetProperty("messageKey").GetString());
        Assert.True(
            !problem.TryGetProperty("detail", out var detail) || detail.ValueKind == JsonValueKind.Null,
            "Подробиця 503 databaseBusy без каталогу — лише ключем, не українським реченням.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "X5-02")]
    public async Task Збій_читання_каталогу_не_підміняє_подробицю_українським_реченням()
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<UiStringCatalog>>(_ => throw new InvalidOperationException("база недоступна"));

        var problem = await ProblemAsync(
            catalog,
            new BusinessRuleException(Code, "Користувач з іменем «ivan» уже існує.", new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-USR-0409.userNameTaken",
                ["userName"] = "ivan",
            }));

        // Заголовок — код, як і раніше (LocalizedErrorTitleTests); подробиці немає, ключ є.
        Assert.Equal(Code, problem.GetProperty("title").GetString());
        Assert.Equal("err.ECR-USR-0409.userNameTaken", problem.GetProperty("messageKey").GetString());
        Assert.True(
            !problem.TryGetProperty("detail", out var detail) || detail.ValueKind == JsonValueKind.Null,
            "messageKey каже клієнтові, що подробиця каталожна: сире українське речення він показав би як є.");

        // Один невдалий похід, а не два.
        await catalog.Received(1).GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static IUiStringCatalog Counting(FakeUiStringCatalog inner)
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => inner.GetAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        return catalog;
    }

    private static async Task<JsonElement> ProblemAsync(IUiStringCatalog catalog, Exception exception)
        => (await RunAsync(catalog, exception)).Problem;

    private static async Task<(JsonElement Problem, HttpContext Context)> RunAsync(
        IUiStringCatalog catalog, Exception exception)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns("en");

        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton(currentUser);

        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception, NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return (JsonDocument.Parse(body).RootElement.Clone(), context);
    }
}
