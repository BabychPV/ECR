// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;

namespace Ecr.Application.Periods;

/// <summary>Будує календар періодів проєкту (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarHandler(IPeriodStore periods, IUnitOfWork uow, IClock clock)
{
    /// <summary>Створює періоди, яких ще немає.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів створено цим викликом.</returns>
    /// <exception cref="NotFoundException">Проєкт не знайдено.</exception>
    public async Task<int> HandleAsync(int projectId, CancellationToken ct)
    {
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException("ECR-PRD-0422", $"Проєкт {projectId} не знайдено.");

        var policy = await periods.GetPolicyAsync(project.PeriodPolicyId, ct).ConfigureAwait(false);

        // ⚠ Межі рахуються опівночі В ПОЯСІ МАЙДАНЧИКА і лише потім переводяться
        // в UTC (D-68). Невідомий ідентифікатор поясу — виняток, а не мовчазний
        // UTC: зсув на кілька годин ніхто б не помітив, поки період не закрився
        // б «не тоді».
        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        // Ідемпотентність забезпечує сам календар: він повертає ЛИШЕ ті періоди,
        // яких ще немає. Повторний виклик після зміни меж проєкту добудує
        // хвіст, а не подвоїть наявне.
        var created = PeriodCalendar.Build(project, policy, zone, project.Periods);
        if (created.Count == 0)
        {
            return 0;
        }

        periods.AddRange(created);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // clock тут не для меж — вони від дат проєкту, а не від «зараз», — а
        // щоб зафіксувати момент побудови в журналі викликача.
        BuiltAt = clock.UtcNow;
        return created.Count;
    }

    /// <summary>Момент останньої побудови.</summary>
    public DateTime BuiltAt { get; private set; }
}
