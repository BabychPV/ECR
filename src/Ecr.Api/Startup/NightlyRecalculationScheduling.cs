using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Нічний перерахунок документів активних проєктів — <b>увімкнений за замовчуванням</b> (рішення людини 21.11, PSS-15,
/// <c>D-562</c>): лише поточний рік і лише документи, входи яких змінились після актуального прогону.
/// </summary>
/// <remarks>
/// ⛔ Історія: директива №09 частина B / Q-331 завела його як ОПЦІЮ, вимкнену за замовчуванням, — бо він переписував усі
/// відкриті періоди всіх років кожного документа кожну ніч (аудит PSS-15). Людина 21.11 вирішила: вмикати, з обмеженням
/// поточним роком і пропуском незмінних. Обидва обмеження живуть у задачі (<c>RecalculationJob.NightlyFanOutAsync</c>), тут —
/// лише розклад і прапорці.
/// <para>
/// ⛔ Реалізовано на рівні КОНФІГУРАЦІЇ (<c>appsettings.json</c>/змінна середовища), а не новим стовпцем БД. Наскрізна
/// прив'язка «опція — per-проєкт» (наприклад, <c>Project.NightlyRecalculationEnabled</c>) — зміна схеми, а рішення про
/// безпечність міграції — не моє й не виконавця (`CLAUDE.md`).
/// </para>
/// <para>
/// ⛔ Час запуску (<see cref="Cron"/>) — СТАЛА, а НЕ ключ конфігурації (<c>Ecr.Architecture.Tests.PrincipleTests.
/// Налаштування_живуть_у_базі_а_не_в_конфігураційних_файлах</c>, ФВ-2.14), як і <c>NightlyCron</c>/<c>HourlyCron</c> у
/// <see cref="RecurringScheduleService"/>.
/// </para>
/// <para>
/// ✎ PSS-15: час — 00:30 (місцевий час сервера, як усі тригери Quartz). Обґрунтування (D-562): (1) нічне прибирання замінених
/// прогонів (<c>SupersededResultsSweepJob</c>, 02:15, працює лише у вікні <c>Calculations:SupersededPurge:NightWindow</c>,
/// типово 01:00–05:00) видаляє пакетами з <c>calc.*</c> — ті самі таблиці й індекси, у які пише перерахунок; одночасно це
/// блокування сторінок/рядків і журнал транзакцій удвох. Перерахунок ДО вікна, а прибирання ПІСЛЯ: його замінені прогони
/// прибирає вже швидкий шлях після лока (L1b/D5), а що не встиг — той самий нічний прохід о 02:15. (2) О 02:15 ще й перевірки
/// (<c>ConsistencyCheckJob</c>, <c>PartitionCheckJob</c>, <c>OrphanScanJob</c>) — їм краще бачити вже перераховані числа.
/// (3) Стани періодів на 00:30 уже оновлено тиком <c>PeriodStateJob</c> о 00:22 (кожні 10 хв, хх:02) — межа місяця о
/// півночі майданчика не лишає «відкритим» щойно закритий місяць. (4) З пропуском незмінних і лише поточним роком звичайна
/// ніч — документи, змінені за добу. ✎ Огляд M2(e), чесно: що вона ЗАКІНЧИТЬСЯ до 02:15, не гарантовано й не виміряно.
/// Повна ніч (перша після оновлення, після публікації методології, правки довідника чи прив'язки) — усі відкриті місяці всіх
/// документів поточного року під стелею дітей розкладу (<c>D-561</c>); на обсязі замовника (~250 документів, місяць Land —
/// хвилини) вона може тривати години й перекритися з прибиранням 02:15 — тоді вони просто конкурують, як доти о 03:30.
/// Колишні 03:30 потрапляли в середину вікна прибирання завжди, а не лише в повну ніч.
/// </para>
/// <para>
/// ⚠ Час — місцевий час СЕРВЕРА застосунку (тригери Quartz без поясу), як і вікно прибирання; пояс проєкту визначає лише
/// «поточний рік» (огляд L3). <c>PeriodStateJob</c> — теж за часом сервера.
/// </para>
/// <para>
/// ⚠ Один тригер НА ПРОЄКТ, активний на момент старту (як доти): проєкт, активований пізніше, потрапить у розклад після
/// перезапуску <c>EcrApi</c>; проєкт, що вже не охоплює поточного року, задача пропускає сама (рядок журналу).
/// </para>
/// </remarks>
public static class NightlyRecalculationScheduling
{
    /// <summary>Прапорець: чи ставити нічний перерахунок на розклад узагалі (типово <c>true</c>).</summary>
    public const string EnabledKey = "Jobs:NightlyRecalculation:Enabled";

    /// <summary>
    /// Прапорець: пропускати документи, входи яких не змінились після актуального прогону (типово <c>true</c>);
    /// <c>false</c> — щоночі рахувати всі відкриті періоди поточного року (аварійний важіль, якщо пропуск під підозрою).
    /// </summary>
    public const string SkipUnchangedKey = "Jobs:NightlyRecalculation:SkipUnchanged";

    /// <summary>00:30 — до нічного вікна прибирання замінених прогонів (01:00–05:00) і перевірок о 02:15.</summary>
    public const string Cron = "0 30 0 * * ?";

    /// <summary>Чи увімкнено нічний перерахунок: усе, крім явного <c>false</c>, — так.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <remarks>
    /// ⚠ Ручний розбір, а не <c>IConfiguration.GetValue&lt;bool&gt;</c>: той живе в пакеті <c>Configuration.Binder</c>, а
    /// <c>Ecr.Api</c> тут тримається самих <c>Configuration.Abstractions</c>. Недійсне значення («ture») зупиняє старт раніше
    /// (<c>EcrConfigurationValidation.Booleans</c>), тож «не розібралось» тут — лише «не задано».
    /// </remarks>
    public static bool IsEnabled(IConfiguration configuration)
        => Flag(configuration, EnabledKey);

    /// <summary>Чи пропускати незмінні документи: усе, крім явного <c>false</c>, — так.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public static bool SkipsUnchanged(IConfiguration configuration)
        => Flag(configuration, SkipUnchangedKey);

    /// <summary>
    /// Ставить (або НЕ ставить) нічний перерахунок на розклад.
    /// </summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <param name="db">Контекст на базу — читає активні проєкти.</param>
    /// <param name="scheduler">Планувальник фонових задач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки проєктів отримали тригер; 0 — прапорець вимкнений (чи активних проєктів немає).</returns>
    /// <remarks>
    /// ⚠ Один тригер НА ПРОЄКТ, а не один спільний на всі: <c>IRecalculationJob</c> приймає рівно один <c>ProjectId</c>, і
    /// <c>QuartzJobScheduler.ScheduleAsync</c> уже вміє ставити багато незалежних тригерів того самого типу задачі — ключ
    /// триґера складається з типу Й ВІДБИТКА payload.
    ///
    /// ⚠ <c>DocumentId</c> НЕ передається — нуль після розбору JSON: розклад проєкту (Q-151/Q-162). <c>PeriodKey = null</c>,
    /// року в payload теж немає: його визначає задача в момент виконання (тригер живе через Новий рік).
    /// <c>Nightly = true</c> вмикає обидва обмеження PSS-15; <c>NightlySkipUnchanged</c> — значення
    /// <see cref="SkipUnchangedKey"/> на момент старту.
    /// </remarks>
    public static async Task<int> ScheduleAsync(
        IConfiguration configuration, EcrDbContext db, IBackgroundJobScheduler scheduler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scheduler);

        if (!IsEnabled(configuration))
        {
            return 0;
        }

        var skipUnchanged = SkipsUnchanged(configuration);
        var projectIds = await db.Projects
            .AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Active)
            .Select(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var projectId in projectIds)
        {
            await scheduler
                .ScheduleAsync<IRecalculationJob>(Cron, PayloadOf(projectId, skipUnchanged), ct)
                .ConfigureAwait(false);
        }

        return projectIds.Count;
    }

    /// <summary>Payload тригера проєкту — та сама форма, що розбирає <c>RecalculationJob</c> (<c>RecalculationRequest</c>).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="skipUnchanged">Пропускати незмінні документи.</param>
    public static object PayloadOf(int projectId, bool skipUnchanged)
        => new
        {
            ProjectId = projectId,
            PeriodKey = (int?)null,
            TriggeredByUserId = (int?)null,
            Nightly = true,
            NightlySkipUnchanged = skipUnchanged,
        };

    private static bool Flag(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !bool.TryParse(configuration[key], out var value) || value;
    }
}
