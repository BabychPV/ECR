// tests/Ecr.Api.Tests/Security/IdleSignOutApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// D-471: вихід після бездіяльності тримає СЕРВЕР — опитування (GET) сеанс не продовжують, дія людини продовжує,
/// межа з налаштування діє для чинних сеансів, вихід лишає подію <c>SignedOut</c> з причиною.
/// </summary>
[Collection("SqlServer")]
public sealed class IdleSignOutApiTests(SqlServerFixture sql) : IDisposable
{
    private const string Password = "Api-Idle-Signout-2026!";

    private static readonly Uri Me = new("/api/v1/me", UriKind.Relative);
    private static readonly Uri Activity = new("/api/v1/session/activity", UriKind.Relative);
    private static readonly Uri Settings = new("/api/v1/security/session-settings", UriKind.Relative);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-471")]
    public async Task Вкладка_що_лише_опитує_виходить_за_межею_з_причиною_idle()
    {
        var userId = await ArrangeUserAsync().ConfigureAwait(true);
        var time = new ShiftedTime();
        using var app = CreateApp(time, minutes: 30);
        using var client = app.CreateClient();
        Assert.True((await LoginAsync(client).ConfigureAwait(true)).IsSuccessStatusCode);

        // Опитування кожні 20 хв: до D-471 ковзний строк продовжувався кожним із них.
        time.Offset = TimeSpan.FromMinutes(20);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        time.Offset = TimeSpan.FromMinutes(40);
        var expired = await client.GetAsync(Me).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        var body = await expired.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
        Assert.Equal("ECR-AUTH-0401", body.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-AUTH-0401.idleTimeout", body.GetProperty("messageKey").GetString());
        Assert.Equal(30, body.GetProperty("idleMinutes").GetInt32());

        // Cookie стерта тією ж відповіддю: далі — звичайний «не входив», сеанс не воскресає.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        // Журнал: рівно ОДИН вихід через бездіяльність, помічений сервером.
        var events = await SignedOutEventsAsync(userId).ConfigureAwait(true);
        var idle = Assert.Single(events);
        Assert.Contains("\"reason\":\"idle\"", idle, StringComparison.Ordinal);
        Assert.Contains("\"by\":\"server\"", idle, StringComparison.Ordinal);
        Assert.Contains("\"idleTimeoutMinutes\":30", idle, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-471")]
    public async Task Сигнал_активності_людини_продовжує_сеанс()
    {
        await ArrangeUserAsync().ConfigureAwait(true);
        var time = new ShiftedTime();
        using var app = CreateApp(time, minutes: 30);
        using var client = app.CreateClient();
        Assert.True((await LoginAsync(client).ConfigureAwait(true)).IsSuccessStatusCode);

        time.Offset = TimeSpan.FromMinutes(20);
        var touch = await client.PostAsync(Activity, content: null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, touch.StatusCode);
        Assert.True(touch.Headers.Contains("Set-Cookie"), "Дія людини мусить перевидати cookie з новим моментом дії.");

        // 40 хв від входу, 20 — від дії: живий.
        time.Offset = TimeSpan.FromMinutes(40);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        // 55 хв від входу, 35 — від дії (опитування о 40-й не рахується): вихід.
        time.Offset = TimeSpan.FromMinutes(55);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-471")]
    public async Task Фоновий_запит_що_змінює_стан_не_продовжує_сеанс()
    {
        await ArrangeUserAsync().ConfigureAwait(true);
        var time = new ShiftedTime();
        using var app = CreateApp(time, minutes: 30);
        using var client = app.CreateClient();
        Assert.True((await LoginAsync(client).ConfigureAwait(true)).IsSuccessStatusCode);

        time.Offset = TimeSpan.FromMinutes(20);
        using (var background = new HttpRequestMessage(HttpMethod.Post, Activity))
        {
            background.Headers.Add("X-Ecr-Background", "1");
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(background).ConfigureAwait(true)).StatusCode);
        }

        time.Offset = TimeSpan.FromMinutes(31);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-471")]
    public async Task Налаштування_зберігається_журналюється_і_діє_для_чинного_сеансу_без_перезапуску()
    {
        var managerId = await ArrangeUserAsync(withPermission: "Security.ManageUsers").ConfigureAwait(true);
        var time = new ShiftedTime();
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = time)));
        using var client = app.CreateClient();

        try
        {
            Assert.True((await LoginAsync(client).ConfigureAwait(true)).IsSuccessStatusCode);

            var before = await client.GetFromJsonAsync<SessionSettingsView>(Settings).ConfigureAwait(true);
            Assert.NotNull(before);
            Assert.Equal(5, before.MinIdleTimeoutMinutes);
            Assert.Equal(480, before.MaxIdleTimeoutMinutes);

            // Поза межами — 422 з ключем, нічого не записано.
            var tooShort = await client.PutAsJsonAsync(Settings, new SessionSettingsInput(4)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
            var problem = await tooShort.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
            Assert.Equal("err.ECR-REQ-0422.idleTimeoutOutOfRange", problem.GetProperty("messageKey").GetString());

            var saved = await client.PutAsJsonAsync(Settings, new SessionSettingsInput(10)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(10, (await saved.Content.ReadFromJsonAsync<SessionSettingsView>().ConfigureAwait(true))!.IdleTimeoutMinutes);

            var me = await client.GetFromJsonAsync<JsonElement>(Me).ConfigureAwait(true);
            Assert.Equal(10, me.GetProperty("idleTimeoutMinutes").GetInt32());

            Assert.Contains(
                "\"idleTimeoutMinutes\":10",
                await LatestEventAsync("SessionSettingsUpdated", managerId).ConfigureAwait(true),
                StringComparison.Ordinal);

            // Сеанс відкрито ДО зміни (30 хв на вході); PUT о 0-й — дія людини. 15 хв без дій > нових 10.
            time.Offset = TimeSpan.FromMinutes(15);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Me).ConfigureAwait(true)).StatusCode);
        }
        finally
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sys_ecr.SystemSetting WHERE [Key] = @key;";
            command.Parameters.AddWithValue("@key", SessionSettings.IdleTimeoutKey);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
            app.Services.GetRequiredService<IIdleTimeoutPolicy>().Invalidate();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-471")]
    public async Task Вихід_таймером_вкладки_журналюється_як_idle_і_не_виводить_інші_пристрої()
    {
        var userId = await ArrangeUserAsync().ConfigureAwait(true);
        var time = new ShiftedTime();
        using var app = CreateApp(time, minutes: 30);
        using var tab = app.CreateClient();
        using var laptop = app.CreateClient();
        Assert.True((await LoginAsync(tab).ConfigureAwait(true)).IsSuccessStatusCode);
        Assert.True((await LoginAsync(laptop).ConfigureAwait(true)).IsSuccessStatusCode);

        var idle = await tab.PostAsync(new Uri("/api/v1/logout/idle", UriKind.Relative), content: null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, idle.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tab.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        // Штамп не прокручено: інший пристрій тієї самої людини працює далі.
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync(Me).ConfigureAwait(true)).StatusCode);

