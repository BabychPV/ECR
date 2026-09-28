// src/Ecr.Infrastructure/Jobs/AbandonedWorkSweeper.cs
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Прибирання покинутої роботи: завислі <c>itg.JobProgress</c>,
/// <c>itg.CollectionRun</c> і <c>itg.MaintenanceRun</c> (U4, U11) плюс
/// ретенція завершених записів прогресу (аудит P2).
/// </summary>
/// <remarks>
/// ⛔ Раніше прибирання було ОДНЕ — на старті, і лише для
/// <c>itg.JobProgress</c> зі старим биттям. Перезапуск, коротший за
/// <see cref="IJobProgressStore.StaleAfter"/> (оновлення MSI, рестарт служби),
/// лишав покинуті задачі «виконуваними» до наступного ДОВГОГО перезапуску, а
/// журнали прогонів збору й обслуговування не прибирав ніхто взагалі.
/// Тепер той самий прохід іде і на старті, і періодично
/// (<c>RecurringScheduleService</c>).
/// <para>
/// ⚠ Безпечний для кількох інстансів одночасно: кожен запис умовний (рядок
/// досі активний і досі застарілий), тож два проходи поспіль закривають рядок
/// рівно один раз.
/// </para>
/// </remarks>
public sealed class AbandonedWorkSweeper(EcrDbContext db, IJobProgressStore progress)
{
    /// <summary>Причина для задач, покинутих посеред роботи (періодичне прибирання).</summary>
    public const string AbandonedJobReason =
        "Задачу покинуто: процес, що її виконував або тримав у черзі, зупинився — биття серця застигло.";

    /// <summary>Причина для прогонів збору й обслуговування, які ніхто не закрив.</summary>
    public const string AbandonedRunReason =
        "Прогін не закрито: задачу скасовано або процес зупинився до того, як прогін записав результат.";

