// src/Ecr.Application/Integration/CollectionScheduleApplier.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Integration;

/// <summary>
/// Доводить стан розкладу збору з бази до планувальника.
/// </summary>
/// <remarks>
/// ⛔ Єдине місце, яке знає, ЯКИМ payload збір ставиться за розкладом. Ключ
/// періодичної задачі — тип плюс payload, тож старт застосунку і майбутнє
/// редагування розкладу мусять складати його однаково: інакше правка cron
/// ставила б ДРУГУ задачу поруч зі стартовою, а вимкнення не знімало б жодної.
/// <para>
/// ⚠ Викликати ПІСЛЯ збереження розкладу: до цього класу розклади читалися з
/// бази рівно раз, на старті, і правка не доходила до планувальника до
/// перезапуску (ФВ-14.3).
/// </para>
/// </remarks>
public sealed class CollectionScheduleApplier(IBackgroundJobScheduler scheduler)
{
    /// <summary>Payload збору за розкладом: уся сутність, без вікна.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    public static CollectionTask PayloadOf(int sourceEntityId) => new(sourceEntityId, null, null);

    /// <summary>Увімкнений розклад ставить (замінюючи попередній cron), вимкнений — знімає.</summary>
    /// <param name="schedule">Розклад у вже збереженому стані.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Cron увімкненого розкладу невалідний.</exception>
    public async Task ApplyAsync(CollectionSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var payload = PayloadOf(schedule.SourceEntityId);

        if (schedule.IsEnabled)
        {
            await scheduler
                .ScheduleAsync<ICollectionJob>(schedule.CronExpression, payload, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await scheduler.UnscheduleAsync<ICollectionJob>(payload, ct).ConfigureAwait(false);
        }
    }
}
