// tests/Ecr.Api.Tests/Security/SessionUserApiTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Api.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <see cref="SessionUserMiddleware"/> у справжньому конвеєрі (AN-108 / S2-05): вкладка, що вважає себе
/// іншим користувачем (<c>X-Ecr-User</c>), отримує <c>409 ECR-AUTH-0409</c> з причиною з каталогу; той
/// самий запит зі своїм id чи без заголовка — як і раніше.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: прибрати <c>UseMiddleware&lt;SessionUserMiddleware&gt;</c> з <c>Program.cs</c> —
/// червоніє перша перевірка; прибрати <c>err.ECR-AUTH-0409.sessionUserChanged</c> із сіду — перевірка
/// подробиці.
/// </remarks>
[Collection("SqlServer")]
public sealed class SessionUserApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-SessionUser-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "S2-05")]
    public async Task Вкладка_іншого_користувача_409_свій_id_і_без_заголовка_як_раніше()
    {
        var userName = await ArrangeUserAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"вхід: {login.StatusCode}: {app.ErrorsText}");

        var me = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true));
        var userId = me.RootElement.GetProperty("userId").GetInt32();

        using var foreign = await PatchAsync(client, (userId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(true);
        var body = await foreign.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(foreign.StatusCode == HttpStatusCode.Conflict, $"чужий id: {foreign.StatusCode}: {body}\n{app.ErrorsText}");
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-AUTH-0409", json.GetProperty("errorCode").GetString());
        Assert.Contains("different tab", json.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Свій id і без заголовка — та сама відповідь обробника, що й до фіксу.
        using var own = await PatchAsync(client, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(true);
        using var plain = await PatchAsync(client, null).ConfigureAwait(true);
        Assert.Equal(plain.StatusCode, own.StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, plain.StatusCode);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string? tabUser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri("/api/v1/documents/987654321/cells", UriKind.Relative))
        {
            Content = JsonContent.Create(new { tableInstanceId = 1, periodKey = 202601, origin = "UserEdit", rows = Array.Empty<object>() }),
        };
        if (tabUser is not null)
        {
            request.Headers.Add(SessionUserMiddleware.HeaderName, tabUser);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private async Task<string> ArrangeUserAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var name = $"su_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }
}
