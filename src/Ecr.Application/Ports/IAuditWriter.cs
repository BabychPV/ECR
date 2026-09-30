using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис аудиту. Пакетний **навмисно**: окремий <c>INSERT</c> на кожну комірку
/// не вкладається в бюджет збереження діапазону (300 мс на 100 комірок).
/// </summary>
/// <remarks>
/// ⛔ C4. Два різновиди подій, і різниця між ними — не стиль, а правда журналу:
/// <list type="bullet">
/// <item><b>Результатна</b> подія («роль перейменовано», «версію
/// опубліковано») описує зміну і мусить жити РІВНО стільки, скільки вона:
/// усі методи, крім <see cref="WriteIndependentSecurityEventAsync"/>,
/// приєднуються до поточної транзакції. Тож викликач пише подію і зберігає
/// зміну всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>:
/// поза транзакцією <c>INSERT</c> автокомітиться одразу, і відкат зміни лишає
/// в журналі подію, якої не сталося (а запис аудиту ПІСЛЯ коміту — зміну без
/// сліду, якщо впав аудит).</item>
/// <item><b>Спроба</b> (доступ до даних, відмова в доступі) — факт незалежно
/// від того, чим скінчився запит: лише
/// <see cref="WriteIndependentSecurityEventAsync"/>.</item>
/// </list>
/// </remarks>
public interface IAuditWriter
{
    /// <summary>Записує зміни комірок однією операцією, у тій самій транзакції.</summary>
    public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct);

    /// <summary>Записує структурну зміну.</summary>
    public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct);

    /// <summary>Записує подію безпеки.</summary>
    public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct);

    /// <summary>
    /// Записує групу подій безпеки за мінімум походів до сервера, у тій самій
    /// транзакції. Для одиночної події лишається <see cref="WriteSecurityEventAsync"/>
    /// — цей метод не замінює його, а додається поруч для викликачів, які й так
    /// збирають список (наприклад, пер-рядковий слід імпорту CSV довідника).
    /// </summary>
    public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct);

    /// <summary>
    /// Записує подію-СПРОБУ безпеки, яка лишається в журналі навіть тоді, коли
    /// транзакцію навколо відкочено (C4).
    /// </summary>
    /// <remarks>
    /// ⚠ Не для результатних подій: подія зміни, записана так, переживе
    /// відкат самої зміни — рівно той дефект, від якого цей метод відділено.
    /// </remarks>
    public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct);

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
/// <param name="IsOutOfWindow">
/// Правка дозволена політикою <c>Warn</c> («дозволити з позначкою») поза вікном
/// доступу до періоду (<c>ФВ-2.16</c>, <c>D-239</c>). Не плутати з
/// <paramref name="IsLateEdit"/>.
/// </param>
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
    string? CorrelationId,
    bool IsOutOfWindow = false);

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
