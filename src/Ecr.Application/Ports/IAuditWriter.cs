using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис аудиту. Пакетний **навмисно**: окремий <c>INSERT</c> на кожну комірку
/// не вкладається в бюджет збереження діапазону (300 мс на 100 комірок).
/// </summary>
public interface IAuditWriter
{
    /// <summary>Записує зміни комірок однією операцією, у тій самій транзакції.</summary>
    public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct);

    /// <summary>Записує структурну зміну.</summary>
    public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct);

    /// <summary>Записує подію безпеки.</summary>
    public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct);

    /// <summary>Записує подію публікації з diff <b>результатів</b>, а не коду (ФВ-9.6).</summary>
    public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct);
}

/// <summary>Зміна комірки для аудиту.</summary>
/// <param name="ChangedAt">Момент зміни в UTC — партиційний ключ аудиту.</param>
/// <param name="Address">Адреса комірки.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="RowKey">Ключ рядка — щоб аудит читався без join.</param>
/// <param name="OldValue">Старе значення в текстовому вигляді.</param>
/// <param name="NewValue">Нове значення.</param>
/// <param name="ChangedByUserId">Автор (<b>не SID</b>, R-A2).</param>
/// <param name="Origin">UserEdit | Import | Recalculation | Migration.</param>
/// <param name="IsLateEdit">Зміна в <c>Grace</c> або після <c>Reopen</c> (D-70).</param>
/// <param name="CorrelationId">Наскрізний ідентифікатор запиту.</param>
public sealed record CellChangeRecord(
    DateTime ChangedAt,
    CellAddress Address,
    long DocumentId,
    string RowKey,
    string? OldValue,
    string? NewValue,
    int ChangedByUserId,
    string Origin,
    bool IsLateEdit,
    string? CorrelationId);

/// <summary>Структурна зміна метаданих.</summary>
public sealed record StructureChangeRecord(
    DateTime ChangedAt, int TemplateVersionId, string EntityType, int EntityId,
    Domain.Enums.ChangeClass ChangeClass, string Operation,
    string? OldJson, string? NewJson, string? ChangeReason, int ChangedByUserId, string? CorrelationId);

/// <summary>Подія безпеки.</summary>
public sealed record SecurityEventRecord(
    DateTime ChangedAt, string EventType, int? TargetUserId, int? TargetRoleId,
    string? DetailsJson, int ChangedByUserId, string? CorrelationId);

/// <summary>Публікація версії шаблону або методології.</summary>
public sealed record PublicationEventRecord(
    DateTime ChangedAt, string EntityType, int EntityId,
    string? ResultDiffJson, string ChangeReason, int ChangedByUserId);
