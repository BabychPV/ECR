using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Q-326: механізм структурованого повідомлення прогресу задачі —
/// кодування/декодування конверта (<see cref="JobProgressMessageCodec"/>) і
/// резолв мовою читача (<see cref="JobProgressMessageResolver"/>), окремо від
/// <c>GetJobStatusHandlerTests</c> (права доступу).
/// </summary>
public sealed class JobProgressMessageTests
{
    /// <summary>
    /// Кодування з ТИПОВИМ кодувальником — те, чим конверти писалися до
    /// переходу кодека на <c>UnsafeRelaxedJsonEscaping</c>.
    /// </summary>
    private static readonly JsonSerializerOptions StandardEscaping = new() { PropertyNamingPolicy = null };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Кодування_і_декодування_повертає_той_самий_ключ_і_параметри()
    {
        var envelope = new JobProgressMessageEnvelope(
            "jobs.recalcFormulasDone",
            new Dictionary<string, string> { ["cells"] = "42" });

        var encoded = JobProgressMessageCodec.Encode(envelope);
        var decoded = JobProgressMessageCodec.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal("jobs.recalcFormulasDone", result.Key);
        Assert.Equal("42", result.Params!["cells"]);
        Assert.Null(result.Inner);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Вкладене_повідомлення_переживає_кодування_і_декодування()
    {
        var inner = new JobProgressMessageEnvelope(
            "jobs.batchProgress", new Dictionary<string, string> { ["ordinal"] = "2", ["total"] = "4" });
        var outer = new JobProgressMessageEnvelope("jobs.phaseMethodologies", Inner: inner);

        var decoded = JobProgressMessageCodec.TryDecode(JobProgressMessageCodec.Encode(outer), out var result);

        Assert.True(decoded);
        Assert.Equal("jobs.phaseMethodologies", result.Key);
        Assert.NotNull(result.Inner);
        Assert.Equal("jobs.batchProgress", result.Inner!.Key);
        Assert.Equal("4", result.Inner.Params!["total"]);
    }

    /// <summary>
    /// ⛔ Мутаційний доказ фолбеку: без цієї перевірки (`raw[0] != '{'`)
    /// `TryDecode` кидав би <c>JsonException</c> на кожному старому прямому
    /// записі (готовий український текст ДО `Q-326`) і на `exportId`
    /// (`ExcelExportJob`, 100 %) — обидва не JSON.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Перерахунок методологій.")]
    [InlineData("4f2c9a10-91d1-4b6a-9c9e-4b8e4b9a2b10")]
    [InlineData("{не валідний json")]
    public void Нерозпізнаний_рядок_не_декодується_як_конверт(string? raw)
    {
        Assert.False(JobProgressMessageCodec.TryDecode(raw, out _));
    }

    /// <summary>
    /// ⛔ Дефект, знайдений наскрізною перевіркою (<c>tools/smoke.ps1</c>,
    /// крок 23): причина провалу задачі йшла в конверт як є, конверт не влізав
    /// у <c>nvarchar(400)</c>, SQL Server відповідав <c>Msg 2628</c> — і разом
    /// із записом губилася сама причина.
    /// </summary>
    /// <remarks>
    /// ⚠ Пастка тут не в довжині тексту, а в КОДУВАННІ: конверт несе власні
    /// ~90 символів (ключ каталогу, імена й значення числових параметрів,
    /// лапки й дужки), тож причина, СИРА довжина якої ще менша за 400, дає
    /// конверт понад 400. Обрізання за сирою довжиною не зробило б нічого.
    /// <para>
    /// ⚠ До переходу кодека на <c>UnsafeRelaxedJsonEscaping</c> розрив був
    /// різкішим (кирилиця по шість символів на літеру: 282 сирих символи —
    /// конверт за півтори тисячі), і тому тут стояло 6 повторів. Правило —
    /// «міряй ПІСЛЯ кодування» — не змінилося, змінився лише поріг, за яким
    /// воно видно.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Довга_кирилична_причина_провалу_вміщається_в_межу_стовпця()
    {
        var reason = string.Concat(
            Enumerable.Repeat("Джерело даних недоступне, сервер не відповідає. ", 7));

        Assert.True(reason.Length >= 200, $"Потрібно ≥ 200 літер, а є {reason.Length}.");

        var envelope = new JobProgressMessageEnvelope(
            "jobs.retryScheduled",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attempt"] = "1",
                ["max"] = "3",
                ["delaySeconds"] = "30",
                ["error"] = reason,
            });

        // Сама пастка, зафіксована твердженням: сирий текст КОРОТШИЙ за межу,
        // а закодований конверт — набагато довший.
        Assert.True(reason.Length < 400);
        Assert.True(
            JobProgressMessageCodec.Encode(envelope).Length > 400,
            "Без обрізання конверт мав би переростати межу — інакше тест нічого не доводить.");

        var encoded = JobProgressMessageCodec.EncodeWithinLimit(envelope, "error");

        // ⛔ Літерал 400, а не посилання на `HasMaxLength`: межа тут — це
        // вимога стовпця `itg.JobProgress.Message`, і вона мусить упасти, якщо
        // константу кодека «підвищать», не чіпаючи схему.
        Assert.True(
            encoded.Length <= 400,
            $"Закодований конверт — {encoded.Length} символів, стовпець тримає 400.");

        Assert.True(JobProgressMessageCodec.TryDecode(encoded, out var decoded));
        Assert.Equal("jobs.retryScheduled", decoded.Key);

        // Причина лишається ВПІЗНАВАНОЮ: початок тексту плюс позначка.
        Assert.StartsWith("Джерело даних недоступне", decoded.Params!["error"], StringComparison.Ordinal);
        Assert.EndsWith("…", decoded.Params["error"], StringComparison.Ordinal);

        // Різався рівно вільний параметр — числа спроби цілі.
        Assert.Equal("1", decoded.Params["attempt"]);
        Assert.Equal("30", decoded.Params["delaySeconds"]);
    }

