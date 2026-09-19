// tests/Ecr.Api.Tests/AuditCellFiltersTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// `BE-03`: фільтри журналу змін комірок і історія ОДНІЄЇ комірки.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>GET /api/v1/audit/cells</c> читав із <c>aud.CellChange</c>
/// колонки <c>RowKey</c>, <c>ColumnDefId</c>, <c>ChangedByUserId</c>,
/// <c>Origin</c>, <c>IsLateEdit</c> — і не вмів за жодною з них фільтрувати.
/// Тобто відповідь на «хто змінив ЦЕ число» в базі була, віддавалася клієнту
/// і лишалася недосяжною: знайти її можна було тільки гортаючи сторінки очима.
///
/// ⛔ Рядки журналу вставляються ПРЯМИМ SQL, а не правками через API. Це не
/// скорочення: <c>aud.*</c> живуть поза моделлю EF (append-only, окремий порт
/// <c>IAuditWriter</c>), а предмет цих тестів — ЧИТАННЯ з фільтром. Прогнати
/// шість різних комбінацій <c>Origin</c>/<c>IsLateEdit</c>/автора через
/// справжній <c>PATCH</c> означало б будувати шаблон із колонками, відкривати
/// періоди і заводити другого користувача заради значень, які журнал усе одно
/// зберігає як є. Те, що журнал НАПОВНЮЄТЬСЯ правильно, доводить окремий
/// наскрізний сценарій (<c>DataEntryScenarios.Аудит_записує_старе_нове_значення_і_RowKey</c>).
///
/// ⚠ Вікно часу лишається ОБОВ'ЯЗКОВИМ у кожному запиті: таблиця партиційована
/// за <c>ChangedAt</c>, і запит без меж пішов би по всіх партиціях.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditCellFiltersTests(SqlServerFixture sql)
{
    private const string Password = "Api-Audit-Filters-2026!";

    /// <summary>Походження, яким позначена «рука людини».</summary>
    private const string UserEdit = "UserEdit";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Фільтр_за_коміркою_повертає_рядки_лише_цієї_комірки()
    {
        using var app = new EcrApiFactory(sql);
        var arranged = await ArrangeAsync(app, ["Security.ViewAudit", "Document.View"]);

        // Три сусіди, які відрізняються РІВНО одним складником адреси: той
        // самий рядок в іншій колонці, та сама колонка в іншому рядку.
        await WriteChangeAsync(arranged.DocumentId, "R1", 11, author: 1, UserEdit, late: false);
        await WriteChangeAsync(arranged.DocumentId, "R1", 22, author: 1, UserEdit, late: false);
        await WriteChangeAsync(arranged.DocumentId, "R2", 11, author: 1, UserEdit, late: false);

        var items = await ReadAsync(
            arranged.Client, app,
            $"documentId={arranged.DocumentId}&rowKey=R1&columnDefId=11");

        // ⛔ Мутаційний доказ: прибрати з `AuditReader` умову `RowKey = @rowKey`
        // — і сюди приїде рядок `R2`, тобто ЧУЖИЙ рядок того самого документа.
        // Прибрати `ColumnDefId = @columnDefId` — приїде колонка 22.
        var only = Assert.Single(items);
        Assert.Equal("R1", only.GetProperty("rowKey").GetString());
        Assert.Equal(11, only.GetProperty("columnDefId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Фільтр_lateOnly_повертає_лише_пізні_правки()
    {
        using var app = new EcrApiFactory(sql);
        var arranged = await ArrangeAsync(app, ["Security.ViewAudit"]);

        await WriteChangeAsync(arranged.DocumentId, "R1", 11, author: 1, UserEdit, late: false);
        await WriteChangeAsync(arranged.DocumentId, "R1", 12, author: 1, UserEdit, late: true);

        var late = await ReadAsync(
            arranged.Client, app, $"documentId={arranged.DocumentId}&lateOnly=true");

        // ⛔ Мутація: прибрати `AND IsLateEdit = 1` — приїдуть обидва рядки.
        Assert.True(Assert.Single(late).GetProperty("isLateEdit").GetBoolean());

        // І фільтр не «вимкнений назавжди»: без нього видно обидва.
        var all = await ReadAsync(arranged.Client, app, $"documentId={arranged.DocumentId}");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Фільтри_автора_і_походження_звужують_видачу_кожен_окремо()
    {
        using var app = new EcrApiFactory(sql);
        var arranged = await ArrangeAsync(app, ["Security.ViewAudit"]);

        await WriteChangeAsync(arranged.DocumentId, "R1", 11, author: 41, UserEdit, late: false);
        await WriteChangeAsync(arranged.DocumentId, "R1", 12, author: 42, UserEdit, late: false);
        await WriteChangeAsync(arranged.DocumentId, "R1", 13, author: 41, "Import", late: false);

        // ⛔ Мутація: прибрати `ChangedByUserId = @changedBy` — 3 замість 2.
        var byAuthor = await ReadAsync(
            arranged.Client, app, $"documentId={arranged.DocumentId}&author=41");
        Assert.Equal(2, byAuthor.Count);
        Assert.All(byAuthor, i => Assert.Equal(41, i.GetProperty("changedByUserId").GetInt32()));

        // ⛔ Мутація: прибрати `Origin = @origin` — 3 замість 1.
        var byOrigin = await ReadAsync(
            arranged.Client, app, $"documentId={arranged.DocumentId}&origin=Import");
        Assert.Equal("Import", Assert.Single(byOrigin).GetProperty("origin").GetString());

        // ⚠ Разом — кон'юнкція, а не об'єднання: інакше «автор 42 + Import»
        // віддавало б два рядки замість жодного, і фільтр означав би «або».
        var both = await ReadAsync(
            arranged.Client, app, $"documentId={arranged.DocumentId}&author=42&origin=Import");
        Assert.Empty(both);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Адреса_комірки_без_documentId_відхиляється_як_помилка_запиту()
    {
        using var app = new EcrApiFactory(sql);
        var arranged = await ArrangeAsync(app, ["Security.ViewAudit"]);

        // ⛔ `RowKey` унікальний у межах екземпляра таблиці, а не системи:
        // «R1» є в кожному документі. Мовчазне ігнорування фільтра віддало б
        // рядки чужих документів і виглядало б як відповідь.
        var response = await arranged.Client
            .GetAsync(new Uri(Url("rowKey=R1"), UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());

        // ⚠ Те саме для колонки без документа — інакше умову можна «полагодити»
        // перевіркою лише `rowKey`, і половина правила зникне мовчки.
        var byColumn = await arranged.Client
            .GetAsync(new Uri(Url("columnDefId=11"), UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byColumn.StatusCode);
    }

    /// <summary>
    /// `D15-16`: історію СВОЄЇ комірки бачить той, хто бачить документ.
    /// </summary>
    /// <remarks>
    /// ⛔ Це головний тест цього файлу. Без другого рівня доступу вкладка
    /// History в інспекторі комірки була б порожньою для всіх, крім аудиторів:
    /// <c>Security.ViewAudit</c> — централізоване комплаєнс-право (Q-177), і
    /// має його мізерна частка ролей. А віддати його заради вкладки означало б
    /// разом із нею віддати наскрізний журнал ПО ВСІХ проєктах.
    ///
    /// ⛔ Обидві половини перевіряються ОДНИМ користувачем: той самий запит,
    /// звужений до адреси комірки, проходить, а розширений до журналу —
    /// ні. Два різні користувачі довели б лише те, що права різні.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Власник_Document_View_читає_історію_комірки_але_не_журнал()
    {
        using var app = new EcrApiFactory(sql);

        // Жодного `Security.ViewAudit` — рівно те, що має пересічний оператор.
        var arranged = await ArrangeAsync(app, ["Document.View"]);

        await WriteChangeAsync(arranged.DocumentId, "R1", 11, author: 1, UserEdit, late: false);

        var history = await arranged.Client
            .GetAsync(new Uri(
                Url($"documentId={arranged.DocumentId}&rowKey=R1&columnDefId=11"), UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутація: прибрати з обробника гілку `single && profile.Has(
        // CellHistoryPermission)` — тут стане 403, і вкладка History знову
        // порожня для 90 % користувачів.
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Single(await Items(history).ConfigureAwait(true));

        // ⛔ А загальний журнал — ні: поріг для нього лишається
        // `Security.ViewAudit`. Мутація: прибрати `!profile.Has(Permission)` з
        // умови — і власник `Document.View` читатиме журнал системи.
        var journal = await arranged.Client
            .GetAsync(new Uri(Url($"documentId={arranged.DocumentId}"), UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, journal.StatusCode);

        // ⚠ Неповна адреса — це вже журнал, а не історія комірки: рядок без
        // колонки віддав би ВЕСЬ рядок. Без цієї перевірки межу можна
        // послабити до «є documentId», і тест лишився б зеленим.
        var wholeRow = await arranged.Client
            .GetAsync(new Uri(
                Url($"documentId={arranged.DocumentId}&rowKey=R1"), UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, wholeRow.StatusCode);
    }

    /// <summary>
    /// Стеля вікна для однієї комірки — 13 місяців, для журналу — 92 дні.
    /// </summary>
    /// <remarks>
    /// ⚠ Людина питає «а торік у цьому місяці це число було таким самим?».
    /// 92 дні на це не вистачає, а запит за однією коміркою обмежений не
    /// вікном, а адресою: комірку правлять одиниці разів на рік.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-03")]
    public async Task Вікно_у_рік_приймається_для_комірки_і_відхиляється_для_журналу()
    {
        using var app = new EcrApiFactory(sql);
        var arranged = await ArrangeAsync(app, ["Security.ViewAudit", "Document.View"]);

        var to = DateTime.UtcNow.AddMinutes(1);
        var from = to.AddDays(-365);

        var cell = await arranged.Client
            .GetAsync(new Uri(
                Url($"documentId={arranged.DocumentId}&rowKey=R1&columnDefId=11", from, to),
                UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ Мутація: повернути в обробник безумовне `MaxWindow` — тут стане
        // 422, і вкладка History не зможе показати рік.
        Assert.Equal(HttpStatusCode.OK, cell.StatusCode);

        var journal = await arranged.Client
            .GetAsync(new Uri(Url($"documentId={arranged.DocumentId}", from, to), UriKind.Relative))
            .ConfigureAwait(true);

        // ⛔ А стеля журналу НЕ послаблена: 365 днів по всіх рядках документа
        // читали б стільки партицій, скільки й було заборонено.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, journal.StatusCode);
    }

    /// <summary>Адреса журналу з обов'язковим вікном і довільним хвостом фільтрів.</summary>
    private static string Url(string filters)
        => Url(filters, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1));

    private static string Url(string filters, DateTime from, DateTime to)
        => "/api/v1/audit/cells"
           + $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}"
           + "&limit=50"
           + (filters.Length == 0 ? string.Empty : "&" + filters);

    /// <summary>Читає сторінку журналу й падає з текстом сервера на будь-якому не-200.</summary>
    private static async Task<List<JsonElement>> ReadAsync(
        HttpClient client, EcrApiFactory app, string filters)
    {
        var response = await client
            .GetAsync(new Uri(Url(filters), UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode} на {Url(filters)}: "
            + $"{await response.Content.ReadAsStringAsync().ConfigureAwait(true)} {app.ErrorsText}");

        return await Items(response).ConfigureAwait(true);
    }

    private static async Task<List<JsonElement>> Items(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true))
            .GetProperty("items").EnumerateArray().ToList();

    /// <summary>Один рядок журналу — прямим ADO, бо <c>aud.*</c> поза моделлю EF.</summary>
    private async Task WriteChangeAsync(
        long documentId, string rowKey, int columnDefId, int author, string origin, bool late)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES
                (@changedAt, 202601, @documentId, 1, @rowKey, @columnDefId,
                 N'1', N'2', @author, @origin, @late);
            """;

        command.Parameters.AddWithValue("@changedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@columnDefId", columnDefId);
        command.Parameters.AddWithValue("@author", author);
        command.Parameters.AddWithValue("@origin", origin);
        command.Parameters.AddWithValue("@late", late);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Документ, роль із правами, грант на проєкт і клієнт із сеансом.</summary>
    private sealed record Arrangement(HttpClient Client, long DocumentId);

    /// <summary>
    /// Шаблон → версія → проєкт → документ → роль із правами → грант → вхід.
    /// </summary>
    /// <remarks>
    /// ⚠ Грант на ПРОЄКТ обов'язковий навіть для аудитора: обробник перевіряє
    /// <c>CanReadDocumentAsync</c> на кожному запиті з <c>documentId</c>
    /// (Q-177). Без нього кожен тест тут отримував би 403 і доводив би не те.
    /// </remarks>
    private async Task<Arrangement> ArrangeAsync(EcrApiFactory app, string[] permissions)
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"AUD_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // periodPolicyId 1 — сіяна політика "ECR-Standard" (той самий факт, на
        // який спирається `DocumentTouchConcurrencyTests`).
        var project = new Project(
            EcrCode.Create($"AUDPRJ_{_tag}"), Text("Project"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var document = new Document(project.Id, $"AUD-{_tag}-0001", 9, DateTime.UtcNow);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var role = new Role(EcrCode.Create($"AUDROLE_{_tag}"), Text("Role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, project.Id, GrantLevel.Read));

        var name = $"aud_{_tag}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var client = app.CreateClient();
        var login = await client
            .PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = name, password = Password })
            .ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return new Arrangement(client, document.Id);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
