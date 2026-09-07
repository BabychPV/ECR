// tests/Ecr.Api.Tests/CellWriteRoundTripTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Наскрізний ЗАПИС із очікуванням УСПІХУ: HTTP → контролер → обробник →
/// справжнє сховище → справжня база → і значення видно наступним читанням.
/// </summary>
/// <remarks>
/// ⛔ Тест з'явився після аудиту, який довів прогоном неприємну річ: набір із
/// 1292 тестів давав ту саму відповідь на системі, яка пускає запис у
/// ЗАКРИТИЙ період, і на системі, яка його не пускає. Причина була проста:
/// наскрізний рівень існував, але жоден бізнес-ендпоінт ЗАПИСУ не мав тесту з
/// очікуванням успіху — усі 52 тести <c>Ecr.Api.Tests</c> ходили на сім URL з
/// ідентифікаторами <c>1</c> і <c>999999</c> і чекали 401/403/404. Такий тест
/// зелений і тоді, коли шар під ним не працює зовсім.
///
/// ⚠ Різниця з <c>CellsControllerTests</c> принципова і названа там прямо: той
/// перевіряє ЗВ'ЯЗУВАННЯ тіла запиту й не доходить до бази («значення в
/// іншому: що тіло, яке надсилає браузер, доходить до обробника не
/// спотвореним»). Тут перевіряється те, від чого він свідомо відмовився, —
/// що шов між шарами тримає навантаження в обидва боки.
///
/// ⛔ Жодної підміни в контейнері. Права справжні: користувач, роль, право
/// <c>Document.View</c>, призначення ролі й <c>ResourceGrant</c> рівня
/// <c>Write</c> на проєкт лягають у базу, а рішення ухвалює справжній
/// <c>AccessDecisionService</c>. Тест, який проходить лише через мок доступу,
/// закривав би рівно той дефект, заради якого написаний.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellWriteRoundTripTests(SqlServerFixture sql)
{
    private const string Password = "Api-Write-RoundTrip-2026!";

    /// <summary>Пояс майданчика; той самий, який ставить <see cref="TestDocumentBuilder"/>.</summary>
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Almaty").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.13")]
    public async Task Правка_комірок_через_HTTP_доходить_до_бази_і_видно_наступним_читанням()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        // ── 1. Коди колонок беруться зі ЗРІЗУ, а не з бази ────────────────
        // ⚠ Саме так їх дізнається клієнт: сітка відкриває таблицю і працює з
        // тим, що прийшло. Взяти коди напряму з `cfg.ColumnDef` означало б
        // перевіряти запис у структуру, якої користувач не бачив.
        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        var columns = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ToList();

        Assert.True(columns.Count >= 2, $"Зріз повернув колонок: {columns.Count}");
        var textColumn = columns[0];     // перша колонка будівника — String
        var numberColumn = columns[1];   // решта — Decimal

        // ── 2. Створення рядка правкою комірок (baseVersion = null) ───────
        // ⛔ Це той самий шлях, на якому жив дефект: адреси для перевірки прав
        // збиралися лише з ОНОВЛЕНЬ, тож батч із самих створень пропускав
        // блок прав цілком і клав значення в закритий період із `200`. Тепер
        // цей шлях питає `CanCreateRowsAsync`, і тест іде саме ним.
        var newRow = $"DYN{Guid.NewGuid():N}"[..12];

        var created = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.Document.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[]
            {
                new
                {
                    rowKey = newRow,
                    baseVersion = (string?)null,
                    cells = new object[]
                    {
                        new { columnCode = textColumn, value = (object)"мазут" },
                        new { columnCode = numberColumn, value = (object)12.5m },
                    },
                },
            },
        }).ConfigureAwait(true);

        // ⚠ Повідомлення асерту несе серверний лог: 403 і 422 тут виглядають
        // однаково беззмістовно, а причина відмови лежить лише в ньому.
        Assert.True(
            created.StatusCode == HttpStatusCode.OK,
            $"PATCH створення: {created.StatusCode}\n"
            + $"{await created.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var createdBody = JsonDocument
            .Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        // ⛔ `appliedCells` більше нуля — це і є доказ, що запис стався, а не
        // «пройшов успішно» з порожнім батчем. Нуль тут виглядав би як успіх.
        Assert.Equal(2, createdBody.GetProperty("appliedCells").GetInt32());

        var versionAfterCreate = createdBody
            .GetProperty("rowVersions").GetProperty(newRow).GetString();
        Assert.False(string.IsNullOrWhiteSpace(versionAfterCreate));

        // ── 3. Наступний запит на читання бачить записане ────────────────
        var afterCreate = await ReadRowAsync(client, sliceUri, newRow, app).ConfigureAwait(true);

        Assert.Equal("мазут", afterCreate.GetProperty("cells").GetProperty(textColumn).GetString());
        Assert.Equal(12.5m, afterCreate.GetProperty("cells").GetProperty(numberColumn).GetDecimal());

        // ⚠ Версія рядка зі зрізу має збігатися з тією, яку віддав PATCH:
        // клієнт бере `baseVersion` саме звідси, і розбіжність означала б, що
        // одразу після запису жодна наступна правка не пройде.
        var versionFromSlice = afterCreate.GetProperty("rowVersion").GetString();
        Assert.Equal(versionAfterCreate, versionFromSlice);

        // ── 4. Оновлення наявного рядка (baseVersion не null) ─────────────
        var updated = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.Document.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[]
            {
                new
                {
                    rowKey = newRow,
                    baseVersion = versionFromSlice,
                    cells = new object[] { new { columnCode = numberColumn, value = (object)41.75m } },
                },
            },
        }).ConfigureAwait(true);

        Assert.True(
            updated.StatusCode == HttpStatusCode.OK,
            $"PATCH оновлення: {updated.StatusCode}\n"
            + $"{await updated.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var updatedBody = JsonDocument
            .Parse(await updated.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

        Assert.Equal(1, updatedBody.GetProperty("appliedCells").GetInt32());

        // ⛔ Версія мусить ЗМІНИТИСЯ. Якщо вона та сама, оптимістичне
        // блокування тихо не працює: наступний запис зі застарілою
        // `baseVersion` пройде як коректний, і чужа правка зникне без сліду.
        Assert.NotEqual(
            versionFromSlice,
            updatedBody.GetProperty("rowVersions").GetProperty(newRow).GetString());

        // ── 5. І оновлене значення теж видно наступним читанням ──────────
        var afterUpdate = await ReadRowAsync(client, sliceUri, newRow, app).ConfigureAwait(true);

        Assert.Equal(41.75m, afterUpdate.GetProperty("cells").GetProperty(numberColumn).GetDecimal());

        // ⚠ Текст залишився недоторканим: у батчі його не було, а «поле
        // відсутнє в запиті» означає «не чіпати», а не «стерти» (R-B4).
        Assert.Equal("мазут", afterUpdate.GetProperty("cells").GetProperty(textColumn).GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.13")]
    public async Task Створення_рядка_через_HTTP_повертає_201_і_рядок_лягає_в_базу()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var rowsUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/rows", UriKind.Relative);
        var rowKey = $"ADD{Guid.NewGuid():N}"[..12];
        var body = new { tableInstanceId = scenario.Document.TableInstanceId, rowKey };

        // ⛔ Саме тут модель доступу не діяла зовсім: `CreateRowHandler`
        // отримував `IAccessDecisionService` і не читав його — рядок додавався
        // в ЗАКРИТИЙ період. Тепер обробник питає `CanCreateRowsAsync`, а
        // відсутнє рішення означає ВІДМОВУ, тож `201` тут можливий лише з
        // справжнім грантом рівня `Write`.
        var created = await client.PostAsJsonAsync(rowsUri, body).ConfigureAwait(true);

        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"POST рядка: {created.StatusCode}\n"
            + $"{await created.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var response = JsonDocument
            .Parse(await created.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(rowKey, response.GetProperty("rowKey").GetString());

        // Рядок у справжній таблиці, а не лише у відповіді.
        await using (var db = scenario.Builder.CreateContext())
        {
            var stored = await db.TableRows
                .AsNoTracking()
                .CountAsync(r => r.TableInstanceId == scenario.Document.TableInstanceId
                                 && r.PeriodKeyValue == scenario.PeriodKey
                                 && r.RowKeyValue == rowKey
                                 && !r.IsDeleted)
                .ConfigureAwait(true);

            Assert.Equal(1, stored);
        }

        // ⚠ І його бачить НАСТУПНИЙ запит: повтор із тим самим ключем має бути
        // відхилений сховищем. Без цієї перевірки «201» доводив би лише те, що
        // обробник не кинув винятку.
        var duplicate = await client.PostAsJsonAsync(rowsUri, body).ConfigureAwait(true);

        Assert.False(duplicate.IsSuccessStatusCode, $"Повтор прийнято: {duplicate.StatusCode}");

        var conflict = JsonDocument
            .Parse(await duplicate.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-ROW-0409", conflict.GetProperty("errorCode").GetString());
    }

    /// <summary>Рядок зрізу за ключем; відсутність рядка — падіння з поясненням.</summary>
    private static async Task<JsonElement> ReadRowAsync(
        HttpClient client, Uri sliceUri, string rowKey, EcrApiFactory app)
    {
        var response = await client.GetAsync(sliceUri).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}\n{app.ErrorsText}");

        var rows = JsonDocument.Parse(text).RootElement.GetProperty("rows");

        foreach (var row in rows.EnumerateArray())
        {
            if (string.Equals(row.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal))
            {
                return row;
            }
        }

        Assert.Fail($"Рядка «{rowKey}» немає у зрізі — записане не видно на читанні.\n{text}");
        return default;
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

    /// <summary>Готує документ, права і стан, за яких запис має бути ДОЗВОЛЕНИЙ.</summary>
    /// <remarks>
    /// ⚠ Кожен крок нижче знімає рівно одну умову з <c>EditRules.CanEdit</c>.
    /// Пропустити будь-який означає отримати законну відмову — і тест, який
    /// звинуватить у ній код, що працює правильно.
    /// </remarks>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // ⛔ Період — ПОТОЧНИЙ, а не фіксований 202601, і це не примха.
        // `PeriodStateJob` виконується один раз одразу на старті застосунку
        // (`A7-24`) і перераховує стан кожного періоду АКТИВНОГО проєкту за
        // обчисленими межами. Період за минулий місяць він законно закрив би
        // просто тому, що термін минув, — і зробив би це між сідом і першим
        // запитом тесту. Поточний період лишається `Open` (на межі місяця —
        // `Grace`), а обидва стани запис дозволяють.
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;

        // ⚠ `Mixed`, а не `Fixed`: інакше `CreateRowHandler` законно відмовляє
        // («рядки задані шаблоном»), і наскрізний шлях додавання рядка не
        // існував би. І не `Dynamic` — будівник заводить `RowDef`-и, а їх
        // динамічна таблиця не приймає навіть при завантаженні знімка.
        var document = await builder
            .BuildAsync(periodKey, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);

        var userName = $"writer_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль СВОЯ на кожен прогін, а не вбудована `DataEntry`: грант
        // вішається на роль, а база одна на всю збірку — правка спільної ролі
        // розповзлася б на сусідні тести.
        var role = new Role(
            EcrCode.Create($"WRITER_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Round-trip writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⛔ Два різні дозволи, і обидва обов'язкові (`A7-53`, `A7-55`).
        // `Document.View` — функціональне право: «ця людина взагалі працює з
        // документами»; його вимагає читання зрізу. `ResourceGrant` — грант на
        // РЕСУРС: «з якими саме документами». Без першого зріз відповідає 403,
        // без другого запис відмовляє з причиною `NoGrant`.
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(
            new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));

        // ⚠ Проєкт активується явно: будівник створює його чернеткою, а
        // чернетку `PeriodStateJob` не обробляє взагалі — тобто в реальній
        // системі періоди такого проєкту не відкрилися б ніколи (`A7-25`).
        var project = await db.Projects
            .FirstAsync(p => p.Id == document.ProjectId)
            .ConfigureAwait(false);
        project.Activate(now);

        // ⛔ Межі періоду рахуються ПОЛІТИКОЮ, а не проставляються руками.
        // Без цього кроку вони лишаються нулями (`0001-01-01`), і найближчий
        // прогін `PeriodStateJob` побачив би період, термін якого минув
        // дві тисячі років тому, — і закрив би його посеред тесту.
        var policy = await db.PeriodPolicies
            .FirstAsync(p => p.Id == project.PeriodPolicyId)
            .ConfigureAwait(false);

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);

        period.RecomputeBoundaries(policy, SiteZone);

        // Період створюється `Scheduled`, а `Scheduled` — це відмова
        // `PeriodNotOpenYet` на кожну комірку.
        period.AdvanceTo(PeriodState.Open, now);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(builder, document, userName, periodKey);
    }

    /// <summary>Підготовлений сценарій: документ, його автор і період.</summary>
    /// <param name="Builder">Будівник — тримає підключення до тієї самої бази.</param>
    /// <param name="Document">Ідентифікатори ланцюга.</param>
    /// <param name="UserName">Локальний користувач із правами на запис.</param>
    /// <param name="PeriodKey">Період, у якому дозволено писати.</param>
    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string UserName,
        int PeriodKey);
}
