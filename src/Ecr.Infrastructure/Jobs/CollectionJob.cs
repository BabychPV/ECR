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
/// </remarks>
public sealed class CollectionJob(
    ICollectionRunner runner,
    EcrDbContext db,
    IBackgroundJobScheduler jobs,
    IClock clock) : ICollectionJob
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
        var to = request.ToUtc ?? now;

        // ⚠ Початок береться з LookbackDays, а НЕ з Watermark. Watermark —
        // оптимізація, а не стан (ER-I-03): перекриття назад закриє проміжок
        // повторно, а природний ключ ext.RawDataPoint не дасть подвоїти точки.
        var from = request.FromUtc ?? to.AddDays(-(schedule?.LookbackDays ?? DefaultLookbackDays));

        await progress
            .ReportAsync(5, $"Збір сутності {request.SourceEntityId} за {from:yyyy-MM-dd}…{to:yyyy-MM-dd}", ct)
            .ConfigureAwait(false);

        // ⚠ Ідемпотентність забезпечує збирач: повторний запуск того самого
        // діапазону не дублює даних. Тому задача НЕ перевіряє «а чи вже
        // збирали» — така перевірка була б другим місцем, де живе те саме
        // правило, і розійшлася б із першим.
        await runner
            .RunAsync(request.SourceEntityId, from, to, progress, ct)
            .ConfigureAwait(false);

        if (schedule is not null)
        {
            // Watermark рухається лише за успішним прогоном і лише вперед.
            schedule.MarkRun(now, to);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await EnqueueMaterializationAsync(request.SourceEntityId, from, to, ct).ConfigureAwait(false);
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
        // ⚠ Мапінги без `TargetRowKey` не матеріалізуються — і це легальний
        // стан (`D-118`): тег може збиратися для звірки, а не для форми.
        var columnIds = await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == sourceEntityId
                        && m.IsActive
                        && m.TargetRowKey != null
                        && m.TargetColumnDefId != null)
            .Select(m => m.TargetColumnDefId!.Value)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (columnIds.Count == 0)
        {
            return;
        }

        // Таблиці, яких стосуються ці колонки, і живі екземпляри цих таблиць.
        var tableDefIds = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => columnIds.Contains(c.Id))
            .Select(c => c.TableDefId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targets = await db.TableInstances
            .AsNoTracking()
            .Where(t => tableDefIds.Contains(t.TableDefId))
            .Join(db.Documents, t => t.DocumentId, d => d.Id, (t, d) => new
            {
                t.Id,
                t.DocumentId,
                t.PeriodKeyValue,
                d.ProjectId,
            })
            .Take(MaxMaterializationTargets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var target in targets)
        {
            await jobs
                .EnqueueAsync<IMaterializeCollectedDataJob>(
                    new MaterializeTask(
                        sourceEntityId,
                        target.ProjectId,
                        target.DocumentId,
                        target.Id,
                        target.PeriodKeyValue,
                        from,
                        to),
                    ct)
                .ConfigureAwait(false);
        }
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
