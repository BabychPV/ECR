using Ecr.Application.Common;
using Ecr.Application.Registries.Export;
using Ecr.Application.Registries.Rows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Дані довідника рядками для редактора (FEATURE-REGISTRY-TABLES §7.1, §8.4): читання — RT-13,
/// пакетний запис — RT-14, історія запису — RT-15, експорт — RT-16.
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер, а не ще дії в <see cref="RegistriesController"/>: той уже несе сімнадцять
/// обробників, а редактор даних росте власними кроками.
/// </remarks>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistryRowsController(
    GetRegistryRowsHandler getRows,
    RegistryBatchHandler saveBatch,
    GetRegistryEntryHistoryHandler getHistory,
    ExportRegistryHandler export,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>Префікс параметра фільтра поля: <c>field.&lt;КОД&gt;=значення</c>.</summary>
    private const string FieldFilterPrefix = "field.";

    /// <summary>
    /// Рядки довідника зі значеннями полів, сторінками за курсором. Право <c>Registry.View</c> або
    /// грант <c>Read</c> на довідник.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="asOf">Бізнес-дата чинності; обов'язкова для темпорального довідника.</param>
    /// <param name="asOfUtc">Системний момент (UTC) — значення «станом на»; немає — поточні.</param>
    /// <param name="parentEntryId">Батько композиції або каскаду.</param>
    /// <param name="id">Лише ці записи (параметр повторюється: <c>?id=1&amp;id=2</c>, ≤ 500).</param>
    /// <param name="q">Підрядок коду, назви або текстового поля.</param>
    /// <param name="cursor">Курсор попередньої сторінки.</param>
    /// <param name="limit">Розмір сторінки, 1…500.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Фільтри полів — параметри <c>field.&lt;КОД&gt;=значення</c> у поданні <c>values[].value</c>.
    /// </remarks>
    [HttpGet("{code}/rows")]
    [ProducesResponseType<PagedResult<RegistryRowDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<RegistryRowDto>>> Rows(
        string code,
        [FromQuery] DateOnly asOf,
        [FromQuery] DateTimeOffset? asOfUtc,
        [FromQuery] long? parentEntryId,
        [FromQuery] long[]? id,
        [FromQuery] string? q,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var fields = Request.Query
            .Where(p => p.Key.StartsWith(FieldFilterPrefix, StringComparison.OrdinalIgnoreCase)
                        && p.Key.Length > FieldFilterPrefix.Length)
            .ToDictionary(p => p.Key[FieldFilterPrefix.Length..], p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var request = new RegistryRowsRequest(
            code, asOf, asOfUtc?.UtcDateTime, parentEntryId, q, fields, new CursorRequest(limit, cursor))
        {
            EntryIds = id ?? [],
        };

        return Ok(await getRows.HandleAsync(request, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Пакетний запис рядків довідника (RT-14): <c>upsert</c> і <c>delete</c> однією транзакцією.
    /// Право <c>Registry.EditData</c> або грант <c>Write</c> на довідник.
    /// </summary>
    /// <remarks>
    /// Звіт — завжди 200, як імпорт CSV: помилки рядків (зокрема <c>ECR-REG-4093 entryChanged</c> для
    /// застарілого <c>baseVersion</c> і <c>4092 keyTaken</c>) — дані для сітки. Хоч одна помилка або
    /// <c>dryRun</c> — не записано нічого. 409 — лише гонка за ключем під час запису.
    /// </remarks>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Рядки пакета, ≤ 2000.</param>
    /// <param name="dryRun">Лише перевірка — із відкатом.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{code}/entries/batch")]
    [ProducesResponseType<RegistryBatchResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegistryBatchResult>> SaveBatch(
        string code,
        [FromBody] RegistryBatchRequest request,
        [FromQuery] bool dryRun,
        CancellationToken ct = default)
        => Ok(await saveBatch.HandleAsync(code, request, dryRun, ct).ConfigureAwait(false));

    /// <summary>
    /// Історія запису довідника (RT-15): хто, коли й що змінив — від найновішого. Право
    /// <c>Registry.View</c> або грант <c>Read</c> на довідник.
    /// </summary>
    /// <remarks>
    /// Джерело — системні версії запису й значень (<c>FOR SYSTEM_TIME ALL</c>), тож історію має будь-який
    /// шлях запису: форма, пакет, CSV, синк. Видалений запис теж має історію; запис іншого довідника —
    /// <c>404</c>.
    /// </remarks>
    /// <param name="code">Код довідника.</param>
    /// <param name="id">Запис.</param>
    /// <param name="cursor">Курсор попередньої сторінки.</param>
    /// <param name="limit">Розмір сторінки, 1…500.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{code}/entries/{id:long}/history")]
    [ProducesResponseType<PagedResult<RegistryEntryHistoryItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<RegistryEntryHistoryItemDto>>> EntryHistory(
        string code,
        long id,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => Ok(await getHistory.HandleAsync(code, id, new CursorRequest(limit, cursor), ct).ConfigureAwait(false));

    /// <summary>
    /// Експорт записів довідника в CSV або XLSX (RT-16): записи, чинні на <c>asOf</c>, посилання —
    /// кодами. Право <c>Registry.View</c> або грант <c>Read</c> на довідник.
    /// </summary>
    /// <remarks>
    /// Формат — <c>?format=csv|xlsx</c>; без нього — за <c>Accept</c> (<c>text/csv</c> або тип книги
    /// Excel), інакше CSV. CSV приймає назад імпорт (<c>POST …/entries/import</c>) без змін. Стеля —
    /// <c>Registries:ExportMaxRows</c> (50 000): понад неї — <c>422</c>, а не обрізаний файл.
    /// </remarks>
    /// <param name="code">Код довідника.</param>
    /// <param name="format"><c>csv</c> або <c>xlsx</c>.</param>
    /// <param name="asOf">Бізнес-дата чинності; без неї — сьогодні (UTC).</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{code}/export")]
    // ⚠ Відповідь — ФАЙЛ, а не JSON: схеми в неї немає і бути не може.
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces(ExportRegistryHandler.CsvContentType, ExportRegistryHandler.XlsxContentType)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        string code,
        [FromQuery] string? format,
        [FromQuery] DateOnly? asOf,
        CancellationToken ct = default)
    {
        var maxRows = configuration.GetValue("Registries:ExportMaxRows", ExportRegistryHandler.DefaultExportMaxRows);
        var file = await export
            .HandleAsync(code, format ?? FormatFromAccept(), asOf, maxRows, ct)
            .ConfigureAwait(false);

        // ⚠ Потік, а не байти: книга лежить у тимчасовому файлі, і FileResult закриває його сам.
        return File(
            file.Content,
            file.ContentType == ExportRegistryHandler.CsvContentType ? "text/csv; charset=utf-8" : file.ContentType,
            file.FileName);
    }

    /// <summary>Формат із <c>Accept</c>; нічого знайомого — <c>null</c> (CSV).</summary>
    private string? FormatFromAccept()
    {
        var accept = Request.Headers.Accept.ToString();
        return accept.Contains(ExportRegistryHandler.XlsxContentType, StringComparison.OrdinalIgnoreCase)
            ? ExportRegistryHandler.Xlsx
            : accept.Contains(ExportRegistryHandler.CsvContentType, StringComparison.OrdinalIgnoreCase)
                ? ExportRegistryHandler.Csv
                : null;
    }
}
