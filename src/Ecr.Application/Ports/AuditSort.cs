namespace Ecr.Application.Ports;

/// <summary>
/// Порядок сторінок журналів аудиту (вкладки Cell changes / Structure changes / Security events / Workflow events).
/// </summary>
/// <remarks>
/// ⛔ Сортування — НА СЕРВЕРІ: сортувати на клієнті одну сторінку означало б брехню між сторінками. Кожен порядок
/// має курсорний ключ, що завершується <c>Id</c> (стабільність за однакових моментів), і курсор несе порядок:
/// курсор чужого порядку відхиляється <c>422 err.ECR-REQ-0422.auditCursorSortMismatch</c>, а не читається мовчки.
///
/// ⚠ Ключ «За користувачем» — відображуване ім'я автора (<c>sec.User.DisplayName</c>, відсутній автор — порожній
/// рядок), далі <c>UserId</c>, далі момент і <c>Id</c>: детермінований і для двох людей з однаковим іменем.
/// </remarks>
public enum AuditSort
{
    /// <summary>Спершу найновіші: <c>(ChangedAt DESC, Id DESC)</c> — типовий порядок.</summary>
    ChangedAtDesc = 0,

    /// <summary>Спершу найстаріші: <c>(ChangedAt, Id)</c>.</summary>
    ChangedAtAsc = 1,

    /// <summary>За ім'ям автора А→Я: <c>(ім'я, UserId, ChangedAt, Id)</c>.</summary>
    UserAsc = 2,

    /// <summary>За ім'ям автора Я→А: усі складники за спаданням.</summary>
    UserDesc = 3,
}
