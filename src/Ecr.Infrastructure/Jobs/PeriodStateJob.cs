using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переводить періоди між станами і оновлює <c>Project.CurrentPeriod</c>.
/// </summary>
/// <remarks>
/// Стан періоду — **збережене значення**, а не функція від <c>now()</c> у
/// запиті (ФВ-1.12). Інакше кожна перевірка доступу рахувала б offsets, а межа
/// «останнього дня» залежала б від того, о котрій виконано запит.
/// </remarks>
public sealed class PeriodStateJob(
    EcrDbContext db,
    PeriodStateCalculator calculator,
    IClock clock) : IBackgroundJob
{
    /// <summary>Перехід, який задача має застосувати.</summary>
    /// <param name="Period">Період.</param>
    /// <param name="Target">Цільовий стан.</param>
    public readonly record struct Transition(Period Period, PeriodState Target);

    /// <summary>
    /// Обчислює переходи, нічого не змінюючи.
    /// </summary>
    /// <param name="periods">Періоди одного проєкту з уже обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="siteTimeZone">Пояс майданчика (<c>D-68</c>).</param>
    /// <param name="calculator">Калькулятор станів.</param>
    /// <remarks>
    /// Винесено окремо від <see cref="ExecuteAsync"/> навмисно: рішення про
    /// стан періоду має бути перевіреним без бази, бо саме воно вирішує, чи
    /// можна редагувати документ.
    /// </remarks>
    public static IReadOnlyList<Transition> Plan(
        IReadOnlyList<Period> periods,
        DateTime utcNow,
        TimeZoneInfo siteTimeZone,
        PeriodStateCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(calculator);

        var transitions = new List<Transition>();

        foreach (var period in periods)
        {
            var target = calculator.Calculate(period, utcNow, siteTimeZone);

            // Уже в цільовому стані — не чіпаємо. Повторний запуск задачі має
            // бути безслідним: інакше StateChangedAt оновлювався б щогодини і
            // журнал перестав би відповідати, коли період справді змінився.
            if (target == period.State)
            {
                continue;
            }

            // ⚠ Назад задача не переводить НІКОЛИ. `Closed → Grace` — виключно
            // рішення адміністратора через Reopen; збій розрахунку не має
            // тихо відкривати закритий період.
            if (period.State == PeriodState.Closed)
            {
                continue;
            }

            transitions.Add(new Transition(period, target));
        }

        return transitions;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var projects = await db.Projects
            .Where(p => p.Status == ProjectStatus.Active)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var utcNow = clock.UtcNow;

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];

            // Пояс майданчика, а не пояс сервера: період, що закривається
            // «31 числа о 23:59», має закритися о 23:59 там, де сидять люди
            // (D-68).
            var zone = ResolveZone(project.TimeZoneId);

            // ⚠ UPDLOCK: Reopen бере той самий рядок так само (ФВ-1.10a).
            // Без нього задача і відкриття періоду перегоняють одне одного, і
            // повернення застосувалося б до вже закритого періоду.
            var periods = await db.Periods
                .FromSql($"""
                    SELECT * FROM doc.Period WITH (UPDLOCK, ROWLOCK)
                     WHERE ProjectId = {project.Id}
                    """)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var (period, target) in Plan(periods, utcNow, zone, calculator))
            {
                period.AdvanceTo(target, utcNow);
            }

            // Pinned не чіпається: «пін» — рішення людини, і задача не має
            // його скасовувати (D-77).
            if (project.CurrentPeriodMode == CurrentPeriodMode.Auto)
            {
                project.SetCurrentPeriodAutomatically(calculator.SelectCurrentPeriod(periods)?.Id, utcNow);
            }

            await progress.ReportAsync(
                (i + 1) * 100 / Math.Max(1, projects.Count), project.Code, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Пояс за ідентифікатором; невідомий — UTC із явним падінням у журнал.</summary>
    private static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        // ⛔ Мовчазний UTC при помилці в ідентифікаторі зсунув би межі періодів
        // на кілька годин — і ніхто б не помітив, поки період не закрився б
        // «не тоді». Тому виняток летить далі.
        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }
}
