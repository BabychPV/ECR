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
/// Узагальнений шлях <c>ExceptionHandlingMiddleware.LocalizedDetailAsync</c>
/// (`Q-30x`): БУДЬ-ЯКИЙ виняток із <c>Details["messageKey"]</c> резолвиться
/// каталогом, а не лише жорстко зашитий `ECR-AUTH-0403`
/// (`RoleAndUserHandlersLocalizedErrorTests`, Q-242/Q-300).
/// </summary>
/// <remarks>
/// ⛔ Основний споживач — <c>Ecr.Domain.ValueObjects.EcrCode.Create</c>: цей
/// шар НІКОЛИ не матиме доступу до <c>IUiStringCatalog</c> (Domain нижче за
/// Application/Infrastructure в шаруванні), тому кожен виклик, що валідує
/// «код» (роль, проєкт, шаблон, довідник, методологія), кидав
/// <c>DomainException</c> із готовим УКРАЇНСЬКИМ реченням, яке доходило до
/// клієнта як є, незалежно від обраної мови інтерфейсу — виявлено живим
/// аудитом (repro: `New role` з кодом, що містить дефіс). Тепер
/// <c>EcrCode.Create</c> несе лише <c>Details["messageKey"] =
/// "err.ECR-CFG-0422"</c> і <c>Details["code"] = value</c> — структуровані
/// дані, не готовий текст — а це саме той шлях, який тут перевіряється.
/// </remarks>
public sealed class GenericMessageKeyLocalizationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Виняток_із_messageKey_резолвиться_каталогом_із_підстановкою()
    {
        var catalog = new FakeUiStringCatalog()
            .Add(
                "en",
                "err.ECR-CFG-0422",
                "The code \"{code}\" is invalid.",
                UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "en", () => throw new DomainException(
            "ECR-CFG-0422",
            "Код «LANE1-ZERO» недопустимий: дозволені латинські літери, цифри й підкреслення.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CFG-0422", ["code"] = "LANE1-ZERO" }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("The code \"LANE1-ZERO\" is invalid.", detail);
        AssertNoCyrillic(detail);
    }

    /// <summary>
    /// ⛔ Мутаційний доказ дефекту ДО фіксу: точне відтворення СТАРОГО
    /// `EcrCode.Create` (без третього аргументу `DomainException`). Без
    /// `Details` узагальнений шлях не спрацьовує — `Detail` лишається сирим
    /// українським реченням, яке й перевіряється тут як RED.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Без_Details_подробиця_лишається_сирим_українським_реченням()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-CFG-0422", "The code \"{code}\" is invalid.", UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "en", () => throw new DomainException(
            "ECR-CFG-0422",
            "Код «LANE1-ZERO» недопустимий: дозволені латинські літери, цифри й підкреслення."));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal(
            "Код «LANE1-ZERO» недопустимий: дозволені латинські літери, цифри й підкреслення.",
            detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Ключа_немає_в_каталозі_повертає_сире_речення_а_не_сам_ключ()
    {
        var catalog = new FakeUiStringCatalog(); // порожній — ключа немає взагалі

        var problem = await ProblemAsync(catalog, "en", () => throw new DomainException(
            "ECR-CFG-0422",
            "Код «X» недопустимий.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CFG-0422", ["code"] = "X" }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("Код «X» недопустимий.", detail);
    }

    /// <summary>
    /// Точне відтворення виправленого `throw new BusinessRuleException(...)`
    /// у `CreateUserHandler.HandleAsync` (`Ecr.Application.Security.RoleAndUserHandlers`)
    /// на дублікаті імені користувача — той самий прийом, що
    /// `RoleAndUserHandlersLocalizedErrorTests` для ECR-AUTH-0403.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task CreateUserHandler_дублікат_імені_користувача_каталожною_мовою()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-USR-0409", "A user named \"{userName}\" already exists.", UiStringScope.Private);

        const string userName = "bootstrap";
        var problem = await ProblemAsync(catalog, "en", () => throw new BusinessRuleException(
            "ECR-USR-0409", $"Користувач з іменем «{userName}» уже існує.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-USR-0409", ["userName"] = userName }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("A user named \"bootstrap\" already exists.", detail);
        AssertNoCyrillic(detail);
    }

    /// <summary>Старий, точковий шлях (`ECR-AUTH-0403`) лишається неушкодженим.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Старий_шлях_ECR_AUTH_0403_без_messageKey_і_далі_працює()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-AUTH-0403.requiresPermission", "Permission required:", UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "en", () => throw new AccessDeniedException(
            "ECR-AUTH-0403", "Потрібне право Security.ManageRoles.",
            new Dictionary<string, object?> { ["permission"] = "Security.ManageRoles" }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("Permission required: Security.ManageRoles", detail);
    }

    private static void AssertNoCyrillic(string? text)
    {
        Assert.NotNull(text);
        Assert.DoesNotMatch("[а-яА-ЯіІїЇєЄ]", text);
    }

    /// <summary>Проганяє відмову через конвеєр і повертає тіло <c>problem+json</c>.</summary>
    private static async Task<JsonElement> ProblemAsync(
        IUiStringCatalog catalog, string language, Func<Task> throwing)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Language.Returns(language);

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
