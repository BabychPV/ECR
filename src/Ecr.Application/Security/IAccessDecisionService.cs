// src/Ecr.Application/Security/IAccessDecisionService.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
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
}
