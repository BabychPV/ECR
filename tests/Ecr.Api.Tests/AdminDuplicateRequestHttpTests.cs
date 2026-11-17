using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// B158 (лінія d): два ОДНАКОВІ запити (або два різні в один ключ — d9) наскрізно через HTTP, послідовно і справді
/// одночасно. Обробники перевіряють «чи вже є» без блокування й лише потім вставляють, тож обидва одночасні запити
/// проходять перевірку, і другого відбиває унікальний індекс (доти — голий 500). Кожен тест вимагає: жодного 5xx, один
/// рядок у базі, а другий запит — ТА САМА відповідь, що й послідовний повтор (статус, код, <c>messageKey</c>, аргументи).
/// </summary>
/// <remarks>
/// ⚠ Де послідовний повтор — не відмова, а успіх (upsert: d2 рівні ієрархії, d3 значення запису, d8 переклад), переможений
/// має або той самий успіх, або загальний <c>409 ECR-CELL-0409 concurrentChange</c> — ту саму відповідь, яку дає будь-яка
/// інша одночасна зміна того самого стану; повторний запит після неї проходить.
/// <para>
/// «Одночасно» — прийом B150/B158c: обидва запити несуть заголовок <see cref="MeetHeader"/>, проміжне ПЗ тесту на вході
/// конвеєра тримає перший, доки не прийде другий, і відпускає обидва разом (асерт <c>Overlapped</c>). Далі — справжній
/// конвеєр без підмін. Застосунок один на клас; d4 (профілі імпорту AIR) — у <c>AirLimitsImportHttpTests</c>, бо
/// потребує розгорнутого типу AIR того класу.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class AdminDuplicateRequestHttpTests(
    SqlServerFixture sql, AdminDuplicateRequestHttpTests.SharedApp shared, ITestOutputHelper output)
    : IClassFixture<AdminDuplicateRequestHttpTests.SharedApp>
{
    /// <summary>Заголовок зустрічі двох запитів на вході конвеєра.</summary>
    internal const string MeetHeader = "X-Test-Meet-B158d";

    private const string Password = "Dup-Request-B158d-2099!";

    private EcrApiFactory App => shared.Factory;

    // ───────────────────────────── d1: довідники ─────────────────────────────

    /// <summary>
    /// d1: <c>POST /registries</c> двічі одночасно — один <c>201</c>, другий та сама відмова <c>ECR-REG-4091
    /// registryCodeTaken</c> з <c>{code, id}</c> переможця, що й послідовний повтор (раніше — 500 на <c>UQ_RegistryDef</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d1_Створення_довідника_двічі_одночасно_одне_201_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditDefinition"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(string code)
            => new { code, nameL10n = new Dictionary<string, string> { ["en"] = "B158d registry" }, isTemporal = false };
        const string url = "/api/v1/registries";

        // Послідовно.
        var code = NewCode("RD");
        var created = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, url, Body(code)).ConfigureAwait(true);

        Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
        AssertRefusal(repeated, repeated.Status, "ECR-REG-4091", "err.ECR-REG-4091.registryCodeTaken", ("code", code), ("id", created.Id));

        // Одночасно.
        var raced = NewCode("RD");
        var replies = await RaceAsync(
            "registries",
            meet => SendAsync(first, HttpMethod.Post, url, Body(raced), meet),
            meet => SendAsync(second, HttpMethod.Post, url, Body(raced), meet)).ConfigureAwait(true);

        AssertNoServerError(replies);
        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        AssertRefusal(
            replies.Single(r => r.Status != HttpStatusCode.Created),
            repeated.Status, "ECR-REG-4091", "err.ECR-REG-4091.registryCodeTaken", ("code", raced), ("id", winner.Id));

        await using var db = Db();
        Assert.Equal(1, await db.RegistryDefs.CountAsync(d => d.Code == raced).ConfigureAwait(true));
    }

    // ───────────────────────────── d2: рівні ієрархії ─────────────────────────────

    /// <summary>
    /// d2: <c>PUT /registries/{code}/hierarchy</c> (перші рівні плаского довідника) двічі одночасно — жодного 500 на
    /// <c>UQ_RegistryLevelDef_Code</c>/<c>_Ordinal</c>: переможений — або той самий <c>200</c>, що й послідовний повтор, або
    /// загальний <c>409 concurrentChange</c>; рівнів рівно стільки, скільки в запиті.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d2_Рівні_ієрархії_двічі_одночасно_без_500_і_без_дублів()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditDefinition"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Level(string code)
            => new { code, name = new { values = new Dictionary<string, string> { ["en"] = code } }, isOptional = false };
        var body = new { isHierarchical = true, levels = new[] { Level("SITE"), Level("SRC") } };
        static string Url(string code) => $"/api/v1/registries/{code}/hierarchy";

        // Послідовно: повтор того самого PUT — той самий 200.
        var (_, code) = await NewRegistryAsync(withField: null).ConfigureAwait(true);
        var saved = await SendAsync(first, HttpMethod.Put, Url(code), body).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Put, Url(code), body).ConfigureAwait(true);

        Assert.True(saved.Status == HttpStatusCode.OK, $"{saved}\n{App.ErrorsText}");
        Assert.True(repeated.Status == HttpStatusCode.OK, $"{repeated}\n{App.ErrorsText}");

        // Одночасно.
        var (racedId, raced) = await NewRegistryAsync(withField: null).ConfigureAwait(true);
        var replies = await RaceAsync(
            "hierarchy",
            meet => SendAsync(first, HttpMethod.Put, Url(raced), body, meet),
            meet => SendAsync(second, HttpMethod.Put, Url(raced), body, meet)).ConfigureAwait(true);

        AssertNoServerError(replies);
        AssertSameSuccessOrConcurrent(replies, repeated.Status);

        await using var db = Db();
        Assert.Equal(2, await db.RegistryLevelDefs.CountAsync(l => l.RegistryDefId == racedId).ConfigureAwait(true));
    }

    // ───────────────────────────── d3: значення запису довідника ─────────────────────────────

    /// <summary>
    /// d3: <c>POST /registries/{code}/entries</c> — правка наявного запису, що вперше заповнює порожнє поле, — двічі
    /// одночасно: жодного 500 на <c>UQ_RegistryValue</c>; переможений — той самий <c>200</c>, що й послідовний повтор, або
    /// загальний <c>409 concurrentChange</c>; значення поля одне.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d3_Значення_запису_двічі_одночасно_без_500_і_без_дублів()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditData"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (registryId, code) = await NewRegistryAsync(withField: "NOTE").ConfigureAwait(true);
        var url = $"/api/v1/registries/{code}/entries";

        object Body(long? id, string entryCode, IReadOnlyDictionary<string, object?> values) => new
        {
            id,
            registryDefId = registryId,
            code = entryCode,
            display = new { values = new { en = entryCode } },
            parentEntryId = (long?)null,
            values,
        };

        var empty = new Dictionary<string, object?>();
        var note = new Dictionary<string, object?> { ["NOTE"] = "B158d" };

        async Task<long> NewEntryAsync(string entryCode)
        {
            var created = await SendAsync(first, HttpMethod.Post, url, Body(null, entryCode, empty)).ConfigureAwait(true);
            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            return created.Json.GetProperty("id").GetInt64();
        }

        // Послідовно: повтор тієї самої правки — той самий 200.
        var e1 = await NewEntryAsync("E1");
        var saved = await SendAsync(first, HttpMethod.Post, url, Body(e1, "E1", note)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, url, Body(e1, "E1", note)).ConfigureAwait(true);

        Assert.True(saved.Status == HttpStatusCode.OK, $"{saved}\n{App.ErrorsText}");
        Assert.True(repeated.Status == HttpStatusCode.OK, $"{repeated}\n{App.ErrorsText}");

        // Одночасно.
        var e2 = await NewEntryAsync("E2");
        var replies = await RaceAsync(
            "entry-values",
            meet => SendAsync(first, HttpMethod.Post, url, Body(e2, "E2", note), meet),
            meet => SendAsync(second, HttpMethod.Post, url, Body(e2, "E2", note), meet)).ConfigureAwait(true);

        AssertNoServerError(replies);
        AssertSameSuccessOrConcurrent(replies, repeated.Status);

        await using var db = Db();
        Assert.Equal(1, await db.RegistryValues.CountAsync(v => v.RegistryEntryId == e2).ConfigureAwait(true));
    }

    // ───────────────────────────── d5: чернетка опису довідника ─────────────────────────────

    /// <summary>
    /// d5: перше <c>PUT /registries/{code}/definition/draft</c> (без версії чернетки) двічі одночасно — один <c>200</c>,
    /// другий та сама відмова <c>ECR-REG-0409 definitionDraftChanged</c> з <c>{registryCode, rowVersion}</c> чернетки
    /// переможця, що й послідовний повтор (раніше — 500 на <c>PK_RegistryDefinitionDraft</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d5_Перша_чернетка_опису_двічі_одночасно_одне_200_друге_та_сама_відмова_що_й_послідовно()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditDefinition"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        static object Body(int fieldId) => new
        {
            fields = new[]
            {
                new
                {
                    id = fieldId, code = "KEY", nameL10n = new { values = new { en = "Key (draft)" } },
                    dataType = "String", ordinal = 1, isRequired = false, isKey = true,
                    lookupRegistryDefId = (int?)null, unitId = (int?)null,
                },
            },
            rules = Array.Empty<object>(),
            reason = "B158d draft",
            rowVersion = (string?)null,
        };
        static string Url(string code) => $"/api/v1/registries/{code}/definition/draft";
        const string ifMatch = "\"1\"";

        // Послідовно.
        var (_, code, fieldId) = await NewRegistryWithKeyAsync().ConfigureAwait(true);
        var saved = await SendAsync(first, HttpMethod.Put, Url(code), Body(fieldId), ifMatch: ifMatch).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Put, Url(code), Body(fieldId), ifMatch: ifMatch).ConfigureAwait(true);

        Assert.True(saved.Status == HttpStatusCode.OK, $"{saved}\n{App.ErrorsText}");
        AssertRefusal(
            repeated, repeated.Status, "ECR-REG-0409", "err.ECR-REG-0409.definitionDraftChanged",
            ("registryCode", code), ("rowVersion", saved.Json.GetProperty("rowVersion").GetString()!));

        // Одночасно.
        var (racedId, raced, racedField) = await NewRegistryWithKeyAsync().ConfigureAwait(true);
        var replies = await RaceAsync(
            "definition-draft",
            meet => SendAsync(first, HttpMethod.Put, Url(raced), Body(racedField), meet, ifMatch),
            meet => SendAsync(second, HttpMethod.Put, Url(raced), Body(racedField), meet, ifMatch)).ConfigureAwait(true);

        AssertNoServerError(replies);
        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        AssertRefusal(
            replies.Single(r => r.Status != HttpStatusCode.OK),
            repeated.Status, "ECR-REG-0409", "err.ECR-REG-0409.definitionDraftChanged",
            ("registryCode", raced), ("rowVersion", winner.Json.GetProperty("rowVersion").GetString()!));

        await using var db = Db();
        Assert.Equal(1, await db.RegistryDefinitionDrafts.CountAsync(d => d.RegistryDefId == racedId).ConfigureAwait(true));
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

    private static async Task<Reply> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body, string? meet = null, string? ifMatch = null)
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

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
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

    /// <summary>Два запити, відпущені в конвеєр РАЗОМ (проміжне ПЗ тесту чекає на обидва).</summary>
    private async Task<Reply[]> RaceAsync(string label, Func<string, Task<Reply>> one, Func<string, Task<Reply>> two)
    {
        var key = Guid.NewGuid().ToString("N");
        var meeting = shared.Meetings.GetOrAdd(key, _ => new Meeting());

        var replies = await Task.WhenAll(Task.Run(() => one(key)), Task.Run(() => two(key))).ConfigureAwait(false);

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
            $"5xx на одночасних запитах:\n{replies[0]}\n{replies[1]}\n{App.ErrorsText}");

    /// <summary>Upsert: хоча б один успіх тим самим статусом, решта — той самий успіх або загальний 409.</summary>
    private void AssertSameSuccessOrConcurrent(Reply[] replies, HttpStatusCode success)
    {
        Assert.Contains(replies, r => r.Status == success);
        foreach (var reply in replies.Where(r => r.Status != success))
        {
            AssertRefusal(reply, HttpStatusCode.Conflict, "ECR-CELL-0409", "err.ECR-CELL-0409.concurrentChange");
        }
    }

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

    private static string NewCode(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    /// <summary>Плаский довідник без записів; з полем-рядком <paramref name="withField"/>, якщо задано.</summary>
    private async Task<(int Id, string Code)> NewRegistryAsync(string? withField)
    {
        await using var db = Db();
        var registry = new RegistryDef(EcrCode.Create(NewCode("RG")), Name("B158d registry"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withField is not null)
        {
            db.RegistryFieldDefs.Add(new RegistryFieldDef(registry.Id, EcrCode.Create(withField), Name(withField), CellDataType.String, 1));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return (registry.Id, registry.Code);
    }

    /// <summary>Довідник з одним ключовим полем <c>KEY</c> (опис версії 1) — основа чернетки опису.</summary>
    private async Task<(int Id, string Code, int FieldId)> NewRegistryWithKeyAsync()
    {
        await using var db = Db();
        var registry = new RegistryDef(EcrCode.Create(NewCode("RK")), Name("B158d draft"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("KEY"), Name("Key"), CellDataType.String, 1);
        field.MarkKey(true);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (registry.Id, registry.Code, field.Id);
    }

    /// <summary>Свіжий користувач із власною роллю (права) і два його сеанси — «дві вкладки».</summary>
    private async Task<(HttpClient First, HttpClient Second)> SignInTwiceAsync(string[] permissions)
    {
        var name = $"dqd_{Guid.NewGuid():N}"[..20];

        await using (var db = Db())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("B158d duplicate request"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
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
