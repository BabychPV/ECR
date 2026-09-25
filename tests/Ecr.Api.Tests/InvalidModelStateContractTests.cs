// tests/Ecr.Api.Tests/InvalidModelStateContractTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B-15 (UX-аудит, четвертий раунд): невалідне тіло запиту (JSON, що не
/// парситься, або тип, що не зв'язується) відповідає у форматі ECR
/// (<c>errorCode</c>, <c>messageKey</c>-локалізований <c>detail</c>), а не
/// типовим <c>ValidationProblemDetails</c> ASP.NET Core без жодного з них.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>[ApiController]</c> сам перехоплював зв'язування моделі ДО
/// дії контролера й відповідав власним <c>ValidationProblemDetails</c> — тіло
/// без <c>errorCode</c>, тому клієнт (<c>problemOf</c>,
/// <c>src/Ecr.Web/src/api/client.ts</c>) не мав чим розрізнити цю відмову від
/// жодної іншої: підставляв запасний заголовок <c>HTTP {status}</c>.
///
/// Фікс — <c>Program.cs</c>, <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c>
/// кидає <c>BusinessRuleException(ECR-REQ-0422)</c> замість власноруч зібраного
/// результату: виняток летить крізь конвеєр дій MVC, тобто ВСЕРЕДИНІ
/// <c>next(context)</c> <c>ExceptionHandlingMiddleware</c>, і дістає ту саму
/// локалізацію, що й будь-яка інша відмова.
/// </remarks>
[Collection("SqlServer")]
public sealed class InvalidModelStateContractTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Синтаксично_невалідний_JSON_повертає_ECR_форму_а_не_голий_ValidationProblemDetails()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // Свідомо зламаний JSON — незакрита лапка в значенні поля.
        using var content = new StringContent(
            """{"userName": "x, "password": "y"}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri("/api/v1/login/local", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = JsonDocument.Parse(body).RootElement;

        // ⚠ Головне твердження: errorCode і correlationId — той самий контракт,
        // що й для будь-якої іншої відмови конвеєра, не типовий "errors" від
        // ValidationProblemDetails.
        Assert.Equal(ErrorCodes.RequestInvalid, json.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()));
        Assert.False(json.TryGetProperty("errors", out _), "Тіло не має нести typову форму ValidationProblemDetails.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_що_не_зв_язується_повертає_ECR_форму()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // password — масив, а не рядок: конструктор LocalLoginRequest не зв'яжеться.
        using var content = new StringContent(
            """{"userName": "x", "password": [1, 2, 3]}""", Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri("/api/v1/login/local", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(ErrorCodes.RequestInvalid, json.GetProperty("errorCode").GetString());
    }
}
