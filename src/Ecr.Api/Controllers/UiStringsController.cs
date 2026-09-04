using Ecr.Application.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    [HttpGet("{lang}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> Get(string lang, [FromQuery] string scope, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: scope=public — анонімний; scope=private — вимагає автентифікації, " +
            "інакше 401 (анонімний запит приватної області відхиляється, ФВ-14.2); " +
            "делегувати get.HandleAsync(lang, publicOnly: scope == \"public\", ct); " +
            "виставити ETag = revision ОБЛАСТІ; If-None-Match зі збігом → 304.");

    /// <summary>Змінює рядок каталогу. Право <c>System.ManageLocalization</c>.</summary>
    /// <remarks>Будь-який запис інкрементує <c>Revision</c> — інакше клієнти не побачать зміни.</remarks>
    [HttpPut("{lang}/{key}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> Set(string lang, string key, [FromBody] SetUiStringRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити System.ManageLocalization; делегувати " +
            "set.HandleAsync(key, lang, request.Value, request.Scope, ct); повернути новий Revision.");
}

/// <summary>Запит на зміну рядка каталогу.</summary>
/// <param name="Value">Текст.</param>
/// <param name="Scope">Область: 0 — публічна, 1 — приватна (<c>D-114</c>).</param>
public sealed record SetUiStringRequest(string Value, byte Scope);
