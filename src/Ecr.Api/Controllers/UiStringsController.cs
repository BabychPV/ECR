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
    SetUiStringHandler set) : ControllerBase
{
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