    /// <summary>
    /// ⚠ Зворотний бік: повідомлення, яке ВМІЩАЄТЬСЯ, не сміє втратити
    /// жодного символу — інакше «виправлення» зіпсувало б кожну нормальну
    /// причину провалу заради рідкісної довгої.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Коротка_причина_провалу_доходить_без_обрізання()
    {
        const string Reason = "Джерело недоступне.";

        var encoded = JobProgressMessageCodec.EncodeWithinLimit(
            new JobProgressMessageEnvelope(
                "jobs.retryScheduled",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = Reason }),
            "error");

        Assert.True(JobProgressMessageCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(Reason, decoded.Params!["error"]);
    }

    /// <summary>
    /// Заради чого кодек перевели на <c>UnsafeRelaxedJsonEscaping</c>: у ту
    /// саму межу стовпця тепер влазить причина, яку можна прочитати.
    /// </summary>
    /// <remarks>
    /// ⛔ З типовим кодувальником кирилиця йшла послідовностями <c>\u04XX</c>
    /// — шість символів на літеру, — і в бюджет конверта вміщалося близько
    /// півсотні літер: причина обрізалася на пів речення й не пояснювала
    /// нічого. Це не «гарніше в базі», це різниця між «сервер відповів
    /// відмо…» і повним реченням.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void У_причину_провалу_влазить_щонайменше_250_кириличних_символів()
    {
        var reason = string.Concat(
            Enumerable.Repeat("Джерело даних недоступне, сервер не відповідає. ", 20));

        var envelope = new JobProgressMessageEnvelope(
            "jobs.retryScheduled",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attempt"] = "1",
                ["max"] = "3",
                ["delaySeconds"] = "30",
                ["error"] = reason,
            });

        var encoded = JobProgressMessageCodec.EncodeWithinLimit(envelope, "error");

        Assert.True(encoded.Length <= 400, $"Конверт — {encoded.Length} символів, стовпець тримає 400.");
        Assert.True(JobProgressMessageCodec.TryDecode(encoded, out var decoded));

        var kept = decoded.Params!["error"];

        // ⛔ Літерал 250, а не «стільки, скільки вийде»: це вимога — причину
        // має бути видно, — і вона мусить упасти, якщо екранування повернуть.
        // З типовим кодувальником тут лишалося ~50 символів.
        Assert.True(kept.Length >= 250, $"У причину влізло {kept.Length} символів, потрібно ≥ 250.");
        Assert.StartsWith("Джерело даних недоступне", kept, StringComparison.Ordinal);

        // Кирилиця лежить у стовпці ЛІТЕРАМИ — саме це й дало місце.
        Assert.Contains("Джерело даних недоступне", encoded, StringComparison.Ordinal);

        // ⚠ Не тавтологія: та сама причина з ТИПОВИМ екрануванням у межу не
        // вміщається — тобто число вище тримається саме на кодувальнику.
        var withStandardEscaping = JsonSerializer.Serialize(
            new { k = decoded.Key, p = decoded.Params }, StandardEscaping);

