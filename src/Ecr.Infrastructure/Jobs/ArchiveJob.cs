using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Архівація закритого року.
/// </summary>
/// <remarks>
/// ⚠ Сам DDL і <c>TRUNCATE … WITH (PARTITIONS)</c> виконує **збережена
/// процедура під окремим principal** (`D-66`): обліковий запис застосунку не
/// має ані DDL-прав, ані права запису в <c>arc.*</c>. Ця задача лише **викликає**
/// процедуру, стежить за прогресом і алертить.
/// </remarks>
public sealed class ArchiveJob(
    EcrDbContext db, ISqlCapabilities capabilities, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "archive-year";

    /// <summary>
    /// Скільки секунд дається команді <c>EXEC arc.usp_ArchiveYear</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>S-09</c>. Глобальний <c>Database:CommandTimeoutSeconds = 60</c>
    /// (<c>DependencyInjection.cs:58</c>) поставлений під ІНТЕРАКТИВНИЙ запит,
    /// і архівація року йшла під ним же. Рік — це десятки мільйонів рядків
    /// (<c>14-performance.md</c> §6 п. 4), тобто таймаут був не ризиком, а
    /// ГАРАНТІЄЮ: кожен прогін падав і кожне падіння вело до ретраю
    /// (<see cref="QuartzJobAdapter.MaxRetryAttempts"/> — три), а ретрай
    /// запускав другу архівацію того самого року поверх першої, яка на сервері
    /// ще котиться.
    /// <para>
    /// ⚠ Чотири години — СТЕЛЯ, а не бюджет: процедура має вкластися у вікно
    /// низької активності, і якщо вона його проїла, це аварія, про яку треба
    /// дізнатися, а не чекати далі. Число — судження: жоден документ не
    /// називає тривалості архівації (<c>14-performance.md</c> §6 п. 4 дає
    /// критерій «без блокування робочих запитів» і не дає часу).
    /// </para>
    /// <para>
    /// ⚠ Константа В КОДІ навмисно. Ключ конфігурації сюди просився б, але
    /// <c>appsettings.json</c> тримає інший рядок директиви разом зі сторожем
    /// <c>ConfigurationKeysTests</c> (ключ без читача і читач без ключа —
    /// обидва червоні). Потреба названа окремим <c>[debt]</c>.
    /// </para>
    /// </remarks>
    public const int CommandTimeoutSeconds = 4 * 60 * 60;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ F-13 (UX-PASS, четвертий раунд). Задачу не запускав НІХТО: маршруту
    /// немає, у розкладі її не було (<c>RecurringScheduleService</c>), і
    /// архівація проєкту лише ставила статус — <c>arc.CellValue</c> лишався
    /// порожнім. Тепер вона стоїть у НІЧНОМУ розкладі з порожнім завданням і
    /// сама знаходить, що переносити (<see cref="SweepAsync"/>).
    /// <para>
    /// ⚠ Задум (<c>ФВ-1.9</c>, коментарі нижче) не змінено: переноситься лише
    /// проєкт, УЖЕ позначений заархівованим людиною, і лише після річного
    /// грейсу. Розклад не вирішує «що архівувати» — він виконує рішення, яке
    /// людина вже ухвалила кнопкою «Archive», у вікні низької активності.
    /// </para>
    /// </remarks>
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = ArchiveRequest.ParseOrNull(payload);

        if (request?.Year is not { } year)
        {
            await SweepAsync(request?.ProjectId, progress, ct).ConfigureAwait(false);
            return;
        }

        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Проєкту {request.ProjectId} не існує.");

        // ⛔ Фізична архівація йде лише для проєкту, ВЖЕ позначеного
        // заархівованим, і лише після того, як сплив річний грейс (ФВ-1.9).
        // Архівація відкритого року означала б, що дані зникають із-під рук
        // того, хто їх зараз заповнює.
        //
        // ⚠ Два кроки навмисно різні: позначку ставить людина через
        // `POST /api/v1/projects/{id}/archive` (перевіряючи, що всі періоди
        // закриті), а фізичне перенесення в `arc.*` робить ця задача. Стан
        // `Closed` на рівні проєкту прибрано (`D-123`): він мав сенс лише для
        // періоду.
        if (project.Status != ProjectStatus.Archived)
        {
            throw new InvalidOperationException(
                $"Проєкт {request.ProjectId} у стані {project.Status}: "
                + "фізична архівація йде лише для позначеного заархівованим.");
        }

        var grace = project.ClosedAt?.AddDays(project.YearGraceOffsetDays);
        if (grace is { } until && clock.UtcNow < until)
        {
            throw new InvalidOperationException(
                $"Річний грейс проєкту {request.ProjectId} триває до {until:yyyy-MM-dd}: "
                + "архівація передчасна.");
        }

        var range = ArchiveRange.ForYear(year, project.PeriodKind);

        await progress
            .ReportKeyAsync(
                0,
                "jobs.archiveRange",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from"] = range.From.ToString(CultureInfo.InvariantCulture),
                    ["to"] = range.To.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        await ArchiveRangeAsync(request.ProjectId, range, ct).ConfigureAwait(false);

        var run = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == request.ProjectId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new RunRow(r.Id, r.Status, r.RowsMoved, r.LastDonePeriodKey))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // ⚠ При провалі — алерт і ЗУПИНКА, не повторна спроба. Дані джерела на
        // місці: процедура не видаляє нічого, поки суми не збіглися. Повторний
        // запуск продовжить із LastDonePeriodKey (АРХ-3a), а не почне спочатку
        // — рік це десятки мільйонів рядків, і другий прохід не вкладеться у
        // вікно низької активності.
        await progress
            .ReportAsync(
                100,
                run is null
                    ? JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope("jobs.archiveRunMissing"))
                    : JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                        "jobs.archiveResult",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["status"] = run.Status,
                            ["rowsMoved"] = run.RowsMoved.ToString(CultureInfo.InvariantCulture),
                            ["lastPeriod"] = run.LastDone?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        })),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Нічний прохід: переносить у <c>arc.*</c> кожен рік кожного
    /// заархівованого проєкту, у якого сплив річний грейс і який ще не
    /// перенесено (F-13).
    /// </summary>
    /// <param name="projectId">Лише цей проєкт; <c>null</c> — усі.</param>
    /// <param name="progress">Прогрес.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Рік ПРОПУСКАЄТЬСЯ, а не валить прохід, коли його партиції ділить
    /// незаархівований проєкт. Партиція йде по періоду, а не по проєкту
    /// (<c>D-117</c>), і процедура в такому разі відмовить <c>50012</c> —
    /// правильно, але щоночі. Пропущений рік дочекається, доки заархівують
    /// сусіда, і перенесеться тієї ж ночі, без втручання людини.
    /// <para>
    /// ⚠ Провал одного року не зупиняє решту: зламаний проєкт не має тримати
    /// архів усіх інших. Але й не ковтається — прохід закінчується відмовою з
    /// переліком, і задача в журналі <c>Failed</c>, а не «успішно».
    /// </para>
    /// </remarks>
    private async Task SweepAsync(int? projectId, IJobProgress progress, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Archived && p.ClosedAt != null)
            .Where(p => projectId == null || p.Id == projectId)
            .Select(p => new { p.Id, p.ClosedAt, p.YearGraceOffsetDays, p.PeriodKind })
            .OrderBy(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var archived = 0;
        var skipped = 0;
        var failures = new List<string>();
        Exception? firstFailure = null;

        foreach (var project in projects)
        {
            // ⚠ Грейс і Custom — пропуск, а не відмова: це не збій, а «ще не
            // час» і «діапазону року не існує» (`ArchiveRange.PeriodsInYear`).
            if (project.ClosedAt!.Value.AddDays(project.YearGraceOffsetDays) > now
                || project.PeriodKind == PeriodKind.Custom)
            {
                skipped++;
                continue;
            }

            var years = await db.Periods
                .AsNoTracking()
                .Where(p => p.ProjectId == project.Id)
                .Select(p => p.PeriodKeyValue / 100)
                .Distinct()
                .OrderBy(y => y)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var year in years)
            {
                var range = ArchiveRange.ForYear(year, project.PeriodKind);

                if (await IsArchivedAsync(project.Id, range, ct).ConfigureAwait(false)
                    || await SharedWithLiveProjectAsync(range, ct).ConfigureAwait(false))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await ArchiveRangeAsync(project.Id, range, ct).ConfigureAwait(false);
                    archived++;
                }
                catch (SqlException ex)
                {
                    firstFailure ??= ex;
                    failures.Add(string.Create(CultureInfo.InvariantCulture, $"{project.Id}:{year}"));
                }
            }
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.archiveSweepDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["archived"] = archived.ToString(CultureInfo.InvariantCulture),
                    ["skipped"] = skipped.ToString(CultureInfo.InvariantCulture),
                    ["failed"] = failures.Count.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        if (firstFailure is not null)
        {
            throw new InvalidOperationException(
                $"Архівація не вдалася для проєкт:рік — {string.Join(", ", failures)}.", firstFailure);
        }
    }

    /// <summary>Чи останній завершений прогін проєкту, що покрив увесь діапазон, — у архів.</summary>
    private async Task<bool> IsArchivedAsync(int projectId, (int From, int To) range, CancellationToken ct)
    {
        var direction = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && r.Status == "Completed"
                        && r.FromPeriodKey <= range.From
                        && r.ToPeriodKey >= range.To)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.Direction)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return direction == Domain.Entities.Integration.ArchiveRun.ToArchive;
    }

    /// <summary>Чи діапазон ділить хоч один незаархівований проєкт (процедура відмовила б <c>50012</c>).</summary>
    private Task<bool> SharedWithLiveProjectAsync((int From, int To) range, CancellationToken ct)
        => db.Periods
            .AsNoTracking()
            .Where(p => p.PeriodKeyValue >= range.From && p.PeriodKeyValue <= range.To)
            .Join(db.Projects.AsNoTracking(), p => p.ProjectId, x => x.Id, (_, x) => x.Status)
            .AnyAsync(status => status != ProjectStatus.Archived, ct);

    /// <summary>Викликає процедуру архівації діапазону під власним таймаутом.</summary>
    private async Task ArchiveRangeAsync(int projectId, (int From, int To) range, CancellationToken ct)
    {
        // ⚠ Таймаут ставиться на КОНТЕКСТ і повертається назад. Контекст задачі
        // scoped (QuartzJobAdapter створює scope на прогін), тож чужого запиту
        // ця стеля не зачепить; але лишити її на решту запитів САМОЇ задачі
        // означало б сховати за чотирма годинами зависання читання нижче —
        // читання журналу прогонів мусить далі падати швидко.
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        try
        {
            // ⚠ Викликається ПРОЦЕДУРА. Копіювання, звірка сум і звільнення
            // партицій — усе там, під окремим principal (D-66). Спроба зробити
            // те саме з застосунку впала б на правах, і — гірше — зробила б
            // половину.
            await db.Database.ExecuteSqlRawAsync(
                "EXEC arc.usp_ArchiveYear @ProjectId, @FromPeriodKey, @ToPeriodKey, @BatchSize",
                [
                    new SqlParameter("@ProjectId", projectId),
                    new SqlParameter("@FromPeriodKey", range.From),
                    new SqlParameter("@ToPeriodKey", range.To),
                    new SqlParameter("@BatchSize", capabilities.ArchiveBatchSize),
                ],
                ct).ConfigureAwait(false);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    /// <summary>Рядок прогону архівації.</summary>
    private sealed record RunRow(long Id, string Status, long RowsMoved, int? LastDone);
}

/// <summary>
/// Діапазон ключів періодів року.
/// </summary>
/// <remarks>
/// ⛔ Виводиться з <see cref="PeriodKind"/>, а не жорстко як
/// <c>YYYY01…YYYY12</c>. <c>PeriodKey = Year*100 + Sequence</c> (R-A6), і
/// <c>Sequence</c> — порядковий номер періоду в році: у квартальному проєкті
/// їх чотири. Жорсткий діапазон до дванадцяти пройшовся б по восьми
/// неіснуючих партиціях — і кожна з них зробила б `TRUNCATE` по чужих даних,
/// якби ключ випадково збігся з іншим проєктом.
/// </remarks>
public static class ArchiveRange
{
    /// <summary>Скільки періодів має рік за гранулярністю.</summary>
    /// <param name="kind">Гранулярність періодів проєкту.</param>
    /// <exception cref="ArgumentOutOfRangeException">Невідома гранулярність.</exception>
    public static int PeriodsInYear(PeriodKind kind)
        => kind switch
        {
            PeriodKind.Monthly => 12,
            PeriodKind.Quarterly => 4,
            PeriodKind.Yearly => 1,

            // ⚠ Custom не має відомого наперед числа періодів (R-B5): діапазон
            // для нього рахується з календаря проєкту, а не з гранулярності.
            // Мовчазна дванадцятка тут була б здогадкою про чужі дані.
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Число періодів року невідоме для цієї гранулярності."),
        };

    /// <summary>Діапазон ключів року.</summary>
    /// <param name="year">Рік.</param>
    /// <param name="kind">Гранулярність періодів проєкту.</param>
    public static (int From, int To) ForYear(int year, PeriodKind kind)
    {
        var count = PeriodsInYear(kind);
        return ((year * 100) + 1, (year * 100) + count);
    }
}

/// <summary>Завдання на архівацію.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Year">Рік, який архівують; <c>null</c> — усі належні роки проєкту (F-13).</param>
public sealed record ArchiveRequest(int ProjectId, int? Year = null)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Розбирає завдання черги; <c>null</c> — завдання порожнє (нічний
    /// прохід по всіх проєктах).
    /// </summary>
    /// <param name="payload">Завдання: типізоване, JSON або порожнє.</param>
    /// <remarks>
    /// ⚠ Розклад кладе порожнє завдання РЯДКОМ <c>"null"</c>
    /// (<c>QuartzJobScheduler</c> серіалізує payload), тож перевіряється і він.
    /// </remarks>
    public static ArchiveRequest? ParseOrNull(object? payload)
        => payload is null or "" or "null" ? null : Parse(payload);

    /// <summary>Розбирає завдання черги.</summary>
    /// <param name="payload">Завдання: типізоване або JSON.</param>
    public static ArchiveRequest Parse(object? payload)
    {
        if (payload is ArchiveRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<ArchiveRequest>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання архівації не розбирається: невідома форма payload.");
    }
}
