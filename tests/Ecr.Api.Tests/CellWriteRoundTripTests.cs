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

    /// <summary>
    /// Шістнадцять знаків після коми, усі різні й жоден не нуль: обрізання на
    /// будь-якому з них змінює РЯДОК, а не лише масштаб (<c>D-148</c>).
    /// </summary>
    private const string SixteenDigits = "0.1234567890123456";

    /// <summary>
    /// 278 МВт·год у базовій одиниці (джоуль) із шістнадцятьма знаками:
    /// тринадцять цілих розрядів, яких <c>decimal(28,16)</c> не вміщав.
    /// </summary>
    /// <remarks>
    /// ⛔ Це наскрізний доказ РОЗШИРЕННЯ (precision 28 → 34, 2026-09-21).
    /// Каталог одиниць має множник <c>MWh → 3 600 000 000</c>, тож звичайні
    /// 278 МВт·год — це 1.0008·10¹² в базовій одиниці, а precision 28 при
    /// масштабі 16 лишає рівно 12 цілих розрядів. До переходу цей самий запит
    /// не округлявся, а відмовляв.
    ///
    /// ⚠ Чому НЕ 18 цілих розрядів, хоча стовпець їх тримає: 18 + 16 = 34
    /// значущі цифри, а <c>System.Decimal</c> несе лише 29. Таке значення не
    /// існує в CLR — <c>decimal.Parse</c> мовчки округлив би його ще до
    /// відправки, і тест порівнював би огризок сам із собою. Стелю стовпця й
    /// стелю CLR розводить окремий тест у
    /// <c>Ecr.Infrastructure.Tests</c> (<c>CellValueScale16Tests</c>).
    /// </remarks>
    private const string ThirteenIntegerDigits = "1000800000000.1234567890123456";

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

        // ⛔ Числова комірка їде РЯДКОМ (`D-30`, `DecimalAsStringJsonConverter`):
        // JSON-число на клієнті проходить через `JSON.parse`, тобто через
        // IEEE-754, і 16-й знак `decimal(34,16)` зникає ще до того, як до
        // нього можна дотягнутися. Хвостові нулі — масштаб самої колонки.
        var createdNumber = afterCreate.GetProperty("cells").GetProperty(numberColumn);

        Assert.Equal(JsonValueKind.String, createdNumber.ValueKind);
        Assert.Equal("12.5", createdNumber.GetString()!.TrimEnd('0'));

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

        var updatedNumber = afterUpdate.GetProperty("cells").GetProperty(numberColumn);

        Assert.Equal(JsonValueKind.String, updatedNumber.ValueKind);
        Assert.Equal("41.75", updatedNumber.GetString()!.TrimEnd('0'));

        // ⚠ Текст залишився недоторканим: у батчі його не було, а «поле
        // відсутнє в запиті» означає «не чіпати», а не «стерти» (R-B4).
        Assert.Equal("мазут", afterUpdate.GetProperty("cells").GetProperty(textColumn).GetString());
    }

    [Theory]
    [InlineData(SixteenDigits)]
    [InlineData(ThirteenIntegerDigits)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Повна_ширина_числа_доживає_від_HTTP_до_бази_і_назад(string text)
    {
        // ⛔ Наскрізний доказ `D-148` («усюди 16 знаків»). Ланок, кожна з яких
        // ріже МОВЧКИ, чотири: `JSON.parse`-подібна втрата на числі в тілі
        // запиту (тому значення їде РЯДКОМ), `SqlMetaData` в
        // `NormalizedCellStore`, тип `doc.CellValueTvp` і сам стовпець
        // `doc.CellValue.ValueNumeric`. Жодна з них не відмовляє — усі
        // округлюють і повертають `200`. Тому твердження одне й просте:
        // введений текст і прочитаний текст збігаються ПОСИМВОЛЬНО.
        //
        // ⚠ Два випадки перевіряють РІЗНІ половини типу, і другий з'явився з
        // переходом на `decimal(34,16)`: перший — масштаб (16 знаків після
        // коми), другий — ширину цілої частини (13 розрядів). Другий на
        // `(28,16)` не проходив узагалі: СУБД відмовляла «Arithmetic
        // overflow», бо precision 28 при масштабі 16 лишає 12 цілих розрядів.
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        // Друга колонка будівника — числова (перша `String`).
        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        var rowKey = $"D16{Guid.NewGuid():N}"[..12];

        var applied = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.Document.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[]
            {
                new
                {
                    rowKey,
                    baseVersion = (string?)null,
                    cells = new object[] { new { columnCode = numberColumn, value = (object)text } },
                },
            },
        }).ConfigureAwait(true);

        Assert.True(
            applied.StatusCode == HttpStatusCode.OK,
            $"PATCH «{text}»: {applied.StatusCode}\n"
            + $"{await applied.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        // ⚠ Читання йде тим самим шляхом, що й у клієнта, — зрізом, а не
        // запитом до таблиці. Значення, яке доїхало до бази цілим, але
        // втратило знак на видачі, — той самий дефект для того, хто звіряє
        // звіт із базою.
        var row = await ReadRowAsync(client, sliceUri, rowKey, app).ConfigureAwait(true);
        var cell = row.GetProperty("cells").GetProperty(numberColumn);

        Assert.Equal(JsonValueKind.String, cell.ValueKind);
        Assert.Equal(text, cell.GetString());

        // І в самій базі теж шістнадцять знаків, а не «щось, що зріз гарно
        // надрукував»: зріз бере число зі сховища, і обидва твердження разом
        // відрізняють цілий шлях від збігу на форматуванні.
        await using var db = scenario.Builder.CreateContext();
        var stored = await db.CellValues
            .AsNoTracking()
            .Where(c => c.PeriodKeyValue == scenario.PeriodKey
                        && c.ColumnDefId == scenario.Document.ColumnDefIds[1])
            .Select(c => c.ValueNumeric)
            .SingleAsync()
            .ConfigureAwait(true);

        Assert.Equal(
            text,
            stored!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.8")]
    public async Task Рядок_довший_за_1000_символів_відхиляється_422_і_в_базу_не_потрапляє()
    {
        // ⛔ `DAT-03`, шлях користувача. До виправлення цей самий запит давав
        // `500 ECR-SYS-0500` — і це не те, чого чекала директива («200 OK і
        // огризок»), тому названо тут дослівно, виміряно мутацією.
        // `NormalizedCellStore.AddNullable` задавав параметру `Size = 1000`,
        // тож у `doc.CellValue` значення обрізалося МОВЧКИ; але наступним
        // кроком того самого батчу `AuditWriter` пише `NewValue` БЕЗ `Size`,
        // і ПОВНЕ значення впиралося в `aud.CellChange.NewValue` (теж
        // `nvarchar(1000)`) — помилка 2628, відкат, `500`.
        //
        // ⚠ Тобто симптом був гірший за обидва очікувані: тиха втрата в
        // сховищі, прикрита збоєм, який називає журнал аудиту замість
        // завеликого вводу. Для оператора це «система зламалася», а не
        // «текст задовгий», — і виправити свій ввід він з такої відповіді не
        // може. Цей тест фіксує обидві половини: відмова тепер на межі, з
        // кодом про ДАНІ, і в базі порожньо.
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        var textColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .First();

        var rowKey = $"LEN{Guid.NewGuid():N}"[..12];
        var tooLong = new string('я', 1001);

        var rejected = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.Document.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[]
            {
                new
                {
                    rowKey,
                    baseVersion = (string?)null,
                    cells = new object[] { new { columnCode = textColumn, value = (object)tooLong } },
                },
            },
        }).ConfigureAwait(true);

        var body = await rejected.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            rejected.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"PATCH із 1001 символом: очікували 422, отримали {rejected.StatusCode}\n{body}\n{app.ErrorsText}");

        // ⚠ Код відмови названий: `422` сам по собі буває і від сусідніх
        // перевірок (невідома колонка, тип), і тест, який дивиться лише на
        // статус, лишався б зеленим, якби запит відхилили з іншої причини.
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CELL-0422", problem.GetProperty("errorCode").GetString());

        // ⚠ `messageKey` перевіряється теж, і це не формальність: саме за ним
        // `ExceptionHandlingMiddleware.ResolveGenericMessageAsync` бере текст
        // із `sys_ecr.UiString` мовою користувача (`Q-314`). Ключа, який казав
        // би саме «текст задовгий», у каталозі (`09-seed.sql`) НЕМАЄ —
        // редагувати сід у цій зміні не можна, тож відмова їде під наявним
        // спільним ключем перевірки, а сама комірка названа в `cells`.
        Assert.Equal(
            "err.ECR-CELL-0422.validationBlocked",
            problem.GetProperty("messageKey").GetString());

        // ⚠ І колонка названа поіменно — інакше клієнт знав би, що «щось у
        // батчі не так», але не яка саме клітинка.
        //
        // ⛔ А от `rowKey` тут `null`, і це зафіксовано як Є, а не обійдено:
        // `PatchCellsHandler` бере ключ рядка з мапи `byRowId`, а рядок цього
        // батчу щойно СТВОРЮЄТЬСЯ і в ній його ще немає. Тобто на шляху
        // «новий рядок + погане значення» відмова називає колонку, але не
        // рядок. Це не в межах `DAT-03` (файл обробника — чужий), тож тут
        // лише закріплено поточну поведінку, щоб її зміну було видно.
        var cell = problem.GetProperty("cells").EnumerateArray().Single();
        Assert.Equal(textColumn, cell.GetProperty("columnCode").GetString());
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("rowKey").ValueKind);

        // ⛔ І головне: у базі НІЧОГО. Це те твердження, яке падало до
        // виправлення, — тоді тут лежав огризок на 1000 символів.
        await using (var db = scenario.Builder.CreateContext())
        {
            var stored = await db.CellValues
                .AsNoTracking()
                .CountAsync(c => c.ColumnDefId == scenario.Document.ColumnDefIds[0]
                                 && c.PeriodKeyValue == scenario.PeriodKey)
                .ConfigureAwait(true);

            Assert.Equal(0, stored);
        }

        // ⚠ Контроль межі: рівно 1000 символів приймаються, доїжджають до бази
        // цілими і читаються назад БЕЗ утрати. Без цієї половини тест доводив
        // би лише «текст не пишеться», а не «межа там, де стовпець».
        //
        // ⛔ Ключ рядка тут ІНШИЙ, і це не косметика. Відхилений батч вище
        // усе одно СТВОРИВ рядок: `PatchCellsHandler` кличе `CreateRowsAsync`
        // всередині `BuildCellChangesAsync`, тобто ДО відмов — це окремий
        // дефект `DAT-04`, і він не в межах цієї зміни. Повторний `PATCH` із
        // тим самим ключем і `baseVersion = null` через це впирається в
        // `ECR-ROW-0409` (виміряно прогоном). Обходити чужий дефект мовчки не
        // можна, тож він названий тут прямо.
        var atLimit = new string('я', 1000);
        var secondRow = $"LEN{Guid.NewGuid():N}"[..12];

        var accepted = await client.PatchAsJsonAsync(patchUri, new
        {
            tableInstanceId = scenario.Document.TableInstanceId,
            periodKey = scenario.PeriodKey,
            origin = "UserEdit",
            rows = new[]
            {
                new
                {
                    rowKey = secondRow,
                    baseVersion = (string?)null,
                    cells = new object[] { new { columnCode = textColumn, value = (object)atLimit } },
                },
            },
        }).ConfigureAwait(true);

        Assert.True(
            accepted.StatusCode == HttpStatusCode.OK,
            $"PATCH із 1000 символами: {accepted.StatusCode}\n"
            + $"{await accepted.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var row = await ReadRowAsync(client, sliceUri, secondRow, app).ConfigureAwait(true);
        var readBack = row.GetProperty("cells").GetProperty(textColumn).GetString();

        Assert.Equal(1000, readBack!.Length);
        Assert.Equal(atLimit, readBack);
    }

    /// <summary>
    /// `U-22` + `U-23` наскрізно, рівно сценарієм живого стенда: число з
    /// хвостом за межею сховища відхиляється, а повтор незмінного значення не
    /// дає рядка в журналі.
    /// </summary>
    /// <remarks>
    /// ⛔ Що було на стенді: `931.925` → дописати `123` → «Saved»; у
    /// <c>doc.CellValue</c> лишилося <c>931.9250000000000000</c> (SqlClient
    /// округлив на клієнті), а <c>aud.CellChange</c> записав
    /// <c>931.9250000000000000 → 931.9250000000000000123</c> — зміну, якої не
    /// було. Тут перевіряється все це на справжній базі: код і ключ відмови,
    /// незмінне значення в сховищі й ЧИСЛО рядків журналу по комірці.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Хвіст_за_межею_сховища_відхиляється_а_повтор_значення_не_журналюється()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        var rowKey = $"U22{Guid.NewGuid():N}"[..12];

        async Task<HttpResponseMessage> PatchAsync(string? baseVersion, string value)
            => await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = scenario.Document.TableInstanceId,
                periodKey = scenario.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion,
                        cells = new object[] { new { columnCode = numberColumn, value = (object)value } },
                    },
                },
            }).ConfigureAwait(true);

        // ── 1. Вихідне значення ──────────────────────────────────────────
        var created = await PatchAsync(null, "931.925").ConfigureAwait(true);
        var createdText = await created.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"PATCH створення: {created.StatusCode}\n{createdText}\n{app.ErrorsText}");

        var version = JsonDocument.Parse(createdText).RootElement
            .GetProperty("rowVersions").GetProperty(rowKey).GetString();

        // ── 2. Хвіст за межею сховища — відмова з ключем, а не «Saved» ───
        var tail = await PatchAsync(version, "931.9250000000000000123").ConfigureAwait(true);
        var tailText = await tail.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            tail.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"PATCH із хвостом: очікували 422, отримали {tail.StatusCode}\n{tailText}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(tailText).RootElement;
        Assert.Equal("ECR-CELL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CELL-0422.tooManyDecimals", problem.GetProperty("messageKey").GetString());

        // ── 3. Повтор того самого значення — 200, але без рядка журналу ──
        var same = await PatchAsync(version, "931.925").ConfigureAwait(true);
        Assert.True(
            same.StatusCode == HttpStatusCode.OK,
            $"PATCH того самого значення: {same.StatusCode}\n"
            + $"{await same.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        // ── 4. Сховище і журнал ──────────────────────────────────────────
        await using (var db = scenario.Builder.CreateContext())
        {
            var stored = await db.CellValues
                .AsNoTracking()
                .Where(c => c.PeriodKeyValue == scenario.PeriodKey
                            && c.ColumnDefId == scenario.Document.ColumnDefIds[1])
                .Select(c => c.ValueNumeric)
                .SingleAsync()
                .ConfigureAwait(true);

            Assert.Equal(931.925m, stored);
        }

        var journal = await CellJournalAsync(
            scenario.Document.DocumentId, rowKey, scenario.Document.ColumnDefIds[1]).ConfigureAwait(true);

        // ⛔ Рівно ОДИН рядок — створення. Ні відхилений хвіст, ні повтор
        // незмінного числа журнал не зачепили: кожен рядок журналу — справжня
        // зміна значення в сховищі.
        var only = Assert.Single(journal);
        Assert.Null(only.OldValue);
        Assert.Equal(931.925m, decimal.Parse(only.NewValue!, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Ціла частина понад межу сховища — <c>422</c> з ключем, а не <c>500</c>
    /// від СУБД; найбільше значення, що вміщається, доживає до бази цілим.
    /// </summary>
    /// <remarks>
    /// ⛔ Доти `1000000000000000000` (19 розрядів) проходило <c>CellValueReader</c>
    /// і падало на записі з <c>Arithmetic overflow</c> — HTTP 500 замість
    /// пояснення. Межа 18 розрядів — не домовленість, а <c>decimal(34,16)</c>;
    /// тому другий бік перевіряється на СПРАВЖНІЙ базі: якби межа в коді була
    /// ширшою за колонку, граничне значення тут дало б 500, а вужчою — 422.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-148")]
    public async Task Ціла_частина_понад_межу_сховища_відхиляється_422_а_межа_доживає_до_бази()
    {
        const string AtLimit = "999999999999999999.9999999999";
        const string Overflow = "1000000000000000000";

        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        var rowKey = $"OVF{Guid.NewGuid():N}"[..12];

        async Task<HttpResponseMessage> PatchAsync(string? baseVersion, string value)
            => await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = scenario.Document.TableInstanceId,
                periodKey = scenario.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion,
                        cells = new object[] { new { columnCode = numberColumn, value = (object)value } },
                    },
                },
            }).ConfigureAwait(true);

        // ── 1. Переповнення цілої частини — 422 з ключем, не 500 ─────────
        var overflow = await PatchAsync(null, Overflow).ConfigureAwait(true);
        var overflowText = await overflow.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(
            overflow.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"PATCH «{Overflow}»: очікували 422, отримали {overflow.StatusCode}\n{overflowText}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(overflowText).RootElement;
        Assert.Equal("ECR-CELL-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CELL-0422.tooManyIntegerDigits", problem.GetProperty("messageKey").GetString());

        // ── 2. Найбільше значення, яке колонка вміщає, — 200 і ціле в базі ─
        var atLimit = await PatchAsync(null, AtLimit).ConfigureAwait(true);
        Assert.True(
            atLimit.StatusCode == HttpStatusCode.OK,
            $"PATCH «{AtLimit}»: {atLimit.StatusCode}\n"
            + $"{await atLimit.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        await using var db = scenario.Builder.CreateContext();
        var stored = await db.CellValues
            .AsNoTracking()
            .Where(c => c.PeriodKeyValue == scenario.PeriodKey
                        && c.ColumnDefId == scenario.Document.ColumnDefIds[1])
            .Select(c => c.ValueNumeric)
            .SingleAsync()
            .ConfigureAwait(true);

        Assert.Equal(decimal.Parse(AtLimit, System.Globalization.CultureInfo.InvariantCulture), stored);
    }

    /// <summary>
    /// Відхилене значення в батчі, що СТВОРЮЄ рядок, не лишає рядка-сироти:
    /// повтор того самого ключа з правильним значенням — 200, а не 409.
    /// </summary>
    /// <remarks>
    /// ⛔ Знайдено тестом переповнення вище: рядок вставлявся до розбору значень
    /// і поза транзакцією запису, тож 422 на «abc» лишав у <c>doc.TableRow</c>
    /// порожній рядок, і наступна спроба користувача падала на
    /// <c>ECR-ROW-0409</c> — «рядок із таким ключем уже існує» для рядка,
    /// якого він ніколи не створював.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B04-2.3")]
    public async Task Відхилене_значення_нового_рядка_не_лишає_рядка_сироти()
    {
        var scenario = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, scenario.UserName).ConfigureAwait(true);

        var sliceUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/tables/{scenario.Document.TableInstanceId}",
            UriKind.Relative);
        var patchUri = new Uri(
            $"/api/v1/documents/{scenario.Document.DocumentId}/cells", UriKind.Relative);

        var opened = await client.GetAsync(sliceUri).ConfigureAwait(true);
        Assert.True(opened.IsSuccessStatusCode, $"GET зрізу: {opened.StatusCode}: {app.ErrorsText}");

        var numberColumn = JsonDocument
            .Parse(await opened.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ElementAt(1);

        var rowKey = $"ORP{Guid.NewGuid():N}"[..12];

        async Task<HttpResponseMessage> CreateAsync(string value)
            => await client.PatchAsJsonAsync(patchUri, new
            {
                tableInstanceId = scenario.Document.TableInstanceId,
                periodKey = scenario.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = numberColumn, value = (object)value } },
                    },
                },
            }).ConfigureAwait(true);

        var refused = await CreateAsync("abc").ConfigureAwait(true);
        Assert.True(
            refused.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"PATCH «abc»: очікували 422, отримали {refused.StatusCode}\n"
            + $"{await refused.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var retried = await CreateAsync("12.5").ConfigureAwait(true);
        Assert.True(
            retried.StatusCode == HttpStatusCode.OK,
            $"Повтор із правильним значенням: очікували 200, отримали {retried.StatusCode} "
            + $"(409 = відхилений батч лишив рядок-сироту)\n"
            + $"{await retried.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
    }

    /// <summary>Рядки <c>aud.CellChange</c> однієї комірки — прямим ADO, бо <c>aud.*</c> поза моделлю EF.</summary>
    private async Task<List<(string? OldValue, string? NewValue)>> CellJournalAsync(
        long documentId, string rowKey, int columnDefId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OldValue, NewValue FROM aud.CellChange
            WHERE DocumentId = @documentId AND RowKey = @rowKey AND ColumnDefId = @columnDefId
            ORDER BY ChangedAt;
            """;
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@columnDefId", columnDefId);

        var rows = new List<(string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
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
