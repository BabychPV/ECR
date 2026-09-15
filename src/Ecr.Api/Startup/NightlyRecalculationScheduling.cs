using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Нічний повний перерахунок УСІХ документів УСІХ активних проєктів —
/// ОПЦІЯ, вимкнена за замовчуванням (директива №09, частина B; Q-331).
/// </summary>
/// <remarks>
/// ⛔ Прогалини 4+5 директиви паритету зі старою системою (Q-327): чинна
/// нічна перевірка (<see cref="Ecr.Infrastructure.Jobs.ConsistencyCheckJob"/>,
/// 02:15) лише ЗНАХОДИТЬ розбіжності — жодна нічна задача НІКОЛИ не
/// перераховувала числа наново, і <see cref="IRecalculationJob"/> не стояв
/// на жодному розкладі взагалі. Людина підтвердила: гарантія «раз на добу
/// все перераховується наново» потрібна, але як ОПЦІЯ, не завжди-увімкнений
/// режим для всіх проєктів одразу.
/// <para>
/// ⛔ Реалізовано на рівні КОНФІГУРАЦІЇ (<c>appsettings.json</c>/змінна
/// середовища), а не новим стовпцем БД. Наскрізна прив'язка «опція —
/// per-проєкт» (наприклад, <c>Project.NightlyRecalculationEnabled</c>) —
/// зміна схеми, а рішення про безпечність міграції — не моє й не виконавця
/// (`CLAUDE.md`): якщо колись з'ясується, що прапорець мусить бути
/// per-проєктним, а не глобальним, — це окреме питання людині, а не мовчазна
/// міграція тут. Глобальний прапорець задовольняє «опція» без торкання схеми.
/// </para>
/// <para>
/// ⛔ Час запуску (<see cref="Cron"/>) — СТАЛА, а НЕ ключ конфігурації, на
/// відміну від першої редакції цього файлу. Причина —
/// <c>Ecr.Architecture.Tests.PrincipleTests.
/// Налаштування_живуть_у_базі_а_не_в_конфігураційних_файлах</c> (ФВ-2.14):
/// `appsettings.json` не несе нічого, що виглядає як розклад чи політика,
/// —  саме тому й `NightlyCron`/`HourlyCron` у
/// <see cref="RecurringScheduleService"/> — константи коду, а не рядки
/// конфігурації, а розклад збору (`ext.CollectionSchedule.CronExpression`)
/// живе в БАЗІ, не в файлі. Той самий принцип застосовано тут: увімкнено/
/// вимкнено — прапорець застосунку (не «дані», якими керує адміністратор без
/// релізу); КОЛИ саме — та сама група, що й решта нічних задач, і місце їй
/// поряд із ними, константою.
/// </para>
/// <para>
/// ⚠ Судження виконавця: типове значення <see cref="EnabledKey"/> —
/// <c>false</c>. Нічний повний перерахунок усіх документів усіх проєктів —
/// це суттєве навантаження (директива №09: бюджет річного перерахунку — до
/// 10 хвилин НА ПРОЄКТ), і типовий стан продової поведінки не повинен
/// змінюватися без явного рішення того, хто розгортає систему.
/// </para>
/// <para>
/// ⚠ Судження виконавця: час запуску (<see cref="Cron"/>) — 03:30, тобто
/// ПІСЛЯ <see cref="RecurringScheduleService.NightlyCron"/> (02:15:
/// <c>PartitionCheckJob</c>/<c>ConsistencyCheckJob</c>/<c>OrphanScanJob</c>/
/// <c>ReportRetentionJob</c>) — з запасом, щоб нічне вікно перевірок встигло
/// завершитися до того, як перерахунок почне читати щойно зібрані дані. Це
/// НЕ гарантія проти довільного адміністраторського розкладу збору джерела
/// (`ext.CollectionSchedule`, кожне зі своїм cron у БАЗІ) — той факт (коли
/// саме збирається КОЖНЕ конкретне джерело якогось замовника) виконавцю не
/// відомий, тож повної гарантії порядку відносно НЬОГО дати не можна.
/// </para>
/// </remarks>
public static class NightlyRecalculationScheduling
{
    /// <summary>Прапорець: чи ставити нічний перерахунок на розклад узагалі.</summary>
    public const string EnabledKey = "Jobs:NightlyRecalculation:Enabled";

    /// <summary>03:30 — після нічного вікна `RecurringScheduleService.NightlyCron` (02:15).</summary>
    public const string Cron = "0 30 3 * * ?";

    /// <summary>
    /// Ставить (або НЕ ставить) нічний перерахунок на розклад.
    /// </summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <param name="db">Контекст на базу — читає активні проєкти.</param>
    /// <param name="scheduler">Планувальник фонових задач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки проєктів отримали тригер; 0 — прапорець вимкнений.</returns>
    /// <remarks>
    /// ⚠ Один тригер НА ПРОЄКТ, а не один спільний на всі: `IRecalculationJob`
    /// приймає рівно один `ProjectId`, і `QuartzJobScheduler.ScheduleAsync`
    /// уже вміє ставити багато незалежних тригерів того самого типу задачі —
    /// ключ триґера складається з типу Й ВІДБИТКА payload (той самий патерн,
    /// що й нічний збір: окремий тригер на кожен `ext.CollectionSchedule`,
    /// не один спільний на «джерела взагалі»).
    ///
    /// ⚠ `DocumentId` НЕ передається — відсутність у payload читається як
    /// нуль після розбору JSON, а `RecalculationJob` уже трактує нуль як
    /// «усі документи проєкту» (Q-151/Q-162). `PeriodKey = null` — увесь рік.
    /// </remarks>
    public static async Task<int> ScheduleAsync(
        IConfiguration configuration, EcrDbContext db, IBackgroundJobScheduler scheduler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scheduler);

        // ⚠ Ручний розбір, а не `IConfiguration.GetValue<bool>`: той живе в
        // пакеті `Configuration.Binder`, а `Ecr.Api` (як і `Ecr.Infrastructure`,
        // `DependencyInjection.ReadInt`) навмисно тримається самих
        // `Configuration.Abstractions` — тягнути пакет заради одного прапорця
        // гірше, ніж рядок розбору.
        if (!(bool.TryParse(configuration[EnabledKey], out var enabled) && enabled))
        {
            return 0;
        }

        var projectIds = await db.Projects
            .AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Active)
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var projectId in projectIds)
        {
            await scheduler
                .ScheduleAsync<IRecalculationJob>(
                    Cron,
                    new { ProjectId = projectId, PeriodKey = (int?)null, TriggeredByUserId = (int?)null },
                    ct)
                .ConfigureAwait(false);
        }

        return projectIds.Count;
    }
}
