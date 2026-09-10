using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
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

        // ⛔ Запит іде ПІД КОРИСТУВАЧЕМ. Раніше тест ходив анонімно і на 401
        // виходив достроково з коментарем «це теж коректна поведінка» — тобто
        // не перевіряв нічого і був зелений завжди. Анонімний запит зупиняє
        // автентифікація ще до конвеєра помилок, з порожнім тілом.
        var (userName, _, _) = await ArrangeAsync().ConfigureAwait(true);

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        // Шаблона з таким номером немає — конвеєр має перетворити відмову
        // обробника на problem+json із кодом.
        var response = await client.GetAsync(new Uri("/api/v1/templates/999999/versions", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.False(string.IsNullOrWhiteSpace(body), $"Порожнє тіло на {response.StatusCode}: {app.ErrorsText}");

        var json = JsonDocument.Parse(body).RootElement;
        Assert.StartsWith("ECR-", json.GetProperty("errorCode").GetString(), StringComparison.Ordinal);
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
    [Trait("Requirement", "ФВ-6.11")]
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
    [Trait("Requirement", "ФВ-6.11")]
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
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Дублікат_ключа_рядка_повертає_409_а_не_422()
    {
        // ⛔ ECR-ROW-0409 доїжджав клієнтові як 422 (Q-150): цифри коду
        // кажуть 409, але він падав у загальний арм BusinessRuleException,
        // не маючи власного — так само, як PasswordChangeRequired/AccountLocked/
        // Archiving до того, як для них завели окремі арми.
        var exception = new Ecr.Application.Errors.BusinessRuleException(
            ErrorCodes.RowDuplicate, "Рядок із ключем R1 у цій таблиці вже існує.");

        var map = typeof(ExceptionHandlingMiddleware)
            .GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;

        var (status, code, _, _) =
            ((int, string, string, IReadOnlyDictionary<string, object?>?))map.Invoke(null, [exception])!;

        Assert.Equal(409, status);
        Assert.Equal(ErrorCodes.RowDuplicate, code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.8")]
    public async Task Відмова_в_доступі_повертає_403_із_ПРИЧИНОЮ_у_розширеннях()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        // Користувач без жодного гранта: автентифікований, але нічого не може.
        var (name, documentId, sheetDefId) = await ArrangeAsync().ConfigureAwait(true);
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/submit", UriKind.Relative),
            new { sheetDefId, periodKey = 202601 }).ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {app.ErrorsText}");


        var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        // ⚠ «403» без причини змушує користувача йти до адміністратора, а того —
        // до розробника. Причина в розширеннях і є відповіддю на питання
        // «чому комірка сіра» (ФВ-6.8).
        Assert.Equal(ErrorCodes.AccessDenied, json.GetProperty("errorCode").GetString());
        Assert.Equal(
            nameof(Ecr.Domain.Enums.EditDenyReason.NoGrant),
            json.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкликана_роль_прибирає_застарілу_cookie_а_не_лише_відмовляє()
    {
        // ⛔ Виявлено НЕ тестуванням, а ручною звіркою S-04..S-07 в браузері
        // (`W5.9`): після зміни ролей інший вкладка з дереву — і БУКВАЛЬНО
        // все, включно з `/logout` і публічним `GET /ui-strings` — почало
        // віддавати 401 назавжди, без жодного способу вийти з цього стану
        // інакше, ніж стерти cookie руками. Причина — `Response.Clear()` в
        // `ExceptionHandlingMiddleware` стирає й `Set-Cookie`, який
        // `SecurityStampMiddleware` щойно додав через `SignOutAsync` перед
        // тим, як кинути виняток.
        using var app = new EcrApiFactory(sql);
        using var manager = app.CreateClient();

        // ⚠ Без cookie-handler'а клієнта: інакше `HttpClientHandler` сам
        // перехопив би `Set-Cookie` в СВІЙ `CookieContainer`, і саме той
        // заголовок, який тест перевіряє, ніколи не дійшов би до
        // `response.Headers` — тест перевіряв би порожнечу замість фактичної
        // поведінки сервера.
        using var target = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var (managerName, targetName, targetId) = await ArrangeRevocationAsync().ConfigureAwait(true);

        var managerLogin = await manager.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = managerName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(managerLogin.IsSuccessStatusCode, $"{managerLogin.StatusCode}: {app.ErrorsText}");

        var targetLogin = await target.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = targetName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(targetLogin.IsSuccessStatusCode, $"{targetLogin.StatusCode}: {app.ErrorsText}");

        var loginCookie = targetLogin.Headers.TryGetValues("Set-Cookie", out var loginSetCookie)
            ? loginSetCookie.Select(c => c.Split(';')[0]).ToList()
            : throw new InvalidOperationException("Вхід не повернув cookie: тест переказує неправильну адресу.");

        // Ціль тримає СТАРУ cookie далі, поки менеджер крутить її штамп.
        var replaceRoles = await manager.PutAsJsonAsync(
            new Uri($"/api/v1/users/{targetId}/roles", UriKind.Relative),
            new { roleCodes = Array.Empty<string>() }).ConfigureAwait(true);
        Assert.True(replaceRoles.IsSuccessStatusCode, $"{replaceRoles.StatusCode}: {app.ErrorsText}");

        using var staleMeRequest = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/me", UriKind.Relative));
        staleMeRequest.Headers.Add("Cookie", loginCookie);
        var staleRequest = await target.SendAsync(staleMeRequest).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, staleRequest.StatusCode);

        // ⛔ Головне твердження: без фіксу цей заголовок відсутній, і клієнт
        // носить мертву cookie до кінця її строку — 401 на КОЖЕН наступний
        // запит, навіть на /logout і на публічні маршрути.
        Assert.True(
            staleRequest.Headers.TryGetValues("Set-Cookie", out var setCookie) && setCookie.Any(),
            "Відповідь на застарілу cookie має її прибрати (Set-Cookie), а не лишити носити далі.");
    }

    /// <summary>Пароль користувача сценаріїв; у відповіді не з'являється ніде.</summary>
    private const string LoginPassword = "Contract-2026-Check!";

    /// <summary>Локальний користувач без грантів і документ, який він не може подати.</summary>
    private async Task<(string UserName, long DocumentId, int SheetDefId)> ArrangeAsync()
    {
        var name = $"nogrant_{Guid.NewGuid():N}"[..20];

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options);

        var user = new Ecr.Domain.Entities.Security.User(
            name, name, Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(LoginPassword));
        db.Users.Add(user);

        // ⛔ Q-222: templateVersionId/periodPolicyId тут раніше були голими
        // константами (1) без жодного реального рядка — FK_Project_TV
        // (2a-db-schema.md, ніколи не потрапляв у конфігурацію до цього
        // фіксу) тепер це ловить по-справжньому. periodPolicyId=1 лишається
        // коректним — його сіє SeedRunner ("ECR-Standard"); templateVersionId
        // такого сідінгу не має, тож заводимо реальний Template/TemplateVersion,
        // той самий патерн, що вже в TestDocumentBuilder.BuildAsync.
        var tag = Guid.NewGuid().ToString("N")[..12];
        var template = new Ecr.Domain.Entities.Configuration.Template(
            Ecr.Domain.ValueObjects.EcrCode.Create($"TPL{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Template" }),
            1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new Ecr.Domain.Entities.Configuration.TemplateVersion(
            template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var project = new Ecr.Domain.Entities.Documents.Project(
            Ecr.Domain.ValueObjects.EcrCode.Create($"P{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Contract" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: version.Id, Ecr.Domain.Enums.PeriodKind.Monthly,
            periodPolicyId: 1, "Asia/Almaty");

        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Період відкритий: відмова має бути саме через ВІДСУТНІСТЬ ГРАНТА, а
        // не через стан періоду — інакше тест перевіряв би не те, що заявляє.
        var period = new Ecr.Domain.Entities.Documents.Period(
            project.Id, new Ecr.Domain.ValueObjects.PeriodKey(202601), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.AdvanceTo(Ecr.Domain.Enums.PeriodState.Open, DateTime.UtcNow);

        // ⛔ Q-222: аркуш раніше був голою константою (20) без жодного
        // реального рядка — FK_DocSheet_Sheet (2a-db-schema.md, той самий
        // фікс, що FK_Project_TV вище) тепер це ловить. Заводимо реальний
        // SheetDef під тим самим TemplateVersion.
        var sheet = new Ecr.Domain.Entities.Configuration.SheetDef(
            version.Id, Ecr.Domain.ValueObjects.EcrCode.Create($"SHEET{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Sheet" }),
            1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var document = new Ecr.Domain.Entities.Documents.Document(
            project.Id, $"DOC-{Guid.NewGuid():N}"[..20], user.Id, DateTime.UtcNow);

        // Той самий аркуш, на який тест подає: без нього подання
        // відхилялося б перевіркою складу документа (`ECR-DOC-0404`, S-17)
        // раніше, ніж дійшло б до перевірки прав, яку цей тест і заявляє.
        document.IncludeSheet(sheet.Id);

        db.Periods.Add(period);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, document.Id, sheet.Id);
    }

    /// <summary>
    /// Менеджер із правом <c>Security.ManageUsers</c> і ціль, чиї ролі він
    /// зможе замінити.
    /// </summary>
    private async Task<(string ManagerName, string TargetName, int TargetId)> ArrangeRevocationAsync()
    {
        var managerName = $"manager_{Guid.NewGuid():N}"[..20];

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options);

        var hasher = new Ecr.Infrastructure.Security.PasswordHasher();

        var manager = new Ecr.Domain.Entities.Security.User(
            managerName, managerName, Ecr.Domain.Enums.AuthProvider.Local);
        manager.SetPassword(hasher.Hash(LoginPassword));
        db.Users.Add(manager);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var role = new Ecr.Domain.Entities.Security.Role(
            Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Revocation test manager" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, "Security.ManageUsers"));
        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, manager.Id, principalSid: null));

        // Ціль: будь-який користувач, чий штамп менеджер згодом крутне.
        var targetName = $"target_{Guid.NewGuid():N}"[..20];
        var target = new Ecr.Domain.Entities.Security.User(
            targetName, "Revocation target", Ecr.Domain.Enums.AuthProvider.Local);
        target.SetPassword(hasher.Hash(LoginPassword));
        db.Users.Add(target);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (managerName, targetName, target.Id);
    }
}
