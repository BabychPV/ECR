using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>§6.5 директиви — розрахунок: S-21..S-26.</summary>
/// <remarks>
/// ⛔ `W6` (директива №09) закрив авторство МЕТОДОЛОГІЇ через API: саму
/// методологію, константи, правила відбору, оголошені виходи, золотий набір,
/// режими обчислення і — головне — прив'язку виходу до колонки документа
/// (<c>cfg.CalculationBinding</c>), яку доти не створювало ніщо, навіть тест.
/// Тому `S-23`, `S-24` і `S-26` більше не доводять «маршруту немає», а
/// проходять увесь шлях: завести → опублікувати → перерахувати → прочитати
/// число. Той самий перехід, який `W5.9` зробив для `S-05`…`S-09`.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationScenarios(SqlServerFixture sql)
{
    /// <summary>
    /// S-21. Формула шаблону: після <c>PATCH</c> у <c>A</c> і <c>B</c> комірка
    /// <c>C</c> стає сумою сама.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Сценарій лишається недоведеним, і причина вже інша, ніж була.</b>
    /// Стара («формулу колонки нема як зберегти, S-05») застаріла: `W5.3` дав
    /// <c>PUT …/tables/{id}/formulas/column/{columnId}</c>. Тепер блокує інше й
    /// глибше: спільне приготування <c>DataEntryScenarios.ArrangeDocumentAsync</c>
    /// будує документ на ПОРОЖНІЙ версії шаблону, тож у ньому немає жодної
    /// таблиці — а `S-24` показує, що з реальною структурою той самий шлях
    /// проходиться цілком. Друга половина, глибша: формули ШАБЛОНУ рахує
    /// <c>FormulaRecalculationJob</c> (результат у <c>doc.CellValue</c>), а
    /// <c>POST …/recalculate</c> ставить у чергу перерахунок МЕТОДОЛОГІЙ
    /// (<c>calc.CalculationResult</c>, <c>D-69</c>). Це два різні конвеєри, і
    /// зробити цей сценарій зеленим означає довести другий — обсяг окремого
    /// пакета, не `W6`.
    ///
    /// ⚠ Два дефекти самого сценарію тут ВИПРАВЛЕНО, бо вони приховували
    /// справжню причину: не було права <c>System.ViewHealth</c> (стан задачі
    /// віддає <c>GET /jobs/{jobId}</c> саме під ним, і опитування отримувало
    /// `403`), а <c>jobId</c> не екранувався (див. <c>ScenarioHelpers</c>).
    /// Через них сценарій падав із «перерахунок не завершився», не почавши
    /// перевіряти те, про що він.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-21")]
    public async Task Формула_шаблону_рахує_C_після_запису_A_і_B()
    {
        using var app = new EcrApiFactory(sql);

        // ⛔ `System.ViewHealth` у переліку не для повноти: стан задачі віддає
        // `GET /jobs/{jobId}` саме під цим правом, і без нього опитування
        // отримувало `403`, а `AwaitJobAsync` мовчки віддавав «немає стану».
        // Сценарій падав із «перерахунок не завершився успіхом», хоча про
        // перерахунок нічого й не питав.
        var admin = await Provisioning.AdministratorAsync(
            app,
            "S21",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit",
                "Calculation.Recalculate", "System.ViewHealth",
            ]);

        (admin, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, admin, "S21");

        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);

        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(30));
        Assert.True(
            final.ValueKind != JsonValueKind.Undefined,
            $"стан задачі {jobId} не прочитався взагалі: {app.ErrorsText}");
        Assert.True(
            !string.Equals(final.GetProperty("state").GetString(), "Running", StringComparison.Ordinal),
            $"перерахунок документа {documentId} не завершився за 30 с: {final.GetRawText()}");

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці — нема де шукати обчислену колонку C.");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        var slice = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        var sliceBody = await slice.Content.ReadFromJsonAsync<JsonElement>();
        var columns = sliceBody.GetProperty("columns");
        var calculated = columns.EnumerateArray()
            .FirstOrDefault(c => string.Equals(c.GetProperty("dataType").GetString(), "Formula", StringComparison.Ordinal));
        Assert.True(calculated.ValueKind != JsonValueKind.Undefined, $"таблиця {tableInstanceId} не має жодної колонки-формули (dataType=Formula) — S-05 не дав змоги її зберегти.");
    }

    /// <summary>S-22. <c>CONVERT</c> між одиницями в формулі шаблону.</summary>
    /// <remarks>
    /// ⚠ Половина, ЯКУ можна довести без структури: сам механізм конверсії
    /// (`POST /units/convert`, ФВ-16.2..ФВ-16.4). Формулу з `CONVERT(...)` у
    /// реальній колонці зберегти нема як (S-05).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-22")]
    public async Task Convert_між_одиницями_в_формулі_шаблону()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(app, "S22", ["Calculation.View", "Template.Edit"]);

        // Робочий шматок: t -> kg у межах тієї самої розмірності Mass (seed §14).
        var convert = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/units/convert", UriKind.Relative),
            new { value = 2m, fromUnit = "t", toUnit = "kg" });
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        var converted = await convert.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2000m, converted.GetProperty("value").GetDecimal());

        // Недосяжна половина: формула CONVERT(...) у РЕАЛЬНІЙ колонці версії.
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(admin.Client, "S22");
        var addColumn = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/1/columns", UriKind.Relative),
            new { code = "C", formula = "CONVERT(A, 't', 'kg')" });
        Assert.Equal(HttpStatusCode.NotFound, addColumn.StatusCode);
    }

    /// <summary>
    /// S-23. Прив'язка методології до таблиці заводиться через API.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Розбіжність із текстом сценарію, і вона задокументована</b>
    /// (`D2-308`). Сценарій очікував <c>POST /methodologies/{id}/binding-rules</c>
    /// з тілом <c>{tableDefId, priority, matchExpression}</c>. Такої сутності в
    /// домені немає й бути не може: те, що прив'язує методологію до ТАБЛИЦІ, —
    /// це <c>cfg.CalculationBinding</c>, і його ключ — трійка <c>(колонка,
    /// методологія, код виходу)</c>, бо результат мусить кудись лягти;
    /// пріоритету в нього немає взагалі. Пріоритет і «перший збіг виграє» —
    /// властивість <c>calc.MethodologyRule</c>, яке відбирає РЯДКИ і живе на
    /// версії, а не на таблиці. Дві різні сутності, і жодна не має тієї форми,
    /// яку припускав сценарій.
    ///
    /// ⚠ Доводиться те саме, що й малося на увазі: реальний HTTP-виклик
    /// створює реальний, збережений запис, який зв'язує методологію з таблицею,
    /// а повторний виклик за тією самою адресою є ідемпотентним (<c>PUT</c> за
    /// адресою, як формула і колонка — <c>D2-147</c>).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-23")]
    public async Task Прив_язка_методології_до_таблиці_заводиться_через_API()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app,
            "S23",
            ["Template.View", "Template.Edit", "Calculation.View", "Calculation.EditFormula", "Calculation.EditRule"]);

        var structure = await ArrangeStructureAsync(app, admin.Client, "S23");
        var methodologyId = await CreateMethodologyAsync(app, admin.Client, "S23");

        var address =
            $"/api/v1/methodologies/{methodologyId}/bindings/{structure.ResultColumnId}/EMISSION";

        var first = await admin.Client.PutAsJsonAsync(
            new Uri(address, UriKind.Relative),
            new { matchJson = "{}", isActive = true });
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");

        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        var bindingId = created.GetProperty("id").GetInt32();

        // ⛔ Таблиця в прив'язці — ВИВЕДЕНА з колонки, а не прийнята від
        // клієнта: два поля про те саме розходяться мовчки, і прив'язка з чужим
        // `TableDefId` просто не спрацьовує (`RecalculationJob` шукає екземпляри
        // саме за ним).
        Assert.Equal(structure.TableDefId, created.GetProperty("tableDefId").GetInt32());

        // Персистентність доводить ЧИТАННЯ тим самим API, яким читає клієнт
        // (Правило 3 §3.2), а не `SELECT`.
        var listed = await admin.Client.GetAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/bindings", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var bindings = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            bindings.EnumerateArray(),
            b => b.GetProperty("id").GetInt32() == bindingId
                 && b.GetProperty("columnDefId").GetInt32() == structure.ResultColumnId
                 && string.Equals(b.GetProperty("outputCode").GetString(), "EMISSION", StringComparison.Ordinal));

        // Ідемпотентність: та сама адреса — той самий запис, а не другий.
        var second = await admin.Client.PutAsJsonAsync(
            new Uri(address, UriKind.Relative),
            new { matchJson = """{"kind":"stack"}""", isActive = false });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var updated = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(bindingId, updated.GetProperty("id").GetInt32());
        Assert.Equal("""{"kind":"stack"}""", updated.GetProperty("matchJson").GetString());
        Assert.False(updated.GetProperty("isActive").GetBoolean());
    }

    /// <summary>
    /// S-24. Методологія: створити методологію, константи, правило відбору → результат.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Приймання пакета `W6`</b> (директива №09): одна методологія
    /// заведена з нуля через інтерфейс і дає правильне число на реальному
    /// документі. Ланцюг тут повний і жоден крок не підмінено фікстурою:
    /// шаблон → проєкт → документ → значення в комірці → методологія →
    /// константа → формула → вихід → правило відбору → золотий набір →
    /// прив'язка до колонки → публікація іншим користувачем (чотири очі,
    /// `D-40`) → перерахунок → число.
    ///
    /// ⚠ Число вибране так, щоб його не можна було отримати випадково:
    /// <c>4 × 2.5 = 10</c> — жодне з двох джерел не дорівнює результату, тож ані
    /// «взяли аргумент», ані «взяли константу» не дали б збігу.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-24")]
    public async Task Методологія_константи_правило_відбору_результат()
    {
        var stand = await ArrangeStandAsync(sql, "S24", argument: 4m, divisor: 2m);
        using var app = stand.App;

        var methodologyId = await CreateMethodologyAsync(app, stand.Admin.Client, "S24");
        var versionId = await CreateDraftVersionAsync(app, stand.Admin.Client, methodologyId, "1.0.0");

        await SaveConstantAsync(app, stand.Admin.Client, methodologyId, versionId, "EF", 2.5m, stand.UnitId);
        await SaveFormulaAsync(app, stand.Admin.Client, methodologyId, versionId, "EMISSION", "@A * CST.EF", "A", stand.UnitId);
        await SaveOutputAsync(app, stand.Admin.Client, methodologyId, versionId, "EMISSION", stand.UnitId);
        await SaveRuleAsync(app, stand.Admin.Client, methodologyId, versionId, "ALL_ROWS", "{}", priority: 1);
        await SaveTestCaseAsync(
            app, stand.Admin.Client, methodologyId, versionId, "GOLDEN",
            Input(stand, argument: 4m, divisor: 2m), """{"EMISSION":10}""");

        await SaveBindingAsync(app, stand.Admin.Client, methodologyId, stand.ResultColumnId, "EMISSION");

        await PublishAsync(app, stand.Publisher.Client, methodologyId, versionId, EffectiveFrom(stand.PeriodKey, yearsBack: 1));

        await RecalculateAsync(app, stand.Admin, stand.DocumentId, stand.PeriodKey);

        var results = await ReadResultsAsync(app, stand.Admin.Client, stand.DocumentId, stand.PeriodKey);

        var emission = results.EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("outputCode").GetString(), "EMISSION", StringComparison.Ordinal));

        Assert.True(
            emission.ValueKind != JsonValueKind.Undefined,
            $"перерахунок не дав жодного результату EMISSION: {results.GetRawText()}; {app.ErrorsText}");

        // ⛔ Саме ЧИСЛО, а не «щось порахувалося»: 4 (комірка A) × 2.5
        // (константа EF) = 10. Перевірка «результат є» пройшла б і на нулі.
        Assert.Equal(10m, emission.GetProperty("value").GetDecimal());
        Assert.Equal(stand.RowKey, emission.GetProperty("sourceRowKey").GetString());
        Assert.Equal(versionId, emission.GetProperty("methodologyVersionId").GetInt32());
    }

    /// <summary>
    /// S-25. Збій розрахунку видимий: задача переходить у кінцевий стан із
    /// причиною, і перелік черги (<c>GET /jobs</c>) її показує.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік черги <b>існує</b> (<c>GET /api/v1/jobs</c>, право
    /// <c>System.ViewHealth</c>) — сценарій доти стверджував протилежне і
    /// перевіряв `404`. Тепер він доводить те, заради чого перелік потрібен:
    /// задачу видно в черзі за її <c>jobId</c>, тобто побачити її може й той,
    /// хто цього <c>jobId</c> не отримував.
    ///
    /// ⛔ Кінцевий стан НЕ фіксується як <c>Failed</c>. Перерахунок документа
    /// без жодної прив'язаної методології не має чому «зламатися»: порожній
    /// набір прив'язок — законний стан, а не помилка (саме тому `W6` і був
    /// потрібен, щоб довести протилежний випадок — `S-24`). Сценарій вимагає
    /// того, що є вимогою насправді: задача не зависає, а доходить до кінця, і
    /// кінцевий стан має пояснення — або успіх, або причина провалу (ФВ-9.14).
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-25")]
    public async Task Збій_розрахунку_видимий_у_стані_задачі()
    {
        using var app = new EcrApiFactory(sql);
        var admin = await Provisioning.AdministratorAsync(
            app, "S25", ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Calculation.Recalculate", "System.ViewHealth"]);

        (admin, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, admin, "S25");

        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);
        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(30));

        Assert.True(final.ValueKind != JsonValueKind.Undefined, $"задача {jobId} не набула кінцевого стану за 30 с: {app.ErrorsText}");
        var state = final.GetProperty("state").GetString();
        var error = final.TryGetProperty("error", out var e) ? e.GetString() : null;

        Assert.True(
            !string.Equals(state, "Running", StringComparison.Ordinal)
            && !string.Equals(state, "Queued", StringComparison.Ordinal),
            $"задача {jobId} лишилася в стані {state}: перерахунок, який не доходить до кінця, невидимий за побудовою.");

        // ⛔ Провал без причини — це і є те, що ФВ-9.14 забороняє: таксономія
        // помилки, а не порожнеча.
        if (string.Equals(state, "Failed", StringComparison.Ordinal))
        {
            Assert.False(string.IsNullOrWhiteSpace(error), "задача Failed без причини — ФВ-9.14 вимагає таксономію помилки.");
        }

        // Перелік черги: задачу видно й без знання її jobId наперед.
        var queueList = await admin.Client.GetAsync(new Uri("/api/v1/jobs", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, queueList.StatusCode);
        var queue = await queueList.Content.ReadFromJsonAsync<JsonElement>();
        var items = queue.ValueKind == JsonValueKind.Array ? queue : queue.GetProperty("items");

        Assert.Contains(
            items.EnumerateArray(),
            j => string.Equals(JobId(j), jobId, StringComparison.Ordinal));
    }

    /// <summary>
    /// S-26. <c>MaskedZero</c>: ділення на нуль у <c>Legacy</c> → <c>0</c> із
    /// причиною в трейсі; у <c>Strict</c> → <c>null</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Доводиться саме те, що маскування ДОСЯЖНЕ через реальний шлях
    /// авторства методології, а не те, що арифметика працює (її перевіряють
    /// модульні тести `MaskedZero`). Доти обидва режими були недосяжні:
    /// методологію не було чим завести, а <c>NumericMode.Strict</c> не було чим
    /// увімкнути — <c>SetModes</c> кликав лише <c>CloneAsDraft</c>, який
    /// переносить режим джерела.
    ///
    /// ⚠ Дві версії однієї методології з різними датами дії, а не дві
    /// методології: саме так це роблять насправді (<c>ФВ-13.2</c>) — режим
    /// змінюють клоном і новою датою, бо він тихо змінює всі числа.
    ///
    /// ⚠ Золотий набір рахує НЕ ділення на нуль (<c>10 / 2</c>): версія без
    /// зеленого набору не публікується взагалі (ФВ-9.12), а нуль у знаменнику
    /// стоїть у комірці ДОКУМЕНТА — там, де він і трапляється в житті.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-26")]
    public async Task MaskedZero_ділення_на_нуль_Legacy_і_Strict()
    {
        var stand = await ArrangeStandAsync(sql, "S26", argument: 10m, divisor: 0m);
        using var app = stand.App;

        var methodologyId = await CreateMethodologyAsync(app, stand.Admin.Client, "S26");
        var legacyVersionId = await CreateDraftVersionAsync(app, stand.Admin.Client, methodologyId, "1.0.0");

        await SaveFormulaAsync(app, stand.Admin.Client, methodologyId, legacyVersionId, "EMISSION", "@A / @B", "A;B", stand.UnitId);
        await SaveOutputAsync(app, stand.Admin.Client, methodologyId, legacyVersionId, "EMISSION", stand.UnitId);
        await SaveRuleAsync(app, stand.Admin.Client, methodologyId, legacyVersionId, "ALL_ROWS", "{}", priority: 1);
        await SaveTestCaseAsync(
            app, stand.Admin.Client, methodologyId, legacyVersionId, "GOLDEN",
            Input(stand, argument: 10m, divisor: 2m), """{"EMISSION":5}""");

        // ⛔ Другий випадок набору — саме ділення на нуль, і очікується від
        // нього НІЧОГО (`{}`). Це не «тест ні про що»: у `Legacy` він дає нуль,
        // у `Strict` — відсутність значення, і жорстко очікувати одне з двох
        // означало б, що набір червоніє від зміни режиму, тобто версію
        // неможливо перевести в `Strict` узагалі. Те, ЩО саме він дає в кожному
        // режимі, перевіряють асерти нижче — на реальному документі.
        await SaveTestCaseAsync(
            app, stand.Admin.Client, methodologyId, legacyVersionId, "ZERO",
            Input(stand, argument: 10m, divisor: 0m), "{}");

        // ⚠ Трейс потрібен ПОВНИЙ саме тут: замаскований нуль пишеться на
        // будь-якому рівні, крім Off, але сусідні кроки — лише на Full, і без
        // них рядок «EMISSION = 0» нема з чим порівняти.
        await SaveModesAsync(app, stand.Admin.Client, methodologyId, legacyVersionId, "Legacy", "Full");
        await SaveBindingAsync(app, stand.Admin.Client, methodologyId, stand.ResultColumnId, "EMISSION");

        await PublishAsync(app, stand.Publisher.Client, methodologyId, legacyVersionId, EffectiveFrom(stand.PeriodKey, yearsBack: 2));
        await RecalculateAsync(app, stand.Admin, stand.DocumentId, stand.PeriodKey);

        var legacyResults = await ReadResultsAsync(app, stand.Admin.Client, stand.DocumentId, stand.PeriodKey);
        var masked = legacyResults.EnumerateArray().FirstOrDefault(
            r => string.Equals(r.GetProperty("outputCode").GetString(), "EMISSION", StringComparison.Ordinal));

        Assert.True(
            masked.ValueKind != JsonValueKind.Undefined,
            $"Legacy мав дати замаскований нуль, а не відсутність результату: {legacyResults.GetRawText()}; {app.ErrorsText}");
        Assert.Equal(0m, masked.GetProperty("value").GetDecimal());

        // ⛔ Друга половина обіцянки Legacy: число те саме, що дала б чинна
        // система, але ПРИЧИНА названа. Прогін без запису (`ФВ-13.5`) — єдиний
        // маршрут, який віддає трейс; ділення на нуль подається йому власним
        // входом, бо в золотому наборі версії стоїть робочий випадок.
        var simulated = await SimulateAsync(app, stand.Admin.Client, methodologyId, legacyVersionId, stand.PeriodKey);
        var trace = string.Join("\n", simulated.GetProperty("trace").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("masked", trace, StringComparison.OrdinalIgnoreCase);

        // Strict — НОВА версія з новою датою дії: режим міняють клоном, бо він
        // тихо змінює всі числа (`ФВ-9.9`, `D-78`).
        var strictVersionId = await CloneDraftVersionAsync(
            app, stand.Admin.Client, methodologyId, "2.0.0", legacyVersionId);
        await SaveModesAsync(app, stand.Admin.Client, methodologyId, strictVersionId, "Strict", "Full");
        await PublishAsync(app, stand.Publisher.Client, methodologyId, strictVersionId, EffectiveFrom(stand.PeriodKey, yearsBack: 1));

        await RecalculateAsync(app, stand.Admin, stand.DocumentId, stand.PeriodKey);

        var strictResults = await ReadResultsAsync(app, stand.Admin.Client, stand.DocumentId, stand.PeriodKey);

        // ⛔ У Strict виходу НЕМАЄ зовсім, і це не те саме, що нуль: нуль
        // виглядає як виміряне значення (`ФВ-9.14`).
        Assert.DoesNotContain(
            strictResults.EnumerateArray(),
            r => string.Equals(r.GetProperty("outputCode").GetString(), "EMISSION", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Кроки стенда. ⚠ Це НЕ `ProjectBuilder`-подібний помічник, який ховає
    // бізнес-логіку: кожен метод — рівно один HTTP-виклик, який зробила б
    // людина, і жоден не пише в базу повз API. Спільними вони стали тому, що
    // `S-24` і `S-26` проходять ОДИН І ТОЙ САМИЙ шлях і різняться лише
    // виразом, режимом і очікуваним числом.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Структура шаблону, на якій рахує методологія.</summary>
    /// <param name="TemplateVersionId">Версія-чернетка шаблону.</param>
    /// <param name="SheetDefId">Аркуш; він же входить у склад документа.</param>
    /// <param name="TableDefId">Таблиця з фіксованими рядками.</param>
    /// <param name="ResultColumnId">Колонка-приймач результату методології.</param>
    private sealed record Structure(
        int TemplateVersionId, int SheetDefId, int TableDefId, int ResultColumnId);

    /// <summary>Готовий стенд: структура, документ із числом і два користувачі.</summary>
    /// <param name="App">
    /// Застосунок, у якому стенд живий. ⛔ Це ДРУГИЙ хост, і саме тому він у
    /// стенді: стани періодів вирівнює <c>PeriodStateJob</c>, а вона в
    /// застосунку виконується один раз на СТАРТІ і далі щогодини
    /// (<c>RecurringScheduleService</c>). Проєкт, створений після старту,
    /// лишається з періодами в стані <c>Scheduled</c> назавжди в межах тесту —
    /// а <c>Scheduled</c> блокує будь-який запис (<c>EditRules</c>,
    /// <c>PeriodNotOpenYet</c>). Тому календар будується в одному хості, а
    /// працюють у наступному, чий старт стани й вирівняв.
    /// </param>
    /// <param name="Admin">Автор методології; опублікувати її він не зможе (<c>D-40</c>).</param>
    /// <param name="Publisher">Той, хто публікує — інша людина, як вимагає домен.</param>
    /// <param name="ResultColumnId">Колонка-приймач результату.</param>
    /// <param name="DocumentId">Документ.</param>
    /// <param name="TableInstanceId">Екземпляр таблиці за цей період.</param>
    /// <param name="PeriodKey">Період.</param>
    /// <param name="RowKey">Рядок, у який записано вхідні числа.</param>
    /// <param name="UnitId">Одиниця <c>t</c> із seed — для константи й виходу.</param>
    private sealed record Stand(
        EcrApiFactory App,
        Provisioning.Administrator Admin,
        Provisioning.Administrator Publisher,
        int ResultColumnId,
        long DocumentId,
        long TableInstanceId,
        int PeriodKey,
        string RowKey,
        int UnitId);

    /// <summary>Заводить аркуш, таблицю з фіксованим рядком і три колонки.</summary>
    private static async Task<Structure> ArrangeStructureAsync(
        EcrApiFactory app, HttpClient client, string prefix)
    {
        var versionId = await StructureScenarios.CreateEmptyDraftVersionAsync(client, prefix);

        var sheet = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Sheet 1" },
                ordinal = 1,
                sheetGroup = (string?)null,
                isMandatory = true,
                isVisible = true,
            });
        Assert.True(sheet.StatusCode == HttpStatusCode.OK, $"аркуш: {sheet.StatusCode}: {app.ErrorsText}");
        var sheetId = (await sheet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var table = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/sheets/SHEET1/tables/TABLE1", UriKind.Relative),
            new
            {
                nameL10n = new Dictionary<string, string> { ["en"] = "Table 1" },
                ordinal = 1,
                layoutKind = "PerPeriodInstance",

                // ⚠ `Dynamic`, а не `Fixed`, і це не спрощення. Зріз таблиці
                // будує рядки з КОМІРОК (`GetTableSliceHandler`), а не з
                // `cfg.RowDef`: незаповнена комірка не матеріалізується
                // (ФВ-3.8), тож у `Fixed`-таблиці без жодного запису рядків
                // немає взагалі — і записати в них теж нема куди. Рядок, у
                // який пише сценарій, має бути створений явно.
                rowMode = "Dynamic",
                maxDynamicRows = (int?)null,
            });
        Assert.True(table.StatusCode == HttpStatusCode.OK, $"таблиця: {table.StatusCode}: {app.ErrorsText}");
        var tableId = (await table.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var resultColumnId = 0;
        foreach (var code in new[] { "A", "B", "EMISSION" })
        {
            var column = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/{code}", UriKind.Relative),
                new
                {
                    headerL10n = new Dictionary<string, string> { ["en"] = code },
                    ordinal = (int?)null,
                    dataType = "Decimal",
                    isRequired = false,
                    isReadOnly = false,
                    isHidden = false,
                    precision = (byte?)null,
                    scale = (byte?)null,
                    defaultValue = (string?)null,
                    displayFormat = (string?)null,
                    styleId = (int?)null,
                    lookupRegistryDefId = (int?)null,
                    lookupFilter = (string?)null,
                    unitId = (int?)null,
                });
            Assert.True(column.StatusCode == HttpStatusCode.OK, $"колонка {code}: {column.StatusCode}: {app.ErrorsText}");

            if (string.Equals(code, "EMISSION", StringComparison.Ordinal))
            {
                resultColumnId = (await column.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
            }
        }

        return new Structure(versionId, sheetId, tableId, resultColumnId);
    }

    /// <summary>Повний стенд: структура, проєкт, документ і записані вхідні числа.</summary>
    /// <remarks>
    /// ⛔ Два хости, і другий — не хитрість, а єдиний доступний шлях. Стан
    /// періоду — <b>збережене значення</b> (ФВ-1.12), яке міняє лише
    /// <c>PeriodStateJob</c>; та виконується один раз на старті застосунку і
    /// далі щогодини (<c>RecurringScheduleService</c>). Проєкт, створений
    /// ПІСЛЯ старту, лишається з періодами в <c>Scheduled</c> до кінця тесту, а
    /// <c>Scheduled</c> блокує будь-який запис (<c>EditRules</c>:
    /// <c>PeriodNotOpenYet</c>) — тобто ані рядка, ані числа в документі не
    /// з'явиться. Тому календар будується в першому хості, а працюють у
    /// другому, чий старт стани й вирівняв. Це та сама послідовність, що в
    /// житті: розгорнули → проєкт завели → система вирівняла стани.
    /// </remarks>
    private static async Task<Stand> ArrangeStandAsync(
        SqlServerFixture sql, string prefix, decimal argument, decimal divisor)
    {
        Structure structure;
        int projectId;
        Provisioning.Administrator admin;
        Provisioning.Administrator publisher;

        using (var setup = new EcrApiFactory(sql))
        {
            admin = await Provisioning.AdministratorAsync(
                setup,
                prefix,
                [
                    "Project.Manage", "Document.View", "Document.Create", "Template.View", "Template.Edit",
                    "Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant",
                    "Calculation.EditRule", "Calculation.Recalculate", "System.ViewHealth",
                ]);

            // ⛔ Другий користувач не для повноти сценарію: `MethodologyVersion.Publish`
            // системно забороняє публікувати ВЛАСНУ правку (правило чотирьох очей,
            // `D-40`). Одним обліковим записом цей ланцюг не проходиться взагалі.
            publisher = await Provisioning.AdministratorAsync(
                setup, $"{prefix}Pub", ["Calculation.Publish", "Calculation.View"]);

            structure = await ArrangeStructureAsync(setup, admin.Client, prefix);

            // ⛔ Проєкт створюється на ТІЙ САМІЙ версії шаблону, що й документ, і це
            // не дрібниця приготування. `RowStore.GetTableInstancesAsync` бере
            // `TemplateVersionId` із ПРОЄКТУ, а не з документа: документ, створений
            // на іншій версії, дістає екземпляри таблиць — і не показує жодної,
            // бо знімок структури береться за версією проєкту. Спільний помічник
            // `CreateProjectAsync` заводить власну порожню версію, тож тут потрібен
            // власний виклик.
            projectId = await CreateProjectOnVersionAsync(
                setup, admin.Client, prefix, structure.TemplateVersionId);
            await ProjectAndPeriodScenarios.ActivateProjectAsync(admin.Client, projectId);
            await Provisioning.GrantAsync(setup, admin.RoleId, "Project", projectId, "Manage");
        }

        var app = new EcrApiFactory(sql);
        admin = await Provisioning.ReauthenticateAsync(app, admin);
        publisher = await Provisioning.ReauthenticateAsync(app, publisher);

        var periodsResponse = await admin.Client.GetAsync(
            new Uri($"/api/v1/projects/{projectId}/periods", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, periodsResponse.StatusCode);
        var periods = (await periodsResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("periods");
        Assert.True(periods.GetArrayLength() > 0, $"календар проєкту {projectId} порожній.");

        // ⛔ Береться період, у який МОЖНА писати: `Open` або `Grace`
        // (`Period.AllowsEditing`). «Перший у переліку» — це січень, і він
        // закритий: сценарій падав би на записі числа з `PeriodNotOpenYet`
        // або `PeriodClosed`, не дійшовши до предмета перевірки.
        var writable = periods.EnumerateArray().FirstOrDefault(
            p => string.Equals(p.GetProperty("state").GetString(), "Open", StringComparison.Ordinal))
            is { ValueKind: JsonValueKind.Object } open
            ? open
            : periods.EnumerateArray().FirstOrDefault(
                p => string.Equals(p.GetProperty("state").GetString(), "Grace", StringComparison.Ordinal));

        Assert.True(
            writable.ValueKind == JsonValueKind.Object,
            "жоден період проєкту не приймає запису (потрібен Open або Grace); стани: "
            + string.Join(", ", periods.EnumerateArray().Select(
                p => $"{p.GetProperty("periodKey").GetInt32()}={p.GetProperty("state").GetString()}")));

        var periodKey = writable.GetProperty("periodKey").GetInt32();

        var createDoc = await admin.Client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new
            {
                projectId,
                templateVersionId = structure.TemplateVersionId,
                sheetDefIds = new[] { structure.SheetDefId },
            });
        Assert.True(createDoc.StatusCode == HttpStatusCode.Created, $"документ: {createDoc.StatusCode}: {app.ErrorsText}");
        var documentId = (await createDoc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentId").GetInt64();

        var tables = await admin.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, tables.StatusCode);
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці: {app.ErrorsText}");
        var tableInstanceId = tableArray[0].GetProperty("tableInstanceId").GetInt64();

        // ⛔ Рядок створює сам `PATCH` — `baseVersion = null` означає намір
        // СТВОРИТИ рядок, а не «мені байдуже до версії» (`R-B2`). Окремий
        // `POST /rows` перед цим був би не зайвим кроком, а конфліктом:
        // наступний `PATCH` з тим самим ключем відхиляється як дублікат
        // (`ECR-ROW-0409`).
        const string rowKey = "R1";

        var patch = await admin.Client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
                        baseVersion = (string?)null,
                        cells = new object[]
                        {
                            new { columnCode = "A", value = argument },
                            new { columnCode = "B", value = divisor },
                        },
                    },
                },
            });
        Assert.True(patch.StatusCode == HttpStatusCode.OK, $"запис у комірки: {patch.StatusCode}: {app.ErrorsText}");

        var units = await admin.Client.GetAsync(new Uri("/api/v1/units", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, units.StatusCode);
        var unitId = (await units.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .First(u => string.Equals(u.GetProperty("code").GetString(), "t", StringComparison.Ordinal))
            .GetProperty("id").GetInt32();

        return new Stand(
            app, admin, publisher, structure.ResultColumnId, documentId, tableInstanceId, periodKey, rowKey, unitId);
    }

    /// <summary>Створює проєкт на заданій версії шаблону.</summary>
    private static async Task<int> CreateProjectOnVersionAsync(
        EcrApiFactory app, HttpClient client, string prefix, int templateVersionId)
    {
        var policiesResponse = await client.GetAsync(new Uri("/api/v1/projects/period-policies", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, policiesResponse.StatusCode);
        var policies = await policiesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(policies.GetArrayLength() > 0, "seed не завів жодної doc.PeriodPolicy.");
        var policyId = policies[0].GetProperty("id").GetInt32();

        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/projects", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} project" },
                timeZoneId = "Asia/Almaty",
                periodKind = "Monthly",
                year = DateTime.UtcNow.Year,
                templateVersionId,
                periodPolicyId = policyId,
            });

        Assert.True(create.StatusCode == HttpStatusCode.Created, $"проєкт: {create.StatusCode}: {app.ErrorsText}");

        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetInt32();
    }

    /// <summary>Заводить методологію-контейнер (`W6`, крок 1).</summary>
    private static async Task<int> CreateMethodologyAsync(
        EcrApiFactory app, HttpClient client, string prefix)
    {
        var code = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/methodologies", UriKind.Relative),
            new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = $"{prefix} methodology" },
                kind = "DataDriven",
                group = (string?)null,
            });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"створення методології: {response.StatusCode}: {app.ErrorsText}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Створює порожню чернетку версії — рівень задає викликач.</summary>
    private static async Task<int> CreateDraftVersionAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, string versionNumber)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative),
            new { versionNumber, copyFromVersionId = (int?)null, level = "Configuration" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"версія {versionNumber}: {response.StatusCode}: {app.ErrorsText}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Клонує версію в нову чернетку — єдиний спосіб змінити опубліковану.</summary>
    private static async Task<int> CloneDraftVersionAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, string versionNumber, int copyFromVersionId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions", UriKind.Relative),
            new { versionNumber, copyFromVersionId, level = "Configuration" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"клон {versionNumber}: {response.StatusCode}: {app.ErrorsText}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>Заводить числову константу версії.</summary>
    private static async Task SaveConstantAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, decimal value, int unitId)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/constants/{code}", UriKind.Relative),
            new
            {
                kind = "Numeric",
                value,
                unitId,
                textValue = (string?)null,
                validFrom = (string?)null,
                validTo = (string?)null,
                category = (string?)null,
                substanceEntryId = (long?)null,
                source = "S-24 acceptance",
            });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"константа {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Заводить формулу версії разом з оголошеним списком аргументів.</summary>
    private static async Task SaveFormulaAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string expression, string argumentsCsv, int unitId)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/formulas/{code}", UriKind.Relative),
            new
            {
                expression,
                resultType = "Number",
                outputUnitId = unitId,

                // ⛔ Без списку звірка пастки 2 (`ECR-CALC-0432`) мовчить: вона
                // отримує `null` і не має чого порівнювати з текстом виразу.
                argumentsCsv,
            });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"формула {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Оголошує вихід версії — без нього модуль не записує нічого.</summary>
    private static async Task SaveOutputAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId, string code, int unitId)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/outputs/{code}", UriKind.Relative),
            new { unitId, ordinal = 1 });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"вихід {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Заводить правило відбору рядків документа.</summary>
    private static async Task SaveRuleAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string matchJson, int priority)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/rules/{code}", UriKind.Relative),
            new { matchJson, priority, isActive = true });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"правило {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Заводить тест золотого набору — без нього версія не публікується.</summary>
    private static async Task SaveTestCaseAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string code, string inputJson, string expectedJson)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/tests/{code}", UriKind.Relative),
            new { inputJson, expectedJson, tolerance = 0.0001m });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"тест {code}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Задає режими обчислення чернетки.</summary>
    private static async Task SaveModesAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId,
        string numericMode, string traceLevel)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/modes", UriKind.Relative),
            new { numericMode, calendarMode = "Actual", traceLevel });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"режими: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Прив'язує вихід методології до колонки документа.</summary>
    private static async Task SaveBindingAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int columnDefId, string outputCode)
    {
        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/bindings/{columnDefId}/{outputCode}", UriKind.Relative),
            new { matchJson = "{}", isActive = true });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"прив'язка {outputCode}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Публікує версію ІНШИМ користувачем — чотири очі (<c>D-40</c>).</summary>
    private static async Task PublishAsync(
        EcrApiFactory app, HttpClient publisher, int methodologyId, int versionId, string effectiveFrom)
    {
        var response = await publisher.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/versions/{versionId}/publish", UriKind.Relative),
            new { changeReason = "Приймання W6: методологія заведена з нуля", effectiveFrom });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"публікація версії {versionId}: {response.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Прогін без запису — єдиний маршрут, який віддає трейс.</summary>
    private static async Task<JsonElement> SimulateAsync(
        EcrApiFactory app, HttpClient client, int methodologyId, int versionId, int periodKey)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{methodologyId}/simulate", UriKind.Relative),
            new { methodologyVersionId = versionId, periodKey });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"прогін: {response.StatusCode}: {app.ErrorsText}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Ставить перерахунок у чергу і чекає кінцевого стану задачі.</summary>
    private static async Task RecalculateAsync(
        EcrApiFactory app, Provisioning.Administrator admin, long documentId, int periodKey)
    {
        var recalc = await admin.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });
        Assert.Equal(HttpStatusCode.Accepted, recalc.StatusCode);

        var jobId = (await recalc.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        var final = await ScenarioHelpers.AwaitJobAsync(admin.Client, jobId, TimeSpan.FromSeconds(60));

        Assert.True(
            final.ValueKind != JsonValueKind.Undefined,
            $"стан задачі {jobId} не прочитався: {app.ErrorsText}");

        var state = final.GetProperty("state").GetString();
        Assert.True(
            string.Equals(state, "Succeeded", StringComparison.Ordinal),
            $"перерахунок документа {documentId} завершився станом {state}: {final.GetRawText()}; {app.ErrorsText}");
    }

    /// <summary>Числа актуального прогону — те, що бачить користувач.</summary>
    private static async Task<JsonElement> ReadResultsAsync(
        EcrApiFactory app, HttpClient client, long documentId, int periodKey)
    {
        var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/calculation-results?periodKey={periodKey}", UriKind.Relative));

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"результати: {response.StatusCode}: {app.ErrorsText}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Вхід золотого набору у формі <c>CalculationInput</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Документ і період — РЕАЛЬНІ: <c>GenericCalculationModule</c> бере
    /// тривалість періоду з <c>doc.Period</c>, а не виводить її з ключа
    /// (<c>D-112</c>), і на вигаданому документі прогін відмовився б із
    /// <c>ECR-PRD-0404</c>. Тест методології тому невіддільний від стенда, на
    /// якому її заводять.
    /// </remarks>
    private static string Input(Stand stand, decimal argument, decimal divisor)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              {"documentId":{{stand.DocumentId}},"tableInstanceId":{{stand.TableInstanceId}},
               "periodKey":{"value":{{stand.PeriodKey}}},"sourceRowKey":"{{stand.RowKey}}",
               "arguments":[
                 {"argumentCode":"A","value":{{argument}},"valueString":null,"unitId":null},
                 {"argumentCode":"B","value":{{divisor}},"valueString":null,"unitId":null}]}
              """);

    /// <summary>Дата набуття чинності — на <paramref name="yearsBack"/> років раніше періоду.</summary>
    /// <remarks>
    /// ⛔ Раніше за період, а не «сьогодні»: версію добирає
    /// <c>MethodologyResolver</c> за датою ПЕРІОДУ (ФВ-9.3), і версія, чинна з
    /// завтра, не порахувала б нічого. Різні роки для різних версій — щоб
    /// <c>PublishVersion</c> не відхилив другу як таку, що займає ту саму дату.
    /// </remarks>
    private static string EffectiveFrom(int periodKey, int yearsBack)
        => new DateOnly((periodKey / 100) - yearsBack, 1, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Ідентифікатор задачі в елементі переліку черги, хоч як він названий.</summary>
    /// <remarks>
    /// ⚠ Перелік черги і стан однієї задачі — різні відповіді різних обробників,
    /// і поле ідентифікатора в них зветься по-різному. Сценарій не вимагає
    /// спільної назви: він шукає задачу, а не поле.
    /// </remarks>
    private static string? JobId(JsonElement item)
        => item.TryGetProperty("jobId", out var jobId) ? jobId.GetString()
            : item.TryGetProperty("id", out var id) ? id.GetString()
            : null;
}
