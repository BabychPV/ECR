using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до проєктів і їхніх періодів для календаря і адміністративних
/// операцій над періодами.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт із тієї самої причини, що й <see cref="IWorkflowStore"/>:
/// <see cref="LockAsync"/> мусить брати рядок періоду з <c>UPDLOCK</c>, бо
/// інакше адміністративне відкриття і <c>PeriodStateJob</c> перегоняють одне
/// одного (ФВ-1.10a). «Взяти з блокуванням» узагальненим сховищем не
/// виражається.
/// </remarks>
public interface IPeriodStore
{
    /// <summary>Проєкт із завантаженими періодами; <c>null</c> — не існує.</summary>
    public Task<Project?> FindProjectAsync(int projectId, CancellationToken ct);

    /// <summary>Політика періодів проєкту.</summary>
    public Task<PeriodPolicy> GetPolicyAsync(int periodPolicyId, CancellationToken ct);

    /// <summary>
    /// Усі політики періодів — для вибору при створенні проєкту.
    /// </summary>
    /// <remarks>
    /// ⚠ Без переліку поле «політика» у формі не має з чого вибирати, а
    /// обробник відхиляє створення без неї (<c>ECR-PRD-0422</c>) — і форма
    /// перетворюється на кнопку, яка ніколи не спрацьовує (<c>A7-56</c>).
    /// Межі сторінки немає: політик одиниці, це конфігурація майданчика.
    /// </remarks>
    public Task<IReadOnlyList<PeriodPolicy>> ListPoliciesAsync(CancellationToken ct);

    /// <summary>Період із <c>UPDLOCK</c> до кінця транзакції.</summary>
    public Task<Period?> LockAsync(int periodId, CancellationToken ct);

    /// <summary>Додає нові періоди календаря.</summary>
    public void AddRange(IEnumerable<Period> periods);

    /// <summary>Додає проєкт; ідентифікатор з'являється після збереження.</summary>
    public Task AddProjectAsync(Project project, CancellationToken ct);

    /// <summary>
    /// Стани періодів проєкту: <c>periodKey</c> → стан.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Конкретний період; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Окремо від <see cref="FindProjectAsync"/> навмисно: перевірка «чи не
    /// закритий період» не потребує ні документів, ні політики, ні поясу — а
    /// агрегат тягне їх усі. На запуску перерахунку повного року це різниця
    /// між одним запитом і завантаженням проєкту цілком.
    /// </remarks>
    public Task<IReadOnlyList<PeriodStateRef>> GetPeriodStatesAsync(
        int projectId, int? periodKey, CancellationToken ct);

    /// <summary>
    /// Межі періоду документа; <c>null</c> — такого періоду немає.
    /// </summary>
    /// <param name="documentId">Документ — через нього знаходиться проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Через ДОКУМЕНТ, а не просто за ключем: <c>PeriodKey</c> не унікальний
    /// глобально, і <c>202601</c> у місячному проєкті — січень, а в
    /// квартальному — перший квартал. Межі залежать від проєкту.
    /// </remarks>
    public Task<PeriodBounds?> FindPeriodBoundsAsync(
        long documentId, int periodKey, CancellationToken ct);

    /// <summary>
    /// Стан періоду документа; <c>null</c> — такого періоду немає.
    /// </summary>
    /// <param name="documentId">Документ — через нього знаходиться проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Заведений заради <c>IsLateEdit</c> (<c>D-70</c>), який до цього
    /// стояв літералом <c>false</c> у ВСІХ трьох місцях, де пишеться зміна
    /// комірки: <c>Period.IsLateEditWindow</c> існував і не мав жодного
    /// читача (директива №09 `W8` п.6). Наслідок — журнал, у якому пізніх
    /// правок не буває ніколи: правку в <c>Grace</c> і після <c>Reopen</c> не
    /// відрізнити від правки в строк, при тому що саме ця відмінність
    /// цікавить того, хто звіряє звітність.
    ///
    /// ⚠ Окремо від <see cref="FindProjectAsync"/> з тієї самої причини, що й
    /// <see cref="GetPeriodStatesAsync"/>: на шляху запису комірок бюджет —
    /// 300 мс на 100 комірок, і завантаження агрегата проєкту з усіма
    /// періодами заради одного значення в нього не вкладається.
    /// </remarks>
    public Task<Ecr.Domain.Enums.PeriodState?> FindPeriodStateAsync(
        long documentId, int periodKey, CancellationToken ct);
}

/// <summary>Стан одного періоду.</summary>
/// <param name="PeriodKey">Ключ періоду (R-A6).</param>
/// <param name="State">Стан; <c>Closed</c> блокує перерахунок (ФВ-9.7).</param>
public sealed record PeriodStateRef(int PeriodKey, Ecr.Domain.Enums.PeriodState State);

/// <summary>Межі періоду — те, з чого рахується його тривалість.</summary>
/// <param name="PeriodStart">Перший день.</param>
/// <param name="PeriodEnd">Останній день.</param>
/// <remarks>
/// ⚠ Саме межі, а не <c>PeriodKey</c>. Вивести тривалість із ключа неможливо:
/// <c>PeriodKey = Year*100 + Sequence</c> (R-A6), і для квартального проєкту
/// <c>202602</c> — це другий КВАРТАЛ, а не лютий. Тлумачити <c>Sequence</c> як
/// місяць означало б поділити на 28 днів замість 91 — усі <c>г/с</c> у звіті
/// стали б утричі більшими (ФВ-16.11a, D-112).
/// </remarks>
public sealed record PeriodBounds(DateOnly PeriodStart, DateOnly PeriodEnd);
