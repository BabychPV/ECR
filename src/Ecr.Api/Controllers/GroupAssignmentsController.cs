using Ecr.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Ролі, призначені групам каталогу (ФВ-6.15). Право <c>Security.ManageUsers</c>.</summary>
[ApiController]
[Route("api/v1/security/group-assignments")]
[Authorize]
public sealed class GroupAssignmentsController(
    ListGroupRoleAssignmentsHandler list,
    AssignGroupRoleHandler assign,
    RevokeGroupRoleHandler revoke) : ControllerBase
{
    /// <summary>Усі групові призначення з резолвленими іменами груп.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<GroupRoleAssignmentView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Призначає роль групі за SID або іменем <c>ДОМЕН\Група</c>.</summary>
    /// <remarks>
    /// ⛔ Роль із небезпечними правами — лише з <c>confirmDangerous: true</c>,
    /// інакше <c>409 ECR-SEC-0409</c> з переліком у <c>details</c>. Дублікат —
    /// той самий код; ім'я, що не резолвиться, — <c>422 ECR-REQ-0422</c>.
    ///
    /// ⚠ <c>effectiveAfterNextSignIn: true</c> — на цей SID раніше не було
    /// призначень, тож у cookie вже залогінених членів групи його немає (`#419`),
    /// і роль подіє для них з наступного входу. Відкликання діє негайно.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<GroupRoleAssignedResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Assign([FromBody] AssignGroupRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await assign
            .HandleAsync(
                request.RoleId, request.Principal, request.ValidFrom, request.ValidTo,
                request.ConfirmDangerous ?? false, ct)
            .ConfigureAwait(false);

        return Created(new Uri("/api/v1/security/group-assignments", UriKind.Relative), result);
    }

    /// <summary>Відкликає роль у групи; діє на вже залогінених негайно.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        await revoke.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }
}

/// <summary>Тіло призначення ролі групі.</summary>
/// <param name="RoleId">Роль.</param>
/// <param name="Principal">SID (<c>S-1-…</c>) або ім'я <c>ДОМЕН\Група</c>.</param>
/// <param name="ValidFrom">Початок дії; <c>null</c> — від завжди.</param>
/// <param name="ValidTo">Кінець дії (включно); <c>null</c> — безстроково.</param>
/// <param name="ConfirmDangerous">Підтвердження видачі ролі з небезпечними правами.</param>
public sealed record AssignGroupRoleRequest(
    int RoleId, string Principal, DateOnly? ValidFrom = null, DateOnly? ValidTo = null, bool? ConfirmDangerous = null);
