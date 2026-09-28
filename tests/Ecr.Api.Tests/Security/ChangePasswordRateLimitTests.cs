// tests/Ecr.Api.Tests/Security/ChangePasswordRateLimitTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Api.Security;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S9: <c>POST /api/v1/auth/change-password</c> — межа частоти на користувача і
/// спільне з входом блокування за хибним чинним паролем.
/// </summary>
/// <remarks>
/// ⛔ До S9 обмежувач покривав лише <c>/api/v1/login</c>, а хибний чинний
/// пароль у зміні пароля не рахувався як невдала спроба. Відкритий сеанс
/// (залишений браузер, викрадена cookie) був необмеженим оракулом пароля:
/// ні межі частоти, ні блокування, ні сліду в аудиті.
/// </remarks>
[Collection("SqlServer")]
public sealed class ChangePasswordRateLimitTests(SqlServerFixture sql)
{
    private const string Password = "Cpw-Rate-Limit-2026!";

    /// <summary>Код відмови — ЛІТЕРАЛОМ, а не константою продукту.</summary>
    private const string ExpectedRejection = "ECR-REQ-0429";

    private const int Permit = LoginRateLimiting.DefaultChangePasswordPermitPerMinute;

    private static readonly Uri ChangePassword = new("/api/v1/auth/change-password", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_межу_за_хвилину_на_користувача_429_а_сусід_з_тієї_ж_адреси_проходить()
    {
        var first = await ArrangeUserAsync("cpra").ConfigureAwait(true);
        var second = await ArrangeUserAsync("cprb").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, first).ConfigureAwait(true);
        using var neighbour = await SignInAsync(app, second).ConfigureAwait(true);

        // ⚠ Чинний пароль ПРАВИЛЬНИЙ, новий — закороткий: кожен запит дає 422 і
        // нічого не змінює — ні пароля, ні лічильника спроб. Інакше тест
        // перевіряв би блокування запису, а не межу частоти.
        //
        // ⚠ Одночасно, а не послідовно: вікно фіксоване (1 хв), і повільна
        // машина розтягнула б послідовний цикл за межу вікна.
        var responses = await Task.WhenAll(Enumerable.Range(0, Permit + 1).Select(_ =>
            client.PostAsJsonAsync(ChangePassword, new { currentPassword = Password, newPassword = "short" })))
            .ConfigureAwait(true);

        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        var allowed = responses.Except(rejected).ToList();

        // ⛔ Мутаційний доказ: прибрати гілку change-password із глобального
        // обмежувача в `LoginRateLimiting` — 429 немає жодного.
        Assert.Single(rejected);
        Assert.All(allowed, r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));

        var body = await rejected[0].Content.ReadAsStringAsync().ConfigureAwait(true);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(ExpectedRejection, json.GetProperty("errorCode").GetString());
        Assert.NotNull(rejected[0].Headers.RetryAfter);

        // ⛔ Межа — на КОРИСТУВАЧА, не на адресу: у тестовому хості адреса одна
        // на всіх, тож межа за адресою відрізала б і сусіда.
        using var neighbourResponse = await neighbour.PostAsJsonAsync(
            ChangePassword, new { currentPassword = Password, newPassword = "short" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, neighbourResponse.StatusCode);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Хибний_чинний_пароль_N_разів_блокує_і_зміну_пароля_і_вхід_і_пише_аудит()
    {
        var name = await ArrangeUserAsync("cprl").ConfigureAwait(true);
        var max = (await PolicyAsync().ConfigureAwait(true)).MaxFailedAttempts;

        // Інакше межа частоти спрацювала б раніше за блокування і тест
        // перевіряв би не те.
        Assert.InRange(max, 1, Permit - 1);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, name).ConfigureAwait(true);

        for (var attempt = 1; attempt <= max; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync(
                ChangePassword, new { currentPassword = "definitely-not-it", newPassword = "Another-Password-2026" })
                .ConfigureAwait(true);
            var wrongBody = await wrong.Content.ReadAsStringAsync().ConfigureAwait(true);

            // Відповідь на хибний чинний пароль НЕ змінилася (контракт клієнта,
            // `ChangePasswordContractApiTests`) — і на пороговій спробі теж.
            Assert.True(wrong.StatusCode == HttpStatusCode.Unauthorized, $"спроба {attempt}: {wrong.StatusCode}: {wrongBody}");
            Assert.Equal(
                "err.ECR-AUTH-0401.currentPasswordWrong",
                JsonDocument.Parse(wrongBody).RootElement.GetProperty("messageKey").GetString());
        }

        // ⛔ Мутаційний доказ: прибрати `RegisterFailedAttemptAsync` із
        // `ChangePasswordHandler` — правильний пароль тут дає 422/204, а не 423.
        using var locked = await client.PostAsJsonAsync(
            ChangePassword, new { currentPassword = Password, newPassword = "short" }).ConfigureAwait(true);
        var lockedBody = await locked.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(locked.StatusCode == HttpStatusCode.Locked, $"зміна пароля: {locked.StatusCode}: {lockedBody}");
        Assert.Equal("ECR-AUTH-0423", JsonDocument.Parse(lockedBody).RootElement.GetProperty("errorCode").GetString());

        // Блокування одне: і вхід із ПРАВИЛЬНИМ паролем відхилено.
        using var fresh = app.CreateClient();
        using var login = await fresh.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Locked, login.StatusCode);

        // ⛔ Кожна спроба лишила подію аудиту — остання з причиною LockedOut.
        var events = await FailedEventsAsync(name).ConfigureAwait(true);
        Assert.Equal(max, events.Count);
        Assert.Contains("LockedOut", events[^1], StringComparison.Ordinal);
        Assert.All(events, e => Assert.DoesNotContain("definitely-not-it", e, StringComparison.Ordinal));
    }

    private async Task<string> ArrangeUserAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];

        await using var db = Context();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"вхід {name}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<PasswordPolicy> PolicyAsync()
    {
        await using var db = Context();
        return await db.PasswordPolicies.FirstOrDefaultAsync(p => p.Code == "Default").ConfigureAwait(false)
               ?? new PasswordPolicy("Default", minLength: 12, maxFailedAttempts: 5);
    }

    /// <summary>DetailsJson подій «хибний чинний пароль» цього користувача, за часом.</summary>
    private async Task<List<string>> FailedEventsAsync(string name)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.DetailsJson FROM aud.SecurityEvent e JOIN sec.[User] u ON u.Id = e.TargetUserId "
            + "WHERE u.UserName = @name AND e.EventType = @type ORDER BY e.ChangedAt, e.Id;";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@type", ChangePasswordHandler.PasswordChangeFailedEvent);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return result;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
