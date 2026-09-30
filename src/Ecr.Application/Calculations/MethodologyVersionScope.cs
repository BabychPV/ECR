// src/Ecr.Application/Calculations/MethodologyVersionScope.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Calculations;

/// <summary>
/// Межа маршруту <c>/methodologies/{id}/versions/{vid}/…</c>: версія мусить
/// належати методології з адреси (B-07, UX-прохід, четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Обробники версії приймають лише <c>vid</c>, і <c>{id}</c> маршруту не
/// звірявся ніде: <c>GET /methodologies/999999999/versions/4/formulas</c>
/// віддавав <c>200</c> із формулами ЧУЖОЇ методології, а публікація, видалення
/// формули й запис константи так само діяли через будь-яку адресу. Звірка
/// одна на всі маршрути, тут, а не копією в кожному з чотирнадцяти обробників.
///
/// ⚠ Порядок — ПРАВО, потім належність. Інакше користувач без права
/// розрізняв би «такої пари немає» (404) і «є, але не можна» (403), тобто
/// дізнавався б про версії, яких бачити не має.
/// </remarks>
public sealed class MethodologyVersionScope(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Вимагає право і належність версії методології.</summary>
    /// <param name="methodologyId">Методологія з маршруту.</param>
    /// <param name="methodologyVersionId">Версія з маршруту або тіла.</param>
    /// <param name="permission">Право дії, яку далі виконає обробник.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Немає права.</exception>
    /// <exception cref="NotFoundException">
    /// <c>ECR-CALC-0404</c> — версії немає або вона належить іншій методології.
    /// </exception>
    public async Task RequireAsync(
        int methodologyId, int methodologyVersionId, string permission, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false);

        // ⚠ Відповідь та сама, що й на неіснуючу версію: чужа версія з
        // погляду цієї адреси не існує.
        if (version is null || version.MethodologyId != methodologyId)
        {
            throw new NotFoundException(
                ErrorCodes.MethodologyVersionNotFound,
                $"Версії {methodologyVersionId} у методології {methodologyId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.version",
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                    ["methodologyId"] = methodologyId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }
}
