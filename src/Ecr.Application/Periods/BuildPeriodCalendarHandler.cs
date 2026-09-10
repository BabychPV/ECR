// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Periods;

/// <summary>Будує календар періодів проєкту (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarHandler(
    IPeriodStore periods,
    IUnitOfWork uow,
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Створює періоди, яких ще немає.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів створено цим викликом.</returns>
    /// <exception cref="NotFoundException">Проєкт не знайдено.</exception>
    public async Task<int> HandleAsync(int projectId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        // ⛔ `ECR-PRJ-0404`, а не `ECR-PRD-0422` (`P-25`, рядок 4). Старий код
        // суперечив сам собі: його цифри кажуть 422, а `NotFoundException`
        // віддає 404 — клієнт, який виводить HTTP із коду, читав із однієї
        // відповіді два різні статуси. Заразом виправлено суб'єкт: немає
        // ПРОЄКТУ, а не «період поза межами проєкту».
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException(
                          ErrorCodes.ProjectNotFound, $"Проєкт {projectId} не знайдено.");

        var policy = await periods.GetPolicyAsync(project.PeriodPolicyId, ct).ConfigureAwait(false);

        // ⚠ Межі рахуються опівночі В ПОЯСІ МАЙДАНЧИКА і лише потім переводяться
        // в UTC (D-68). Невідомий ідентифікатор поясу — виняток, а не мовчазний
        // UTC: зсув на кілька годин ніхто б не помітив, поки період не закрився
        // б «не тоді».
        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        // Ідемпотентність забезпечує сам календар: він СТВОРЮЄ лише ті періоди,
        // яких ще немає, і перераховує межі наявних. Повторний виклик після
        // зміни меж проєкту добудує хвіст, а не подвоїть наявне.
        //
        // ⛔ T6/#36: `CustomPeriodCount` передається ЯВНО, а не через параметр
        // за замовчуванням. До цього виклик завжди йшов з `customCount = 0`, і
        // `PeriodKind.Custom` був недосяжний через API: `CountFor` кидав
        // `ECR-PRD-4224` для БУДЬ-ЯКОГО Custom-проєкту на першому ж
        // `GET …/periods`, незалежно від того, що ввів користувач при
        // створенні.
        var created = PeriodCalendar.Build(
            project, policy, zone, project.Periods, project.CustomPeriodCount ?? 0);

        if (created.Count > 0)
        {
            periods.AddRange(created);
        }

        // ⛔ Зберігаємо ЗАВЖДИ, а не лише коли щось створено. До `A7-26` тут
        // стояло дострокове повернення при `created.Count == 0` — і перераховані
        // межі наявних періодів просто губилися. Зміна політики або поясу
        // майданчика не діяла ніколи, а період, створений в обхід календаря,
        // назавжди лишався з межами `0001-01-01`, тобто вважався закритим.
        //
        // ⚠ Це не зайвий запис: EF надсилає UPDATE лише для рядків, які справді
        // змінилися, тому другий виклик поспіль не робить нічого.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // clock тут не для меж — вони від дат проєкту, а не від «зараз», — а
        // щоб зафіксувати момент побудови в журналі викликача.
        BuiltAt = clock.UtcNow;
        return created.Count;
    }

    /// <summary>Момент останньої побудови.</summary>
    public DateTime BuiltAt { get; private set; }
}
