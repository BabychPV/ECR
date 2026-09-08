using System.Text.Json;
using Ecr.Application.Localization;
using Ecr.Application.Templates;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>Операції над версією шаблону: клон, публікація, diff, структура.</summary>
[ApiController]
[Route("api/v1/template-versions/{id:int}")]
[Authorize]
public sealed class TemplateVersionsController(
    CloneTemplateVersionHandler clone,
    PublishTemplateVersionHandler publish,
    DiffTemplateVersionsHandler diff,
    PatchPresentationHandler patchPresentation,
    GetTemplateStructureHandler structure,
    GetAccessMatrixHandler accessMatrix,
    ListTableRelationsHandler listRelations,
    SaveTableRelationHandler saveRelation,
    DeleteTableRelationHandler deleteRelation,
    SaveSheetDefHandler saveSheet,
    DeleteSheetDefHandler deleteSheet,
    SaveTableDefHandler saveTable,
    DeleteTableDefHandler deleteTable,
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>Клонує версію. Право <c>Template.Edit</c>.</summary>
    [HttpPost("clone")]
    [ProducesResponseType<Contracts.VersionIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Clone(
        int id, [FromBody] CloneVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var versionId = await clone
            .CloneAsync(id, request.NewVersion, UserId, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/template-versions/{versionId}", new Contracts.VersionIdResponse(versionId));
    }

    /// <summary>
    /// Публікує версію. Право <c>Template.Publish</c>.
    /// </summary>
    /// <remarks>
    /// Після публікації структура незмінна: тригер БД відхиляє структурний
    /// <c>UPDATE</c>, презентаційний пропускає. Цикл у графі формул — помилка
    /// **публікації** (<c>ECR-TMPL-4221</c>), а не рантайму (ФВ-9.4).
    /// </remarks>
    [HttpPost("publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        // Цикл у графі формул, нерозв'язані посилання і несумісні одиниці —
        // помилки ПУБЛІКАЦІЇ, і кидає їх обробник. Контролер лише передає.
        await publish.PublishAsync(id, UserId, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Виводить версію з обігу. Право <c>Template.Publish</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Відкат (<c>ФВ-7.8</c>) — це переведення в <c>Deprecated</c>, а НЕ
    /// видалення: на версію посилаються проєкти, подані форми, зрізи й
    /// аудит. Стан існував від Етапу 1 і був недосяжний — перевести
    /// версію в нього не міг ніхто, тобто відкат був неможливий у
    /// принципі, а єдиним «відкатом» лишалося видалення.
    /// </remarks>
    /// <param name="id">Версія.</param>
    /// <param name="request">Причина відкату.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("deprecate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Deprecate(
        int id, [FromBody] DeprecateVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await publish.DeprecateAsync(id, UserId, request.Reason, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Diff двох версій. Право <c>Template.View</c>.</summary>
    /// <remarks>Зіставлення за ідентичністю (<c>Code</c>, <c>RowKey</c>), не за позицією.</remarks>
    [HttpGet("diff/{otherId:int}")]
    [ProducesResponseType<TemplateDiffDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TemplateDiffDto>> Diff(int id, int otherId, CancellationToken ct)
        => await diff.HandleAsync(id, otherId, ct).ConfigureAwait(false);

    /// <summary>
    /// Патч презентаційного шару опублікованої версії. Право <c>Template.Edit</c>.
    /// </summary>
    /// <remarks>
    /// Єдина зміна, дозволена після публікації. Інкрементує
    /// <c>PresentationRevision</c> одним statement із <c>OUTPUT</c> (R-B7) —
    /// саме тому ключ кешу <c>v{id}:r{rev}</c> не потребує інвалідації.
    /// </remarks>
    [HttpPatch("presentation")]
    // ⛔ Тип відповіді оголошений ЯВНО, а тіло — іменований запис, а не
    // анонімний об'єкт. Інакше в схемі OpenAPI лишається порожня 200-ка,
    // згенерувати клієнтський тип ні з чого, і клієнт описує відповідь
    // рукописним інтерфейсом — з помилкою в назві поля, яку ніхто не
    // побачить (`A7-16`, `A7-32`).
    [ProducesResponseType<PresentationRevisionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PatchPresentation(
        int id, [FromBody] JsonElement patch, CancellationToken ct)
    {
        var revision = await patchPresentation
            .PatchAsync(id, patch.GetRawText(), UserId, ct)
            .ConfigureAwait(false);

        // Нова ревізія — це новий ключ кешу v{id}:r{rev}. Клієнт має її
        // отримати, інакше він і далі питатиме структуру за старим ключем.
        return Ok(new PresentationRevisionResponse(revision));
    }

    /// <summary>Структура версії для клієнта. Право <c>Template.View</c>.</summary>
    /// <remarks>Кешується за ключем <c>v{id}:r{rev}</c> (ФВ-2.5); віддається з <c>ETag</c>.</remarks>
    [HttpGet("structure")]
    [ProducesResponseType<TemplateStructureDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<TemplateStructureDto>> Structure(int id, CancellationToken ct)
    {
        var dto = await structure.HandleAsync(id, ct).ConfigureAwait(false);

        // ETag = v{id}:r{rev} — той самий ключ, що й у кеші метаданих (ФВ-2.5).
        // Одне значення на дві ролі: інвалідація не потрібна ні тут, ні там.
        var etag = $"\"v{dto.TemplateVersionId}:r{dto.PresentationRevision}\"";
        Response.Headers[HeaderNames.ETag] = etag;

        if (UiStringResolver.IsNotModified(Request.Headers[HeaderNames.IfNoneMatch], etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return dto;
    }

    /// <summary>
    /// Матриця доступу <c>період × аркуш</c> для конструктора. Право <c>Template.View</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Будується тим самим обчислювачем, що й доступ у документі
    /// (<c>ФВ-2.18</c>): друга реалізація «для перегляду» показувала б не те,
    /// що система робить насправді, і перегляд перестав би ловити помилку в
    /// правилах — тобто робив би рівно протилежне тому, заради чого існує.
    /// </remarks>
    [HttpGet("access-matrix")]
    [ProducesResponseType<AccessMatrixDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccessMatrixDto>> AccessMatrix(int id, CancellationToken ct)
        => await accessMatrix.HandleAsync(id, ct).ConfigureAwait(false);

    /// <summary>
    /// Зв'язки між таблицями версії. Право <c>Template.View</c>.
    /// </summary>
    /// <param name="id">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Порожній перелік — законна відповідь: механізм опційний
    /// (<c>ФВ-2.12</c>), і шаблон без жодного зв'язку працює так само.
    ///
    /// ⛔ Саме тому відповідь — конверт із <c>isEditable</c>, а не голий масив:
    /// на порожньому переліку масив нічого не каже про стан версії, і клієнт
    /// показав би кнопку «новий зв'язок» на опублікованій версії, де сервер
    /// однаково відмовить.
    /// </remarks>
    [HttpGet("relations")]
    [ProducesResponseType<TableRelationsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TableRelationsDto>> Relations(
        int id, CancellationToken ct)
        => Ok(await listRelations.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує зв'язок між таблицями чернетки. Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код зв'язку.</param>
    /// <param name="request">Налаштування зв'язку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Це і є <c>ФВ-2.13</c>: зв'язки налаштовуються у вебі, а не в конфігах
    /// чи коді. Тому маршрут існує окремо від патча презентації — той свідомо
    /// відхиляє все структурне, а зв'язок структурний.
    ///
    /// ⚠ <c>PUT</c> за кодом: створення й зміна — та сама дія, бо адресу задає
    /// викликач (<c>D2-147</c>). Стан версії перевіряє домен, а не ця дія:
    /// опублікована відхиляє правку сама (<c>ECR-TMPL-0409</c>, <c>ФВ-7.1</c>).
    /// </remarks>
    [HttpPut("relations/{code}")]
    [ProducesResponseType<TableRelationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TableRelationDto>> SaveRelation(
        int id, string code, [FromBody] SaveTableRelationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveRelation
            .HandleAsync(
                id,
                code,
                new SaveTableRelationCommand(
                    request.SourceTableDefId,
                    request.TargetTableDefId,
                    request.RelationKind,
                    request.MatchJson,
                    request.MapJson,
                    request.OnSourceChange,
                    request.IsActive),
                ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає зв'язок із чернетки. Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код зв'язку.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("relations/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRelation(int id, string code, CancellationToken ct)
    {
        await deleteRelation.HandleAsync(id, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Записує аркуш чернетки. Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код аркуша.</param>
    /// <param name="request">Налаштування аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Перший вертикальний зріз авторства структури шаблону через API
    /// (`ФВ-2.1`..`ФВ-2.5`): до цього аркуш, таблицю, колонку чи рядок
    /// створював лише офлайновий генератор тестових даних.
    ///
    /// ⚠ <c>PUT</c> за кодом — та сама форма, що й <c>relations/{code}</c>
    /// вище: створення й зміна є однією дією, бо адресу задає викликач
    /// (<c>D2-147</c>). Стан версії перевіряє домен: опублікована відхиляє
    /// правку сама (<c>ECR-TMPL-0409</c>, <c>ФВ-7.1</c>).
    /// </remarks>
    [HttpPut("sheets/{code}")]
    [ProducesResponseType<SheetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SheetDto>> SaveSheet(
        int id, string code, [FromBody] SaveSheetDefRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveSheet
            .HandleAsync(
                id,
                code,
                new SaveSheetDefCommand(
                    request.NameL10n, request.Ordinal, request.SheetGroup,
                    request.IsMandatory, request.IsVisible),
                ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає аркуш із чернетки (м'яко, <c>ФВ-7.6</c>). Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="code">Код аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("sheets/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSheet(int id, string code, CancellationToken ct)
    {
        await deleteSheet.HandleAsync(id, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Записує таблицю на аркуші чернетки. Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="sheetCode">Код аркуша, якому належить таблиця.</param>
    /// <param name="code">Код таблиці.</param>
    /// <param name="request">Налаштування таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Другий вертикальний зріз авторства структури шаблону через API
    /// (<c>W5.1</c>), той самий патерн, що й <c>sheets/{code}</c> вище
    /// (<c>W5.0</c>).
    ///
    /// ⚠ Таблиця адресується ДВОМА кодами — <c>{sheetCode}/tables/{code}</c>,
    /// а не голим кодом версії, як аркуш: код таблиці унікальний лише в межах
    /// свого аркуша (<see cref="Ecr.Domain.Entities.Configuration.SheetDef.AddTable"/>),
    /// тож без коду аркуша в адресі дві таблиці з однаковим кодом на різних
    /// аркушах були б нерозрізнимі маршрутом. Батько — аркуш — адресується
    /// саме своїм кодом, а не ідентифікатором, з тієї самої причини, що й сам
    /// аркуш у `sheets/{code}`: адресу задає викликач (<c>D2-147</c>), а
    /// ідентифікатор аркуша до першого читання структури клієнту невідомий.
    ///
    /// ⚠ <c>PUT</c> за кодом: створення й зміна — одна ідемпотентна дія. Стан
    /// версії перевіряє домен: опублікована відхиляє правку сама
    /// (<c>ECR-TMPL-0409</c>, <c>ФВ-7.1</c>).
    /// </remarks>
    [HttpPut("sheets/{sheetCode}/tables/{code}")]
    [ProducesResponseType<TableDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TableDto>> SaveTable(
        int id, string sheetCode, string code, [FromBody] SaveTableDefRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveTable
            .HandleAsync(
                id,
                sheetCode,
                code,
                new SaveTableDefCommand(
                    request.NameL10n, request.Ordinal, request.LayoutKind, request.RowMode,
                    request.MaxDynamicRows),
                ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає таблицю з аркуша чернетки (м'яко, <c>ФВ-7.6</c>). Право <c>Template.Edit</c>.
    /// </summary>
    /// <param name="id">Версія-чернетка.</param>
    /// <param name="sheetCode">Код аркуша, якому належить таблиця.</param>
    /// <param name="code">Код таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("sheets/{sheetCode}/tables/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteTable(int id, string sheetCode, string code, CancellationToken ct)
    {
        await deleteTable.HandleAsync(id, sheetCode, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Поточний користувач; анонім сюди не доходить через [Authorize].</summary>
    private int UserId => currentUser.UserId
        ?? throw new Application.Errors.AccessDeniedException(
            ErrorCodes.Unauthorized, "Сесія не містить користувача.");
}

/// <summary>Запит на виведення версії з обігу.</summary>
/// <param name="Reason">
/// Причина; потрапляє в журнал публікацій. Обов'язкова: «чому цю версію
/// більше не використовують» — питання, на яке через рік має бути
/// відповідь.
/// </param>
public sealed record DeprecateVersionRequest(string Reason);

/// <summary>Запит на клонування версії.</summary>
/// <param name="NewVersion">Номер нової версії.</param>
public sealed record CloneVersionRequest(string NewVersion);

/// <summary>Налаштування зв'язку між таблицями (<c>ФВ-2.12</c>).</summary>
/// <param name="SourceTableDefId">Таблиця-джерело; має належати цій версії.</param>
/// <param name="TargetTableDefId">Таблиця-приймач; має належати цій версії.</param>
/// <param name="RelationKind">Вид зв'язку.</param>
/// <param name="MatchJson">Як зіставляються рядки джерела і приймача.</param>
/// <param name="MapJson">Які колонки на які; <c>null</c> — перенесення немає.</param>
/// <param name="OnSourceChange">Реакція на зміну джерела: 0 Recalc, 1 Warn, 2 Block.</param>
/// <param name="IsActive">Чи діє зв'язок.</param>
public sealed record SaveTableRelationRequest(
    int SourceTableDefId,
    int TargetTableDefId,
    Ecr.Domain.Enums.TableRelationKind RelationKind,
    string MatchJson,
    string? MapJson,
    byte OnSourceChange,
    bool IsActive);

/// <summary>Нова ревізія презентаційного шару.</summary>
/// <param name="PresentationRevision">Ревізія; входить у ключ кешу метаданих (D-16).</param>
public sealed record PresentationRevisionResponse(int PresentationRevision);

/// <summary>Налаштування аркуша чернетки (<c>ФВ-2.1</c>).</summary>
/// <param name="NameL10n">Назва аркуша мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — новий аркуш стає останнім за порядком.</param>
/// <param name="SheetGroup">Група для правил складу документа; <c>null</c> — поза групами.</param>
/// <param name="IsMandatory">Чи обов'язковий аркуш для складу документа.</param>
/// <param name="IsVisible">Видимість аркуша.</param>
public sealed record SaveSheetDefRequest(
    IReadOnlyDictionary<string, string> NameL10n,
    int? Ordinal,
    string? SheetGroup,
    bool IsMandatory,
    bool IsVisible);

/// <summary>Налаштування таблиці на аркуші чернетки (<c>W5.1</c>).</summary>
/// <param name="NameL10n">Назва таблиці мовами каталогу.</param>
/// <param name="Ordinal"><c>null</c> — нова таблиця стає останньою на аркуші за порядком.</param>
/// <param name="LayoutKind">Розкладка: як періоди лягають на структуру.</param>
/// <param name="RowMode">Спосіб формування рядків.</param>
/// <param name="MaxDynamicRows">Стеля кількості рядків, якщо таблиця приймає додані користувачем; <c>null</c> — без стелі.</param>
public sealed record SaveTableDefRequest(
    IReadOnlyDictionary<string, string> NameL10n,
    int? Ordinal,
    Ecr.Domain.Enums.TableLayoutKind LayoutKind,
    Ecr.Domain.Enums.TableRowMode RowMode,
    int? MaxDynamicRows);