    /// <summary>
    /// Вік, після якого прогін збору вважається покинутим НЕЗАЛЕЖНО від того,
    /// чи йде зараз якийсь інший збір.
    /// </summary>
    /// <remarks>
    /// ⚠ Подвоєна стеля прогону (<c>CollectionRunner.DefaultMaxRunDuration</c>,
    /// 15 хв, Q-250): живий прогін свій watchdog закриває сам, тож старший за
    /// дві стелі — гарантовано без господаря. Константою, а не посиланням: цей
    /// шар адаптера PI не бачить.
    /// </remarks>
    public static readonly TimeSpan CollectionRunAbandonedAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Вік, після якого прогін обслуговування НЕВІДОМОЇ задачі вважається покинутим.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише для коду, якому не знайдено типу задачі (див.
    /// <see cref="KindOf"/>): для решти живість видно з биття її задачі, і
    /// такої межі не треба. Доба — з запасом більша за будь-який нічний прохід.
    /// </remarks>
    public static readonly TimeSpan MaintenanceRunAbandonedAfter = TimeSpan.FromHours(24);

    /// <summary>Скільки рядків прогресу видаляти за один прохід ретенції.</summary>
    public const int PurgeBatch = 5_000;

    /// <summary>Стеля кандидатів кожного журналу за прохід.</summary>
    private const int MaxRunsPerSweep = 500;

    /// <summary>Стеля кодів живих задач (їх одиниці — межа від правила 6).</summary>
    private const int MaxLiveCodes = 1_000;

    /// <summary>Суфікс типу задачі збору: і маркер <c>ICollectionJob</c>, і клас <c>CollectionJob</c>.</summary>
    private const string CollectionJobSuffix = "CollectionJob";

    private static readonly JsonSerializerOptions DetailsOptions = new(JsonSerializerDefaults.Web)
    {
        // Той самий вибір, що в `MaintenanceRunFailure`: текст іде в лист зведення.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Код прогону обслуговування → ім'я класу задачі, яка його пише.
    /// </summary>
    /// <remarks>
    /// ⚠ З відбиття, а не рукописним переліком: нова задача з
    /// <c>MaintenanceRun</c> інакше мовчки випала б із прибирання. Код —
    /// статична властивість <c>Code</c> класу задачі (так їх оголошено всі).
    /// </remarks>
    private static readonly Lazy<Dictionary<string, string>> KindByCode = new(BuildKindMap);

    /// <summary>Один прохід.</summary>
    /// <param name="jobReason">Причина для завислих задач (старт і періодичний прохід пишуть різне).</param>
    /// <param name="utcNow">Поточний момент у UTC.</param>
    /// <param name="purge">Чи виконувати ретенцію завершених записів прогресу.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="startingInstance">
    /// Лише на СТАРТІ: процес, що стартує (<c>JobProgressStore.CurrentMachineName</c>,
    /// <c>JobProgressStore.CurrentInstanceId</c>). Тоді активні рядки
    /// попередніх процесів цієї машини закриваються НЕЗАЛЕЖНО від биття.
    /// <c>null</c> — періодичний прохід: лише за віком биття.
    /// </param>
    public async Task<SweepOutcome> SweepAsync(
        string jobReason, DateTime utcNow, bool purge, CancellationToken ct,
        (string MachineName, string InstanceId)? startingInstance = null)
    {
        // ⚠ Задачі — ПЕРШИМИ: живість прогонів нижче визначається саме за
        // активними задачами, і покинута задача, ще не закрита, тримала б
        // «живим» і свій покинутий прогін.
        var previous = startingInstance is { } me
            ? await progress
                .FailPreviousInstanceAsync(me.MachineName, me.InstanceId, jobReason, utcNow, ct)
                .ConfigureAwait(false)
            : 0;

        var jobs = previous + await progress.FailStaleAsync(jobReason, utcNow, ct).ConfigureAwait(false);

        var live = await LiveJobCodesAsync(utcNow, ct).ConfigureAwait(false);
        var collection = await CloseCollectionRunsAsync(utcNow, live, ct).ConfigureAwait(false);
        var maintenance = await CloseMaintenanceRunsAsync(utcNow, live, ct).ConfigureAwait(false);

        var purged = purge
            ? await progress
                .PurgeFinishedAsync(utcNow - IJobProgressStore.RetainFinishedFor, PurgeBatch, ct)
                .ConfigureAwait(false)
            : 0;

        return new SweepOutcome(jobs, collection, maintenance, purged);
    }

    /// <summary>Коди задач, які зараз ЖИВІ (активні, зі свіжим биттям).</summary>
    private async Task<IReadOnlyList<string>> LiveJobCodesAsync(DateTime utcNow, CancellationToken ct)
    {
        var threshold = utcNow - IJobProgressStore.StaleAfter;

        return await db.JobProgresses
            .AsNoTracking()
            .Where(p => (p.State == "Running" || p.State == "Queued") && p.HeartbeatAt >= threshold)
            .Select(p => p.JobCode)
            .Distinct()
            .OrderBy(code => code)
            .Take(MaxLiveCodes)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Закриває прогони збору без господаря.</summary>
    /// <remarks>
    /// ⚠ Прогін не знає свого <c>jobId</c> (колонки немає — міграція), тож
    /// господаря видно лише непрямо: якщо зараз не живе ЖОДНА задача збору,
    /// будь-який «Running» старший за <see cref="IJobProgressStore.StaleAfter"/> —
    /// сирота; якщо якась живе — сиротою вважається лише старший за
    /// <see cref="CollectionRunAbandonedAfter"/>.
    /// <para>
    /// ⚠ Доповнює, а не дублює закриття в <c>CollectionRunner</c> (d03d5828):
    /// той закриває прогін на винятку й на скасуванні, але якщо саме в цю мить
    /// недоступна база, закрити нікому — це підбирає прохід тут.
    /// </para>
    /// </remarks>
    private async Task<int> CloseCollectionRunsAsync(
        DateTime utcNow, IReadOnlyList<string> live, CancellationToken ct)
    {
        var staleBefore = utcNow - IJobProgressStore.StaleAfter;
        var hardBefore = utcNow - CollectionRunAbandonedAfter;
        var anyCollectionAlive = live.Any(c => c.EndsWith(CollectionJobSuffix, StringComparison.Ordinal));
        var startedBefore = anyCollectionAlive ? hardBefore : staleBefore;

        var candidates = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => r.Status == "Running" && r.StartedAt < startedBefore)
            .OrderBy(r => r.StartedAt)
            .Select(r => r.Id)
            .Take(MaxRunsPerSweep)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // ⛔ Умова «досі Running» повторюється в записі: прогін міг закритися
        // сам між вибіркою і цим рядком, і його справжній результат важливіший.
        return await db.CollectionRuns
            .Where(r => candidates.Contains(r.Id) && r.Status == "Running")
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, CollectionFailure.FailedStatus)
                    .SetProperty(r => r.FinishedAt, utcNow)
                    .SetProperty(r => r.ErrorMessage, AbandonedRunReason),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Закриває прогони обслуговування без господаря.</summary>
    private async Task<int> CloseMaintenanceRunsAsync(
        DateTime utcNow, IReadOnlyList<string> live, CancellationToken ct)
    {
        var staleBefore = utcNow - IJobProgressStore.StaleAfter;

        var candidates = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.Status == "Running" && r.StartedAt < staleBefore)
            .OrderBy(r => r.StartedAt)
            .Select(r => new { r.Id, r.JobCode, r.StartedAt })
            .Take(MaxRunsPerSweep)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var orphans = candidates
            .Where(r => KindOf(r.JobCode) is { } kind
                ? !live.Any(c => c.EndsWith(kind, StringComparison.Ordinal))
                : r.StartedAt < utcNow - MaintenanceRunAbandonedAfter)
            .Select(r => r.Id)
            .ToList();

        if (orphans.Count == 0)
        {
            return 0;
        }

        var details = JsonSerializer.Serialize(new { error = AbandonedRunReason }, DetailsOptions);

        return await db.MaintenanceRuns
            .Where(r => orphans.Contains(r.Id) && r.Status == "Running")
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, MaintenanceRunFailure.FailedStatus)
                    .SetProperty(r => r.FinishedAt, utcNow)
                    .SetProperty(r => r.DetailsJson, details),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Ім'я класу задачі, що пише прогони з кодом <paramref name="code"/>; <c>null</c> — невідомий.</summary>
    /// <param name="code">Код прогону обслуговування.</param>
    public static string? KindOf(string code)
        => KindByCode.Value.TryGetValue(code, out var kind) ? kind : null;

    private static Dictionary<string, string> BuildKindMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in typeof(AbandonedWorkSweeper).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(IBackgroundJob).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetProperty("Code", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is string code)
            {
                map[code] = type.Name;
            }
        }

        return map;
    }
}

/// <summary>Підсумок одного проходу прибирання.</summary>
/// <param name="Jobs">Задач позначено <c>Failed</c>.</param>
/// <param name="CollectionRuns">Прогонів збору закрито.</param>
/// <param name="MaintenanceRuns">Прогонів обслуговування закрито.</param>
/// <param name="Purged">Завершених записів прогресу видалено.</param>
public sealed record SweepOutcome(int Jobs, int CollectionRuns, int MaintenanceRuns, int Purged)
{
    /// <summary>Чи зробив прохід хоч щось.</summary>
    public bool Any => Jobs + CollectionRuns + MaintenanceRuns + Purged > 0;
}
