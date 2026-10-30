// src/Ecr.Application/Documents/SheetChanges/SheetChangesDtos.cs
namespace Ecr.Application.Documents.SheetChanges;

/// <summary>Таблиця аркуша, дані якої змінив хтось інший після маркера.</summary>
/// <param name="TableCode">Код таблиці (стабільний між версіями шаблону).</param>
/// <param name="TableInstanceId">Екземпляр таблиці за період — той, за яким клієнт читає зріз.</param>
public sealed record SheetChangedTableDto(string TableCode, long TableInstanceId);

/// <summary>Відповідь опитування змін аркуша (живе оновлення сітки).</summary>
/// <param name="Marker">
/// Непрозорий маркер; наступне опитування передає його в <c>since</c>. Без <c>since</c> (перше опитування) — лише
/// маркер, <c>changed = false</c>.
/// </param>
/// <param name="Changed">
/// Після <c>since</c> дані видимих таблиць аркуша змінив ХТОСЬ ІНШИЙ (людина, імпорт, інтеграція, дозвіл; не сам
/// викликач і не перерахунок як такий). Власні записи викликача маркер просто пересувають.
/// </param>
/// <param name="LastChangedByUserId">Автор найсвіжішої чужої зміни; <c>null</c> — невідомий (зміна без журналу значень).</param>
/// <param name="LastChangedBy">Його ім'я для відображення; <c>null</c> — невідомий чи системний запис.</param>
/// <param name="LastChangedAt">Час найсвіжішої чужої зміни (UTC); <c>null</c> — невідомий.</param>
/// <param name="ChangedTables">
/// Видимі таблиці з чужими змінами (і перерахованими слідом за ними); порожньо, коли <c>changed = false</c>.
/// Схована від викликача таблиця сюди не потрапляє ніколи.
/// </param>
public sealed record SheetChangesDto(
    string Marker,
    bool Changed,
    int? LastChangedByUserId,
    string? LastChangedBy,
    DateTime? LastChangedAt,
    IReadOnlyList<SheetChangedTableDto> ChangedTables);
