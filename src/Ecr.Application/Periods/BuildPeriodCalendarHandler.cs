// src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
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
    /// <summary>
    /// Створює періоди, яких ще немає, і перераховує межі наявних — лише коли викликач має право ПИСАТИ в проєкт і
    /// це не симуляція; інакше нічого не робить і не відмовляє (читання календаря перевіряє
    /// <see cref="GetPeriodCalendarHandler"/>).
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів створено цим викликом (0 — нічого не будувалося).</returns>
    /// <remarks>
    /// ⛔ A1-01 (аудит 09.10c). Обробник викликається з <c>GET …/periods</c> перед читанням. Доти він ВИМАГАВ грант
    /// <c>Read</c> на проєкт і кидав <c>403</c>: роль, звужена аркушами чи періодами (D-214, D-302), гранти якої
    /// живуть у звуженому шарі, а не в <c>Grants</c>, календаря не бачила ЗОВСІМ, хоча
    /// <see cref="GetPeriodCalendarHandler"/> її навмисно пускає. А читач із грантом <c>Read</c> і адміністратор у
    /// режимі симуляції своїм GET писали в <c>cfg.Period</c> (симуляція блокує лише не-GET). Тепер побудова — побічна
    /// дія лише для того, хто й так може писати в проєкт (грант <c>Write</c>+ і <c>Document.View</c> у ньому), і
    /// ніколи — у симуляції; усі інші просто читають наявне.
    /// </remarks>
    public async Task<int> HandleAsync(int projectId, CancellationToken ct)
    {
        // ⛔ A1-01: симуляція «очима користувача» — лише читання, навіть на GET.
        if (currentUser.SimulationSessionId is not null || currentUser.UserId is not { } userId)
        {
            return 0;
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ Q-246/S17: без гранта на проєкт — не пишемо нічого (і не розповідаємо, чи проєкт є: відмову дасть
        // читання). ⛔ A1-01: запис календаря — рівень ПРОЄКТУ, тож потрібен грант на запис у сам проєкт; грант
        // звуженого шару (D-214) у `LevelFor` не видно — і так і має бути: звужена роль календар лише читає.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Write
            || !Security.PermissionCheck.IsGrantedIn(profile, "Document.View", projectId))
        {
            return 0;
        }

        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            return 0;
        }

        // ⚠ Сама побудова живе в `PeriodCalendarMaterializer`, бо той самий
        // календар потрібен і активації проєкту, у якої ІНШЕ право. Тут
        // лишилося рівно те, що специфічне для цього маршруту: перевірка прав
        // вище і збереження нижче.
        var now = clock.UtcNow;
        var created = await materializer.MaterializeAsync(project, now, ct).ConfigureAwait(false);

        // ⛔ Зберігаємо ЗАВЖДИ, а не лише коли щось створено. До `A7-26` тут
        // стояло дострокове повернення при `created.Count == 0` — і перераховані
        // межі наявних періодів просто губилися. Зміна політики або поясу
        // майданчика не діяла ніколи, а період, створений в обхід календаря,
        // назавжди лишався з межами `0001-01-01`, тобто вважався закритим.
        //
        // ⚠ Це не зайвий запис: EF надсилає UPDATE лише для рядків, які справді
        // змінилися, тому другий виклик поспіль не робить нічого.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⚠ Межі рахуються від дат проєкту, а не від «зараз»; момент потрібен
        // лише сторожу «межу закриття вже минуто» (V9-01) і журналу викликача.
        BuiltAt = now;
        return created.Count;
    }

    /// <summary>Момент останньої побудови.</summary>
    public DateTime BuiltAt { get; private set; }
}
