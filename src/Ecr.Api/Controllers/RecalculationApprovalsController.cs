using Ecr.Application.Calculations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Погодження перерахунку закритого періоду (ФВ-9.7, аудит безпеки S1). Право
/// <c>Calculation.Recalculate</c>; підтвердження — ще й грант Manage на проєкт.
/// </summary>
/// <remarks>
/// ⛔ Хто підтверджує, бере обробник із сесії, а не з тіла: ініціатор не може
/// вписати «другу людину» сам. Самопідтвердження — <c>409 ECR-CALC-0409</c>.
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:int}/recalculation-approvals")]
[Authorize]
public sealed class RecalculationApprovalsController(RecalculationApprovalHandlers approvals) : ControllerBase
{
    /// <summary>Живі погодження проєкту: не використані й не прострочені.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RecalculationApprovalDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RecalculationApprovalDto>>> List(int projectId, CancellationToken ct)
        => Ok(await approvals.ListAsync(projectId, ct).ConfigureAwait(false));

    /// <summary>Створює запит на погодження від імені поточного користувача.</summary>
    [HttpPost]
    [ProducesResponseType<RecalculationApprovalDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RecalculationApprovalDto>> Create(
        int projectId, [FromBody] RecalculationApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await approvals.RequestAsync(projectId, request.PeriodKey, request.Reason, ct).ConfigureAwait(false);
        return Created(new Uri($"/api/v1/projects/{projectId}/recalculation-approvals", UriKind.Relative), created);
    }

    /// <summary>Підтверджує чужий запит під сесією поточного користувача.</summary>
    [HttpPost("{id:long}/confirm")]
    [ProducesResponseType<RecalculationApprovalDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecalculationApprovalDto>> Confirm(int projectId, long id, CancellationToken ct)
        => Ok(await approvals.ConfirmAsync(projectId, id, ct).ConfigureAwait(false));
}

/// <summary>Запит на погодження перерахунку закритого періоду.</summary>
/// <param name="PeriodKey">Закритий період, який треба перерахувати.</param>
/// <param name="Reason">Причина; обов'язкова, потрапляє в журнал.</param>
public sealed record RecalculationApprovalRequest(int PeriodKey, string? Reason);
