using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Збір даних із зовнішнього джерела за розкладом.
/// </summary>
/// <remarks>
/// Простій джерела має бути **затримкою, а не втратою** (ФВ-11.3):
/// обслуговування PI AF відбуватиметься незалежно від нашої згоди. Тому
/// відмова джерела не робить задачу невдалою — діапазон іде в catch-up.
/// <para>
/// ⛔ Виняток один і він наш дефект, а не властивість контуру (<c>H-20</c>):
/// <c>401</c>/<c>403</c>. Такий збір задачу **валить**, нічого не ставить у
/// чергу і надсилає алерт негайно, не чекаючи погодинного зведення. Доти збір
/// із неправильними обліковими даними завершувався успішно з нулем рядків, і
/// відрізнити його від справного джерела, яке просто мовчить, не міг ніхто.
/// </para>
/// </remarks>
public sealed class CollectionJob(
    ICollectionRunner runner,
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    IClock clock,
    INotificationOutbox outbox,
    Integration.OutboxDispatcher dispatcher,
    IRegistrySyncJob registrySync) : ICollectionJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "collection";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = CollectionJobRequest.Parse(payload);
        var schedule = await ScheduleAsync(request.SourceEntityId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;

        // ⛔ Сутність, прив'язана до довідника, — не часовий ряд (ФВ-8.11, S5):
        // її атрибути — поточні значення полів записів, і збирати їх у
        // ext.RawDataPoint з матеріалізацією в комірки означало б записати
        // довідник у документи. Той самий розклад і та сама кнопка «Зібрати»
        // ведуть у синк довідника замість збору.
        if (await IsRegistryBoundAsync(request.SourceEntityId, ct).ConfigureAwait(false))
        {
            await registrySync.ExecuteAsync(request.SourceEntityId, ct).ConfigureAwait(false);

            if (schedule is not null)
            {
                // Прогін фіксується, watermark — ні: у синку довідника немає
                // «зібраного до» моменту.
                await SaveRunAsync(db, schedule, now, watermark: null, ct).ConfigureAwait(false);
            }

            return;
        }
        var to = request.ToUtc ?? now;

        // ⚠ Початок береться з LookbackDays, а НЕ з Watermark. Watermark —
        // оптимізація, а не стан (ER-I-03): перекриття назад закриє проміжок
        // повторно, а природний ключ ext.RawDataPoint не дасть подвоїти точки.
        var from = request.FromUtc ?? to.AddDays(-(schedule?.LookbackDays ?? DefaultLookbackDays));

        await progress
            .ReportKeyAsync(
                5,
                "jobs.collectionRange",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceEntityId"] = request.SourceEntityId.ToString(CultureInfo.InvariantCulture),
                    ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        try
        {
            // ⚠ Ідемпотентність забезпечує збирач: повторний запуск того самого
            // діапазону не дублює даних. Тому задача НЕ перевіряє «а чи вже
            // збирали» — така перевірка була б другим місцем, де живе те саме
            // правило, і розійшлася б із першим.
            await runner
                .RunAsync(request.SourceEntityId, from, to, progress, ct)
                .ConfigureAwait(false);
        }
        catch (SourceAuthenticationException failure)
        {
            await AlertAuthenticationAsync(request.SourceEntityId, failure, ct).ConfigureAwait(false);

            // ⛔ Кидаємо далі. Задача мусить стати `Failed`: `Succeeded` тут
            // означав би, що система вважає роботу зробленою — і наступного
            // разу спробує рівно те саме з тими самими обліковими даними.
            //
            // ⛔ Watermark НЕ рухається і матеріалізація НЕ ставиться: після
            // відмови в автентифікації в черзі не лишається нічого, що
            // повторило б запит.
            throw;
        }

        if (schedule is not null)
        {
            // Watermark рухається лише за успішним прогоном і лише вперед.
            await SaveRunAsync(db, schedule, now, to, ct).ConfigureAwait(false);
        }

        await EnqueueMaterializationAsync(request.SourceEntityId, from, to, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ставить у чергу і НЕГАЙНО надсилає алерт про відмову в автентифікації.
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="failure">Відмова, як її сформулював збирач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Негайно, а не зі зведенням (<c>D-125</c>). Зведення ходить щогодини;
    /// збір, який не автентифікується, не збере нічого й за цю годину, і за
    /// решту ночі. Алерт, що приходить уранці, повідомляє про втрачений
    /// нічний прогін, а не запобігає йому.
    ///
    /// ⚠ Подія однаково лягає в чергу: без запису в <c>itg.NotificationOutbox</c>
    /// ненадісланий лист (пошта лежить, транспорт не налаштований) зник би
    /// безслідно, і «алерт надіслано» означало б лише «ми спробували».
    ///
    /// ⚠ Адресати — <c>null</c>: їх визначає політика розсилки за
    /// <c>sec.User.ReceivesAlerts</c>, як і для решти подій.
    /// </remarks>
    private async Task AlertAuthenticationAsync(
        int sourceEntityId, SourceAuthenticationException failure, CancellationToken ct)
    {
        var code = await db.SourceEntities
            .AsNoTracking()
            .Where(e => e.Id == sourceEntityId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? sourceEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await outbox
            .EnqueueAsync(
                CollectionFailure.AlertEventCode,
                CollectionFailure.AlertSubject(code),
                failure.Message,
                recipients: null,
                ct)
            .ConfigureAwait(false);

        // ⚠ Порт кладе подію в набір змін і НЕ зберігає його сам — саме щоб
        // подія їхала комітом того, що її породило. Тут породжувача-транзакції
        // немає, тому коміт робиться явно, і лише після нього має сенс
        // відправляти.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await dispatcher.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ставить у чергу перенесення зібраних точок у комірки (<c>D-118</c>).
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="from">Початок зібраного інтервалу.</param>
    /// <param name="to">Кінець інтервалу, виключно.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ ОКРЕМА задача, а не продовження цієї. Збір і матеріалізація мають
    /// різну ціну відмови: невдалий збір повторюється безслідно, а невдалий
    /// запис у комірки лишає документ напівзаповненим. Об'єднати їх означало б
    /// повторювати збір щоразу, коли не вдався запис.
    ///
    /// ⚠ Ставиться ПІСЛЯ успішного збору — не при відкритті документа (бюджет
    /// 400 мс) і не при поданні (запізно).
    ///
    /// ⚠ Адресат шукається за мапінгами: одна сутність джерела може живити
    /// кілька таблиць у кількох документах, і кожна пара «документ + таблиця»
    /// отримує власне завдання. Спільне завдання на всі означало б, що збій в
    /// одному документі зупиняє перенесення в решту.
    /// </remarks>
    private async Task EnqueueMaterializationAsync(
        int sourceEntityId, DateTime from, DateTime to, CancellationToken ct)
    {
        // ⛔ D16-03: задача — лише в екземпляри, чий ПЕРІОД перетинає вікно
        // збору. Раніше її отримував кожен екземпляр усіх періодів, і всі вони
        // згортали одне вікно — одне й те саме число в січень і лютий, а
        // `Scheduled`-періоди щопрогону писали хибне «пізній збір лишається
        // сирим». За станом НЕ фільтруємо: закритий період, що перетинає вікно,
        // і далі має отримати `SkippedPeriodClosed`.
        //
        // ⚠ Точна межа — у поясі проєкту (`Period.UtcBounds`), тож у запиті лише
        // грубий фільтр за датами з запасом у добу в обидва боки (пояс ≤ ±14 год),
        // а точний — у пам'яті.
        //
        // ⛔ Період, що ВІДКРИВСЯ поза вікном, тут НЕ добирається (була гілка
        // `StateChangedAt >= LastRunAt`, 8d1929e1). Задачу йому ставить сам
        // перехід — `IMaterializationScheduler` після коміту `PeriodStateJob` і
        // активації проєкту; друга постановка звідси давала б дубль на кожне
        // відкриття, а за вимкненого розкладу не спрацювала б узагалі. Тут
        // лишається постановка за перетином вікна — для точок, зібраних уже
        // під час `Open`.
        var targets = (await MaterializationTargets
                .FindAsync(
                    db,
                    sourceEntityId,
                    projectId: null,
                    periodKeys: null,
                    periodEndNotBefore: DateOnly.FromDateTime(from).AddDays(-1),
                    periodStartNotAfter: DateOnly.FromDateTime(to).AddDays(1),
                    MaxMaterializationTargets,
                    ct)
                .ConfigureAwait(false))
            .Where(t => t.UtcBounds().Overlaps(from, to))
            .ToList();

        await MaterializationTargets.EnqueueAsync(jobs, targets, (from, to), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Стеля завдань перенесення на один прогін збору.
    /// </summary>
    /// <remarks>
    /// Двісті — це вже не «сутність живить кілька таблиць», а наслідок
    /// помилки мапінгу; поставити їх усі означало б забити чергу задачами,
    /// кожна з яких нічого не знайде.
    /// </remarks>
    private const int MaxMaterializationTargets = 200;

    /// <summary>Скільки днів перекривати, коли розкладу немає.</summary>
    /// <remarks>
    /// Сім діб — той самий запас, що й у <c>ext.CollectionSchedule</c> за
    /// замовчуванням. Розбіжність між ручним і плановим збором була б
    /// найгіршою: обидва «працюють», а дані різні.
    /// </remarks>
    private const int DefaultLookbackDays = 7;

    /// <summary>Фіксує успішний прогін на розкладі.</summary>
    /// <remarks>
    /// ⛔ Розклад прочитано ДО збору, а збір триває години: відколи рядок має
    /// <c>rowversion</c>, правка cron посеред збору дала б конфлікт версії — і
    /// успішний збір став би <c>Failed</c>. Тому на конфлікт рядок
    /// перечитується і прогін фіксується поверх чужої правки, не затираючи її.
    /// </remarks>
    public static async Task SaveRunAsync(
        EcrDbContext db,
        Domain.Entities.External.CollectionSchedule schedule,
        DateTime now,
        DateTime? watermark,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schedule);

        schedule.MarkRun(now, watermark);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(schedule).ReloadAsync(ct).ConfigureAwait(false);
            schedule.MarkRun(now, watermark);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Сутність наповнює довідник (<c>ext.SourceEntity.RegistryDefId</c>).</summary>
    private Task<bool> IsRegistryBoundAsync(int sourceEntityId, CancellationToken ct)
        => db.SourceEntities
            .AsNoTracking()
            .AnyAsync(e => e.Id == sourceEntityId && e.RegistryDefId != null, ct);

    /// <summary>Розклад сутності; <c>null</c> — збір запустили руками.</summary>
    private Task<Domain.Entities.External.CollectionSchedule?> ScheduleAsync(
        int sourceEntityId, CancellationToken ct)
        => db.CollectionSchedules
            .FirstOrDefaultAsync(s => s.SourceEntityId == sourceEntityId && s.IsEnabled, ct);
}

/// <summary>Завдання на збір.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="FromUtc">Початок; <c>null</c> — за <c>LookbackDays</c> розкладу.</param>
/// <param name="ToUtc">Кінець; <c>null</c> — «зараз».</param>
public sealed record CollectionJobRequest(int SourceEntityId, DateTime? FromUtc, DateTime? ToUtc)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    /// <param name="payload">Завдання: типізоване або JSON.</param>
    public static CollectionJobRequest Parse(object? payload)
    {
        if (payload is CollectionJobRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        var request = System.Text.Json.JsonSerializer.Deserialize<CollectionJobRequest>(json, Options)
                      ?? throw new InvalidOperationException(
                          "Завдання збору не розбирається: невідома форма payload.");

        // ⛔ Нульова сутність — не «збирати все». Задача без адресата мовчки
        // не збирала б нічого, і побачити це можна було б лише за порожнім
        // журналом покриття через місяць.
        if (request.SourceEntityId <= 0)
        {
            throw new InvalidOperationException(
                "Завдання збору не називає сутності джерела: збирати нічого.");
        }

        return request;
    }
}
