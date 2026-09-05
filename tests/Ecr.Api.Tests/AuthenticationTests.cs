// tests/Ecr.Api.Tests/AuthenticationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Два провайдери, одна cookie (ФВ-6.1) і негайна дія відкликання прав.
/// </summary>
/// <remarks>
/// ⚠ Фікстура — <see cref="SqlServerFixture"/>, а не голий
/// <c>WebApplicationFactory</c>, як у <c>06e-tests-api.md</c> (`Q-053`):
/// застосунок не стартує без бази, а вхід перевіряє реальні
/// <c>sec.User</c> і <c>sec.LoginAttempt</c>.
///
/// ⚠ Доменний вхід цими тестами **не покривається**: Negotiate вимкнений, бо
/// його обробник вимагає <c>IConnectionItemsFeature</c>, якого TestServer не
/// має (`Q-054`). Тому «одна cookie» перевіряється з іншого боку — через те,
/// що схема входу в застосунку рівно одна.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuthenticationTests(SqlServerFixture sql)
{
    private const string Password = "Kashagan-2026-Winter!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Локальний_вхід_видає_ту_саму_cookie_що_й_доменний()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        // Ім'я cookie береться з конфігурації, а не з тесту: інакше тест
        // перевіряв би свою власну константу.
        var cookieName = app.Services.GetRequiredService<IConfiguration>()["Auth:CookieName"];
        Assert.Contains(
            cookieName!,
            string.Join(";", response.Headers.GetValues("Set-Cookie")),
            StringComparison.Ordinal);

        // ⚠ Схем, здатних ПІДПИСАТИ вхід, у застосунку рівно одна. Саме це і
        // означає «одна cookie на два провайдери» (ФВ-6.1): доменний вхід
        // фізично нема куди підписати інакше. Друга sign-in схема означала б
        // дві сесії з різним терміном і різним відкликанням.
        using var scope = app.Services.CreateScope();
        var schemes = await scope.ServiceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync().ConfigureAwait(true);

        var signIn = schemes
            .Where(s => s.HandlerType.IsAssignableTo(typeof(IAuthenticationSignInHandler)))
            .Select(s => s.Name)
            .ToList();

        Assert.Equal(["Cookies"], signIn);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невірний_пароль_і_неіснуючий_користувач_дають_однакову_відповідь()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var wrongPassword = await LoginAsync(client, name, "не-той-пароль").ConfigureAwait(true);
        var unknownUser = await LoginAsync(client, name + "_немає", Password).ConfigureAwait(true);

        // ⚠ Різна відповідь перетворила б ендпоінт на засіб перебору імен: за
        // кодом було б видно, які облікові записи існують, а це половина
        // роботи зловмисника ще до першого підбору пароля.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
        Assert.Equal(unknownUser.Status, wrongPassword.Status);
        Assert.Equal(unknownUser.ErrorCode, wrongPassword.ErrorCode);
        Assert.Equal(unknownUser.Detail, wrongPassword.Detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_N_невдалих_спроб_обліковий_запис_блокується()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var policy = await PolicyAsync().ConfigureAwait(true);
        for (var attempt = 0; attempt < policy.MaxFailedAttempts; attempt++)
        {
            var failure = await LoginAsync(client, name, "не-той-пароль").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.Status);
        }

        // ⚠ Тепер відповідь відрізняється — і навмисно: законний власник має
        // дізнатися, що запис заблоковано, інакше він підбиратиме пароль, який
        // давно правильний (ФВ-6.4a).
        var locked = await LoginAsync(client, name, Password).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Locked, locked.Status);
        Assert.Equal("ECR-AUTH-0423", locked.ErrorCode);

        // Кожна спроба лишила слід — включно з тією, що привела до блокування.
        await using var db = CreateContext();
        var attempts = await db.LoginAttempts
            .Where(a => a.UserName == name)
            .ToListAsync().ConfigureAwait(true);

        Assert.Equal(policy.MaxFailedAttempts + 1, attempts.Count);
        Assert.All(attempts, a => Assert.False(a.IsSuccess));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_ролей_робить_поточну_сесію_недійсною_негайно()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        var before = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Відкликання ролі крутить SecurityStamp — саме це і робить дію
        // негайною, без жодного «розлогінити всіх».
        await using (var db = CreateContext())
        {
            var user = await db.Users.FirstAsync(u => u.UserName == name).ConfigureAwait(true);
            user.RefreshSecurityStamp();
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var after = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);

        // ⚠ Не «після закінчення cookie», а цим же запитом. Інакше відкликана
        // роль жила б ще вісім годин — тиха діра, якої не видно ні в логах, ні
        // в UI (ФВ-6.7).
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пароль_не_зустрічається_у_логах_трасуванні_і_відповідях()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var ok = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);
        var okBody = await ok.Content.ReadAsStringAsync().ConfigureAwait(true);

        var failed = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = "не-той-пароль" }).ConfigureAwait(true);
        var failedBody = await failed.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Ні у відповіді, ні в журналі БУДЬ-ЯКОГО рівня. Лог живе довше за
        // секрет і читається ширшим колом людей, ніж база (ФВ-6.11).
        var surfaces = new List<string> { okBody, failedBody };
        surfaces.AddRange(app.ServerLog);

        await using var db = CreateContext();
        var hash = await db.Users.Where(u => u.UserName == name)
                                 .Select(u => u.PasswordHash)
                                 .FirstAsync().ConfigureAwait(true);

        var attempts = await db.LoginAttempts.Where(a => a.UserName == name)
                                             .ToListAsync().ConfigureAwait(true);
        surfaces.AddRange(attempts.Select(a => JsonSerializer.Serialize(a)));

        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(Password, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(hash!, surface, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Анонімний_запит_до_захищеного_ендпоінта_дає_401_а_не_редирект()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);

        // ⚠ Це API, а не MVC. 302 на форму входу клієнт прийняв би за успіх і
        // показав би HTML сторінки входу там, де чекав JSON.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>Створює локального користувача з відомим паролем.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_зміни_пароля_сеанс_працює_попри_живий_кеш_штампа()
    {
        var name = await ArrangeLocalUserAsync().ConfigureAwait(true);

        // ⛔ Кеш УВІМКНЕНИЙ — інакше тест перевіряє не те, що ламалося.
        // Решта тестів ставить 0, і саме тому `A7-21` жив: фікстура вимикала
        // механізм, який давав хибну відмову.
        using var app = new EcrApiFactory(sql, stampCacheSeconds: 30);
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        // Перший запит наповнює кеш штампом ДО зміни пароля.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true)).StatusCode);

        const string Next = "Stamp-Cache-Check-2026!";

        var change = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/change-password", UriKind.Relative),
            new { currentPassword = Password, newPassword = Next }).ConfigureAwait(true);
        Assert.True(change.IsSuccessStatusCode, $"{change.StatusCode}: {app.ErrorsText}");

        // ⛔ Ось воно: вхід успішний, а наступний запит отримував 401 «права
        // змінилися». Користувача викидало на форму входу, він входив — і за
        // наступним запитом опинявся там знову, усі п'ять секунд.
        using var after = app.CreateClient();

        var relogin = await after.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Next }).ConfigureAwait(true);
        Assert.True(relogin.IsSuccessStatusCode, $"{relogin.StatusCode}: {app.ErrorsText}");

        var me = await after.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    private async Task<string> ArrangeLocalUserAsync()
    {
        var name = $"local_{Guid.NewGuid():N}"[..20];

        await using var db = CreateContext();

        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));

        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private async Task<PasswordPolicy> PolicyAsync()
    {
        await using var db = CreateContext();
        return await db.PasswordPolicies
                   .FirstOrDefaultAsync(p => p.Code == "Default").ConfigureAwait(false)
               ?? new PasswordPolicy("Default", minLength: 12, maxFailedAttempts: 5);
    }

    private static async Task<(HttpStatusCode Status, string? ErrorCode, string? Detail)> LoginAsync(
        HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password }).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = JsonDocument.Parse(body).RootElement;

        return (
            response.StatusCode,
            json.TryGetProperty("errorCode", out var code) ? code.GetString() : null,
            json.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}
