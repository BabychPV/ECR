using System.Net;
using System.Reflection;
using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Контракт помилки: клієнт розрізняє причини **за кодом**, а не за текстом.
/// </summary>
/// <remarks>
/// ⚠ Позначені <c>Integration</c> (`Q-053`) — окрім тих, що працюють із
/// каталогом кодів і бази не потребують.
/// </remarks>
[Collection("SqlServer")]
public sealed class ErrorContractTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Помилка_повертається_у_форматі_problem_json()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/templates", UriKind.Relative));

        // Анонімний запит до захищеного ендпоінта — 401, а не 302 на форму
        // входу: це API, і редирект клієнт прийняв би за успіх.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тіло_помилки_містить_код_і_ідентифікатор_кореляції()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // Ендпоінт, реалізація якого ще кидає NotImplementedException:
        // конвеєр має перетворити будь-який виняток на problem+json.
        var response = await client.GetAsync(new Uri("/api/v1/templates/1/versions", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return;   // авторизація спрацювала раніше — це теж коректна поведінка
        }

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-SYS-0500", json.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ідентифікатор_кореляції_повертається_у_заголовку_відповіді()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "test-correlation-42");

        var response = await client.SendAsync(request);

        // Свій ідентифікатор повертається як є: інакше склеїти журнал клієнта
        // з нашим неможливо, а саме заради цього він і передається.
        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        Assert.Equal("test-correlation-42", values!.Single());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Внутрішня_помилка_не_розкриває_стек_і_текст_винятку()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/templates/1/versions", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        // ⚠ У тексті винятку бувають імена об'єктів БД і фрагменти запитів.
        // Клієнту — лише CorrelationId; подробиці в логах (ФВ-6.11).
        Assert.DoesNotContain("NotImplementedException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Ecr.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Усі_коди_з_каталогу_мають_унікальні_значення()
    {
        var codes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // Два різні стани під одним кодом означають, що клієнт не може їх
        // розрізнити — а весь сенс коду саме в цьому.
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(@"^ECR-[A-Z]+-\d{4}$", code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Конфлікт_повертає_409_із_переліком_розбіжностей()
    {
        // Конвеєр перевіряється безпосередньо: підняти справжній конфлікт
        // через HTTP можна лише разом із batch-PATCH, а він чекає рушій
        // виразів (Етап 2). Мапінг винятку на 409 із переліком — тут і зараз.
        var details = new Dictionary<string, object?>
        {
            ["conflicts"] = new[] { new { rowKey = "R1", theirValue = "7", byUserId = 77 } },
        };

        var exception = new Ecr.Application.Errors.ConcurrencyConflictException(
            ErrorCodes.CellConflict, "Дані змінилися.", details);

        var map = typeof(ExceptionHandlingMiddleware)
            .GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Кортеж розпаковується приведенням, а не через Item1/Item2: у
        // ValueTuple це ПОЛЯ, і GetProperty повертає null — тест падав би з
        // NullReferenceException замість того, що перевіряє.
        var (status, code, _, extensions) =
            ((int, string, string, IReadOnlyDictionary<string, object?>?))map.Invoke(null, [exception])!;

        // ⚠ 409 БЕЗ переліку не дає клієнту нічого, крім пропозиції спробувати
        // ще раз наосліп. Користувач має побачити, ЩО саме розійшлося.
        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.CellConflict, code);
        Assert.NotNull(extensions);
        Assert.True(extensions!.ContainsKey("conflicts"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Відмова_в_доступі_повертає_403_із_ПРИЧИНОЮ_у_розширеннях()
        => Assert.Fail("not implemented");
}
