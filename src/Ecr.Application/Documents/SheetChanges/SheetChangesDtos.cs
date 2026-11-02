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
/// <param name="SheetStatus">✎ LIVE-STATUS: поточний стан погодження аркуша за період (<c>Draft</c>, якщо подань не було).</param>
/// <param name="StatusRevision">
/// Ревізія стану погодження (непрозора, hex): змінюється на КОЖНОМУ переході — подання, відхилення, затвердження й
/// крок маршруту (стан той самий, кнопки — інші), повернення в роботу, відкликання. Інша ревізія — перечитати стан
/// аркуша (банер, кнопки дій); дані комірок від цього не змінюються.
/// </param>
/// <param name="StatusChanged">
/// Після <c>since</c> стан погодження аркуша змінився (будь-ким, і самим викликачем теж — сторінка іншої вкладки має
/// оновитися). Окремо від <c>changed</c> (дані): клієнт розрізняє зміну стану й зміну комірок.
/// </param>
public sealed record SheetChangesDto(
    string Marker,
    bool Changed,
    int? LastChangedByUserId,
    string? LastChangedBy,
    DateTime? LastChangedAt,
    IReadOnlyList<SheetChangedTableDto> ChangedTables,
    Ecr.Domain.Enums.DocumentStatus SheetStatus,
    string StatusRevision,
    bool StatusChanged)
{
    /// <summary>
    /// <c>ETag</c> відповіді — з усього, ОКРІМ маркера (маркер несе глобальну межу <c>rowversion</c> і момент видачі,
    /// тож змінюється між опитуваннями й без жодних змін аркуша). Той самий стан і ті самі зміни — той самий <c>ETag</c>
    /// і <c>304</c> на <c>If-None-Match</c>; будь-який перехід погодження міняє ревізію, отже й <c>ETag</c>.
    /// </summary>
    public string ETag()
    {
        var canonical = string.Join(
            "|",
            Changed ? Marker : "-",
            StatusRevision,
            ((int)SheetStatus).ToString(System.Globalization.CultureInfo.InvariantCulture),
            StatusChanged ? "1" : "0");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return "\"" + Convert.ToHexStringLower(hash.AsSpan(0, 12)) + "\"";
    }
}
