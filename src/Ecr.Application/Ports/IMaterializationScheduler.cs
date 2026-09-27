// src/Ecr.Application/Ports/IMaterializationScheduler.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Постановка перенесення зібраних точок PI у комірки (<c>D-118</c>) з місця
/// ПЕРЕХОДУ періоду <c>Scheduled → Open</c> (або одразу в <c>Grace</c>).
/// </summary>
/// <remarks>
/// ⛔ Навіщо окремий шлях, коли матеріалізацію й так ставить збір. Для
/// <c>Scheduled</c> матеріалізація законно нічого не пише — точки чекають
/// відкриття. Якщо ж розклад збору вимкнено або він ходить рідше, ніж період
/// проходить <c>Open → Closed</c>, відкриття збір не побачить НІКОЛИ: точки,
/// зібрані до відкриття, лишаються сирими назавжди, а журнал покриття мовчить.
/// Сам перехід — єдина мить, яка про відкриття знає напевно.
/// <para>
/// ⛔ Викликати лише ПІСЛЯ коміту переходу. Черга не транзакційна (Quartz у
/// пам'яті, <c>D-66</c>): задача, поставлена до коміту, могла б стартувати,
/// поки період ще <c>Scheduled</c> (і нічого не записати), або пережити відкат
/// переходу. Реалізація відмовляє, якщо транзакція на контексті ще відкрита.
/// </para>
/// </remarks>
public interface IMaterializationScheduler
{
    /// <summary>
    /// Ставить задачу матеріалізації кожній парі «сутність джерела + екземпляр
    /// таблиці» відкритих періодів, де сутність має матеріалізований мапінг на
    /// колонку таблиці екземпляра.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKeys">Ключі періодів, що щойно відкрилися; порожньо — нічого не робить.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task EnqueueForOpenedPeriodsAsync(int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);
}

/// <summary>Що вважається «відкриттям» періоду для матеріалізації.</summary>
public static class PeriodOpening
{
    /// <summary>
    /// Чи перехід <paramref name="before"/> → <paramref name="after"/> відкрив період.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Grace</c> — теж відкриття: після простою задача станів проходить
    /// <c>Scheduled → Open → Grace</c> за один прогін, і в період усе ще можна
    /// писати. <c>Scheduled → … → Closed</c> — ні: період закрився, так і не
    /// побувши відкритим між прогонами, і задача матеріалізації лише записала б
    /// «пізній збір лишається сирим».
    /// </remarks>
    public static bool Opened(PeriodState before, PeriodState after)
        => before == PeriodState.Scheduled && after is PeriodState.Open or PeriodState.Grace;
}
