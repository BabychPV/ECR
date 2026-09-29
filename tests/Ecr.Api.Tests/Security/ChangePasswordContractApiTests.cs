// tests/Ecr.Api.Tests/Security/ChangePasswordContractApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// V-16 (UX-прохід, третій раунд): те, на що спирається клієнт
/// (`api/client.ts`, `isFormAnswer401`; `ChangePasswordPage`), — наскрізно.
/// </summary>
/// <remarks>
/// ⚠ Клієнт відрізняє «хибний поточний пароль» від «сеанс скінчився» лише за
/// <c>messageKey</c>: обидва — <c>401 ECR-AUTH-0401</c>. Якщо ключ зникне з
/// тіла, клієнт знову виводитиме з системи — і помітить це саме цей тест.
/// </remarks>
[Collection("SqlServer")]
public sealed class ChangePasswordContractApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Change-Pwd-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Хибний_поточний_пароль_401_з_ключем_currentPasswordWrong_і_сеанс_живий()
    {
        await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/change-password", UriKind.Relative),
            new { currentPassword = "definitely-not-it", newPassword = "Another-Password-2026" }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-AUTH-0401", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-AUTH-0401.currentPasswordWrong", problem.GetProperty("messageKey").GetString());

        // Сеанс не зачеплено: саме тому клієнт не має виводити з системи.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_сервера_лише_довжина_пароль_без_малих_літер_приймається()
    {
        // ⚠ Підказка `password.policy` мусить казати саме це правило: до V-16
        // вона вимагала великі, малі й цифру, а сервер приймав `FSEC-NOLOWER-2026`.
        await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app).ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/change-password", UriKind.Relative),
            new { currentPassword = Password, newPassword = "NOLOWER-NODIGIT-X" }).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}");
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"cpw_{_tag}", password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task ArrangeAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = new User($"cpw_{_tag}", $"cpw_{_tag}", AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