        // Звичайний вихід — причина `user`.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await laptop.PostAsync(new Uri("/api/v1/logout", UriKind.Relative), content: null).ConfigureAwait(true)).StatusCode);

        var events = await SignedOutEventsAsync(userId).ConfigureAwait(true);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Contains("\"reason\":\"idle\"", StringComparison.Ordinal)
                                     && e.Contains("\"by\":\"client\"", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Contains("\"reason\":\"user\"", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private readonly List<EcrApiFactory> _factories = [];

    private WebApplicationFactory<Program> CreateApp(ShiftedTime time, int minutes)
    {
        var factory = new EcrApiFactory(sql);
        _factories.Add(factory);
        return factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, options => options.TimeProvider = time);
            services.RemoveAll<IIdleTimeoutPolicy>();
            services.AddSingleton<IIdleTimeoutPolicy>(new FixedPolicy(minutes));
        }));
    }

    private Task<HttpResponseMessage> LoginAsync(HttpClient client)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = $"idle_{_tag}", password = Password });

    private async Task<int> ArrangeUserAsync(string? withPermission = null)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var user = new User($"idle_{_tag}", $"idle_{_tag}", AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withPermission is not null)
        {
            var role = new Role(
                EcrCode.Create($"I{_tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Idle sign-out test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RolePermissions.Add(new RolePermission(role.Id, withPermission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return user.Id;
    }

    private async Task<List<string>> SignedOutEventsAsync(int userId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DetailsJson FROM aud.SecurityEvent
             WHERE EventType = N'SignedOut' AND TargetUserId = @user AND ChangedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
             ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@user", userId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<string> LatestEventAsync(string eventType, int byUserId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
             WHERE EventType = @type AND ChangedByUserId = @user AND ChangedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
             ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@user", byUserId);
        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false) ?? "{}";
    }

    /// <summary>Межа бездіяльності, незалежна від спільної тестової бази.</summary>
    internal sealed class FixedPolicy(int minutes) : IIdleTimeoutPolicy
    {
        public int MinimumMinutes => SessionSettings.MinIdleTimeoutMinutes;

        public ValueTask<int> GetMinutesAsync(CancellationToken ct) => ValueTask.FromResult(minutes);

        public void Invalidate()
        {
        }
    }

    /// <summary>Годинник cookie, зсунутий уперед на задану величину.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
