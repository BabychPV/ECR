// tests/Ecr.Api.Tests/JobListMineTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `BE-08`: <c>GET /api/v1/jobs?mine=true</c> — власні фонові задачі без права
/// <c>System.ViewHealth</c>, і межа, яку не обійти.
/// </summary>
/// <remarks>
/// ⛔ Предмет — саме МЕЖА ДОСТУПУ, а не зручність фільтра. Автор отримує
/// <c>jobId</c> у відповіді <c>202</c> і доти міг подивитися лише його один
/// (<c>GET /jobs/{jobId}</c>, Q-156): перелік власних задач вимагав права на
/// стан СИСТЕМИ, тобто шухляда «Мої задачі» була порожня для всіх, крім
/// адміністраторів.
///
/// ⛔ Звідси два твердження, які мусять триматися РАЗОМ:
/// <list type="number">
/// <item><c>mine=true</c> показує рівно свої задачі — і ЖОДНОЇ чужої;</item>
/// <item>без <c>mine</c> і без права — <c>403</c>, а не порожній перелік:
/// «задач немає» і «вам їх не показують» — різні відповіді, і перша тут
/// неправда.</item>
/// </list>
///
/// ⚠ Тести НАСКРІЗНІ (справжній HTTP, справжня автентифікація, справжня база),
/// і саме тому третій із них щось доводить: підставити чужого власника можна
/// лише РЯДКОМ ЗАПИТУ, а рядок запиту бачить лише справжній конвеєр прив'язки
/// моделі. Виклик обробника напряму довів би, що параметра немає в сигнатурі —
/// і нічого про те, що його не можна додати ззовні.
///
/// ⚠ Рядки <c>itg.JobProgress</c> створюються доменною сутністю (той самий
/// шлях, що й у <c>JobProgressStore</c>), а не прямим SQL: предмет перевірки —
/// ЧИТАННЯ з фільтром, і задача, поставлена планувальником, писала б у ті самі
/// стовпці.
/// </remarks>
[Collection("SqlServer")]
public sealed class JobListMineTests(SqlServerFixture sql)
{
    private const string Password = "Api-Job-List-Mine-2026!";

    /// <summary>Право на перегляд ЧУЖИХ задач — стану системи.</summary>
    private const string ViewHealth = "System.ViewHealth";

    /// <summary>Мітка прогону: код задачі, за яким свої рядки впізнаються серед чужих.</summary>
    private readonly string _tag = $"T{Guid.NewGuid():N}"[..12];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Автор_без_ViewHealth_бачить_рівно_свої_задачі_і_жодної_чужої()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        var stranger = await SignedInAsync(app).ConfigureAwait(true);
        stranger.Client.Dispose();

        // ⚠ У наборі ОБОВ'ЯЗКОВО задачі двох різних авторів і одна системна
        // (без автора). Один автор довів би лише те, що запит щось повертає:
        // фільтр, який не фільтрує нічого, був би так само зеленим.
        var mine = await QueueAsync(author.UserId, count: 2).ConfigureAwait(true);
        var others = await QueueAsync(stranger.UserId, count: 2).ConfigureAwait(true);
        var system = await QueueAsync(createdByUserId: null, count: 1).ConfigureAwait(true);

        var seen = await IdsAsync(client, app, "mine=true").ConfigureAwait(true);

