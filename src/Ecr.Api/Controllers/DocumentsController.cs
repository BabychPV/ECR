using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Domain.ValueObjects;
using Ecr.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Документи: перелік, створення, читання, валідація, робочий процес.</summary>
/// <remarks>
/// Комірки і рядки — в окремому <see cref="CellsController"/>: у них інший
/// профіль навантаження і власний бюджет часу.
/// </remarks>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public sealed class DocumentsController(
    ListDocumentsHandler listDocuments,
    GetDocumentListSummaryHandler listSummary,
    GetDocumentHandler getDocument,
    CreateDocumentHandler create,
    ValidateDocumentHandler validate,
    GetValidationResultHandler validationResult,
    SubmitSheetHandler submit,
    ApproveSheetHandler approve,
    ReopenDocumentHandler reopen,
    RecalculateDocumentHandler recalculate,
    GetDocumentTablesHandler tables,
    GetTableStatusHandler tableStatus,
    GetCalculationResultsHandler calculationResults,
    ExportDocumentHandler export,
    DownloadExportHandler downloadExport,
    PreviewImportHandler previewImport,
    ApplyImportHandler applyImport,
    DeleteDocumentHandler delete,
    ChangeDocumentKeyHandler changeKey,
    GetDocumentHeaderHandler getHeader,
    PatchDocumentHeaderHandler patchHeader) : ControllerBase
{
    /// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Зведений стан документа <b>рахується запитом</b> із <c>wf.ApprovalState</c>,
    /// а не зберігається полем (<c>D-93</c>): скалярний статус був би другим
    /// джерелом істини і рано чи пізно показав би <c>Approved</c> на документі
    /// з половиною аркушів у <c>Draft</c>.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<Ecr.Application.Common.PagedResult<Ecr.Application.Ports.DocumentSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int limit,
        [FromQuery] string? cursor,
        [FromQuery] int? projectId,
        [FromQuery] int? periodKey,
        [FromQuery] string? state,
        [FromQuery] bool mine,
        [FromQuery] bool? hasLateEdits,
        CancellationToken ct)
    {
        var page = new CursorRequest(limit == 0 ? 50 : limit, cursor);

        // ⛔ Перевірки `page.IsValid` тут БІЛЬШЕ НЕМАЄ, і це не послаблення.
        // Той самий `page` перевіряє `ListDocumentsHandler` — і перевіряє
        // ПРАВИЛЬНО: `BusinessRuleException(ErrorCodes.RequestInvalid, …)`,
        // тобто `problem+json` із кодом `ECR-REQ-0422`, який клієнт уміє
        // розрізнити, і з реченням із каталогу
        // (`err.ECR-REQ-0422.pageSizeOutOfRange`).
        //
        // ⚠ Тут же стояв `BadRequest(new { error = "limit поза межами 1..N" })`
        // — звичайний JSON повз `ExceptionHandlingMiddleware`, українське
        // речення БЕЗ коду помилки, і він ПЕРЕХОПЛЮВАВ правильну відмову,
        // до якої справа просто не доходила (`UI-WALKTHROUGH.md`, F1/F4).
        // Дві перевірки того самого — це не подвійна надійність, а гарантія,
        // що працює гірша з двох.

        // ⛔ Зведений стан рахується запитом по wf.ApprovalState і лише коли
        // вказано період: без періоду «стан документа» не визначений — аркуші
        // за різні періоди бувають у різних станах одночасно (D-93).
        return Ok(await listDocuments
            .HandleAsync(projectId, periodKey, state, mine, hasLateEdits, page, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Зведення переліку за період (<c>BE-09</c>). Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// ⚠ Лічить лише документи, які користувач БАЧИТЬ, — тією самою межею
    /// грантів, що й перелік вище; інакше смуга й таблиця під нею розійдуться.
    /// Період обов'язковий: без нього стан документа не визначений (<c>D-93</c>).
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<Ecr.Application.Documents.Dto.DocumentListSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(
        [FromQuery] int periodKey, [FromQuery] int? projectId, CancellationToken ct)
        => Ok(await listSummary.HandleAsync(projectId, periodKey, ct).ConfigureAwait(false));

    /// <summary>Створює документ. Право <c>Document.Create</c>.</summary>
    [HttpPost]
    [ProducesResponseType<Contracts.DocumentIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var documentId = await create
            .HandleAsync(request.ProjectId, request.TemplateVersionId, request.SheetDefIds, request.Name, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/documents/{documentId}", new Contracts.DocumentIdResponse(documentId));
    }

    /// <summary>Документ і його аркуші. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<Ecr.Application.Ports.DocumentSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, [FromQuery] int? periodKey, CancellationToken ct)
    {
        var document = await getDocument.HandleAsync(id, periodKey, ct).ConfigureAwait(false);

        // ⛔ Кидок, а не `NotFound(new { errorCode })` — і це знайдено проходом
        // інтерфейсу як користувач (`docs/build/UI-WALKTHROUGH.md`, F4).
        // Анонімний об'єкт — це звичайний JSON, а не `application/problem+json`:
        // `ExceptionHandlingMiddleware` такої відповіді не бачить жодним боком,
        // тож у тілі немає ні `title`, ні `detail`. Клієнт підставляв свій
        // запасний варіант (`api/client.ts`, `problemOf`: `HTTP ${status}`), а
        // `EcrApiError` (`detail ?? title`) робив із нього ще й текст — звідси
        // «HTTP 404» ДВІЧІ на екрані при живому `errorCode`.
        //
        // ⚠ Речення в каталозі було весь час: `err.ECR-DOC-0404.document` =
        // «Document {documentId} was not found.» Механізм справний — до цього
        // шляху його просто не довели.
        return document is null
            ? throw new Ecr.Application.Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Документ {id} не знайдено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
            : Ok(document);
    }

    /// <summary>Видаляє документ-чернетку. Право <c>Document.Delete</c>.</summary>
    /// <remarks>
    /// Лише чернетку: хоч один аркуш поданий, погоджений, відхилений або вже
    /// проходив погодження — <c>409</c> <c>ECR-DOC-0409</c> із причиною.
    /// Чужий документ — <c>404</c>, як і неіснуючий.
    /// </remarks>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Змінює бізнес-ключ документа (ФВ-3.9). Право <c>Document.ChangeKey</c>.</summary>
    /// <remarks>
    /// Причина обов'язкова (<c>422</c>); ключ зайнятий, застарілий <c>expectedBusinessKey</c>
    /// або аркуш поданий/погоджений — <c>409</c> <c>ECR-DOC-0409</c>.
    /// </remarks>
    [HttpPost("{id:long}/business-key")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangeKey(long id, [FromBody] ChangeDocumentKeyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await changeKey
            .HandleAsync(id, request.BusinessKey, request.ExpectedBusinessKey, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Поточні значення шапки документа. Право <c>Document.View</c>.</summary>
    [HttpGet("{id:long}/header")]
    [ProducesResponseType<DocumentHeaderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DocumentHeaderDto>> GetHeader(long id, CancellationToken ct)
        => Ok(await getHeader.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Оновлює значення полів шапки документа. Право — грант <c>Write</c> на
    /// проєкт документа (через <c>IAccessDecisionService</c>, як і <c>PATCH
    /// …/cells</c> — без окремого функціонального права).
    /// </summary>
    [HttpPatch("{id:long}/header")]
    [ProducesResponseType<DocumentHeaderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<DocumentHeaderDto>> PatchHeader(
        long id, [FromBody] PatchDocumentHeaderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await patchHeader.HandleAsync(id, request, ct).ConfigureAwait(false));
    }

    /// <summary>Валідація документа. Право <c>Document.View</c>.</summary>
    /// <remarks>
    /// Повертає повідомлення трьох рівнів. Запис блокує <b>лише</b> комірковий
    /// <c>Error</c> (R-B3); рядковий і табличний повертаються у відповіді й
    /// запису не заважають.
    /// </remarks>
    [HttpPost("{id:long}/validate")]

    // ⛔ Тип відповіді оголошений явно, а тіло — іменований запис, а не
    // анонімний об'єкт: в анонімного немає імені в схемі OpenAPI, тому
    // згенерувати клієнтський тип ні з чого і клієнт описує його рукописно
    // (`A7-16`, `A7-32`).
    [ProducesResponseType<ValidationResultResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate(
        long id, [FromBody] DocumentPeriodRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Період БЕРЕТЬСЯ З ТІЛА і перевіряється. До `A7-28` він читався з
        // рядка запиту, а клієнт надсилав його в тілі: параметр не зв'язувався,
        // ставав нулем, і валідація йшла по неіснуючому періоду — відповідаючи
        // «помилок немає». Зелений результат, який нічого не означає, гірший за
        // помилку: на нього спираються, подаючи звітність.
        var periodKey = request.PeriodKey;
        var messages = await validate.HandleAsync(id, PeriodKey.Parse(periodKey), ct).ConfigureAwait(false);

        // Повертаються ВСІ рівні. Рішення «чи можна подавати» ухвалює клієнт
        // за наявністю Error, а не сервер за кодом відповіді: 200 тут означає
        // «перевірку виконано», а не «зауважень немає».
        return Ok(new ValidationResultResponse(
            id,
            periodKey,
            [.. messages.Select(m => new ValidationFindingDto(
                m.Severity.ToString(), m.RuleCode, m.Message,
                m.TableDefId, m.RowKey, m.ColumnCode, m.BlocksSave))]));
    }

    /// <summary>
    /// Останній результат перевірки. Право <c>Document.View</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Читання, а не повторний прогін (директива №09 `W8` п.3, `S-19`).
    /// Підсумок зберігався давно (`ФВ-5.19`), і прочитати його не міг ніхто:
    /// <c>IValidationResultStore.GetLatestAsync</c> не мав жодного виклику.
    /// Ціна видна на екрані — перелік зауважень жив рівно до перезавантаження
    /// сторінки, і щоб побачити його знову, оператор мусив ЗАПУСТИТИ
    /// перевірку заново.
    ///
    /// ⚠ <c>404</c>, а не порожній перелік, коли перевірку ще не запускали:
    /// «зауважень немає» і «ще не перевіряли» — різні відповіді, і показувати
    /// першу замість другої означає повідомити неправду про готовність.
    /// </remarks>
    /// <param name="id">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:long}/validation")]
    [ProducesResponseType<ValidationResultResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LastValidation(
        long id, [FromQuery] int periodKey, CancellationToken ct)
    {
        var messages = await validationResult
            .HandleAsync(id, PeriodKey.Parse(periodKey), ct)
            .ConfigureAwait(false);

        // ⛔ Той самий дефект, що в `Get` вище (F4), і те саме лікування. Код
        // відповіді лишається `404` — саме на нього спирається клієнт, щоб
        // відрізнити «ще не перевіряли» від «перевірили, зауважень немає».
        // Змінюється лише те, що в тілі: `problem+json` із поясненням замість
        // голого `errorCode`, з якого клієнт міг зібрати хіба «HTTP 404».
        //
        // ⚠ Ключ ОКРЕМИЙ (`notValidated`), а не `periodEmpty`: той самий код
        // означає тут інше — документ є, період є, перевірку ще не запускали.
        return messages is null
            ? throw new Ecr.Application.Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Документ {id} за період {periodKey} ще не перевіряли.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-DOC-0404.notValidated",
                    ["documentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["periodKey"] = periodKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
            : Ok(new ValidationResultResponse(
                id,
                periodKey,
                [.. messages.Select(m => new ValidationFindingDto(
                    m.Severity.ToString(), m.RuleCode, m.Message,
                    // ⚠ Тут значення приходить зі ЗБЕРЕЖЕНОГО підсумку, а не з
                    // щойно порахованого: `wf.ValidationResult.MessagesJson` —
                    // це серіалізований `List<ValidationMessage>` цілком
                    // (`ValidateDocumentHandler.cs:106`), тож таблиця в ньому
                    // вже є і міграція для цього поля не потрібна.
                    m.TableDefId, m.RowKey, m.ColumnCode, m.BlocksSave))]));
    }

    /// <summary>Перерахунок документа, або лише одного його аркуша. Право <c>Calculation.Recalculate</c>.</summary>
    /// <remarks>
    /// Довга операція — у фон із прогресом; повертає <c>jobId</c>, а не результат.
    /// <c>SheetDefId</c> звужує перерахунок до одного аркуша (Q-331); без нього —
    /// увесь документ, як і раніше.
    /// </remarks>
    [HttpPost("{id:long}/recalculate")]
    [ProducesResponseType<Contracts.RecalculationAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Recalculate(
        long id, [FromBody] RecalculateDocumentRequest request, CancellationToken ct)
    {
        // Контролер лише делегує: рішення про чергу, payload і умови — у
        // прикладному шарі, інакше те саме правило почало б жити у двох місцях.
        ArgumentNullException.ThrowIfNull(request);

        var periodKey = request.PeriodKey;
        var jobId = await recalculate
            .HandleAsync(id, PeriodKey.Parse(periodKey), request.SheetDefId, ct)
            .ConfigureAwait(false);

        return Accepted(new Contracts.RecalculationAcceptedResponse(jobId, id, periodKey));
    }

    /// <summary>Подання аркуша на погодження.</summary>
    [HttpPost("{id:long}/submit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(
        long id, [FromBody] SheetWorkflowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await submit.HandleAsync(id, request.SheetDefId, request.PeriodKey, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Погодження або відхилення аркуша.</summary>
    [HttpPost("{id:long}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        long id, [FromBody] ApproveSheetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await approve
            .HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Approved, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Відкриває поданий документ. Право <c>Document.Reopen</c>.
    /// </summary>
    /// <remarks>
    /// Відкриття документа при <b>закритому періоді</b> відхиляється
    /// (<c>ECR-PRD-4223</c>): спершу відкривають період, і це інше право
    /// (<c>D-67</c>).
    /// </remarks>
    [HttpPost("{id:long}/reopen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reopen(
        long id, [FromBody] ReopenDocumentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await reopen
            .HandleAsync(id, request.SheetDefId, request.PeriodKey, request.Reason, ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Таблиці документа за період. Право <c>Document.View</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Grid читає і пише за <c>TableInstanceId</c>, а екземпляр існує <b>на
    /// кожен період окремо</b> (R-A6). Ані <c>DocumentSummary</c>, ані
    /// структура версії шаблону його не несуть: перша описує документ,
    /// друга — опис таблиць, а не їхні екземпляри (`A7-05`).
    /// </remarks>
    [HttpGet("{id:long}/tables")]
    [ProducesResponseType<IReadOnlyList<DocumentTableDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tables(long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await tables.HandleAsync(id, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Заповненість таблиць документа за період. Право <c>Document.View</c>.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="periodKey">Період; екземпляри таблиць існують окремо на кожен (R-A6).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Окремий маршрут, а не поля в <c>DocumentTableDto</c> сусіднього
    /// <c>GET …/tables</c>: той віддає СТРУКТУРУ (що є в документі) і
    /// читається один раз на відкриття, а це — СТАН (скільки введено), який
    /// змінюється після кожного запису. Склеїти їх означало б або
    /// перечитувати структуру заради лічильника, або показувати лічильник
    /// із моменту відкриття сторінки.
    ///
    /// ⛔ <c>errorCount</c>/<c>warningCount</c> приходять <c>null</c>, доки
    /// документ за цей період не перевіряли. Нуль тут був би тією самою
    /// неправдою, що й «0 зауважень» у неперевіреного документа
    /// (<c>A7-28</c>): у клієнта має лишитися змога показати «—», а не
    /// зелений нуль. Сусідній <c>GET …/validation</c> тримає той самий поділ
    /// кодом <c>404</c> (<c>err.ECR-DOC-0404.notValidated</c>); тут
    /// <c>404</c> не годиться — заповненість відома й до першої перевірки.
    /// </remarks>
    [HttpGet("{id:long}/tables/status")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Documents.Dto.TableStatusDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TablesStatus(long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await tableStatus.HandleAsync(id, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Числа, які дав розрахунок методологій. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Документ.</param>
    /// <param name="periodKey">Період; результати партиційовані за ним.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Окремий маршрут, а не поле зрізу таблиці, і це <c>D-69</c>: результат
    /// методології НЕ потрапляє в <c>doc.CellValue</c> — інакше нічний
    /// перерахунок писав би десятки мільйонів рядків у партиції документів. У
    /// документ він приходить посиланням через <c>cfg.CalculationBinding</c>.
    ///
    /// ⛔ Доти побачити це число було НІДЕ: перерахунок завершувався успіхом,
    /// значення лягало в <c>calc.CalculationResult</c>, і жоден маршрут його не
    /// віддавав. Тобто питання «чи порахувала методологія правильно» мало рівно
    /// одну відповідь — <c>SELECT</c> у базі.
    ///
    /// ⚠ Віддаються числа АКТУАЛЬНОГО прогону, не останнього за часом: прогін,
    /// який упав, лишає по собі частину рядків, і суміш двох версій методології
    /// на екрані виглядала б цілком правдоподібно.
    /// </remarks>
    [HttpGet("{id:long}/calculation-results")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Calculations.Dto.CalculationResultDto>>(
        StatusCodes.Status200OK)]
    public async Task<IActionResult> CalculationResults(
        long id, [FromQuery] int periodKey, CancellationToken ct)
        => Ok(await calculationResults.HandleAsync(id, periodKey, ct).ConfigureAwait(false));

    /// <summary>Експорт у <c>.xlsx</c>. Право <c>Document.Export</c>.</summary>
    [HttpPost("{id:long}/export")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Export(long id, [FromBody] ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ 202 з jobId, а не файл у відповіді. Бюджет експорту — 10 с p95, і
        // це середнє: книга на 500×60×12 будується довше за будь-який
        // розумний таймаут проксі.
        var jobId = await export
            .HandleAsync(
                id,
                new Ecr.Application.Ports.ExcelExportOptions(
                    request.IncludeFormulas, request.IncludeStyles, request.Language, request.PeriodKey),
                ct,
                request.Format)
            .ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }

    /// <summary>
    /// Віддає побудовану книгу. Право <c>Document.Export</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Ідентифікатор експорту приходить у повідомленні прогресу задачі:
    /// саме тому побудова повертає <c>202</c> з <c>jobId</c>, а не файл.
    /// Книга живе годину — довше тримати немає сенсу, це знімок даних на
    /// момент побудови.
    /// </remarks>
    [HttpGet("{id:long}/export/{exportId}")]
    // ⚠ Тут відповідь — ФАЙЛ, а не JSON, тому схеми в неї немає і бути не
    // може. Форма `Type = typeof(FileResult)` каже це прямо; узагальнена
    // `ProducesResponseType<T>` описувала б неіснуючий об'єкт.
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces(
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/zip",
        "application/json")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExport(long id, string exportId, CancellationToken ct)
    {
        var content = await downloadExport.HandleAsync(exportId, ct).ConfigureAwait(false);
        var (contentType, extension) = Ecr.Application.Documents.DocumentExportFormat.OfContent(content);

        return File(content, contentType, $"document-{id}-{exportId}.{extension}");
    }

    /// <summary>Попередній перегляд імпорту. Право <c>Document.Import</c>.</summary>
    /// <remarks>Імпорт **завжди** через перегляд diff (ФВ-4.3): застосування — окремим викликом.</remarks>
    [HttpPost("{id:long}/import/preview")]
    [ProducesResponseType<Ecr.Application.Ports.ImportPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportPreview(long id, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // ⛔ Перегляд НЕ застосовує нічого. Це не проміжний крок майстра, а
        // сам механізм захисту: імпорт без перегляду непомітно перезаписує
        // чужу роботу (ФВ-4.3).
        await using var stream = file.OpenReadStream();

        return Ok(await previewImport.HandleAsync(id, stream, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Застосування раніше переглянутого імпорту. Право <c>Document.Import</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ 202 з jobId — лише для diff, що перевищує поріг
    /// <see cref="Ecr.Application.Documents.ApplyImportHandler.LargeImportThreshold"/>
    /// (директива №11, T10 #45); звичайний, невеликий імпорт лишається 200 із
    /// результатом одразу, як і раніше.
    /// </remarks>
    [HttpPost("{id:long}/import/apply")]
    [ProducesResponseType<Ecr.Application.Documents.Dto.PatchCellsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ImportApply(
        long id, [FromBody] ImportApplyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Конфлікт версій рядків підіймається зі звичайного шляху запису як
        // ECR-CELL-0409 і перетворюється на 409 середовищем обробки помилок:
        // окрема перевірка тут була б другою, яка вміє розійтися з першою.
        var result = await applyImport
            .HandleAsync(id, request.PreviewToken, ct)
            .ConfigureAwait(false);

        return result.JobId is { } jobId
            ? Accepted(new Contracts.JobAcceptedResponse(jobId))
            : Ok(result.Response);
    }
}

/// <summary>Запит на створення документа.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="TemplateVersionId">
/// Необов'язкове. ⛔ `V-11`: документ заводиться на версії шаблону ПРОЄКТУ;
/// поле, якщо задане, мусить із нею збігатися (інакше <c>422</c>
/// <c>err.ECR-DOC-0422.versionNotProject</c>). Клієнту його надсилати не треба:
/// склад аркушів для діалогу дає <c>GET /projects/{id}/document-template</c>.
/// </param>
/// <param name="SheetDefIds">Аркуші, які входять у документ.</param>
/// <param name="Name">
/// Людське ім'я документа мовами каталогу; <c>null</c> — без імені.
/// Опційне і суто презентаційне: <c>BusinessKey</c> лишається технічним
/// ключем незалежно від нього (директива "людське ім'я документа").
/// </param>
public sealed record CreateDocumentRequest(
    int ProjectId,
    IReadOnlyList<int> SheetDefIds,
    IReadOnlyDictionary<string, string>? Name = null,
    int? TemplateVersionId = null);

/// <summary>Дія над документом у межах одного періоду.</summary>
/// <param name="PeriodKey">Період; <c>Рік*100 + Номер</c> (R-A6).</param>
public sealed record DocumentPeriodRequest(int PeriodKey);

/// <summary>Запит на перерахунок документа (Q-331).</summary>
/// <param name="PeriodKey">Період; <c>Рік*100 + Номер</c> (R-A6).</param>
/// <param name="SheetDefId">
/// Аркуш; <c>null</c> — увесь документ (поведінка до Q-331). Заданий —
/// звужує перерахунок до ОДНОГО аркуша (директива паритету зі старою
/// системою, прогалина 2): вхідні дані читаються як і раніше з усього
/// документа, звужується лише те, ЩО ЗАПИСУЄТЬСЯ.
/// </param>
public sealed record RecalculateDocumentRequest(int PeriodKey, int? SheetDefId = null);

/// <summary>Аркуш × період — адреса операції робочого процесу.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record SheetWorkflowRequest(int SheetDefId, int PeriodKey);

/// <summary>Запит на погодження аркуша.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Approved"><c>true</c> — погодити, <c>false</c> — відхилити.</param>
/// <param name="Reason">Причина; обов'язкова при відхиленні.</param>
public sealed record ApproveSheetRequest(int SheetDefId, int PeriodKey, bool Approved, string? Reason);

/// <summary>Запит на відкриття поданого документа.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Reason">Причина; обов'язкова.</param>
public sealed record ReopenDocumentRequest(int SheetDefId, int PeriodKey, string Reason);

/// <summary>Запит на зміну бізнес-ключа документа (ФВ-3.9).</summary>
/// <param name="BusinessKey">Новий ключ.</param>
/// <param name="ExpectedBusinessKey">Чинний ключ, який бачила людина; розбіжність — 409.</param>
/// <param name="Reason">Причина; обов'язкова, лягає в аудит.</param>
public sealed record ChangeDocumentKeyRequest(string? BusinessKey, string? ExpectedBusinessKey, string? Reason);

/// <summary>Запит на експорт.</summary>
/// <param name="IncludeFormulas">
/// Додати формули у вивантаження (ФВ-4.2): <c>xlsx</c> — транслювати вирази в
/// Excel-синтаксис; <c>csv</c>/<c>json</c> — сирий вираз мовою редактора
/// виразів проєкту (без трансляції, бо там немає сітки клітинок).
/// </param>
/// <param name="IncludeStyles">Переносити стилі шаблону.</param>
/// <param name="Language">Мова заголовків.</param>
/// <param name="PeriodKey">Період вивантаження (R-A6).</param>
/// <param name="Format"><c>xlsx</c> (типово), <c>csv</c> (zip, файл на таблицю) або <c>json</c> — ФВ-4.2.</param>
/// <remarks>
/// ⚠ Період обовʼязковий: подання, затвердження і перерахунок працюють за
/// період, і «експорт усього документа» означав би книгу, у якій неможливо
/// сказати, який стовпчик за який місяць.
/// </remarks>
public sealed record ExportRequest(
    bool IncludeFormulas, bool IncludeStyles, string Language, int PeriodKey, string? Format = null);

/// <summary>Запит на застосування імпорту.</summary>
/// <param name="PreviewToken">Токен раніше побудованого diff.</param>
public sealed record ImportApplyRequest(string PreviewToken);

/// <summary>Результат перевірки документа за період.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період, за який виконано перевірку.</param>
/// <param name="Messages">Зауваження ВСІХ рівнів.</param>
/// <remarks>
/// ⚠ <c>200</c> означає «перевірку виконано», а не «зауважень немає»: рішення,
/// чи можна подавати, ухвалює клієнт за наявністю рівня <c>Error</c>.
/// </remarks>
public sealed record ValidationResultResponse(
    long DocumentId, int PeriodKey, IReadOnlyList<ValidationFindingDto> Messages);

/// <summary>Одне зауваження перевірки.</summary>
/// <param name="Severity">Рівень: <c>Error</c>, <c>Warning</c>, <c>Info</c>.</param>
/// <param name="RuleCode">Код правила.</param>
/// <param name="Message">Текст, уже локалізований.</param>
/// <param name="TableDefId">
/// Таблиця, у якій знайдено зауваження.
/// </param>
/// <param name="RowKey">Рядок; <c>null</c> — зауваження до таблиці.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — зауваження до рядка.</param>
/// <param name="BlocksSave">Чи блокує збереження.</param>
/// <remarks>
/// ⛔ <c>TableDefId</c> — частина АДРЕСИ, а не довідкове поле. Аркуш містить
/// кілька таблиць, і ключ рядка унікальний лише в межах своєї: без таблиці
/// пара <c>(RowKey, ColumnCode)</c> вказує на стільки комірок, скільки таблиць
/// аркуша мають такий рядок. <c>ValidationMessage.TableDefId</c> цю адресу ніс
/// давно — відображення в DTO його відкидало, тож клієнт отримував
/// зауваження, за яким не міг перейти до комірки однозначно.
/// </remarks>
public sealed record ValidationFindingDto(
    string Severity, string RuleCode, string Message, int TableDefId, string? RowKey, string? ColumnCode, bool BlocksSave);
