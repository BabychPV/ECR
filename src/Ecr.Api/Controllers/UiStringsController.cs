using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>Каталог рядків інтерфейсу.</summary>
/// <remarks>
/// ⚠ Файла немає в дереві <c>05-skeleton.md</c> §1, але ендпоінти
/// <c>/api/v1/ui-strings/…</c> оголошені в <c>02-contracts.md</c> §9, а
/// обробники <see cref="GetUiStringsHandler"/> і <see cref="SetUiStringHandler"/>
/// існують. Розміщувати їх в іншому контролері не можна: локалізація — власна
/// область, і єдиний контролер із анонімним доступом, крім входу (Q-015).
///
/// Каталог розділений на дві області (<c>D-114</c>). <c>scope=public</c>
/// анонімний — сторінка входу потребує підписів кнопок раніше, ніж хтось
/// автентифікований. <c>scope=private</c> — лише після входу: назви
/// адміністративних областей і прав не мають бути видимі тому, хто ще не
/// увійшов (ФВ-14.2).
/// </remarks>
[ApiController]
[Route("api/v1/ui-strings")]
public sealed class UiStringsController(
    GetUiStringsHandler get,
    SetUiStringHandler set,
    GetUiStringCoverageHandler coverage,
    ListUiStringsHandler list,
    ExportUiStringsCsvHandler export,
    UiStringImportHandler import,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>Експорт перекладу в CSV: <c>key, scope, en, &lt;lang&gt;, updatedAt</c>. Право <c>System.ManageLocalization</c>.</summary>
    /// <remarks>UTF-8 із BOM і CRLF, щоб Excel прочитав кирилицю; формули нейтралізовані.</remarks>
    /// <param name="lang">Мова перекладу (не мова за замовчуванням).</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("export.csv")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileResult))]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export([FromQuery] string lang, CancellationToken ct)
    {
        var csv = await export.HandleAsync(lang, ct).ConfigureAwait(false);
        var bytes = new System.Text.UTF8Encoding(true).GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();

        return File(bytes, "text/csv; charset=utf-8", $"ui-strings-{lang}.csv");
    }

    /// <summary>Імпорт перекладу з CSV. Право <c>System.ManageLocalization</c>.</summary>
    /// <remarks>
    /// Звіт — завжди 200: помилки рядків є даними для термінолога. Є хоч одна
    /// помилка або <c>dryRun</c> — не записано нічого. Стеля файлу —
    /// <c>Localization:ImportMaxBytes</c>.
    /// </remarks>
    /// <param name="lang">Мова перекладу.</param>
    /// <param name="dryRun">Лише перевірка.</param>
    /// <param name="file">CSV у кодуванні UTF-8.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("import")]
    [Authorize]
    [ProducesResponseType<UiStringImportReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Import(
        [FromQuery] string lang, [FromQuery] bool dryRun, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var maxBytes = configuration.GetValue("Localization:ImportMaxBytes", UiStringImportHandler.DefaultMaxBytes);

        // Понад стелю файл не читається — обробник відмовить після перевірки права.
        var content = string.Empty;
        if (file.Length <= maxBytes)
        {
            using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        return Ok(await import.HandleAsync(lang, content, file.Length, maxBytes, dryRun, ct).ConfigureAwait(false));
    }

    /// <summary>Покриття перекладу по мовах. Право <c>System.ManageLocalization</c>.</summary>
    /// <remarks>
    /// ⚠ Літеральний сегмент <c>coverage</c> виграє в шаблону <c>{lang}</c> за
    /// правилами маршрутизації; мови з таким кодом не буває — код мови це
    /// <c>char(2..5)</c> реєстру.
    /// </remarks>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("coverage")]
    [Authorize]
    [ProducesResponseType<UiStringCoverageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Coverage(CancellationToken ct)
        => Ok(await coverage.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Адміністративний перелік рядків мови **без fallback**. Право
    /// <c>System.ManageLocalization</c>.
    /// </summary>
    /// <remarks>
    /// Каталог <c>GET {lang}</c> підміняє відсутній переклад мовою за
    /// замовчуванням, і відсутнє там невидиме. Тут <c>value = null</c> означає
    /// рівно «перекладу немає».
    /// </remarks>
    /// <param name="lang">Мова; порожньо — мова за замовчуванням.</param>
    /// <param name="missingOnly">Лише рядки без перекладу.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [Authorize]
    [ProducesResponseType<UiStringListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? lang, [FromQuery] bool missingOnly, CancellationToken ct)
        => Ok(await list
            .HandleAsync(string.IsNullOrWhiteSpace(lang) ? UiStringResolver.DefaultLanguage : lang, missingOnly, ct)
            .ConfigureAwait(false));

    /// <summary>Каталог рядків для мови.</summary>
    /// <remarks>
    /// Кожна область має **власний** <c>ETag = revision</c>; на
    /// <c>If-None-Match</c> зі збігом — <c>304</c>. Відсутній переклад
    /// підмінюється мовою за замовчуванням, а відсутній ключ повертається
    /// як сам ключ — порожній підпис у UI гірший за англійський.
    /// </remarks>
    /// <param name="lang">Мова інтерфейсу.</param>
    /// <param name="scope"><c>public</c> або <c>private</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{lang}")]
    [AllowAnonymous]

    // ⛔ Тип відповіді оголошений явно. Без нього в схемі OpenAPI лишалася
    // порожня 200-ка, клієнт описував каталог рукописним інтерфейсом і
    // помилявся в назві поля: чекав `language`, а сервер віддає `languageCode`
    // (`A7-16`). Помилка нічого не ламала — і саме тому жила.
    [ProducesResponseType<Ecr.Application.Ports.UiStringCatalog>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(string lang, [FromQuery] string scope, CancellationToken ct)
    {
        // Усе, крім явного "public", вважається приватним. Помилка в написанні
        // має закривати каталог, а не відкривати його.
        var publicOnly = string.Equals(scope, "public", StringComparison.OrdinalIgnoreCase);

        // Анонімний запит приватної області відхиляється в обробнику (ФВ-14.2);
        // тут лише умовний запит.
        var catalog = await get.HandleAsync(lang, publicOnly, ct).ConfigureAwait(false);
        var etag = GetUiStringsHandler.ETag(publicOnly, lang, catalog.Revision);

        Response.Headers[HeaderNames.ETag] = etag;

        if (UiStringResolver.IsNotModified(Request.Headers[HeaderNames.IfNoneMatch], etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(catalog);
    }

    /// <summary>Змінює рядок каталогу. Право <c>System.ManageLocalization</c>.</summary>
    /// <remarks>Будь-який запис інкрементує <c>Revision</c> — інакше клієнти не побачать зміни.</remarks>
    /// <param name="lang">Мова.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="request">Текст і область.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{lang}/{key}")]
    [Authorize]
    [ProducesResponseType<UiStringRevisionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Set(
        string lang, string key, [FromBody] SetUiStringRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var revision = await set
            .HandleAsync(key, lang, request.Value, (byte)request.Scope, ct)
            .ConfigureAwait(false);

        return Ok(new UiStringRevisionResponse(revision));
    }
}

/// <summary>Запит на зміну рядка каталогу.</summary>
/// <param name="Value">Текст.</param>
/// <param name="Scope">Область: 0 — публічна, 1 — приватна (<c>D-114</c>).</param>
public sealed record SetUiStringRequest(string Value, UiStringScope Scope);

/// <summary>Версія каталогу після запису.</summary>
/// <param name="Revision">Версія; слугує <c>ETag</c>.</param>
public sealed record UiStringRevisionResponse(int Revision);
