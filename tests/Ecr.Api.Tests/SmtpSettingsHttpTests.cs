// tests/Ecr.Api.Tests/SmtpSettingsHttpTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Налаштування SMTP у UI (D-263) крізь HTTP — кроки сценарію Н-О1/Н-О2
/// (<c>docs/build/TESTER-SCENARIOS-2026-10-01.md</c>): збереження чернетки без
/// пароля у відповіді, ключі відмов за полем, проба без транспорту.
/// </summary>
/// <remarks>
/// Рядок <c>sys_ecr.SmtpSettings</c> — один на систему, а база колекції спільна:
/// кожен тест прибирає його в <c>finally</c> і ніколи не вмикає налаштування
/// (<c>isEnabled=true</c> зробив би транспорт «налаштованим» для чужих тестів).
/// </remarks>
[Collection("SqlServer")]
public sealed class SmtpSettingsHttpTests(SqlServerFixture sql)
{
    private const string Permission = "System.ManageNotifications";

    private static readonly Uri Settings = new("/api/v1/notifications/smtp", UriKind.Relative);

    private static readonly Uri Probe = new("/api/v1/notifications/smtp/test", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Чернетка_з_паролем_зберігається_а_пароля_немає_ні_в_PUT_ні_в_GET()
    {
        const string secret = "S3cr3t-7c1e-pass";
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

            using var put = await client.PutAsJsonAsync(Settings, new
            {
                host = "smtp.corp.example",
                port = 2525,
                encryptionMode = "StartTls",
                fromAddress = "ecr@corp.example",
                fromName = "ECR",
                authMode = "Password",
                userName = "ecr-mailer",
                password = secret,
                clearPassword = false,
                isEnabled = false,
            }).ConfigureAwait(true);
            var putText = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(put.StatusCode == HttpStatusCode.OK, $"{put.StatusCode}: {putText}");

            var getText = await client.GetStringAsync(Settings).ConfigureAwait(true);
            var view = JsonDocument.Parse(getText).RootElement;

            Assert.Equal("smtp.corp.example", view.GetProperty("host").GetString());
            Assert.Equal(2525, view.GetProperty("port").GetInt32());
            Assert.True(view.GetProperty("hasPassword").GetBoolean());
            Assert.False(view.GetProperty("isEnabled").GetBoolean());

            // Вимкнені налаштування не діють: у фікстурі немає Smtp:Host — транспорту немає.
            Assert.Equal("none", view.GetProperty("source").GetString());
            Assert.False(view.GetProperty("configured").GetBoolean());

            Assert.DoesNotContain(secret, putText + getText, StringComparison.Ordinal);
            Assert.False(view.TryGetProperty("password", out _), "поле password не має віддаватися взагалі");
        }
        finally
        {
            await ClearAsync().ConfigureAwait(true);
        }
    }

    /// <remarks>
    /// Кожна відмова — <c>422 smtpSettingsInvalid</c> з ім'ям поля, яке підсвічує
    /// UI. Мутація: прибрати перевірку <c>CheckSqlServerAddress</c> для host —
    /// рядок <c>169.254.169.254</c> дає 200.
    /// </remarks>
    [Theory]
    [InlineData("smtp.corp.example", 0, "ecr@corp.example", "None", "", false, "port")]
    [InlineData("", 587, "ecr@corp.example", "None", "", true, "host")]
    [InlineData("169.254.169.254", 587, "ecr@corp.example", "None", "", false, "host")]
    [InlineData("smtp.corp.example", 587, "a@[127.0.0.1]", "None", "", false, "from")]
    [InlineData("smtp.corp.example", 587, "a@corp.example.", "None", "", false, "from")]
    [InlineData("smtp.corp.example", 587, "ecr@corp.example", "Password", "", false, "auth")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Невалідне_поле_дає_422_smtpSettingsInvalid_з_іменем_поля(
        string host, int port, string from, string authMode, string userName, bool isEnabled, string field)
    {
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

            using var put = await client.PutAsJsonAsync(Settings, new
            {
                host,
                port,
                encryptionMode = "StartTls",
                fromAddress = from,
                authMode,
                userName,
                isEnabled,
            }).ConfigureAwait(true);

            var text = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{put.StatusCode}: {text}");
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal("err.ECR-REQ-0422.smtpSettingsInvalid", problem.GetProperty("messageKey").GetString());
            Assert.Equal(field, problem.GetProperty("name").GetString());

            await using var db = Context();
            Assert.False(await db.SmtpSettings.AsNoTracking().AnyAsync().ConfigureAwait(true), "відмова не має нічого записати");
        }
        finally
        {
            await ClearAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Невідомий_режим_шифрування_рядком_дає_422_malformedRequest()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

        using var put = await client.PutAsJsonAsync(Settings, new
        {
            host = "smtp.corp.example",
            port = 587,
            encryptionMode = "Ssl",
            fromAddress = "ecr@corp.example",
            authMode = "None",
            isEnabled = false,
        }).ConfigureAwait(true);

        var text = await put.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(put.StatusCode == HttpStatusCode.UnprocessableEntity, $"{put.StatusCode}: {text}");
        Assert.Equal("err.ECR-REQ-0422.malformedRequest", JsonDocument.Parse(text).RootElement.GetProperty("messageKey").GetString());
    }

    /// <remarks>
    /// Без транспорту проба — не відмова запиту, а <c>200 {ok:false}</c> з ключем,
    /// який UI показує під кнопкою. Список адрес — <c>422 smtpTestRecipientInvalid</c>
    /// (проба не розсилка).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_без_транспорту_200_ok_false_smtpNotConfigured_а_дві_адреси_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, Permission).ConfigureAwait(true);

        using var probe = await client.PostAsJsonAsync(Probe, new { to = "tester@corp.example" }).ConfigureAwait(true);
        var text = await probe.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(probe.StatusCode == HttpStatusCode.OK, $"{probe.StatusCode}: {text}");
        var result = JsonDocument.Parse(text).RootElement;
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("notifications.test.smtpNotConfigured", result.GetProperty("messageKey").GetString());

        using var list = await client.PostAsJsonAsync(Probe, new { to = "a@corp.example, b@corp.example" }).ConfigureAwait(true);
        var listText = await list.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(list.StatusCode == HttpStatusCode.UnprocessableEntity, $"{list.StatusCode}: {listText}");
        var problem = JsonDocument.Parse(listText).RootElement;
        Assert.Equal("err.ECR-REQ-0422.smtpTestRecipientInvalid", problem.GetProperty("messageKey").GetString());
        Assert.Equal("to", problem.GetProperty("name").GetString());
    }

    private async Task ClearAsync()
    {
        await using var db = Context();
        await db.SmtpSettings.ExecuteDeleteAsync().ConfigureAwait(true);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
