// src/Ecr.Application/Documents/ApproverEditRule.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ D-442/D-445: хто може змінювати значення ПОДАНОГО аркуша — лише погоджувач поточного кроку маршруту.
/// </summary>
/// <remarks>
/// ⛔ Одне правило на запис і на підказку клієнту. Запис (<c>PatchCellsHandler</c>, сітка й книга Excel) перечитує
/// його під блокуванням аркуша; ендпоінт <see cref="GetApproverEditAvailabilityHandler"/> віддає те саме рішення
/// клієнту, щоб погоджувач ІНШОГО кроку не бачив «Імпорт…» і редаговної сітки там, де сервер однаково відмовить.
/// Друга копія предиката на клієнті чи в іншому обробнику розійшлася б із цією мовчки.
/// <para>
/// ⚠ Правило — <see cref="IAccessDecisionService.CanApproveAsync"/>: видимість, аркуш у стані <c>Submitted</c>,
/// роль ПОТОЧНОГО кроку маршруту, грант <c>Approve</c>, не симуляція й не інтеграційний запис. Той самий
/// <c>EditRules.CanApprove</c> дає рішенням зрізу позначку <c>ByApprover</c> (D-442), тож три місця — рішення
/// комірок, повторна перевірка під блокуванням і ця підказка — відповідають однаково.
/// </para>
/// </remarks>
public static class ApproverEditRule
{
    /// <summary>Чи користувач саме зараз — погоджувач поточного кроку цього поданого аркуша.</summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — правку значень поданого аркуша сервер пропустить (решту правил комірки — окремо).</returns>
    public static async Task<bool> IsCurrentStepApproverAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var decision = await access
            .CanApproveAsync(profile, documentId, sheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        return decision.IsAllowed;
    }
}

/// <summary>Чи може поточний користувач змінювати значення поданого аркуша (D-442/D-445).</summary>
/// <param name="CanApproverEdit">
/// <c>true</c> — аркуш поданий, і користувач — погоджувач ПОТОЧНОГО кроку маршруту: сітка редаговна, імпорт Excel
/// доступний. <c>false</c> — аркуш не поданий або черга не його (автор, погоджувач іншого кроку). Лише підказка
/// інтерфейсу: запис перевіряє те саме правило під блокуванням аркуша.
/// </param>
public sealed record ApproverEditAvailabilityDto(bool CanApproverEdit);

/// <summary>
/// ✎ борг D-445: підказка клієнту «чи пропустить сервер правку поданого аркуша цим користувачем».
/// </summary>
/// <remarks>
/// ⚠ Окремий маршрут, а не поле <c>DocumentSummary</c>/<c>DocumentTableDto</c>: перше — спільне з переліком
/// документів, друге — СТРУКТУРА, яку екран читає раз на відкриття. «Чия черга» змінюється з кожним погодженням
/// кроку, а ключ запиту клієнта лежить під префіксом документа × періоду, який скидає кожна дія робочого
/// процесу — та сама схема, що в <c>GET …/recall</c>.
/// </remarks>
public sealed class GetApproverEditAvailabilityHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Рішення для аркуша документа за період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<ApproverEditAvailabilityDto> HandleAsync(
        long documentId, int sheetDefId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Невидимий документ — 404, як і `GET /documents/{id}` (B-08): відповідь «false» розкривала б, що він є.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        return new ApproverEditAvailabilityDto(
            await ApproverEditRule
                .IsCurrentStepApproverAsync(access, profile, documentId, sheetDefId, PeriodKey.Parse(periodKey), ct)
                .ConfigureAwait(false));
    }
}
