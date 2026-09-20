using Ecr.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Описи звітів і зрізи для регламентної звітності.</summary>
/// <remarks>
/// ⚠ Самих звітів тут немає: звітність лишається в SSRS (<c>D-52</c>).
/// Наша межа — <c>rpt.*</c>: незмінний зріз, який SSRS читає.
///
/// ⛔ Опис звіту (<c>GET</c>/<c>POST /reports</c>, <c>W7</c>) цієї межі не
/// зсуває. Це не конструктор звітів — ТЗ виносить його за обсяг разом із
/// веб-переглядачем (<c>ФВ-10.6</c>). Це рядок ДАНИХ, без якого побудова
/// зрізу не має за що зачепитися: <c>ReportDef</c> і <c>ReportVersion</c> не
/// створювало ніщо, і тому <c>POST /reports/{code}/build</c> відмовляв
/// <c>ECR-RPT-0404</c> на будь-який код (<c>ФВ-10.4</c>: визначення звіту —
/// дані, нова державна форма не має потребувати релізу коду).
/// </remarks>
[ApiController]
[Route("api/v1/reports")]
[Authorize]
public sealed class ReportsController(
    ListReportSnapshotsHandler snapshots,
    BuildReportSnapshotHandler build,
    VerifyReportSnapshotHandler verify,
    GetSnapshotRowsHandler rows,
    ExportSnapshotHandler exportSnapshot,
    ListReportDefsHandler definitions,
    CreateReportDefHandler createDefinition,
    CreateReportVersionHandler createVersion,
    PublishReportVersionHandler publishVersion) : ControllerBase
{
    /// <summary>Перелік описів звітів. Право <c>Report.ViewRegulatory</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Версії приходять разом з описом, а не окремим запитом: побудувати
    /// зріз можна лише за ОПУБЛІКОВАНОЮ версією, і перелік, у якому цього не
    /// видно, показував би звіти, кожен другий з яких відмовляє на побудову
    /// без пояснення.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Ports.ReportDefinitionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Definitions(CancellationToken ct)
        => Ok(await definitions.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Заводить опис звіту разом із першою версією-чернеткою.
    /// Право <c>Report.EditDefinition</c>.
    /// </summary>
    /// <param name="request">Код, назва, ознака регуляторності й перша версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Колонки й правила приходять СТРУКТУРОЮ, а не рядком JSON: у базі це
    /// справді JSON (<c>ФВ-10.4</c> — опис звіту є даними), але клієнт, який
    /// складає його сам, рано чи пізно складе такий, якого побудова не
    /// прочитає, і дізнається про це порожнім зрізом.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<Ecr.Application.Ports.ReportDefinitionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateDefinition(
        [FromBody] CreateReportDefRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await createDefinition
            .HandleAsync(
                new CreateReportDefCommand(
                    request.Code, request.NameL10n, request.IsRegulatory,
                    request.Version, request.Columns, request.Rules),
                ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Заводить нову версію-чернетку наявного опису. Право <c>Report.EditDefinition</c>.
    /// </summary>
    /// <param name="id">Опис звіту.</param>
    /// <param name="request">Номер версії, колонки й правила.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Єдиний спосіб змінити опублікований опис: опублікована версія
    /// незмінна, бо на її колонки посилаються вже побудовані зрізи, які читає
    /// SSRS. Дії «правити версію» немає навмисно.
    /// </remarks>
    [HttpPost("{id:int}/versions")]
    [ProducesResponseType<Ecr.Application.Ports.ReportVersionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateVersion(
        int id, [FromBody] CreateReportVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await createVersion
            .HandleAsync(id, new CreateReportVersionCommand(request.Version, request.Columns, request.Rules), ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Публікує версію опису звіту. Право <c>Report.EditDefinition</c>.
    /// </summary>
    /// <param name="id">Опис звіту.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Побудова бере ЛИШЕ опубліковану версію — чернетка зрізу не дає
    /// взагалі. Опис звіту правлять саме тоді, коли ще не впевнені в ньому, і
    /// зріз за чернеткою потрапив би в регуляторну вʼюху нарівні зі справжнім.
    /// </remarks>
    [HttpPost("{id:int}/versions/{vid:int}/publish")]
    [ProducesResponseType<Ecr.Application.Ports.ReportVersionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PublishVersion(int id, int vid, CancellationToken ct)
        => Ok(await publishVersion.HandleAsync(id, vid, ct).ConfigureAwait(false));

    /// <summary>Перелік побудованих зрізів. Право <c>Report.ViewRegulatory</c>.</summary>
    /// <remarks>
    /// У переліку є час побудови й контрольна сума: споживач має бачити, на
    /// яких даних побудовано зріз. Два зрізи однієї версії за один період
    /// відрізняються лише цим.
    /// </remarks>
    [HttpGet("snapshots")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Ports.ReportSnapshotSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Snapshots(
        [FromQuery] int? projectId, [FromQuery] int? periodKey, CancellationToken ct)
        => Ok(await snapshots.HandleAsync(projectId, periodKey, ct).ConfigureAwait(false));

    /// <summary>
    /// Перевіряє незмінність зрізу: перераховує суму збереженого вмісту й
    /// порівнює зі збереженою. Право <c>Report.ViewRegulatory</c>.
    /// </summary>
    /// <param name="id">Зріз.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>POST</c>, хоча нічого не змінює: це дія з ціною (читає всі рядки
    /// зрізу), а не ресурс, який можна кешувати чи підвантажувати наперед.
    /// </remarks>
    [HttpPost("snapshots/{id:long}/verify")]
    [ProducesResponseType<SnapshotVerifyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifySnapshot(long id, CancellationToken ct)
        => Ok(await verify.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Рядки зрізу сторінками, у широкому вигляді. Право <c>Report.ViewRegulatory</c>.
    /// </summary>
    /// <param name="id">Зріз.</param>
    /// <param name="cursor">Останній уже отриманий <c>rowNo</c>; без нього — з початку.</param>
    /// <param name="limit">Розмір сторінки, не більше 500; типово 100.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// D-52a: другий споживач <c>rpt.*</c> поруч із SSRS. Колонки — з опису
    /// версії, за якою зріз побудовано; чужий зріз — той самий 404, що й неіснуючий.
    /// </remarks>
    [HttpGet("snapshots/{id:long}/rows")]
    [ProducesResponseType<Ecr.Application.Ports.SnapshotRowsPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SnapshotRows(
        long id, [FromQuery] int? cursor, [FromQuery] int? limit, CancellationToken ct)
        => Ok(await rows.HandleAsync(id, cursor, limit, ct).ConfigureAwait(false));

    /// <summary>
    /// Вивантажує зріз у <c>.xlsx</c>. Право <c>Report.Export</c>.
    /// </summary>
    /// <param name="id">Зріз.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Файл у відповіді ОДРАЗУ, без <c>202</c> і фонової задачі, на відміну
    /// від експорту документа: там книга на 500×60×12 не вкладається в жоден
    /// таймаут, тут стеля — <c>ExportSnapshotHandler.MaxRows</c> рядків одного
    /// плаского аркуша. Черга заради цього коштувала б користувачеві двох
    /// зайвих кроків і GUID у руках.
    ///
    /// ⛔ <c>D-52a</c> не зсувається: це ЗРІЗ, а не державна форма. PDF
    /// держформи лишається в SSRS (<c>D-52</c>).
    /// </remarks>
    [HttpGet("snapshots/{id:long}/export.xlsx")]
    // ⚠ Відповідь — ФАЙЛ, а не JSON: схеми в неї немає і бути не може.
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces(SnapshotExport.ContentType)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportSnapshot(long id, CancellationToken ct)
    {
        var book = await exportSnapshot.HandleAsync(id, ct).ConfigureAwait(false);

        // ⚠ Потік, а не байти: вміст лежить у тимчасовому файлі, і `FileResult`
        // закриває його сам після відправки — разом із самим файлом
        // (`FileOptions.DeleteOnClose`).
        return File(book.Content, SnapshotExport.ContentType, book.FileName);
    }

    /// <summary>
    /// Будує зріз. Право <c>Report.BuildSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// Зріз **незмінний**: повторна побудова створює новий, а не переписує
    /// старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні
    /// давали б різні числа без жодного сліду.
    /// </remarks>
    [HttpPost("{code}/build")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Build(
        string code, [FromBody] BuildSnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jobId = await build
            .HandleAsync(code, request.ProjectId, request.PeriodKey, request.Parameters, ct)
            .ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}

/// <summary>Запит на побудову зрізу.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Parameters">
/// Значення параметрів звіту за іменем (<c>R6</c>, <c>02b</c> §8a): число,
/// рядок, булеве або дата рядком — тип диктує ОГОЛОШЕННЯ параметра у версії.
/// Невідоме ім'я, обов'язковий параметр без значення і без <c>default</c> або
/// значення не того типу — <c>422 ECR-RPT-0422</c> одразу у відповіді на цей
/// запит, а не невдалою задачею через хвилину.
/// </param>
public sealed record BuildSnapshotRequest(
    int ProjectId, int PeriodKey, IReadOnlyDictionary<string, object?>? Parameters = null);

/// <summary>Запит на створення опису звіту разом із першою версією.</summary>
/// <param name="Code">Код звіту; ним адресується побудова зрізу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsRegulatory">
/// Чи звіт іде регулятору: від цього залежить фільтр статусів у вʼюсі
/// <c>rpt.v_*</c> (<c>D-65</c>), а не «важливість».
/// </param>
/// <param name="Version">Номер першої версії.</param>
/// <param name="Columns">Колонки зрізу: код і тип значення.</param>
/// <param name="Rules">Правила відбору рядків; <c>null</c> — джерело за замовчуванням.</param>
public sealed record CreateReportDefRequest(
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    bool IsRegulatory,
    string Version,
    IReadOnlyList<ReportColumnCommand> Columns,
    ReportRulesCommand? Rules);

/// <summary>Запит на створення версії-чернетки опису звіту.</summary>
/// <param name="Version">Номер версії; унікальний у межах опису.</param>
/// <param name="Columns">Колонки зрізу.</param>
/// <param name="Rules">Правила відбору рядків; <c>null</c> — джерело за замовчуванням.</param>
public sealed record CreateReportVersionRequest(
    string Version, IReadOnlyList<ReportColumnCommand> Columns, ReportRulesCommand? Rules);
