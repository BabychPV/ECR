// src/Ecr.Application/Calculations/MethodologyVersionScope.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
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

    /// <summary>
    /// Відмовляє, якщо версія керується скриптом: її формули, константи, категорії, правила, речовини й виходи
    /// виводяться з тексту, і ручна правка розійшлася б із ним (MS, план 5.1).
    /// </summary>
    /// <param name="methodologyVersionId">Версія з маршруту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0409</c>, ключ <c>scriptManaged</c> — версія має рушій <c>Script</c>.
    /// </exception>
    /// <remarks>
    /// ⚠ Викликається ПІСЛЯ <see cref="RequireAsync"/>: версія вже знайдена й належить методології з адреси.
    /// Тести, обов'язкові колонки, режими й прив'язки лишаються доступними — вони не виводяться зі скрипта.
    /// </remarks>
    public async Task RequireHandEditableAsync(int methodologyVersionId, CancellationToken ct)
    {
        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false);

        if (version is { Engine: MethodologyEngine.Script })
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Версія {version.Version} керується скриптом: формули, константи, категорії, правила, речовини й "
                + "виходи редагуються в тексті скрипта, а не окремо.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.scriptManaged",
                    ["version"] = version.Version,
                });
        }
    }
}
