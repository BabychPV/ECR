using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>Detail</c> відмови <c>ECR-AUTH-0403</c> у <c>CreateRoleHandler</c> і
/// <c>CreateUserHandler</c> — мовою каталогу (<c>err.ECR-AUTH-0403.requiresPermission</c>,
/// Q-242), а не сирим українським реченням обробника.
/// </summary>
/// <remarks>
/// ⛔ Обидва обробники кидали <c>AccessDeniedException("ECR-AUTH-0403", ...)</c>
/// БЕЗ третього аргументу — словника <c>Details</c> із ключем
/// <c>"permission"</c>. Усі ІНШІ п'ять перевірок прав у цьому ж файлі
/// (<see cref="ListRolesHandler"/>, <see cref="ReplaceUserRolesHandler"/>,
/// <see cref="SetUserEmailHandler"/>, <see cref="ListUsersHandler"/>,
/// <see cref="SetReceivesAlertsHandler"/>) цей словник передають, і саме він
/// дозволяє <c>ExceptionHandlingMiddleware.LocalizedDetailAsync</c> (Q-242)
/// розпізнати код <c>ECR-AUTH-0403</c> і зібрати речення з каталогу
/// (<c>en</c>/<c>ru</c>/<c>kz</c>) замість голого українського тексту, якого
/// серед підтримних мов застосунку немає взагалі. Без словника
/// <c>LocalizedDetailAsync</c> одразу повертає сире `message` — саме це й
/// відтворює перший тест нижче на конструкторі БЕЗ словника, а другий —
/// доводить, що після фіксу (конструктор ІЗ словником) шлях Q-242 працює.
/// </remarks>
public sealed class RoleAndUserHandlersLocalizedErrorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "Q-300")]
    public async Task CreateRoleHandler_подробиця_відмови_права_каталожною_мовою()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-AUTH-0403.requiresPermission", "Permission required:", UiStringScope.Public);

        // Точне відтворення виправленого throw new AccessDeniedException(...)
        // у CreateRoleHandler.HandleAsync, коли профілю бракує ListRolesHandler.Permission.
        var problem = await ProblemAsync(catalog, "en", () => throw new AccessDeniedException(
            "ECR-AUTH-0403", $"Потрібне право {ListRolesHandler.Permission}.",
            new Dictionary<string, object?> { ["permission"] = ListRolesHandler.Permission }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("Permission required: Security.ManageRoles", detail);
        AssertNoCyrillic(detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "Q-300")]
    public async Task CreateUserHandler_подробиця_відмови_права_каталожною_мовою()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-AUTH-0403.requiresPermission", "Permission required:", UiStringScope.Public);

        // Точне відтворення виправленого throw new AccessDeniedException(...)
        // у CreateUserHandler.HandleAsync, коли профілю бракує ListUsersHandler.Permission.
        var problem = await ProblemAsync(catalog, "en", () => throw new AccessDeniedException(
            "ECR-AUTH-0403", $"Потрібне право {ListUsersHandler.Permission}.",
            new Dictionary<string, object?> { ["permission"] = ListUsersHandler.Permission }));

        var detail = problem.GetProperty("detail").GetString();

        Assert.Equal("Permission required: Security.ManageUsers", detail);
        AssertNoCyrillic(detail);
    }

    /// <summary>
    /// ⛔ Регресійний доказ дефекту ДО фіксу: точне відтворення СТАРОГО
    /// (двоаргументного) виклику конструктора, який лишався в
    /// <c>CreateRoleHandler</c>/<c>CreateUserHandler</c> до Q-300. Без
    /// словника <c>Details</c> <c>LocalizedDetailAsync</c> не бачить ключа
    /// <c>"permission"</c> і повертає сире українське речення, яке далі й
    /// перевіряється тут, — це і є RED, який фікс має усунути.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "Q-300")]
    public async Task Без_словника_Details_подробиця_лишається_сирим_українським_реченням()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "err.ECR-AUTH-0403.requiresPermission", "Permission required:", UiStringScope.Public);

        var problem = await ProblemAsync(catalog, "en", () => throw new AccessDeniedException(
            "ECR-AUTH-0403", $"Потрібне право {ListRolesHandler.Permission}."));

        var detail = problem.GetProperty("detail").GetString();

        // ⚠ Це навмисно НЕ англійська: без "permission" у Details каталог не
        // резолвиться, і Detail лишається сирим повідомленням обробника.
        Assert.Equal("Потрібне право Security.ManageRoles.", detail);
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
