using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Методології розрахунку: перелік, версії, формули, публікація, симуляція.
/// </summary>
/// <remarks>
/// ⛔ Дві половини цього контролера відповідають на різні питання. Читання для
/// РОЗРАХУНКУ бачить лише опубліковане (<c>GET /methodologies</c>), бо рахувати
/// чернеткою не можна ніколи. Читання для РЕДАГУВАННЯ бачить чернетки
/// (<c>GET /methodologies/{id}/versions</c>), бо правити можна лише їх
/// (ФВ-9.15). Злити їх в один маршрут із прапорцем означало б, що одна
/// необережна зміна фільтра пускає незавершену версію в числа звіту.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologiesController(
    ListMethodologiesHandler listMethodologies,
    PublishMethodologyHandler publish,
    SimulateMethodologyHandler simulate,
    ListMethodologyVersionsHandler listVersions,
    CreateMethodologyVersionHandler createVersion,
    ListMethodologyFormulasHandler listFormulas,
    SaveMethodologyFormulaHandler saveFormula,
    DeleteMethodologyFormulaHandler deleteFormula) : ControllerBase
{
    /// <summary>Перелік методологій. Право <c>Calculation.View</c>.</summary>
    /// <param name="ids">Методології, які цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Три осі версійності не змішуються (ФВ-13.2): версія визначення, вікно
    /// дії і версія даних. Остання тут не з'являється взагалі — вона живе в
    /// довідниках.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MethodologyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyDto>>> List(
        [FromQuery] int[] ids, CancellationToken ct)
        => Ok(await listMethodologies.HandleAsync(ids ?? [], ct).ConfigureAwait(false));

    /// <summary>
    /// Усі версії методології, включно з чернетками. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Саме тут видно те, чого не показує <c>GET /methodologies</c>: версію,
    /// яку ще не опублікували. Доти кнопка «Опублікувати» стояла в переліку
    /// для версій, яких той перелік не містив за побудовою.
    /// </remarks>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType<IReadOnlyList<MethodologyDraftVersionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MethodologyDraftVersionDto>>> Versions(
        int id, CancellationToken ct)
        => Ok(await listVersions.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Створює версію-чернетку. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="request">Номер версії і, за потреби, версія-джерело.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ **Єдиний спосіб змінити опубліковану версію** (ФВ-9.1): вона
    /// незмінна, бо на її числа посилаються вже подані форми. «Зміна» — це
    /// клон із власним вікном дії, і саме тому клонування є частиною
    /// редагування, а не зручністю.
    /// </remarks>
    [HttpPost("{id:int}/versions")]
    [ProducesResponseType<MethodologyDraftVersionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MethodologyDraftVersionDto>> CreateVersion(
        int id, [FromBody] CreateMethodologyVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await createVersion
            .HandleAsync(id, request.VersionNumber, request.CopyFromVersionId, request.Level, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Формули версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/formulas")]
    [ProducesResponseType<IReadOnlyList<MethodologyFormulaDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyFormulaDto>>> Formulas(
        int id, int vid, CancellationToken ct)
        => Ok(await listFormulas.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує формулу версії-чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="request">Вираз, тип результату й одиниця.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>PUT</c>, а не <c>POST</c>: адресою формули є її **код** у межах
    /// версії, і повторний запит із тим самим тілом має дати той самий стан.
    /// Створення й зміна — та сама дія рівно тому, що код задає викликач.
    ///
    /// ⛔ Стан версії перевіряє домен, а не ця дія: опублікована версія
    /// відхиляє правку сама (<c>ECR-CALC-0409</c>, ФВ-13.2).
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/formulas/{code}")]
    [ProducesResponseType<MethodologyFormulaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyFormulaDto>> SaveFormula(
        int id, int vid, string code, [FromBody] SaveMethodologyFormulaRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveFormula
            .HandleAsync(vid, code, request.Expression, request.ResultType, request.OutputUnitId, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає формулу з версії-чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{id:int}/versions/{vid:int}/formulas/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteFormula(int id, int vid, string code, CancellationToken ct)
    {
        await deleteFormula.HandleAsync(vid, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Публікує версію методології. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Причина зміни і дата набуття чинності.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <b>Найнебезпечніша операція в системі</b> (ФВ-9.6): вона тихо змінює
    /// числа у вже поданих формах. Тому обов'язкові: причина зміни (ФВ-14.7),
    /// зелений тест (ФВ-9.12, інакше <c>ECR-CALC-0422</c>) і правило чотирьох
    /// очей — публікувати власну правку заборонено системно
    /// (<c>D-40</c>, <c>ECR-CALC-0409</c>).
    /// </remarks>
    [HttpPost("{id:int}/versions/{vid:int}/publish")]
    [ProducesResponseType<MethodologyPublicationDiff>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyPublicationDiff>> Publish(
        int id, int vid, [FromBody] PublishMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Diff РЕЗУЛЬТАТІВ повертається клієнтові, а не лише пишеться в журнал:
        // той, хто щойно опублікував, має побачити, що саме змінилося в числах.
        var diff = await publish
            .HandleAsync(vid, request.ChangeReason, request.EffectiveFrom, ct)
            .ConfigureAwait(false);

        return Ok(diff);
    }

    /// <summary>
    /// Прогін методології <b>без запису</b>. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="request">Версія і період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Показує, що вийде, якщо опублікувати (ФВ-13.5). Нічого не зберігає:
    /// саме тому доступний із правом на перегляд, а не на публікацію.
    /// </remarks>
    [HttpPost("{id:int}/simulate")]
    [ProducesResponseType<SimulationResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationResultDto>> Simulate(
        int id, [FromBody] SimulateMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await simulate
            .HandleAsync(request.MethodologyVersionId, request.PeriodKey, ct)
            .ConfigureAwait(false));
    }
}

/// <summary>Запит на публікацію версії методології.</summary>
/// <param name="ChangeReason">Причина зміни; обов'язкова і непорожня (ФВ-14.7).</param>
/// <param name="EffectiveFrom">
/// Дата набуття чинності. Обов'язкова: без неї невідомо, які періоди рахувати
/// цією версією, а які — попередньою.
/// </param>
public sealed record PublishMethodologyRequest(string ChangeReason, DateOnly? EffectiveFrom);

/// <summary>Запит на створення версії-чернетки.</summary>
/// <param name="VersionNumber">
/// Номер нової версії; унікальний у межах методології (<c>UQ_MethodologyVersion</c>).
/// </param>
/// <param name="CopyFromVersionId">
/// Версія-джерело. <c>null</c> — порожня чернетка; це законно лише для
/// методології, у якій ще нічого немає.
/// </param>
/// <param name="Level">
/// Рівень драбини виразності для ПОРОЖНЬОЇ чернетки. Клон бере рівень із
/// джерела: версія, що змінила рівень, — уже інша методологія, а не її нова
/// редакція (ФВ-9.2).
/// </param>
public sealed record CreateMethodologyVersionRequest(
    string VersionNumber, int? CopyFromVersionId, CalculationLevel Level);

/// <summary>Запит на запис формули версії-чернетки.</summary>
/// <param name="Expression">Вираз діалекту методологій.</param>
/// <param name="ResultType">
/// Що формула повертає: число чи текст. Оголошується явно — у корпусі формула
/// повертає <c>'Сверхнорматив'</c> як ЗНАЧЕННЯ, і без типу воно пішло б у
/// числову колонку результату.
/// </param>
/// <param name="OutputUnitId">
/// Одиниця результату; <c>null</c> — зняти. На текстовому результаті одиниця
/// відхиляється: вимір — властивість числа (ФВ-16.6).
/// </param>
public sealed record SaveMethodologyFormulaRequest(
    string Expression, FormulaResultType ResultType, int? OutputUnitId);

/// <summary>Запит на симуляцію.</summary>
/// <param name="MethodologyVersionId">Версія, яку проганяємо.</param>
/// <param name="PeriodKey">Період, на даних якого проганяємо.</param>
public sealed record SimulateMethodologyRequest(int MethodologyVersionId, int PeriodKey);
