// src/Ecr.Application/Security/IAccessDecisionService.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
/// <summary>Крок маршруту погодження, якого чекає аркуш.</summary>
/// <param name="StepId">Ідентифікатор кроку.</param>
/// <param name="Ordinal">Порядковий номер кроку в маршруті, від 1.</param>
/// <param name="RoleId">Роль, яка затверджує на цьому кроці.</param>
/// <param name="NextStepId">Наступний крок; <c>null</c> — цей останній.</param>
/// <param name="TotalSteps">Скільки кроків у маршруті — для підпису «крок 2 з 3».</param>
public sealed record ApprovalStepView(
    int StepId, int Ordinal, int RoleId, int? NextStepId, int TotalSteps);

public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Крок маршруту погодження, якого чекає аркуш (<c>ФВ-5.17</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Повертає <c>null</c>, коли маршруту немає — і це нормальний,
    /// найчастіший стан: затвердження одноетапне, як було до маршрутів.
    /// </remarks>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalStepView?> CurrentApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
