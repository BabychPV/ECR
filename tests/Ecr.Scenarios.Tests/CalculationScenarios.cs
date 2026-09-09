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
    /// ⛔ <b>Сценарій переписано (`W10.2`, директива №10), бо він доводив не те,
    /// про що він.</b> Дефектів було три, і найгірший — не той, який називали.
    /// <list type="number">
    /// <item>Документ будувався спільним <c>ArrangeDocumentAsync</c> — на
    /// ПОРОЖНІЙ версії шаблону, без жодної таблиці. Найменший із трьох:
    /// приготування, а не твердження.</item>
    /// <item>У <c>A</c> і <c>B</c> не писалося НІЧОГО — жодного <c>PATCH</c> у
    /// тілі тесту, хоч ім'я тесту саме про запис <c>A</c> і <c>B</c>.</item>
    /// <item>Головний: єдина асерція перевіряла, що в таблиці ІСНУЄ колонка з
    /// <c>dataType == "Formula"</c>. Про число в <c>C</c> вона не питала
    /// взагалі — тобто полагодити пункт 1 означало б отримати зелений
    /// сценарій, який не доводить нічого. Той самий «порожній зелений», який
    /// `W5.9` прибрав із `S-05`…`S-09`, а `W6` — із `S-23`/`S-24`/`S-26`.</item>
    /// </list>
    ///
    /// ⛔ Тепер доводиться ЧИСЛО: <c>4 + 2.5 = 6.5</c>, потім <c>10 + 2.5 =
    /// 12.5</c> після зміни входу. Обидва підібрані так, що збіг випадковим
    /// бути не може: результат не дорівнює жодному з доданків, ані їх добутку
    /// чи різниці. Асерція «в <c>C</c> щось є» пройшла б і на нулі, і на
    /// скопійованому <c>A</c>.
    ///
    /// ⛔ Число саме СИСТЕМНЕ, і це доводиться окремо, бо інакше воно
    /// невідрізниме від уведеного людиною: колонка має тип <c>Formula</c>,
    /// зріз віддає заборону <c>CalculatedCell</c> на цю комірку, а спроба
    /// записати в неї руками відхиляється (<c>ECR-CELL-4221</c>). Комірковий
    /// прапорець <c>IsCalculated</c> у контракті зрізу не публікується взагалі
    /// (<c>RowDto.Cells</c> — це <c>значення</c>, а не запис), тож саме ці три
    /// ознаки і є те, чим «обчислено системою» видно клієнтові.
    ///
    /// ⚠ <b>Вимір, який спростовує попередній діагноз</b> (`Q-160`, `D2-335`,
    /// `D2-336`): інтерактивний конвеєр рахує <c>C</c> САМ. <c>PatchCellsHandler</c>
    /// ставить у чергу <c>IFormulaRecalculationJob</c>, той кличе
    /// <c>RecalculationService</c>, і після `W6`/`W8` (`D2-334`: знімок нарешті
    /// вантажить <c>cfg.FormulaDef</c>) ланцюг замкнений. Тому асерція на
    /// <c>6.5</c> тут — не очікування майбутнього пакета, а чинна поведінка.
    ///
    /// ⛔ Чого цей сценарій НЕ доводить і доводити не може, доки не зіллється
    /// `W10.0`/`W10.1`: що <c>POST …/recalculate</c> сам уміє рахувати формули
    /// шаблону. Він і досі ставить у чергу лише перерахунок МЕТОДОЛОГІЙ
    /// (<c>RecalculationJob</c> не має <c>RecalculationService</c> серед
    /// залежностей узагалі), а «перерахувати все» для формул не існує як
    /// операції. Тут він викликається і перевіряється на тому, за що
    /// відповідає вже зараз: не втратити й не зіпсувати обчислених значень,
    /// які після `W10.1` стануть його власними.
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
        var admin = await Provisioning.AdministratorAsync(
            app,
            "S21",
            [
                "Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Template.Publish",
                "Calculation.View", "Calculation.EditFormula", "Calculation.Recalculate", "System.ViewHealth",
            ]);

        // 1. Реальна структура: дві числові колонки і третя — типу `Formula`,
        //    з виразом, збереженим маршрутом `W5.3`, ДО публікації версії.
        //    Без публікації немає `cfg.FormulaDependency`, а без них — плану
        //    перерахунку: формула лежала б у базі й не рахувалася ніколи.
        var doc = await DataEntryScenarios.ArrangeRealDocumentAsync(
            app, admin, "S21",
            extraNumericColumns: ["B"],
            formulaColumn: ("C", "[A] + [B]"));
        admin = doc.Admin;

        var tableInstanceId = await TableInstanceAsync(app, admin.Client, doc.DocumentId, doc.PeriodKey);
        var rowKey = doc.RowKeys[0];

        // 2. Колонка `C` — саме обчислювана, а не просто третя числова.
        var columns = (await ReadSliceAsync(admin.Client, doc.DocumentId, tableInstanceId)).GetProperty("columns");
        var formulaColumn = columns.EnumerateArray().FirstOrDefault(
            c => string.Equals(c.GetProperty("code").GetString(), "C", StringComparison.Ordinal));
        Assert.True(formulaColumn.ValueKind == JsonValueKind.Object, $"у зрізі немає колонки C: {columns.GetRawText()}");
        Assert.Equal("Formula", formulaColumn.GetProperty("dataType").GetString());

        // 3. Запис `A` і `B` — те, чого в цьому сценарії не було зовсім.
        await WriteInputsAsync(app, admin.Client, doc.DocumentId, tableInstanceId, doc.PeriodKey, rowKey, 4m, 2.5m);

        // 4. ⛔ ЧИСЛО. Перерахунок після правки комірки асинхронний
        //    (`FormulaRecalculationJob`), тому зріз опитується, а не читається
        //    один раз: миттєве читання перевіряло б чергу, а не результат.
        var sum = await AwaitCellAsync(
            admin.Client, doc.DocumentId, tableInstanceId, rowKey, "C", TimeSpan.FromSeconds(30));
        Assert.True(
            sum is not null,
            $"комірка C рядка {rowKey} лишилася порожньою за 30 с після запису A=4, B=2.5: "
            + $"{(await ReadSliceAsync(admin.Client, doc.DocumentId, tableInstanceId)).GetRawText()}; {app.ErrorsText}");
        Assert.Equal(6.5m, sum!.Value);

        // 5. Число НЕ людське: зріз віддає заборону на цю комірку саме як
        //    `CalculatedCell`, а спроба записати в неї відхиляється. Без цих
        //    двох перевірок 6.5 могло б бути чим завгодно, що туди поклали.
        var permissions = (await ReadSliceAsync(admin.Client, doc.DocumentId, tableInstanceId))
            .GetProperty("cellPermissions");
        Assert.True(
            permissions.TryGetProperty($"{rowKey}:C", out var reason)
            && string.Equals(reason.GetString(), "CalculatedCell", StringComparison.Ordinal),
            $"зріз не позначив C як обчислену системою: {permissions.GetRawText()}");

        var manualWrite = await TryWriteCalculatedAsync(
            admin.Client, doc.DocumentId, tableInstanceId, doc.PeriodKey, rowKey);
        Assert.False(
            manualWrite.IsSuccess,
            $"запис руками в обчислювану колонку C прийнято ({manualWrite.Status}) — тоді число в ній нічого не доводить.");

        // 6. Перерахунок, а не разовий запис: змінюємо `A` і вимагаємо НОВОЇ
        //    суми. Формула, порахована один раз і застигла, пройшла б крок 4.
        await WriteInputsAsync(app, admin.Client, doc.DocumentId, tableInstanceId, doc.PeriodKey, rowKey, 10m, 2.5m);
        var recomputed = await AwaitCellAsync(
            admin.Client, doc.DocumentId, tableInstanceId, rowKey, "C", TimeSpan.FromSeconds(30), expected: 12.5m);
        Assert.Equal(12.5m, recomputed);

        // 7. Явний перерахунок документа — той самий виклик, який `W10.1` має
        //    навчити рахувати ще й формули шаблону. Сьогодні він відповідає за
        //    методології; перевіряється те, за що він відповідає ВЖЕ: дійти до
        //    кінцевого стану і не зіпсувати обчислених чисел.
        await RecalculateAsync(app, admin, doc.DocumentId, doc.PeriodKey);

        var afterRecalculate = await ReadCellAsync(admin.Client, doc.DocumentId, tableInstanceId, rowKey, "C");
        Assert.Equal(12.5m, afterRecalculate);
    }

    /// <summary>Єдиний екземпляр таблиці документа за період.</summary>
    private static async Task<long> TableInstanceAsync(
        EcrApiFactory app, HttpClient client, long documentId, int periodKey)
    {
        var tables = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={periodKey}", UriKind.Relative));
        Assert.True(tables.StatusCode == HttpStatusCode.OK, $"таблиці: {tables.StatusCode}: {app.ErrorsText}");
        var tableArray = await tables.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tableArray.GetArrayLength() > 0, $"документ {documentId} не має жодної таблиці: {app.ErrorsText}");

        return tableArray[0].GetProperty("tableInstanceId").GetInt64();
    }

    /// <summary>Пише <c>A</c> і <c>B</c> в один рядок одним <c>PATCH</c>.</summary>
    /// <remarks>
    /// ⚠ <c>baseVersion</c> береться зі ЗРІЗУ, а не подається як <c>null</c>:
    /// рядок фіксованої таблиці вже існує (`S-13`), і <c>null</c> означав би
    /// намір СТВОРИТИ його — тобто дублікат (<c>ECR-ROW-0409</c>, `409`).
    /// </remarks>
    private static async Task WriteInputsAsync(
        EcrApiFactory app, HttpClient client, long documentId, long tableInstanceId, int periodKey,
        string rowKey, decimal a, decimal b)
    {
        var slice = await ReadSliceAsync(client, documentId, tableInstanceId);
        var row = slice.GetProperty("rows").EnumerateArray()
            .FirstOrDefault(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));
        Assert.True(row.ValueKind == JsonValueKind.Object, $"рядка {rowKey} у зрізі {tableInstanceId} немає.");
        var baseVersion = row.GetProperty("rowVersion").GetString();

        var patch = await client.PatchAsJsonAsync(
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
                        baseVersion,
                        cells = new object[]
                        {
                            new { columnCode = "A", value = a },
                            new { columnCode = "B", value = b },
                        },
                    },
                },
            });

        Assert.True(patch.StatusCode == HttpStatusCode.OK, $"запис A/B у {rowKey}: {patch.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>Зріз таблиці — те саме читання, яким малює grid.</summary>
    private static async Task<JsonElement> ReadSliceAsync(
        HttpClient client, long documentId, long tableInstanceId)
    {
        var slice = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{tableInstanceId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, slice.StatusCode);

        return await slice.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Значення однієї комірки зі зрізу; <c>null</c> — комірки немає.</summary>
    private static async Task<decimal?> ReadCellAsync(
        HttpClient client, long documentId, long tableInstanceId, string rowKey, string columnCode)
    {
        var slice = await ReadSliceAsync(client, documentId, tableInstanceId);
        var row = slice.GetProperty("rows").EnumerateArray()
            .FirstOrDefault(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal));

        if (row.ValueKind != JsonValueKind.Object
            || !row.GetProperty("cells").TryGetProperty(columnCode, out var cell)
            || cell.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return cell.GetDecimal();
    }

    /// <summary>
    /// Чекає, поки комірка з'явиться у зрізі (і, якщо задано, набуде
    /// <paramref name="expected"/>); повертає останнє прочитане.
    /// </summary>
    /// <remarks>
    /// ⚠ <paramref name="expected"/> потрібне саме для ПЕРЕрахунку: після
    /// зміни входу в комірці вже лежить старе число, тож «дочекатися, поки
    /// значення з'явиться» повернуло б його ж і сценарій пройшов би на
    /// застарілому результаті.
    /// </remarks>
    private static async Task<decimal?> AwaitCellAsync(
        HttpClient client, long documentId, long tableInstanceId, string rowKey, string columnCode,
        TimeSpan timeout, decimal? expected = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        decimal? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadCellAsync(client, documentId, tableInstanceId, rowKey, columnCode);
            if (last is not null && (expected is null || last == expected))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return last;
    }

    /// <summary>
    /// Пробує записати число руками в обчислювану колонку <c>C</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Код відповіді не фіксується жорстко: сценарій доводить, що запис НЕ
    /// приймається (<c>ColumnDef.ValidateValue</c> → <c>ECR-CELL-4221</c>), а
    /// не те, якою саме цифрою про це сказано.
    /// </remarks>
    private static async Task<(bool IsSuccess, HttpStatusCode Status)> TryWriteCalculatedAsync(
        HttpClient client, long documentId, long tableInstanceId, int periodKey, string rowKey)
    {
        var slice = await ReadSliceAsync(client, documentId, tableInstanceId);
        var baseVersion = slice.GetProperty("rows").EnumerateArray()
            .First(r => string.Equals(r.GetProperty("rowKey").GetString(), rowKey, StringComparison.Ordinal))
            .GetProperty("rowVersion").GetString();

        var patch = await client.PatchAsJsonAsync(
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
                        baseVersion,
                        cells = new object[] { new { columnCode = "C", value = 999m } },
                    },
                },
            });

        return (patch.IsSuccessStatusCode, patch.StatusCode);
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

    /// <remarks>
    /// ⛔ Q-174 (аудит фази 2, авторизація). `RecalculateDocumentHandler`
    /// перевіряв лише загальне право `Calculation.Recalculate`, без гранта на
    /// проєкт документа. Наслідок: користувач із цим правом (виданим під
    /// власний проєкт) міг поставити в чергу перезапис обчислених значень
    /// чужого документа.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-25")]
    public async Task Чужий_документ_не_перераховується()
    {
        using var app = new EcrApiFactory(sql);
        var owner = await Provisioning.AdministratorAsync(
            app, "S25bOwner",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Calculation.Recalculate"]);
        var stranger = await Provisioning.AdministratorAsync(
            app, "S25bStranger", ["Calculation.Recalculate"]);

        (owner, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, owner, "S25bOwner");

        // ⛔ Доказ сценарію: `stranger` має право запускати перерахунок
        // узагалі, але жодного гранта на проєкт `owner` — постановка в чергу
        // чужого документа має дати 4xx, а не 202.
        var recalc = await stranger.Client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/recalculate", UriKind.Relative), new { periodKey });

        Assert.True(
            (int)recalc.StatusCode is >= 400 and < 500,
            $"перерахунок чужого документа мав дати 4xx, а дав {recalc.StatusCode}: {await recalc.Content.ReadAsStringAsync()}");
    }

    /// <remarks>
    /// ⛔ Q-175 (аудит фази 2, авторизація). `GetCalculationResultsHandler`
    /// перевіряв лише загальне право `Calculation.View`, без гранта на проєкт
    /// документа. Наслідок: будь-хто з цим правом бачив показники методологій
    /// (речовини, обсяги викидів) чужого документа.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Scenario", "S-25")]
    public async Task Чужі_результати_розрахунку_не_читаються()
    {
        using var app = new EcrApiFactory(sql);
        var owner = await Provisioning.AdministratorAsync(
            app, "S25cOwner",
            ["Project.Manage", "Document.View", "Document.Create", "Template.Edit", "Calculation.View"]);
        var stranger = await Provisioning.AdministratorAsync(
            app, "S25cStranger", ["Calculation.View"]);

        (owner, _, var documentId, var periodKey) = await DataEntryScenarios.ArrangeDocumentAsync(app, owner, "S25cOwner");

        var results = await stranger.Client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/calculation-results?periodKey={periodKey}", UriKind.Relative));

        Assert.True(
            (int)results.StatusCode is >= 400 and < 500,
            $"результати чужого документа мали дати 4xx, а дав {results.StatusCode}: {await results.Content.ReadAsStringAsync()}");
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
            // ⚠ Грант Manage тепер видає сам ActivateProjectAsync (Q-179):
            // Activate вимагає його ще ДО активації, тож окремий виклик
            // GrantAsync після неї дублював би той самий грант ролі й упав би
            // («роль уже має грант(и)» — PUT замінює набір цілком).
            admin = await ProjectAndPeriodScenarios.ActivateProjectAsync(setup, admin, projectId);
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