        Assert.True(
            withStandardEscaping.Length > 400,
            $"З типовим екрануванням той самий конверт мав би бути завеликим, а він — "
            + $"{withStandardEscaping.Length} символів.");
    }

    /// <summary>
    /// Записи, зроблені ДО зміни кодувальника, читаються далі.
    /// </summary>
    /// <remarks>
    /// ⛔ Стовпець не мігрують і не чистять: у <c>itg.JobProgress.Message</c>
    /// лежать конверти обох форм одночасно. Кодувальник впливає лише на ЗАПИС,
    /// а <c>\u04XX</c> лишається валідним JSON — але поки це не перевірено,
    /// це лише припущення.
    ///
    /// ⚠ Стара форма будується серіалізацією з типовим кодувальником, а не
    /// літералом у джерелі: так вона гарантовано та сама, що лежить у базі, і
    /// не залежить від того, як редактор чи інструмент запису обійдеться з
    /// екрануванням у тексті тесту.
    ///
    /// ⚠ Про ЗАПИС тут навмисно немає жодного твердження: читання старих
    /// записів мусить пережити будь-яку подальшу зміну кодувальника, тож
    /// повернення екранування цей тест не сміє чіпати. Те, що нова форма кладе
    /// кирилицю літерами, стереже сусідній тест про 250 символів.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Запис_у_старому_екрануванні_читається_далі()
    {
        const string Reason = "Джерело даних недоступне.";

        var legacy = JsonSerializer.Serialize(
            new
            {
                k = "jobs.retryScheduled",
                p = new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = Reason },
            },
            StandardEscaping);

        // Ознака старої форми, задана властивістю, а не пошуком підрядка:
        // кирилиці як літер там немає взагалі, увесь рядок — ASCII.
        Assert.True(legacy.All(char.IsAscii), $"Старий запис мав бути суто ASCII, а він: {legacy}.");
        Assert.DoesNotContain(Reason, legacy, StringComparison.Ordinal);

        Assert.True(
            JobProgressMessageCodec.TryDecode(legacy, out var decoded),
            $"Старий запис перестав декодуватися: {legacy}.");
        Assert.Equal("jobs.retryScheduled", decoded.Key);
        Assert.Equal(Reason, decoded.Params!["error"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_підставляє_параметри_в_шаблон_каталогу_мовою_читача()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "jobs.orphanScanDone", "Rows changed: {changed}");

        var raw = JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.orphanScanDone", new Dictionary<string, string> { ["changed"] = "7" }));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal("Rows changed: 7", resolved);
    }

    /// <summary>
    /// Q-326: композиція (`RecalculationJob`'s префікс документа,
    /// `CalculationOrchestrator.PhaseProgress`'s префікс фази) резолвиться
    /// РЕКУРСИВНО — вкладене повідомлення підставляється як <c>{message}</c>
    /// уже резольваним текстом, не сирим ключем.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_розгортає_вкладене_повідомлення_рекурсивно()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "jobs.documentPrefix", "Document {id} ({index} of {count}): {message}")
            .Add("en", "jobs.recalcFormulas", "Recalculating template formulas.");

        var raw = JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.documentPrefix",
                new Dictionary<string, string> { ["id"] = "5", ["index"] = "1", ["count"] = "3" },
                Inner: new JobProgressMessageEnvelope("jobs.recalcFormulas")));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal("Document 5 (1 of 3): Recalculating template formulas.", resolved);
    }

    /// <summary>
    /// Не JSON-конверт (стара форма запису чи дані на кшталт <c>exportId</c>)
    /// — резолвер повертає його БЕЗ ЗМІН, не звертаючись до каталогу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_нерозпізнаного_рядка_повертає_його_без_змін()
    {
        var catalog = Substitute.For<IUiStringCatalog>();

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", "export-abc123", CancellationToken.None);

        Assert.Equal("export-abc123", resolved);
        await catalog.DidNotReceiveWithAnyArgs()
            .GetScopedAsync(default!, default, CancellationToken.None);
    }

    /// <summary>
    /// ⛔ Q-304-подібний принцип: збій самого каталогу не має валити читання
    /// стану задачі. Мутаційний доказ — прибери <c>try/catch</c> у
    /// <see cref="JobProgressMessageResolver.ResolveAsync"/>, і цей тест
    /// впаде на необробленому винятку замість повернутого сирого конверта.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Збій_каталогу_повертає_сирий_конверт_а_не_валить_читання()
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetScopedAsync(Arg.Any<string>(), Arg.Any<UiStringScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UiStringCatalog>(new InvalidOperationException("каталог недоступний")));

        var raw = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope("jobs.orphanScanChecking"));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal(raw, resolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_без_повідомлення_повертає_null()
    {
        var catalog = Substitute.For<IUiStringCatalog>();

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", null, CancellationToken.None);

        Assert.Null(resolved);
    }
}
