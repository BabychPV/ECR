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

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = ArchiveRequest.Parse(payload);

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

        var range = ArchiveRange.ForYear(request.Year, project.PeriodKind);

        await progress
            .ReportAsync(0, $"Архівація {range.From}…{range.To}", ct)
            .ConfigureAwait(false);

        // ⚠ Викликається ПРОЦЕДУРА. Копіювання, звірка сум і звільнення
        // партицій — усе там, під окремим principal (D-66). Спроба зробити те
        // саме з застосунку впала б на правах, і — гірше — зробила б половину.
        await db.Database.ExecuteSqlRawAsync(
            "EXEC arc.usp_ArchiveYear @ProjectId, @FromPeriodKey, @ToPeriodKey, @BatchSize",
            [
                new SqlParameter("@ProjectId", request.ProjectId),
                new SqlParameter("@FromPeriodKey", range.From),
                new SqlParameter("@ToPeriodKey", range.To),
                new SqlParameter("@BatchSize", capabilities.ArchiveBatchSize),
            ],
            ct).ConfigureAwait(false);

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
                    ? "Прогін не зафіксовано"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{run.Status}: перенесено {run.RowsMoved}, останній період {run.LastDone}"),
                ct)
            .ConfigureAwait(false);
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
/// <param name="Year">Рік, який архівують.</param>
public sealed record ArchiveRequest(int ProjectId, int Year)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

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
