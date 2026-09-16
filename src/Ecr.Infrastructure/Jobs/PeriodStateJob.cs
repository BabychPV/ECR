using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
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
    IUnitOfWork uow,
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
    ///
    /// ⛔ Саме рішення живе тепер у домені (<see cref="PeriodStateCalculator.Plan"/>),
    /// а не тут: доки воно лежало в <c>Ecr.Infrastructure</c>, прикладний шар
    /// не мав до нього шляху, і активація проєкту не могла відкрити період
    /// сама — вона чекала наступного годинного прогону (директива №09 `W8`,
    /// `S-11`). Тут лишився перехідник до вже наявних викликів.
    /// </remarks>
    public static IReadOnlyList<Transition> Plan(
        IReadOnlyList<Period> periods,
        DateTime utcNow,
        TimeZoneInfo siteTimeZone,
        PeriodStateCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);

        return [.. calculator
            .Plan(periods, utcNow, siteTimeZone)
            .Select(t => new Transition(t.Period, t.Target))];
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

            // ⛔ Читання під UPDLOCK і запис — В ОДНІЙ транзакції, з комітом
            // на КОЖНИЙ проєкт (аудит 2026-09-16, §6.1). До цього фіксу
            // транзакції не було зовсім: `UPDLOCK` поза явною транзакцією
            // звільняється щойно завершується сам `SELECT` — задовго до
            // `AdvanceTo` і задовго до `SaveChangesAsync`, який до цього
            // викликався один раз ПІСЛЯ циклу по всіх активних проєктах.
            // Тобто коментар обіцяв взаємовиключення, а блокування не
            // тримало нічого.
            //
            // Сценарій: задача читає період о T1 і вирішує закрити його. До
            // власного коміту користувач відкриває період через Reopen —
            // бачить ще Open, дозволяє, комітить. Задача потім комітить уже
            // обчислений перехід у Closed, тихо перекриваючи Reopen: конфлікту
            // немає, бо в `Period` немає RowVersion.
            //
            // ⚠ Коміт по одному проєкту, а не один фінальний: «довгі
            // транзакції заборонені» (D-29), а блокування, взяте на першому
            // проєкті, трималося б до кінця прогону по всіх.
            await uow.ExecuteInTransactionAsync(async innerCt =>
            {
                // ⚠ UPDLOCK: Reopen бере той самий рядок так само (ФВ-1.10a).
                // Тепер блокування справді тримається до кінця транзакції.
                var periods = await db.Periods
                    .FromSql($"""
                        SELECT * FROM doc.Period WITH (UPDLOCK, ROWLOCK)
                         WHERE ProjectId = {project.Id}
                        """)
                    .ToListAsync(innerCt)
                    .ConfigureAwait(false);

                foreach (var (period, target) in Plan(periods, utcNow, zone, calculator))
                {
                    period.AdvanceTo(target, utcNow);
                }

                // Pinned не чіпається: «пін» — рішення людини, і задача не має
                // його скасовувати (D-77).
                if (project.CurrentPeriodMode == CurrentPeriodMode.Auto)
                {
                    project.SetCurrentPeriodAutomatically(
                        calculator.SelectCurrentPeriod(periods)?.Id, utcNow);
                }

                await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await progress.ReportAsync(
                (i + 1) * 100 / Math.Max(1, projects.Count), project.Code, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Правила поясу майданчика за збереженим ідентифікатором IANA.</summary>
    /// <remarks>
    /// ⛔ Мовчазного UTC тут НЕМАЄ і бути не може. Він був: порожній
    /// ідентифікатор повертав <c>TimeZoneInfo.Utc</c> — при тому, що сусідній
    /// коментар обіцяв протилежне. Для майданчика на <c>Asia/Aqtau</c> це
    /// зсунуло б кожну межу періоду на п'ять годин, і «31 числа о 23:59»
    /// закривалося б о 18:59 за місцем — тобто рівно посеред робочого дня,
    /// коли форми ще дозаповнюють. Помітили б це лише за скаргою «не встиг
    /// подати», і причину шукали б де завгодно, крім порожньої колонки.
    ///
    /// ⚠ Виняток тут — не аварія задачі, а єдиний спосіб дізнатися, що в базі
    /// лежить пояс, якого система не знає.
    /// </remarks>
    private static TimeZoneInfo ResolveZone(string? timeZoneId)
        => SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();
}
