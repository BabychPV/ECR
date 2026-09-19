// tests/Ecr.Api.Tests/Security/MiddlewareOrderTests.cs

using System.Net;
using System.Text.Json;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>ExceptionHandlingMiddleware</c> — найзовнішній (<c>S-23</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Доти зовні стояв <c>CorrelationIdMiddleware</c>. Поки він не
/// падає, різниці не видно — і саме тому дефект пережив усі попередні прогони:
/// він проявляється рівно в тому випадку, який ніхто не відтворював. Виняток,
/// кинутий у САМОМУ зовнішньому middleware, не мав кому стати
/// <c>problem+json</c>: він виходив у хост, і зовнішній споживач отримував
/// обірване з'єднання замість коду, тіла й <c>correlationId</c>.
///
/// ⚠ Кидок відтворюється підміною журналу (<see cref="ThrowingScopeLogger{T}"/>),
/// а не правкою middleware: перевіряється ПОРЯДОК, а не текст
/// <c>CorrelationIdMiddleware</c>, і той лишається недоторканим.
/// </remarks>
[Collection("SqlServer")]
public sealed class MiddlewareOrderTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виняток_із_CorrelationIdMiddleware_теж_стає_problem_json()
    {
        using var app = new EcrApiFactory(sql);
        using var broken = FailingPipeline.WithFailingCorrelationLogger(app);
        using var client = broken.CreateClient();

        // ⚠ `/health/live` — найдешевший анонімний шлях: предмет тут конвеєр, а
        // не дані, і будь-який «змістовний» ендпоінт тягнув би за собою права,
        // документи й половину системи.
        var response = await client
            .GetAsync(new Uri("/health/live", UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутаційний доказ: повернути в `Program.cs` порядок
        // `CorrelationIdMiddleware` → `ExceptionHandlingMiddleware` — і виняток
        // виходить у хост: `TestServer` віддає його викликачеві, тобто рядок
        // вище кидає `InvalidOperationException` замість того, щоб повернути
        // відповідь. Тест червоніє першим же рядком.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        var json = JsonDocument.Parse(body).RootElement;

        // Код — стабільний і той самий, що й для будь-якої іншої необробленої
        // помилки: клієнт розрізняє причини за кодом, а не за тим, який саме
        // middleware не впорався.
        Assert.Equal(ErrorCodes.Internal, json.GetProperty("errorCode").GetString());

        // ⚠ І це не дрібниця: `ExceptionHandlingMiddleware` читає ідентифікатор
        // з `HttpContext.Items`, який `CorrelationIdMiddleware` встиг заповнити
        // до падіння. Тобто обмін місцями не забрав у відповіді кореляцію —
        // саме цього й побоюєшся, міняючи порядок.
        Assert.False(
            string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()),
            $"у тілі немає correlationId: {body}");

        // Стек лишився в журналі сервера — там, де йому й місце (ФВ-6.11).
        Assert.Contains(
            app.ServerErrors,
            line => line.Contains(ThrowingScopeLogger<object>.Message, StringComparison.Ordinal));
    }
}
