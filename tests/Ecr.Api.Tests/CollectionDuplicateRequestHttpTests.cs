// tests/Ecr.Api.Tests/CollectionDuplicateRequestHttpTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// B158 (лінія c): два ОДНАКОВІ запити створення наскрізно через HTTP — послідовно і справді одночасно (подвійний
/// клік, дві вкладки). Обробники перевіряють «чи вже є» без блокування й лише потім вставляють, тож обидва одночасні
/// запити проходять перевірку, і другого відбиває унікальний індекс. Кожен тест вимагає: рівно один успіх, а другий —
/// ТА САМА відмова, що й послідовний повтор (статус, код, <c>messageKey</c> і аргументи), ніколи не 5xx і не два рядки.
/// </summary>
/// <remarks>
/// ⚠ Як зроблено «одночасно» — той самий прийом, що в B150 (<c>DuplicateRequestHttpTests</c>): обидва запити несуть
/// заголовок <see cref="MeetHeader"/>, проміжне ПЗ тесту на вході конвеєра тримає перший, доки не прийде другий, і
/// відпускає обидва разом (асерт <c>Overlapped</c>). Далі — справжній конвеєр без підмін. Застосунок один на клас.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class CollectionDuplicateRequestHttpTests(
    SqlServerFixture sql, CollectionDuplicateRequestHttpTests.SharedApp shared, ITestOutputHelper output)
    : IClassFixture<CollectionDuplicateRequestHttpTests.SharedApp>
{
    private const string Password = "Dup-Request-B158c-2099!";
    private const string MeetHeader = "X-Test-Meet";

    private EcrApiFactory App => shared.Factory;

    // ───────────────────────────── c1: одиниці ─────────────────────────────

    /// <summary>
    /// c1: <c>POST /units</c> двічі одночасно — один <c>201</c>, другий та сама відмова <c>ECR-UOM-4091 unitCodeTaken</c>
    /// з <c>{code, id}</c> переможця, що й послідовний повтор (раніше — 500 на <c>UQ_Unit_Code</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c1_Створення_одиниці_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Uom.EditCatalog"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(string code) => new
        {
            code,
            symbolL10n = new Dictionary<string, string> { ["en"] = code },
            nameL10n = new Dictionary<string, string> { ["en"] = code },
            dimensionId = 1,
            factorToBase = 2.5m,
            offsetToBase = 0m,
        };

        // Послідовно.
        var code = $"u{Guid.NewGuid():N}"[..10];
        var created = await SendAsync(first, HttpMethod.Post, "/api/v1/units", Body(code)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, "/api/v1/units", Body(code)).ConfigureAwait(true);

        Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
        AssertRefusal(repeated, repeated.Status, "ECR-UOM-4091", "err.ECR-UOM-4091.unitCodeTaken", ("code", code), ("id", created.Id));

        // Одночасно.
        var raced = $"u{Guid.NewGuid():N}"[..10];
        var replies = await RaceAsync("units", first, second, "/api/v1/units", Body(raced)).ConfigureAwait(true);

        AssertNoServerError(replies);
        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        AssertRefusal(
            replies.Single(r => r.Status != HttpStatusCode.Created),
            repeated.Status, "ECR-UOM-4091", "err.ECR-UOM-4091.unitCodeTaken", ("code", raced), ("id", winner.Id));

        await using var db = Db();
        Assert.Equal(1, await db.Units.CountAsync(u => u.Code == raced).ConfigureAwait(true));
    }

    // ───────────────────────────── c2: канали сповіщень ─────────────────────────────

    /// <summary>
    /// c2: <c>POST /notifications/channels</c> двічі одночасно — один <c>201</c>, другий та сама відмова
    /// <c>ECR-REQ-0422 notificationChannelNameTaken</c> з <c>{name}</c> (раніше — 500 на <c>UQ_NotificationChannel_Name</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c2_Створення_каналу_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["System.ManageNotifications"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(string name) => new { kind = "TeamsWebhook", name, settings = new { title = "ECR" } };
        const string url = "/api/v1/notifications/channels";

        // Послідовно.
        var name = $"b158c-{Guid.NewGuid():N}";
        var created = await SendAsync(first, HttpMethod.Post, url, Body(name)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, url, Body(name)).ConfigureAwait(true);

        Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
        AssertRefusal(repeated, repeated.Status, "ECR-REQ-0422", "err.ECR-REQ-0422.notificationChannelNameTaken", ("name", name));

        // Одночасно.
        var raced = $"b158c-{Guid.NewGuid():N}";
        var replies = await RaceAsync("channels", first, second, url, Body(raced)).ConfigureAwait(true);

        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        AssertRefusal(
            replies.Single(r => r.Status != HttpStatusCode.Created),
            repeated.Status, "ECR-REQ-0422", "err.ECR-REQ-0422.notificationChannelNameTaken", ("name", raced));

        await using var db = Db();
        Assert.Equal(1, await db.NotificationChannels.CountAsync(c => c.Name == raced).ConfigureAwait(true));
    }

    // ───────────────────────────── c3: джерела даних ─────────────────────────────

    /// <summary>
    /// c3: <c>POST /data-sources</c> двічі одночасно — один <c>201</c>, другий та сама відмова
    /// <c>ECR-REQ-0422 dataSourceCodeTaken</c> з <c>{code}</c> (раніше — 500 на <c>UQ_DataSource</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158")]
    public async Task c3_Створення_джерела_даних_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Integration.Manage"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(string code) => new
        {
            code,
            nameL10n = new Dictionary<string, string> { ["en"] = "B158c source" },
            transport = "PiWebApi",
            endpoint = "https://pi.corp.example/piwebapi",
            catalog = "EcrDb",
            maxParallel = 4,
            isActive = true,
            secretConfirmation = (string?)null,
        };

        const string url = "/api/v1/data-sources";
        var code = $"DS{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var raced = $"DS{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        try
        {
            // Послідовно.
            var created = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);
            var repeated = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);

            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            AssertRefusal(repeated, repeated.Status, "ECR-REQ-0422", "err.ECR-REQ-0422.dataSourceCodeTaken", ("code", code));

            // Одночасно.
            var replies = await RaceAsync("data-sources", first, second, url, Body(raced)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
            AssertRefusal(
                replies.Single(r => r.Status != HttpStatusCode.Created),
                repeated.Status, "ECR-REQ-0422", "err.ECR-REQ-0422.dataSourceCodeTaken", ("code", raced));

            await using var db = Db();
            Assert.Equal(1, await db.DataSources.CountAsync(s => s.Code == raced).ConfigureAwait(true));
        }
        finally
        {
            // Активне джерело без збору фарбувало б `SourcesHealthCheck` інших тестів спільної бази.
            await using var db = Db();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE ext.DataSource SET IsActive = 0 WHERE Code IN ({code}, {raced})").ConfigureAwait(true);
        }
    }

    // ───────────────────────────── запити ─────────────────────────────

    /// <summary>Відповідь сервера: код, поля тіла помилки й саме тіло.</summary>
    private sealed record Reply(HttpStatusCode Status, string? ErrorCode, string? MessageKey, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        /// <summary>Ідентифікатор створеного (поле <c>id</c>) рядком — так його несуть аргументи відмов.</summary>
        public string Id => Json.GetProperty("id").GetRawText();

        /// <summary>Аргумент відмови рядком (рядок — без лапок, решта — як у JSON); <c>null</c> — поля немає.</summary>
        public string? Arg(string name)
            => !Json.TryGetProperty(name, out var value) ? null
                : value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

        public override string ToString() => $"{(int)Status} {Body}";
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string? meet = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (meet is not null)
        {
            request.Headers.Add(MeetHeader, meet);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.IsSuccessStatusCode || text.Length == 0 || text[0] != '{')
        {
            return new Reply(response.StatusCode, null, null, text);
        }

        var root = JsonDocument.Parse(text).RootElement;
        return new Reply(response.StatusCode, Text(root, "errorCode"), Text(root, "messageKey"), text);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Два однакові <c>POST</c>, відпущені в конвеєр РАЗОМ (проміжне ПЗ тесту чекає на обидва).</summary>
    private async Task<Reply[]> RaceAsync(string label, HttpClient first, HttpClient second, string url, object body)
    {
        var key = Guid.NewGuid().ToString("N");
        var meeting = shared.Meetings.GetOrAdd(key, _ => new Meeting());

        var replies = await Task.WhenAll(
            Task.Run(() => SendAsync(first, HttpMethod.Post, url, body, key)),
            Task.Run(() => SendAsync(second, HttpMethod.Post, url, body, key))).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            output.WriteLine($"RACE {label} {(int)reply.Status} {reply.ErrorCode} {reply.MessageKey}");
        }

        Assert.True(meeting.Overlapped, $"{label}: запити не зустрілися на вході конвеєра");
        return replies;
    }

    private void AssertNoServerError(Reply[] replies)
        => Assert.True(
            replies.All(r => (int)r.Status < 500),
            $"5xx на однакових одночасних запитах:\n{replies[0]}\n{replies[1]}\n{App.ErrorsText}");

    /// <summary>Відмова — той самий статус, код, ключ і аргументи (за іменем і значенням), що й послідовна.</summary>
    private void AssertRefusal(
        Reply reply, HttpStatusCode status, string errorCode, string messageKey, params (string Name, string Value)[] args)
    {
        Assert.True((int)status is >= 400 and < 500, $"послідовний повтор — не відмова 4xx: {reply}\n{App.ErrorsText}");
        Assert.True(reply.Status == status, $"очікувалось {(int)status}: {reply}\n{App.ErrorsText}");
        Assert.Equal(errorCode, reply.ErrorCode);
        Assert.Equal(messageKey, reply.MessageKey);

        foreach (var (name, value) in args)
        {
            Assert.True(string.Equals(value, reply.Arg(name), StringComparison.Ordinal), $"аргумент {name}: очікувалось «{value}»: {reply}");
        }
    }

    private EcrDbContext Db() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Свіжий користувач із власною роллю (права й гранти) і два його сеанси — «дві вкладки».</summary>
    private async Task<(HttpClient First, HttpClient Second)> SignInTwiceAsync(
        string[] permissions, params (ResourceKind Kind, int ResourceId, GrantLevel Level)[] grants)
    {
        var name = $"dqc_{Guid.NewGuid():N}"[..20];

        await using (var db = Db())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("B158c duplicate request"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            foreach (var (kind, resourceId, level) in grants)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, kind, resourceId, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return (await SignInAsync(name).ConfigureAwait(false), await SignInAsync(name).ConfigureAwait(false));
    }

    private async Task<HttpClient> SignInAsync(string userName)
    {
        var client = App.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {App.ErrorsText}");

        return client;
    }

    // ───────────────────────────── застосунок класу ─────────────────────────────

    /// <summary>Зустріч двох запитів: перший чекає на другий (до 20 с), далі обидва йдуть разом.</summary>
    public sealed class Meeting
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;

        /// <summary>Обидва запити дійшли до входу конвеєра, поки жоден не пішов далі сам.</summary>
        public bool Overlapped => both.Task.IsCompletedSuccessfully;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref arrived) >= 2)
            {
                both.TrySetResult();
            }

            try
            {
                await both.Task.WaitAsync(Patience).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Другий запит не прийшов: перший іде далі сам, а тест упаде на асерті `Overlapped`.
            }
        }
    }

    /// <summary>Один застосунок на клас: проміжне ПЗ зустрічі двох запитів на вході конвеєра.</summary>
    public sealed class SharedApp : IDisposable
    {
        public SharedApp(SqlServerFixture sql)
        {
            Factory = new EcrApiFactory(sql)
            {
                ExtraServices = services => services.AddSingleton<IStartupFilter>(new MeetingStartupFilter(Meetings)),
            };
        }

        public EcrApiFactory Factory { get; }

        public ConcurrentDictionary<string, Meeting> Meetings { get; } = new(StringComparer.Ordinal);

        public void Dispose() => Factory.Dispose();
    }

    /// <summary>Запит із заголовком зустрічі чекає на свою пару на вході конвеєра; решта проходять як є.</summary>
    public sealed class MeetingStartupFilter(ConcurrentDictionary<string, Meeting> meetings) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, following) =>
                {
                    if (context.Request.Headers.TryGetValue(MeetHeader, out var key)
                        && meetings.TryGetValue(key.ToString(), out var meeting))
                    {
                        await meeting.ArriveAsync().ConfigureAwait(false);
                    }

                    await following(context).ConfigureAwait(false);
                });

                next(app);
            };
    }
}
