// tests/Ecr.Api.Tests/Security/PasswordChangeGateUiStringsApiTests.cs
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
/// S16: із разовим паролем каталог рядків доступний лише на ЧИТАННЯ однієї
/// мови — наскрізно, через конвеєр (<c>PasswordChangeMiddleware</c>).
/// </summary>
/// <remarks>
/// ⛔ До фіксу ворота дозволяли весь префікс <c>/api/v1/ui-strings</c>, тож
/// <c>PUT {lang}/{key}</c> і <c>POST import</c> проходили повз них і
/// відмовляли (якщо відмовляли) лише правом — тобто для власника разового
/// пароля з правом <c>Localization.Manage</c> запис був відкритий.
/// </remarks>
[Collection("SqlServer")]
public sealed class PasswordChangeGateUiStringsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Gate-Once-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.18")]
    [Trait("Finding", "S16")]
    public async Task Разовий_пароль_запис_каталогу_428_читання_мови_200()
    {
        await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"gate_{_tag}", password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        var put = await client.PutAsJsonAsync(
            new Uri("/api/v1/ui-strings/en/common.save", UriKind.Relative),
            new { value = "Pwned" }).ConfigureAwait(true);
        await AssertGateAsync(put).ConfigureAwait(true);

        using var csv = new MultipartFormDataContent
        {
            { new StringContent("key,value\ncommon.save,Pwned\n"), "file", "strings.csv" },
        };
        var import = await client.PostAsync(
            new Uri("/api/v1/ui-strings/import?lang=en&dryRun=false", UriKind.Relative), csv).ConfigureAwait(true);
        await AssertGateAsync(import).ConfigureAwait(true);

        // Тексти екрана зміни пароля — як і досі доступні.
        var read = await client.GetAsync(new Uri("/api/v1/ui-strings/en?scope=private", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.True(read.StatusCode == HttpStatusCode.OK, $"{read.StatusCode}: {app.ErrorsText}");
    }

    private static async Task AssertGateAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == HttpStatusCode.PreconditionRequired, $"{response.StatusCode}: {body}");
        Assert.Equal("ECR-PWD-0428", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    private async Task ArrangeAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = new User($"gate_{_tag}", $"gate_{_tag}", AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        user.RequirePasswordChange();
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
