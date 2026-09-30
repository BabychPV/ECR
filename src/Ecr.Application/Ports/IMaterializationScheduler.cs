// src/Ecr.Application/Ports/IMaterializationScheduler.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Постановка перенесення зібраних точок PI у комірки (<c>D-118</c>) з місця
/// ПЕРЕХОДУ періоду — див. <see cref="PeriodMaterializationTrigger"/>.
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
    /// таблиці» заданих періодів, де сутність має матеріалізований мапінг на
    /// колонку таблиці екземпляра.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKeys">
    /// Ключі періодів, чий перехід щойно закомічено і для яких
    /// <see cref="PeriodMaterializationTrigger.Requires"/> — так; порожньо — нічого не робить.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task EnqueueAfterTransitionAsync(int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);
}

/// <summary>Які переходи періоду потребують задачі матеріалізації.</summary>
/// <remarks>
/// ⛔ Одне правило на всі місця переходу (<c>PeriodStateJob</c>, активація
/// проєкту, перевідкриття періоду): копія в кожному розійшлася б першою ж
/// правкою, і одне місце ставило б задачі, яких інше не ставить.
/// </remarks>
public static class PeriodMaterializationTrigger
{
    /// <summary>
    /// Чи перехід <paramref name="before"/> → <paramref name="after"/> потребує задачі матеріалізації.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Scheduled → Open/Grace</c> — відкриття: точки, зібрані до нього,
    /// записуються. ⚠ <c>Grace</c> — теж відкриття: після простою задача
    /// станів проходить <c>Scheduled → Open → Grace</c> за один прогін.</item>
    /// <item><c>Scheduled → … → Closed</c> — період закрився, так і не побувши
    /// відкритим між прогонами. Задача нічого не запише, але лишить
    /// <c>SkippedPeriodClosed</c> у журналі покриття — пропуск видно
    /// адміністратору, а не мовчки.</item>
    /// <item><c>Closed → Grace/Open</c> — перевідкриття: підхоплює точки,
    /// раніше пропущені як <c>SkippedPeriodClosed</c>.</item>
    /// </list>
    /// Решта (<c>Open → Grace</c>, <c>Open/Grace → Closed</c>) — ні: період
    /// побував відкритим, і збір уже матеріалізував точки за перетином вікна.
    /// </remarks>
    public static bool Requires(PeriodState before, PeriodState after)
        => (before == PeriodState.Scheduled && after is PeriodState.Open or PeriodState.Grace or PeriodState.Closed)
           || (before == PeriodState.Closed && after is PeriodState.Open or PeriodState.Grace);
}
