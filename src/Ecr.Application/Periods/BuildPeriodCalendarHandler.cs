// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Periods;

/// <summary>Будує календар періодів проєкту (ФВ-1.5).</summary>
public sealed class BuildPeriodCalendarHandler(
    IPeriodStore periods,
    IUnitOfWork uow,
    IClock clock,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    PeriodCalendarMaterializer materializer)
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
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        // ⛔ `ECR-PRJ-0404`, а не `ECR-PRD-0422` (`P-25`, рядок 4). Старий код
        // суперечив сам собі: його цифри кажуть 422, а `NotFoundException`
        // віддає 404 — клієнт, який виводить HTTP із коду, читав із однієї
        // відповіді два різні статуси. Заразом виправлено суб'єкт: немає
        // ПРОЄКТУ, а не «період поза межами проєкту».
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException(
                          ErrorCodes.ProjectNotFound, $"Проєкт {projectId} не знайдено.",
                          new Dictionary<string, object?>
                          {
                              ["messageKey"] = "err.ECR-PRJ-0404.project",
                              ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                          });

        // ⛔ Q-246: цей обробник не лише ЧИТАЄ — він ПИШЕ нові рядки `cfg.Period`
        // (нижче, `periods.AddRange` + `SaveChangesAsync`). Без цієї перевірки
        // будь-хто з глобальним `Document.View` міг ініціювати запис у чужий
        // проєкт, якого немає навіть у його власному списку `/api/v1/projects`
        // (`ListProjectsHandler` фільтрує саме за цим грантом). Той самий
        // патерн, що й `ActivateProjectHandler`/`RunCalculationHandler` (Q-179):
        // перевірка ПІСЛЯ existence-check, щоб відсутній проєкт лишався 404.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Read)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на проєкт {projectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectGrant",
                    ["projectId"] = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Сама побудова живе в `PeriodCalendarMaterializer`, бо той самий
        // календар потрібен і активації проєкту, у якої ІНШЕ право. Тут
        // лишилося рівно те, що специфічне для цього маршруту: перевірка прав
        // вище і збереження нижче.
        var created = await materializer.MaterializeAsync(project, ct).ConfigureAwait(false);

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
