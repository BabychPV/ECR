using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Listener;

namespace Ecr.TestKit;

/// <summary>
/// Слухач Quartz для тестових застосунків: розклади (cron) СТОЯТЬ у планувальнику,
/// але не спрацьовують. Разові постановки й явний запуск задачі виконуються як завжди.
/// </summary>
/// <remarks>
/// ⛔ Чому це потрібно. Тестовий застосунок (<c>WebApplicationFactory&lt;Program&gt;</c>) піднімає
/// справжній Quartz і справжній <c>RecurringScheduleService</c>, тобто ставить ті самі розклади, що й
/// прод: <c>NotificationJob</c>/<c>PeriodStateJob</c>/<c>RowWindowRefetchJob</c> о hh:05, нічні
/// перевірки о 02:15, збір за <c>ext.CollectionSchedule</c>. Хост, що прожив межу hh:05, виконував
/// справжню задачу проти СПІЛЬНОЇ бази колекції — і її наслідки залежали від годинника, а не від
/// тесту. 23.10 (прогін 40007519316, api-2) щогодинний <c>NotificationJob</c> звів чужі збої в
/// зведення failures = 2, sent = 0, і <c>/health/ready</c> пожовтів для тестів, що йшли після
/// 19:05.
/// <para>
/// ⚠ Чому вето, а не вимкнення розкладів чи <c>Standby</c>. (1) <c>JobsHealthCheck</c> вимагає
/// ЗАПУЩЕНИЙ планувальник і хоч один розклад — без них <c>/health/ready</c> не зелений, тож
/// тести «зелений» перевіряли б уже не той стан, що в проді. (2) Разові задачі (перерахунок,
/// імпорт, «перевірити зараз») ідуть простими тригерами того ж планувальника — <c>Standby</c>
/// зупинив би і їх. (3) Постановка розкладів лишається під тестом: тригери, їхні cron і ключі
/// можна читати з планувальника як і раніше. Вето бере лише <see cref="ICronTrigger"/>:
/// <c>IScheduler.TriggerJob</c>, постановки в чергу й перезапуски — прості тригери, їх не чіпає.
/// </para>
/// <para>
/// ⚠ Лише тестовий код. У продукті ключа «вимкнути розклади» немає свідомо: він міг би тихо
/// вимкнути нічні перевірки на об'єкті замовника.
/// </para>
/// </remarks>
public sealed class RecurringTriggerVeto : TriggerListenerSupport
{
    /// <summary>Ім'я слухача в <c>ListenerManager</c>.</summary>
    public const string ListenerName = "ecr.test.recurring-trigger-veto";

    /// <summary>Ключі cron-тригерів, чиє спрацювання відхилено (по одному запису на спрацювання).</summary>
    public ConcurrentQueue<TriggerKey> Vetoed { get; } = new();

    /// <inheritdoc />
    public override string Name => ListenerName;

    /// <inheritdoc />
    public override Task<bool> VetoJobExecution(
        ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (trigger is not ICronTrigger)
        {
            return Task.FromResult(false);
        }

        Vetoed.Enqueue(trigger.Key);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Реєструє вето в контейнері застосунку: <c>ServiceCollectionSchedulerFactory</c> додає кожен
    /// <see cref="ITriggerListener"/> із DI до планувальника при його створенні (без матчерів — на
    /// всі тригери).
    /// </summary>
    /// <param name="services">Сервіси тестового застосунку (<c>ConfigureTestServices</c>).</param>
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<RecurringTriggerVeto>();
        services.AddSingleton<ITriggerListener>(sp => sp.GetRequiredService<RecurringTriggerVeto>());
    }
}
