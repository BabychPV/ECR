using Ecr.Application.Calculations.Scripting.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Перенесення наявних методологій на «ECR Script» на живій базі (MS, план 8.5): запис тіньових текстів із пакета
/// <c>pack</c> і перемикання рушія групи версій. Право <c>Calculation.Publish</c>, кожна зміна — в журналі.
/// </summary>
[ApiController]
[Route("api/v1/methodologies/script-migration")]
[Authorize]
public sealed class MethodologyScriptMigrationController(
    ApplyScriptPackageHandler apply,
    SwitchScriptEngineHandler switchEngine) : ControllerBase
{
    /// <summary>
    /// Звіряє пакет <c>package.json</c> з базою й записує тіньові тексти. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <param name="request">Пакет: версії з текстом і відбитком джерела.</param>
    /// <param name="dryRun"><c>true</c> — лише перевірити, нічого не записувати.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Для кожної версії відбиток джерела перераховується З БАЗИ тим самим алгоритмом, що й в інструменті
    /// (<c>Fingerprint</c>); не збігся — відмова для цієї версії. Текст мусить компілюватися без діагностик, разом із
    /// перевірками оточення. Версії обробляються незалежно: відповідь перелічує статус кожної
    /// (<c>applied</c>, <c>wouldApply</c>, <c>refused</c> з ключем причини). Рушій версії не змінюється.
    /// </remarks>
    [HttpPost("apply")]
    [ProducesResponseType<ApplyScriptPackageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApplyScriptPackageResponse>> Apply(
        [FromBody] ApplyScriptPackageRequest request, [FromQuery] bool dryRun, CancellationToken ct)
        => Ok(await apply.HandleAsync(request, dryRun, ct).ConfigureAwait(false));

    /// <summary>
    /// Перемикає рушій групи опублікованих версій однією транзакцією. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <param name="request">Версії, рушій (<c>Script</c> чи <c>Formulas</c>), причина.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// На <c>Script</c>: у кожної версії є текст, що компілюється без діагностик, а склад, виведений зі скрипта
    /// (виходи з одиницею й ознакою «по речовинах», речовини, імпорти), збігається з наявним — інакше
    /// <c>422 switchChecksFailed</c> з переліком розбіжностей; бібліотека й усі її імпортери — в одному запиті
    /// (<c>422 switchGroupIncomplete</c> називає відсутні версії). Тоді записуються таблиці читання й залежності.
    /// На <c>Formulas</c>: просте перемикання, таблиці читання знімаються. Старі формули не видаляються.
    /// </remarks>
    [HttpPost("switch")]
    [ProducesResponseType<SwitchScriptEngineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwitchScriptEngineResponse>> Switch(
        [FromBody] SwitchScriptEngineRequest request, CancellationToken ct)
        => Ok(await switchEngine.HandleAsync(request, ct).ConfigureAwait(false));
}