        // ⛔ Мутаційний доказ: прибрати з `JobProgressStore.ListRecentAsync`
        // предикат `p.CreatedByUserId == author` (або перестати класти
        // `userId` у `JobListFilter` в `ListJobsHandler`) — і сюди приїдуть
        // задачі чужого автора разом із системними.
        Assert.Equal(mine.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));

        foreach (var foreign in others.Concat(system))
        {
            Assert.DoesNotContain(foreign, seen, StringComparer.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Без_mine_і_без_права_перелік_відмовляє_а_не_віддає_порожній()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        await QueueAsync(author.UserId, count: 1).ConfigureAwait(true);

        var denied = await client
            .GetAsync(new Uri("/api/v1/jobs", UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутаційний доказ: замінити відмову на порожній перелік (тобто
        // завжди фільтрувати за автором, коли права немає) — і тут стане 200.
        // Порожній перелік читається як «задач немає», тобто бреше рівно тому,
        // хто прийшов дізнатися, що в системі коїться.
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var body = await denied.Content.ReadAsStringAsync().ConfigureAwait(true);

        // Відмова називає ПРАВО, якого бракує: інакше єдина доступна дія —
        // писати в підтримку «щось не працює».
        Assert.Contains(ViewHealth, body, StringComparison.Ordinal);

        // ⚠ Те саме для `mine=false`, написаного явно: інакше межу можна
        // «полагодити» перевіркою лише відсутності параметра.
        var explicitFalse = await client
            .GetAsync(new Uri("/api/v1/jobs?mine=false", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, explicitFalse.StatusCode);

        // ⚠ Друга половина доказу: та сама адреса тому самому конвеєру, лише з
        // правом, віддає 200. Без неї 403 міг би походити від чого завгодно —
        // від зламаного маршруту до збою автентифікації.
        var admin = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);
        using var allowed = admin.Client;

        var granted = await allowed
            .GetAsync(new Uri("/api/v1/jobs", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(granted.IsSuccessStatusCode, $"{granted.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Чужий_ідентифікатор_у_запиті_не_змінює_видачі()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        var stranger = await SignedInAsync(app).ConfigureAwait(true);
        stranger.Client.Dispose();

        var mine = await QueueAsync(author.UserId, count: 1).ConfigureAwait(true);
        var others = await QueueAsync(stranger.UserId, count: 2).ConfigureAwait(true);

        // ⛔ Головне твердження файлу. `mine=true` не вимагає
        // `System.ViewHealth`, тож будь-який спосіб НАЗВАТИ чужого власника
        // перетворив би це звільнення на читання чужої черги. Тут перелічені
        // всі імена, під якими такий параметр міг би з'явитися — зокрема ті,
        // що вже існують в інших ендпоінтах (`author` — у журналі аудиту,
        // `createdByUserId` — у стовпці `itg.JobProgress`).
        var spoofed = await IdsAsync(
            client,
            app,
            $"mine=true&createdByUserId={stranger.UserId}&userId={stranger.UserId}"
            + $"&author={stranger.UserId}&ownerId={stranger.UserId}&createdBy={stranger.UserId}")
            .ConfigureAwait(true);

        Assert.Equal(mine.Order(StringComparer.Ordinal), spoofed.Order(StringComparer.Ordinal));

        foreach (var foreign in others)
        {
            Assert.DoesNotContain(foreign, spoofed, StringComparer.Ordinal);
        }

        // ⚠ І другий бік тієї самої спроби: назвати чужий ідентифікатор БЕЗ
        // `mine` — це запит на чужу чергу, тобто 403, а не тихе звуження до
        // названого автора.
        var withoutMine = await client
            .GetAsync(new Uri(
                $"/api/v1/jobs?createdByUserId={stranger.UserId}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, withoutMine.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Власник_ViewHealth_бачить_задачі_всіх_авторів()
    {
        using var app = new EcrApiFactory(sql);

        var admin = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);
        using var client = admin.Client;

        var stranger = await SignedInAsync(app).ConfigureAwait(true);
        stranger.Client.Dispose();

        var mine = await QueueAsync(admin.UserId, count: 1).ConfigureAwait(true);
        var others = await QueueAsync(stranger.UserId, count: 1).ConfigureAwait(true);

        // ⚠ Фільтр за кодом — щоб не залежати від чужих рядків спільної бази:
        // перелік віддає 50 найсвіжіших задач ВСІЄЇ системи, і сусідній тест,
        // що поставив свою, інакше витіснив би звідси мою.
        var seen = await IdsAsync(client, app, $"code={_tag}").ConfigureAwait(true);

        Assert.Equal(
            mine.Concat(others).Order(StringComparer.Ordinal),
            seen.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Фільтр_стану_звужує_перелік_а_невідомий_стан_відхиляється()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        var running = await QueueAsync(author.UserId, count: 1).ConfigureAwait(true);
        var finished = await QueueAsync(author.UserId, count: 1, state: "Succeeded").ConfigureAwait(true);

        // ⛔ Мутація: прибрати предикат `p.State == state` — сюди приїдуть оби-
        // два рядки, і фільтр «покажи, що зараз виконується» нічого не означає.
        Assert.Equal(
            finished,
            await IdsAsync(client, app, "mine=true&state=Succeeded").ConfigureAwait(true));

        Assert.Equal(
            running,
            await IdsAsync(client, app, "mine=true&state=Running").ConfigureAwait(true));

        // ⛔ Невідомий стан — 422, а не порожній перелік. Друкарська помилка у
        // фільтрі інакше відповідала б «таких задач немає» — тобто збрехала б
        // саме тому, хто шукає збій.
        var unknown = await client
            .GetAsync(new Uri("/api/v1/jobs?mine=true&state=Frozen", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Contains(
            "ECR-REQ-0422",
            await unknown.Content.ReadAsStringAsync().ConfigureAwait(true),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Розмір_переліку_поза_межами_відхиляється_а_не_обрізається_мовчки()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        foreach (var limit in new[] { "0", "5000" })
        {
            var response = await client
                .GetAsync(new Uri($"/api/v1/jobs?mine=true&limit={limit}", UriKind.Relative))
                .ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        // ⚠ А межа з дозволеного діапазону приймається — інакше перевірка вище
        // трималася б на тому, що параметр просто не працює.
        var accepted = await client
            .GetAsync(new Uri("/api/v1/jobs?mine=true&limit=1", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(accepted.IsSuccessStatusCode, $"{accepted.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Перелік_віддає_автора_і_повідомлення_а_стан_стелю_спроб()
    {
        using var app = new EcrApiFactory(sql);

        var author = await SignedInAsync(app).ConfigureAwait(true);
        using var client = author.Client;

        var jobId = Assert.Single(await QueueAsync(author.UserId, count: 1, message: "step-2").ConfigureAwait(true));

        var items = await client
            .GetFromJsonAsync<JsonElement>(new Uri("/api/v1/jobs?mine=true", UriKind.Relative))
            .ConfigureAwait(true);
        var item = items.EnumerateArray().Single(i => i.GetProperty("jobId").GetString() == jobId);

        Assert.Equal(author.Name, item.GetProperty("createdByDisplayName").GetString());
        Assert.Equal("step-2", item.GetProperty("message").GetString());
        // Рядок переліку завжди з журналу — стеля та сама, що в стані: 1 + 3 ретраї.
        Assert.Equal(4, item.GetProperty("maxAttempts").GetInt32());

        var status = await client
            .GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(4, status.GetProperty("maxAttempts").GetInt32());
    }

    /// <summary>Ідентифікатори задач із відповіді; падає з текстом сервера на не-200.</summary>
    private static async Task<List<string>> IdsAsync(HttpClient client, EcrApiFactory app, string query)
    {
        var address = $"/api/v1/jobs?{query}";

        var response = await client
            .GetAsync(new Uri(address, UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode} на {address}: "
            + $"{await response.Content.ReadAsStringAsync().ConfigureAwait(true)} {app.ErrorsText}");

        var items = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);

        return [.. items.EnumerateArray().Select(i => i.GetProperty("jobId").GetString() ?? string.Empty)];
    }

    /// <summary>Ставить у <c>itg.JobProgress</c> задачі одного автора.</summary>
    /// <param name="createdByUserId">Автор; <c>null</c> — системна задача за розкладом.</param>
    /// <param name="count">Скільки рядків.</param>
    /// <param name="state">Кінцевий стан; <c>null</c> — лишити <c>Running</c>.</param>
    /// <param name="message">Повідомлення прогресу; <c>null</c> — без нього.</param>
    private async Task<List<string>> QueueAsync(
        int? createdByUserId, int count, string? state = null, string? message = null)
    {
        await using var db = Context();

        var ids = new List<string>();
        var now = DateTime.UtcNow;

        for (var i = 0; i < count; i++)
        {
            var jobId = $"{_tag}#{Guid.NewGuid():N}"[..40];
            var entry = new JobProgress(jobId, _tag, now, createdByUserId);

            if (message is not null)
            {
                entry.Report(10, message, now);
            }

            if (state is not null)
            {
                entry.Finish(state, null, now);
            }

            db.JobProgresses.Add(entry);
            ids.Add(jobId);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return ids;
    }

    /// <summary>Користувач із чинним сеансом і його ідентифікатор.</summary>
    private sealed record Session(HttpClient Client, int UserId, string Name);

    /// <summary>Заводить користувача, видає права й входить локально.</summary>
    /// <param name="app">Фабрика застосунку.</param>
    /// <param name="permissions">Права; порожньо — користувач без жодного.</param>
    private async Task<Session> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"job_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            userId = user.Id;

            if (permissions.Length > 0)
            {
                var role = new Role(
                    EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Job list test" }));

                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return new Session(client, userId, name);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
