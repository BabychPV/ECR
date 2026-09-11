using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Q-242: подробиця (<c>detail</c>) відмови в праві мовою інтерфейсу, а не
/// сирим українським реченням обробника.
/// </summary>
/// <remarks>
/// ⛔ Виявлено реальним входом у застосунок під час аудиту (не прогоном
/// тестів): `ExceptionHandlingMiddleware.LocalizedTitleAsync` уже читає
/// заголовок відмови (<c>Title</c>) з каталогу мовою користувача (`D-95`),
/// а `Detail` поруч — ні: `PermissionCheck.RequireAsync` (і ще 13
/// копій того самого патерну по інших обробниках) кидали `AccessDeniedException`
/// із повідомленням `$"Потрібне право {permission}."`, написаним
/// УКРАЇНСЬКОЮ для СЕРВЕРНОГО читача, а не для показу. Користувач бачив один
/// абзац англійською (заголовок) і один українською (подробиця) — двомовне
/// речення в системі, де підтримані мови — лише en/ru/kz (`D-95`), а
/// української серед них немає взагалі.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessDeniedDetailScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// Подробиця відмови — мовою каталогу (`err.ECR-AUTH-0403.requiresPermission`)
    /// плюс код права, а НЕ сире речення обробника.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Finding", "Q-242")]
    public async Task Подробиця_відмови_в_праві_каталожною_мовою_а_не_сирим_реченням()
    {
        using var app = new EcrApiFactory(sql);

        // ⚠ Жодного права — саме цей шлях кидає ECR-AUTH-0403 у
        // PermissionCheck.RequireAsync (документи — Document.View).
        var stranger = await Provisioning.AdministratorAsync(app, "Q242", []);

        var response = await stranger.Client.GetAsync(new Uri("/api/v1/documents", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ECR-AUTH-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal(
            "You do not have permission for this action.", problem.GetProperty("title").GetString());

        var detail = problem.GetProperty("detail").GetString();

        // ⛔ Ось сама відмінність, яку доводить тест: подробиця читається
        // каталогом (англійською — єдина мова, для якої сьогодні заповнений
        // сам ключ) І несе код права — а не залишається сирим українським
        // реченням обробника.
        Assert.Equal("Requires permission Document.View", detail);
        Assert.DoesNotContain("Потрібне", detail, StringComparison.Ordinal);

        // Структурована подробиця (Q-238-style Extensions) несе той самий
        // код права машинно — той-таки код, з якого middleware зібрала
        // текстову Detail вище.
        Assert.Equal("Document.View", problem.GetProperty("permission").GetString());
    }
}
