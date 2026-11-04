using Ecr.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// ✎ D-461: «Дозволити ручне значення» на формулі шаблону — і в опублікованій версії, без нової версії.
/// Право <c>Template.Edit</c>.
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер, а не ще один метод <c>TemplateVersionsController</c>: налаштування адресується Id формули
/// (його віддає <c>GET …/structure</c> — <c>formulaId</c> колонки, рядка й комірочних формул), а не ціллю, як авторство
/// виразу в чернетці.
/// </remarks>
[ApiController]
[Route("api/v1/template-versions/{id:int}/formulas/{formulaId:int}/manual-override")]
[Authorize]
public sealed class FormulaManualOverrideController(FormulaManualOverrideHandler handler) : ControllerBase
{
    /// <summary>
    /// Вмикає/вимикає ручне значення замість формули; <c>dryRun</c> — лише лічильник наявних ручних значень
    /// (для попередження перед вимкненням).
    /// </summary>
    /// <param name="id">Версія шаблону (будь-який стан).</param>
    /// <param name="formulaId">Формула версії.</param>
    /// <param name="request">Новий стан і <c>dryRun</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <c>403</c> — немає <c>Template.Edit</c>; <c>404 ECR-TMPL-0404</c> — у версії немає такої (живої) формули.
    /// Вимкнення наявні ручні значення не стирає — лише забороняє нові.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType<FormulaManualOverrideResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FormulaManualOverrideResult>> Set(
        int id, int formulaId, [FromBody] SetFormulaManualOverrideRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await handler
            .HandleAsync(id, formulaId, request.Allow, request.DryRun, ct)
            .ConfigureAwait(false));
    }
}

/// <summary>✎ D-461: тіло <c>PUT …/formulas/{formulaId}/manual-override</c>.</summary>
/// <param name="Allow">Дозволити ручне значення.</param>
/// <param name="DryRun"><c>true</c> — нічого не змінювати, лише порахувати наявні ручні значення.</param>
public sealed record SetFormulaManualOverrideRequest(bool Allow, bool DryRun = false);
