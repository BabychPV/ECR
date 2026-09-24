// tests/Ecr.Api.Tests/TableStatusTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /documents/{id}/tables/status</c> — заповненість таблиць (`BE-10`).
/// </summary>
/// <remarks>
/// ⛔ Предмет перевірки — <b>два числа, якими легко збрехати</b>:
/// <list type="number">
/// <item><c>inputCells</c>, що порахував би й формульні колонки. Тоді
/// «заповнено 40 %» означало б «60 % — формули», тобто смуга прогресу
/// показувала б частку формул у шаблоні, а не роботу людини.</item>
/// <item><c>errorCount = 0</c> під документом, якого ніхто не перевіряв —
/// та сама неправда, що <c>A7-28</c>: зелений нуль, на який спираються,
/// подаючи звітність.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class TableStatusTests(SqlServerFixture sql)
{
    private const string Password = "Api-Table-Status-2026!";

    /// <summary>
    /// Формульна колонка не входить ні в знаменник, ні в чисельник.
    /// </summary>
    /// <remarks>
    /// ⚠ Сценарій підібраний так, що <b>жодна</b> підстановка сталої не
    /// лишається зеленою, і кожне з двох чисел падає від СВОЄЇ мутації:
    /// <list type="bullet">
    /// <item>таблиця має 2 рядки × 3 колонки = 6 комірок, з яких формульна
    /// колонка забирає 2 → <c>inputCells = 4</c>, а не 6. Мутація «рахувати
    /// всі колонки» дає 6 і валить саме цей рядок;</item>
    /// <item>значення записані в ОДНУ вхідну колонку (2 комірки) і в
    /// формульну (ще 2) → <c>filledCells = 2</c>, а не 4. Мутація «не
    /// виключати обчислювані колонки з підрахунку заповненого» дає 4 і валить
    /// саме цей рядок — і її не ховає стеля <c>Math.Min</c>, бо знаменник
    /// лишається 4.</item>
    /// </list>
    ///
    /// ⚠ Комірки формульної колонки записані з <c>IsCalculated = 0</c>
    /// НАВМИСНО. З одиницею їх відкинув би предикат по самій комірці, і тест
    /// доводив би не те, що перевіряє: класифікацію КОЛОНКИ. Такий стан ще й
    /// реальний — значення, введені до того, як колонку зробили формульною.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Формульна_колонка_не_входить_у_InputCells()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var status = await ReadStatusAsync(app, client, scenario).ConfigureAwait(true);
        var table = Single(status, scenario.TableDefId);

        Assert.Equal(scenario.SheetCode, table.GetProperty("sheetCode").GetString());

        // 2 рядки × 2 людські колонки. Не 6: третя колонка — формула.
        Assert.Equal(4, table.GetProperty("inputCells").GetInt32());

        // Заповнена рівно одна вхідна колонка в обох рядках. Не 4: дві
        // комірки формульної колонки — не робота людини.
        Assert.Equal(2, table.GetProperty("filledCells").GetInt32());
    }

    /// <summary>
    /// Комірки, закриті правилом доступу до періоду, теж не входять.
    /// </summary>
    /// <remarks>
    /// ⚠ Правило — <c>EditablePeriodOnly</c> із вікном <c>7…9</c> на таблицю,
    /// а період документа має номер <c>1</c>: <c>AppliesTo(1)</c> хибне, тож
    /// <c>PeriodAccessRules</c> віддає <c>OutOfAccessWindow</c> із поведінкою
    /// <c>ReadOnly</c>, тобто <c>Blocks</c>. Уся таблиця закрита на введення —
    /// заповнювати в ній нічого, і знаменник дорівнює нулю.
    ///
    /// ⛔ Перший асерт навмисно перевіряє, що БЕЗ правила та сама таблиця дає
    /// ненульовий знаменник. Без нього «нуль» міг би означати що завгодно —
    /// наприклад, що екземпляра таблиці взагалі немає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Закрита_правилом_періоду_таблиця_не_має_вхідних_комірок()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using (var before = new EcrApiFactory(sql))
        using (var client = await SignedInAsync(before, scenario.UserName).ConfigureAwait(true))
        {
            var status = await ReadStatusAsync(before, client, scenario).ConfigureAwait(true);

            Assert.Equal(4, Single(status, scenario.TableDefId).GetProperty("inputCells").GetInt32());
        }

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.PeriodAccessRules.Add(PeriodAccessRuleDef
                .EditablePeriodOnly(
                    scenario.TemplateVersionId, OutOfWindowBehavior.ReadOnly,
                    fromSequence: 7, toSequence: 9)
                .ForTable(scenario.TableDefId));

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var after = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var closed = Single(
            await ReadStatusAsync(app, after, scenario).ConfigureAwait(true), scenario.TableDefId);

        Assert.Equal(0, closed.GetProperty("inputCells").GetInt32());
        Assert.Equal(0, closed.GetProperty("filledCells").GetInt32());
    }

    /// <summary>
    /// Рядки шаблону, яких у базі ще немає, теж входять у знаменник (`R-13`).
    /// </summary>
    /// <remarks>
    /// ⛔ Живцем на стенді: документ, якого за період ще не відкривали, має
    /// екземпляри таблиць, але жодного рядка — рядки шаблону матеріалізуються
    /// лише при читанні зрізу. Знаменник рахувався з рядків БАЗИ, тож кожна
    /// таблиця давала <c>inputCells = 0</c>, і порожній документ показував
    /// «Tables filled completely: 92 of 92».
    ///
    /// ⚠ Сценарій — той самий документ, у якого рядки прибрано з бази:
    /// шаблон лишає два описи рядків × дві людські колонки = 4. Мутація
    /// «рахувати лише рядки бази» дає 0 і валить саме цей рядок.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-13")]
    public async Task Нематеріалізовані_рядки_шаблону_входять_у_InputCells()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"""
                DELETE c FROM doc.CellValue AS c
                  JOIN doc.TableRow AS r ON r.Id = c.TableRowId AND r.PeriodKey = c.PeriodKey
                  JOIN doc.TableInstance AS i ON i.Id = r.TableInstanceId
                 WHERE i.DocumentId = {scenario.DocumentId} AND c.PeriodKey = {scenario.PeriodKey};
                DELETE r FROM doc.TableRow AS r
                  JOIN doc.TableInstance AS i ON i.Id = r.TableInstanceId
                 WHERE i.DocumentId = {scenario.DocumentId} AND r.PeriodKey = {scenario.PeriodKey};
                """).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var table = Single(await ReadStatusAsync(app, client, scenario).ConfigureAwait(true), scenario.TableDefId);

        Assert.Equal(4, table.GetProperty("inputCells").GetInt32());
        Assert.Equal(0, table.GetProperty("filledCells").GetInt32());
    }

    /// <summary>
    /// Ніколи не перевірений документ віддає <c>null</c>, а не нулі.
    /// </summary>
    /// <remarks>
    /// ⛔ Обидві половини в одному тесті, і кожна без другої нічого не
    /// доводить. «<c>null</c> до перевірки» сам по собі проходить і на
    /// полі, яке завжди <c>null</c>; «число після перевірки» — на полі, яке
    /// завжди дорівнює кількості порушень. Разом вони не лишають способу
    /// відповісти сталою.
    ///
    /// ⚠ Правило рівня таблиці з предикатом <c>FALSE</c> порушене завжди,
    /// тож після перевірки в таблиці рівно одна помилка і жодного
    /// попередження — тобто перевіряється і те, що нуль ПІСЛЯ перевірки
    /// лишається нулем, а не стає <c>null</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Неперевірений_документ_не_видає_нуль_зауважень()
    {
        var scenario = await ArrangeAsync(withFailingRule: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var before = Single(
            await ReadStatusAsync(app, client, scenario).ConfigureAwait(true), scenario.TableDefId);

        Assert.Equal(
            JsonValueKind.Null,
            before.GetProperty("errorCount").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            before.GetProperty("warningCount").ValueKind);

        var validate = await client
            .PostAsJsonAsync(
                new Uri($"/api/v1/documents/{scenario.DocumentId}/validate", UriKind.Relative),
                new { periodKey = scenario.PeriodKey })
            .ConfigureAwait(true);

        Assert.True(
            validate.IsSuccessStatusCode,
            $"POST перевірки: {validate.StatusCode}\n{app.ErrorsText}");

        var after = Single(
            await ReadStatusAsync(app, client, scenario).ConfigureAwait(true), scenario.TableDefId);

        Assert.Equal(1, after.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, after.GetProperty("warningCount").GetInt32());
    }

    /// <summary>
    /// Хто не бачить документа — не бачить і його заповненості.
    /// </summary>
    /// <remarks>
    /// ⚠ У чужого користувача є функціональне право <c>Document.View</c> і
    /// НЕМАЄ гранта на проєкт. Саме ця пара й відрізняє «працює з
    /// документами взагалі» від «працює з ЦИМ» (<c>Q-172</c>): без другої
    /// перевірки маршрут віддавав би стороннім те, скільки і де введено в
    /// чужому документі.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Без_гранта_на_проєкт_статус_недоступний()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);
        var stranger = await AddStrangerAsync(scenario).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, stranger).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri(
                $"/api/v1/documents/{scenario.DocumentId}/tables/status?periodKey={scenario.PeriodKey}",
                UriKind.Relative))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// <c>GET /documents/summary</c> ходить справжнім HTTP (<c>BE-09a</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Живе ТУТ, а не у власному файлі, щоб не копіювати вхід і сценарій.
    /// Єдине, що стереже DI-реєстрацію обробника й сховища зведення: без неї
    /// маршрут віддає <c>500</c> лише в рантаймі, і жоден модульний тест цього
    /// не бачить. Читач має грант рівно на СВІЙ проєкт, тож сума станів —
    /// кількість його видимих документів, а не всіх у базі.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-09")]
    public async Task Зведення_переліку_ходить_справжнім_HTTP_і_вимагає_період()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var projectId = db.Documents.Where(d => d.Id == scenario.DocumentId).Select(d => d.ProjectId).Single();
            db.Documents.Add(new Document(
                projectId, $"SUM-{Guid.NewGuid():N}"[..20], 1, new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/documents/summary?periodKey={scenario.PeriodKey}", UriKind.Relative))
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET зведення: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        var summary = JsonDocument.Parse(body).RootElement;
        var states = summary.GetProperty("draft").GetInt32()
                     + summary.GetProperty("submitted").GetInt32()
                     + summary.GetProperty("approved").GetInt32()
                     + summary.GetProperty("rejected").GetInt32();

        // Два документи проєкту, жоден не подано й не перевіряли.
        Assert.Equal(2, states);
        Assert.Equal(2, summary.GetProperty("draft").GetInt32());
        Assert.Equal(0, summary.GetProperty("withIssues").GetInt32());

        string[] invalidQueries = [string.Empty, "?periodKey=13"];
        foreach (var query in invalidQueries)
        {
            var invalid = await client
                .GetAsync(new Uri($"/api/v1/documents/summary{query}", UriKind.Relative))
                .ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }
    }

    /// <summary>Читає статус і перевіряє, що відповідь узагалі успішна.</summary>
    private static async Task<List<JsonElement>> ReadStatusAsync(
        EcrApiFactory app, HttpClient client, Scenario scenario)
    {
        var response = await client
            .GetAsync(new Uri(
                $"/api/v1/documents/{scenario.DocumentId}/tables/status?periodKey={scenario.PeriodKey}",
                UriKind.Relative))
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET статусу: {response.StatusCode}\n{body}\n{app.ErrorsText}");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()];
    }

    /// <summary>Запис саме про нашу таблицю; інакше — зрозуміла відмова.</summary>
    private static JsonElement Single(List<JsonElement> status, int tableDefId)
    {
        var found = status
            .Where(t => t.GetProperty("tableDefId").GetInt32() == tableDefId)
            .ToList();

        Assert.True(found.Count == 1, $"Таблиці {tableDefId} немає у відповіді або вона не одна.");

        return found[0];
    }

    /// <summary>Клієнт із чинним сеансом уже заведеного локального користувача.</summary>
    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Документ із трьох колонок, третя — формульна, і двох рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ Третю колонку додає сам тест, а не <c>TestDocumentBuilder</c>:
    /// будівник спільний, і розширювати його заради одного сценарію означало
    /// б змінювати фікстуру, якою користуються чужі тести.
    /// </remarks>
    private async Task<Scenario> ArrangeAsync(bool withFailingRule = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 2).ConfigureAwait(false);

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();

        var formula = new ColumnDef(
            document.TableDefId, EcrCode.Create($"F1_{tag}"), Name("Formula"), 3, CellDataType.Formula);

        db.ColumnDefs.Add(formula);

        if (withFailingRule)
        {
            // `FALSE` — предикат, який не виконується ніколи: правило рівня
            // таблиці порушене завжди, незалежно від даних.
            db.ValidationRules.Add(new ValidationRule(
                document.TableDefId, EcrCode.Create($"TS_{tag}"), ValidationSeverity.Error,
                scope: 2, "FALSE", Name("завжди порушене")));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // ── Значення: перша (людська) колонка і формульна ────────────────
        // Друга людська колонка лишається порожньою навмисно — інакше
        // чисельник зійшовся б зі знаменником, і мутація «не виключати
        // обчислювані колонки» лишилась би непоміченою під стелею.
        var input = document.ColumnDefIds[0];

        foreach (var rowId in document.RowIds)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, input),
                document.TableDefId,
                new CellValueData { ValueString = $"значення {rowId}" }));

            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, formula.Id),
                document.TableDefId,
                new CellValueData { ValueNumeric = 42m }));
        }

        // ── Права: функціональне + грант на ресурс ───────────────────────
        var userName = $"status_{Guid.NewGuid():N}"[..20];

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль СВОЯ на кожен прогін: база одна на всю збірку, і правка
        // вбудованої ролі розповзлася б на сусідні тести.
        var role = new Role(EcrCode.Create($"STATUS_{Guid.NewGuid():N}"), Name("Status reader"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Read));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, document.PeriodKey.Value, document.TemplateVersionId,
            document.TableDefId, document.SheetCode, userName);
    }

    /// <summary>Користувач із правом <c>Document.View</c> і БЕЗ гранта на проєкт.</summary>
    private async Task<string> AddStrangerAsync(Scenario scenario)
    {
        var userName = $"alien_{Guid.NewGuid():N}"[..20];

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"ALIEN_{Guid.NewGuid():N}"), Name("Stranger"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));

        // ⚠ ResourceGrant НЕ додається — у цьому й полягає сценарій.
        await db.SaveChangesAsync().ConfigureAwait(false);

        _ = scenario;

        return userName;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Підготовлений сценарій: документ, його таблиця й читач.</summary>
    private sealed record Scenario(
        long DocumentId,
        int PeriodKey,
        int TemplateVersionId,
        int TableDefId,
        string SheetCode,
        string UserName);
}
