// src/Ecr.Application/Security/IAccessDecisionService.cs
namespace Ecr.Application.Security;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
