using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Projects;

/// <summary>Проєкт у переліку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="Status">Стан проєкту.</param>
/// <param name="TimeZoneId">Пояс майданчика — ідентифікатор IANA (`Asia/Aqtau`).</param>
/// <param name="PeriodKind">Періодичність.</param>
/// <param name="CurrentPeriodId">Поточний період — підказка UI, не правило доступу (D-77).</param>
/// <param name="PeriodCount">Скільки періодів у календарі.</param>
public sealed record ProjectSummary(
    int Id,
    string Code,
    ProjectStatus Status,
    string TimeZoneId,
    PeriodKind PeriodKind,
    int? CurrentPeriodId,
    int PeriodCount);

/// <summary>Перелік проєктів. Право <c>Document.View</c>.</summary>
public sealed class ListProjectsHandler(
    IProjectStore projects, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду.</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає сторінку проєктів, видимих користувачу.</summary>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<ProjectSummary>> HandleAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        // ⛔ Родина REQ, а не CELL. Саме цей рядок і назвав дефект (`P-25`,
        // рядок 1): хибний `limit` у переліку ПРОЄКТІВ приходив клієнтові як
        // помилка валідації комірки документа — екрана, до якого користувач
        // ще навіть не дійшов.
        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.");
        }

        var all = await projects.ListAsync(page, ct).ConfigureAwait(false);

        // ⚠ Фільтр за грантами робиться ТУТ, а не запитом: гранти вже
        // розгорнуті в профілі, і другий похід у базу за тим самим нічого не
        // додав би. Але фільтр обов'язковий: перелік проєктів, до яких немає
        // доступу, — це вже розвідка структури підприємства.
        var visible = all.Items
            .Where(p => profile.LevelFor(ResourceKind.Project, p.Id) >= GrantLevel.Read)
            .ToList();

        return new PagedResult<ProjectSummary>(visible, all.NextCursor, all.TotalCount);
    }
}

/// <summary>
/// Політики періодів для вибору при створенні проєкту. Право <c>Project.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Обробник з'явився через <c>A7-56</c>: форма створення проєкту не мала з
/// чого вибирати політику, тому надсилала запит без неї, а сервер відхиляв
/// його з <c>ECR-PRD-0422</c>. Тобто перший крок роботи із системою — створити
/// проєкт — не працював із інтерфейсу взагалі.
/// </remarks>
public sealed class ListPeriodPoliciesHandler(
    IPeriodStore periods, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право керування проєктами: політика потрібна лише при створенні.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Повертає всі політики.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<PeriodPolicyDto>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var all = await periods.ListPoliciesAsync(ct).ConfigureAwait(false);

        return [.. all.Select(p => new PeriodPolicyDto(
            p.Id, p.Code, p.OpenOffsetDays, p.GraceOffsetDays, p.HardCloseOffsetDays, p.YearGraceOffsetDays))];
    }
}

/// <summary>Політика зсувів періодів.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код політики.</param>
/// <param name="OpenOffsetDays">Через скільки днів після початку періоду він відкривається.</param>
/// <param name="GraceOffsetDays">Скільки днів після кінця періоду діє пільговий строк.</param>
/// <param name="HardCloseOffsetDays">Через скільки днів період закривається остаточно.</param>
/// <param name="YearGraceOffsetDays">Пільговий строк на рік.</param>
public sealed record PeriodPolicyDto(
    int Id,
    string Code,
    int OpenOffsetDays,
    int GraceOffsetDays,
    int HardCloseOffsetDays,
    int YearGraceOffsetDays);

/// <summary>
/// Створення політики періодів (T6/#37). Право <c>Project.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ До цього CRUD не було: єдиний спосіб завести політику — сідинг або рука
/// DBA. Річний пільговий строк <c>Project.cs:33</c> стояв літералом
/// <c>45</c> НЕЗАЛЕЖНО від політики саме тому — політику неможливо було
/// налаштувати інакше, ніж редагуючи `09-seed.sql` і перерозгортаючи базу.
/// </remarks>
public sealed class CreatePeriodPolicyHandler(
    IPeriodStore periods, IAccessDecisionService access, ICurrentUser currentUser, IUnitOfWork uow)
{
    /// <summary>Право керування проєктами.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Створює політику.</summary>
    /// <param name="code">Код політики; має бути унікальним.</param>
    /// <param name="openOffsetDays">Коли період відкривається від початку.</param>
    /// <param name="graceOffsetDays">Пільговий строк після кінця періоду.</param>
    /// <param name="hardCloseOffsetDays">Коли період закривається остаточно.</param>
    /// <param name="yearGraceOffsetDays">Пільговий строк після кінця року.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Код зайнятий (<c>ECR-PRD-4091</c>).</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-PRD-4225</c> — пільговий строк довший за жорстке закриття, або
    /// річний пільговий строк від'ємний.
    /// </exception>
    public async Task<PeriodPolicyDto> HandleAsync(
        string code, int openOffsetDays, int graceOffsetDays, int hardCloseOffsetDays,
        int yearGraceOffsetDays, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var ecrCode = EcrCode.Create(code);

        // ⚠ Перевірка ТУТ, а не покладання на `UQ_PeriodPolicy`: без неї
        // помилка друкарки в коді доїжджала б `500`-кою без пояснення поля —
        // той самий клас дефекту, що й `ECR-USR-0409`/`ECR-RPT-4091`.
        // Політик — одиниці (`ListPoliciesAsync` не має межі сторінки саме
        // тому), тож другий похід у базу не потрібен.
        var existing = await periods.ListPoliciesAsync(ct).ConfigureAwait(false);
        if (existing.Any(p => string.Equals(p.Code, ecrCode.Value, StringComparison.Ordinal)))
        {
            throw new BusinessRuleException(
                ErrorCodes.PeriodPolicyDuplicate,
                $"Політика з кодом «{ecrCode.Value}» уже існує.");
        }

        var policy = new PeriodPolicy(
            ecrCode, openOffsetDays, graceOffsetDays, hardCloseOffsetDays, yearGraceOffsetDays);

        periods.AddPolicy(policy);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return PeriodPolicyMapping.ToDto(policy);
    }
}

/// <summary>
/// Зміна offsets наявної політики періодів (T6/#37). Право <c>Project.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Проєкти, які вже посилаються на цю політику, не перераховують межі
/// автоматично: наступний ідемпотентний виклик <c>GET …/periods</c>
/// (<c>BuildPeriodCalendarHandler</c>) підхопить нові offsets сам.
/// </remarks>
public sealed class UpdatePeriodPolicyHandler(
    IPeriodStore periods, IAccessDecisionService access, ICurrentUser currentUser, IUnitOfWork uow)
{
    /// <summary>Право керування проєктами.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Змінює offsets політики.</summary>
    /// <param name="id">Політика.</param>
    /// <param name="openOffsetDays">Коли період відкривається від початку.</param>
    /// <param name="graceOffsetDays">Пільговий строк після кінця періоду.</param>
    /// <param name="hardCloseOffsetDays">Коли період закривається остаточно.</param>
    /// <param name="yearGraceOffsetDays">Пільговий строк після кінця року.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Політики немає (<c>ECR-PRD-0422</c>).</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-PRD-4225</c> — пільговий строк довший за жорстке закриття, або
    /// річний пільговий строк від'ємний.
    /// </exception>
    public async Task<PeriodPolicyDto> HandleAsync(
        int id, int openOffsetDays, int graceOffsetDays, int hardCloseOffsetDays,
        int yearGraceOffsetDays, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var policy = await periods.GetPolicyAsync(id, ct).ConfigureAwait(false);
        policy.UpdateOffsets(openOffsetDays, graceOffsetDays, hardCloseOffsetDays, yearGraceOffsetDays);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return PeriodPolicyMapping.ToDto(policy);
    }
}

/// <summary>Спільне перетворення сутності в DTO для обох обробників CRUD політик.</summary>
internal static class PeriodPolicyMapping
{
    public static PeriodPolicyDto ToDto(PeriodPolicy policy) => new(
        policy.Id, policy.Code, policy.OpenOffsetDays, policy.GraceOffsetDays,
        policy.HardCloseOffsetDays, policy.YearGraceOffsetDays);
}

/// <summary>Створення проєкту. Право <c>Project.Manage</c>.</summary>
public sealed class CreateProjectHandler(
    IPeriodStore periods,
    IAccessDecisionService access,
    IUserStore users,
    IAuditWriter audit,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування проєктами.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Створює проєкт на звітний рік.</summary>
    /// <param name="code">Код проєкту.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="timeZoneId">Пояс майданчика — ідентифікатор IANA (<c>Asia/Aqtau</c>).</param>
    /// <param name="periodKind">Періодичність.</param>
    /// <param name="year">Звітний рік; <c>null</c> — поточний **у поясі майданчика**.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="periodPolicyId">Політика періодів.</param>
    /// <param name="customPeriodCount">
    /// Кількість періодів для <see cref="PeriodKind.Custom"/> (T6/#36);
    /// ігнорується для решти періодичностей.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-CFG-4221</c> — пояс порожній, невідомий або не є ідентифікатором IANA;
    /// <c>ECR-PRD-4224</c> — <paramref name="customPeriodCount"/> поза межами
    /// 1..12 або не ділить рік нарівно (лише для <see cref="PeriodKind.Custom"/>).
    /// </exception>
    public async Task<int> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        string timeZoneId,
        PeriodKind periodKind,
        int? year,
        int templateVersionId,
        int periodPolicyId,
        CancellationToken ct,
        int? customPeriodCount = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Пояс перевіряється ПЕРШИМ із усього, і саме тут. Він вічний
        // (ФВ-1.1a), тож невідомий ідентифікатор став би вічною властивістю
        // проєкту; і без нього не можна порахувати навіть звітний рік нижче.
        //
        // ⛔ Перевіряє `SiteTimeZone`, а не `FindSystemTimeZoneById`. Тут стояв
        // саме він, і вимогу «IANA» не забезпечував: виміряно, що на Windows
        // він приймає і `Central Asia Standard Time`, і `UTC+13`. Тобто
        // директиву №06 §3 старий код проходив лише на вигляд.
        //
        // ⚠ Виняток доменний, тому це 422 з кодом і текстом, а не 500:
        // невідомий пояс — помилка ВВЕДЕННЯ, і той, хто його надіслав, має
        // побачити, що саме не так.
        var zone = SiteTimeZone.Create(timeZoneId);

        // ⛔ Рік беремо в поясі МАЙДАНЧИКА, а не сервера. Тут стояло
        // `clock.UtcNow.Year`, і для майданчика на `Asia/Almaty` (UTC+6)
        // проєкт, створений 1 січня о 03:00 за місцем (це 31 грудня 21:00
        // UTC), отримував МИНУЛИЙ рік: дванадцять періодів із ключами
        // `YYYY*100+N` не того року. `PeriodKey` — ключ партиціонування (R-A6),
        // тож дані поїхали б у чужі партиції й у чужий архів, а виглядало б це
        // як «конфігуратор помилився роком».
        var reportingYear = year ?? TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, zone.ToTimeZoneInfo()).Year;

        // ⛔ Нуль тут — не «значення за замовчуванням», а відсутність вибору.
        // Проєкт без версії шаблону не має структури, без політики періодів —
        // меж; створений таким, він виглядав би робочим до першої спроби
        // відкрити документ.
        if (templateVersionId <= 0)
        {
            throw new BusinessRuleException(
                "ECR-TMPL-0404", "Проєкт неможливо створити без версії шаблону.");
        }

        if (periodPolicyId <= 0)
        {
            throw new BusinessRuleException(
                "ECR-PRD-0422", "Проєкт неможливо створити без політики періодів.");
        }

        // ⛔ Політика завантажується ТУТ, а не лише посилається ідентифікатором
        // (T6/#37): дві причини одразу. Перша — `YearGraceOffsetDays` проєкту
        // раніше був літералом `45` НЕЗАЛЕЖНО від обраної політики (`Project.cs`
        // до цієї правки), тобто дві політики з різним річним грейсом давали
        // проєктам однаковий результат; тепер значення проєкту — це знімок
        // `PeriodPolicy.YearGraceOffsetDays` політики, обраної при створенні.
        // Друга — неіснуючий `periodPolicyId` раніше падав аж на
        // `FK_Project_Policy` під час `SaveChanges` (500 без коду й тексту),
        // а `GetPolicyAsync` віддає `ECR-PRD-0422` заздалегідь.
        var policy = await periods.GetPolicyAsync(periodPolicyId, ct).ConfigureAwait(false);

        // ⛔ T6/#36: перевіряється ТУТ, а не відкладається до першого
        // `GET …/periods`. `PeriodCalendar.CountFor` кидає `ECR-PRD-4224`, якщо
        // кількість поза 1..12 або не ділить рік нарівно — а для решти
        // періодичностей (`Monthly`/`Quarterly`/`Yearly`) аргумент просто
        // ігнорується, тож виклик безпечний завжди.
        var validatedCustomCount = Domain.Services.PeriodCalendar.CountFor(periodKind, customPeriodCount ?? 0);

        var project = new Project(
            EcrCode.Create(code),
            new LocalizedText(name.ToDictionary(StringComparer.Ordinal)),
            new DateOnly(reportingYear, 1, 1),
            new DateOnly(reportingYear, 12, 31),
            templateVersionId,
            periodKind,
            periodPolicyId,

            // Перевірене значення, а не вхідний рядок: у базу має лягти рівно
            // те, за чим порахований `reportingYear` вище.
            zone,
            policy.YearGraceOffsetDays,

            // Зберігається лише для Custom: для решти періодичностей кількість
            // визначає сам `PeriodKind`, і зберігати тут щось означало б давати
            // друге джерело істини про те саме число.
            periodKind == PeriodKind.Custom ? validatedCustomCount : null);

        await periods.AddProjectAsync(project, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Виявлено ПІСЛЯ `Q-179`: якщо є право створити проєкт — є право
        // ним володіти. До цього творець не отримував ЖОДНОГО гранта на
        // щойно створений проєкт — `Activate`/`Archive`/`Clone`/маршрут
        // погодження (усі перевіряють `GrantLevel.Manage` на конкретний
        // `projectId` після `Q-179`) відмовляли б власному творцю доти,
        // доки хтось не видасть грант окремим кроком.
        await GrantOwnershipAsync(profile, project.Id, ct).ConfigureAwait(false);

        return project.Id;
    }

    /// <summary>
    /// Видає щойно створений проєкт у володіння творцю.
    /// </summary>
    /// <remarks>
    /// ⛔ Грант прив'язаний до РОЛІ (`sec.ResourceGrant.RoleId`), не до
    /// користувача — окремої таблиці особистих грантів немає. Рішення
    /// людини: видати грант КОЖНІЙ ролі творця, яка сама несе
    /// <see cref="Permission"/> — тій самій «сумі ролей», яку вже застосовує
    /// <c>AccessDecisionService.LoadAsync</c> для читання грантів (найширший
    /// рівень серед ролей перемагає). Побічний наслідок — свідомо прийнятий:
    /// усі, хто поділяє цю роль із творцем, теж отримують доступ до нового
    /// проєкту, так само як вони вже поділяють саме право його створювати.
    ///
    /// ⚠ НЕ `RotateStampsForRoleAsync`. Перша версія цього фікса викликала
    /// його — і розлоговувала ТВОРЦЯ його ж власною дією: наступний запит
    /// тією самою сесією отримував `401`, бо ротація штампа інвалідує живу
    /// сесію негайно й навмисно (ФВ-6.7) — саме так і мало бути, коли
    /// адміністратор відкликає ЧУЖИЙ доступ, але не тоді, коли користувач
    /// побічно розширює ВЛАСНИЙ. Підтверджено сценарієм
    /// `Творець_одразу_активує_власний_проєкт_без_стороннього_гранта`:
    /// з ротацією — `Unauthorized` на першому ж запиті після створення.
    ///
    /// Замість цього — точкове скидання кешованого профілю ЛИШЕ творця
    /// (`InvalidateProfileAsync`, без зміни штампа): наступний запит цією ж
    /// сесією просто перебудує профіль і побачить новий грант, а сесія
    /// лишається дійсною. Побічний наслідок, прийнятий свідомо: інші носії
    /// тієї самої ролі побачать грант лише при природному сплині кешу (до 30
    /// хв) або новому вході — так само, як будь-яка інша зміна грантів, що
    /// не супроводжується ротацією штампа.
    /// </remarks>
    private async Task GrantOwnershipAsync(AccessProfile profile, int projectId, CancellationToken ct)
    {
        var allRoles = await users.ListRolesAsync(ct).ConfigureAwait(false);
        var qualifyingRoles = allRoles
            .Where(r => profile.RoleIds.Contains(r.Id) && r.Permissions.Contains(Permission))
            .ToList();

        var grantedAny = false;

        foreach (var role in qualifyingRoles)
        {
            var existing = await users.ListGrantsAsync(role.Id, ct).ConfigureAwait(false);

            // ⚠ Проєкт щойно створений — дубліката бути не може за
            // побудовою (`projectId` ще не існував ні для кого), але
            // перевірка тут коштує дешевше за мовчазний `UQ_ResourceGrant`
            // виняток, якби це припущення колись перестало виконуватися.
            if (existing.Any(g => g.ResourceKind == ResourceKind.Project && g.ResourceId == projectId))
            {
                continue;
            }

            var updated = existing
                .Append(new ResourceGrantDto(ResourceKind.Project, projectId, GrantLevel.Manage, IsDeny: false))
                .ToList();

            await users.ReplaceGrantsAsync(role.Id, updated, ct).ConfigureAwait(false);
            grantedAny = true;

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    "ResourceGrantsReplaced",
                    TargetUserId: null,
                    TargetRoleId: role.Id,
                    DetailsJson: JsonSerializer.Serialize(new
                    {
                        role = role.Code,
                        reason = "CreateProjectOwnership",
                        projectId,
                    }),
                    // ⚠ `!.Value`, не повторна перевірка: `PermissionCheck.RequireAsync`
                    // вище вже вимагав автентифікованого користувача, інакше
                    // сюди взагалі не дійшли б.
                    ChangedByUserId: currentUser.UserId!.Value,
                    CorrelationId: currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        if (grantedAny)
        {
            await access.InvalidateProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Активація проєкту: <c>Draft → Active</c>. Право <c>Project.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Обробник закриває `A7-25` — відсутність переходу, без якого система не
/// працює взагалі. Проєкт створюється чернеткою, а <c>PeriodStateJob</c>
/// обробляє лише активні: доки проєкт лишається чернеткою, жоден період не
/// відкривається, і на кожній комірці стоїть «період ще не відкрито». Причина
/// при цьому неправдива — за датами період відкритий.
///
/// ⚠ Активація вимагає, щоб у проєкті БУЛИ періоди. Активний проєкт без
/// періодів — це та сама мовчазна непрацездатність, тільки на крок далі:
/// відкривати нема чого, а стан каже, що все гаразд.
///
/// ⛔ Активація САМА переводить періоди в належний стан — тією ж транзакцією
/// (директива №09 `W8` п.1, `S-11`). Доти єдиним, хто кликав
/// <c>Period.AdvanceTo</c>, був <c>PeriodStateJob</c> на годинному розкладі:
/// щойно активований проєкт до години показував «період ще не відкрито», хоч
/// за датами він давно відкритий. Причина неправдива, а перевірити її
/// оператору нічим — саме той клас дрібниці, що ламає довіру до всього екрана.
/// </remarks>
public sealed class ActivateProjectHandler(
    IPeriodStore periods,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    Domain.Services.PeriodStateCalculator periodStates,
    IClock clock)
{
    /// <summary>Право на активацію.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Активує проєкт.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-PRJ-0422</c> — проєкт уже не чернетка або в ньому немає періодів.
    /// </exception>
    public async Task HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Родина PRJ, а не ROW (`P-25`, рядок 2). `ROW` — це рядок ТАБЛИЦІ
        // ДОКУМЕНТА, і «проєкту немає» доїжджало до обробника помилок сітки,
        // якої на екрані переліку проєктів немає взагалі.
        //
        // ⚠ Існування перевіряється ДО гранта: грант на неіснуючий `projectId`
        // не буває виданий нікому за визначенням, і зворотний порядок
        // перетворив би КОЖЕН запит на неіснуючий проєкт на `403`, приховуючи
        // справжню причину (`ECR-PRJ-0404`) за помилковим кодом гранта.
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Проєкту {projectId} не існує.");

        // ⛔ Q-179 (аудит фази 2, авторизація): грант на КОНКРЕТНИЙ проєкт,
        // не лише глобальне `Project.Manage` — рішення людини. Глобальне
        // право каже «ця людина взагалі керує проєктами», грант — «саме
        // цим». Той самий патерн, що вже застосований до `CreateDocumentHandler`
        // (`Q-176`) і сусідів.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.");
        }

        if (project.Status != Domain.Enums.ProjectStatus.Draft)
        {
            throw new BusinessRuleException(
                ErrorCodes.ProjectActivationInvalid,
                $"Активувати можна лише чернетку; проєкт у стані {project.Status}.");
        }

        if (project.Periods.Count == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.ProjectActivationInvalid,
                "У проєкті немає жодного періоду: активувати нічого.");
        }

        var now = clock.UtcNow;
        project.Activate(now);

        // ⛔ Стани періодів рахуються ТУТ САМО, а не чекають годинного прогону
        // `PeriodStateJob` (директива №09 `W8` п.1, `S-11`). Рішення те саме —
        // спільний `PeriodStateCalculator.Plan`, — тому «примусовий прогін
        // задачі» і «явний перехід» дають однаковий результат; різниця лише в
        // тому, що тут немає ні черги, ні гонки: перехід лягає тією ж
        // транзакцією, що й сама активація.
        //
        // ⚠ Межі періодів уже пораховані календарем (`PeriodCalendar`,
        // побічний ефект `GET …/periods`), інакше активації не було б на чому
        // спрацювати — вона вимагає непорожнього календаря.
        var zone = Domain.ValueObjects.SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo();

        foreach (var (period, target) in periodStates.Plan(project.Periods, now, zone))
        {
            period.AdvanceTo(target, now);
        }

        // ⚠ Поточний період — теж зараз, а не за годину: інакше щойно
        // активований проєкт лишався б без поточного періоду, і кожен екран,
        // що на нього спирається, показував би порожнечу. «Пін» людини не
        // чіпаємо (D-77).
        if (project.CurrentPeriodMode == Domain.Enums.CurrentPeriodMode.Auto)
        {
            project.SetCurrentPeriodAutomatically(
                periodStates.SelectCurrentPeriod(project.Periods)?.Id, now);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Позначає проєкт заархівованим. Право <c>Project.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Другий бік `A7-25`: стан <c>Archived</c> існував у переліку, і до нього
/// не вів жоден перехід. Стан, якого не досягти, — це той самий дефект, що й
/// <c>Active</c> без активації, тільки в кінці життєвого циклу.
///
/// ⚠ Дозволено лише коли ВСІ періоди закриті або заархівовані (`D-123`).
/// Архівація проєкту з відкритим періодом означала б, що дані стають
/// доступними лише для читання під руками того, хто їх заповнює.
/// </remarks>
public sealed class ArchiveProjectHandler(
    IPeriodStore periods,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Право на архівацію.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Позначає проєкт заархівованим.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>ECR-PRD-0409</c> — є незакриті періоди; у подробицях їхній перелік.
    /// </exception>
    public async Task HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування — ДО гранта (див. пояснення в `ActivateProjectHandler`):
        // грант на неіснуючий `projectId` не буває виданий нікому.
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Проєкту {projectId} не існує.");

        // ⛔ Q-179 (аудит фази 2, авторизація): грант на КОНКРЕТНИЙ проєкт,
        // не лише глобальне `Project.Manage` — рішення людини.
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.");
        }

        // ⚠ Перелік незакритих повертається В ПОДРОБИЦЯХ, а не ховається за
        // текстом: людині треба знати, які саме періоди закрити, а не що
        // «щось відкрите».
        var open = project.Periods
            .Where(p => p.State is not (Domain.Enums.PeriodState.Closed))
            .Select(p => p.PeriodKeyValue)
            .Order()
            .ToList();

        if (open.Count > 0)
        {
            throw new ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} має незакриті періоди: архівація неможлива.",
                new Dictionary<string, object?> { ["periodKeys"] = open });
        }

        project.Archive(clock.UtcNow);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Змінює пояс майданчика проєкту. Право <c>Project.Manage</c> (T6/#52).
/// </summary>
/// <remarks>
/// ⚠ Ре-верифіковано проти твердження аудиту (`[звірка]`, слабша впевненість):
/// домен УЖЕ мав повний, протестований <see cref="Project.ChangeTimeZone"/>
/// (перевірено — <c>TimeZoneImmutabilityTests</c> покриває і дозволений, і
/// заборонений випадок), просто без застосункового обробника й ендпоінта над
/// ним. Тобто прогалина була рівно там, де аудит і назвав: не в правилі, а в
/// доступі до нього через API. Само правило («не після відкриття першого
/// періоду», <c>ECR-CFG-4221</c>/<c>ECR-PRD-0409</c>) не чіпається: обробник
/// лише виносить наявний метод сутності на HTTP.
///
/// ⛔ «Шість сутностей для деактивації», згадані в тому ж пункті аудиту, — не
/// реалізовано. Жодного тексту, який їх називає (структура, коментарі, історія
/// git), у цьому репозиторії не знайдено: сам аудит зізнається, що цей
/// підпункт «частково незрозумілий». Вигадувати шість сутностей означало б
/// закривати рядок аудиту, а не проблему; правильна дія тут — назвати
/// прогалину, а не заповнити її здогадкою (правило винятку «факти, яких я не
/// знаю» цього ж проєкту).
/// </remarks>
public sealed class ChangeProjectTimeZoneHandler(
    IPeriodStore periods, IAccessDecisionService access, ICurrentUser currentUser, IUnitOfWork uow)
{
    /// <summary>Право на зміну поясу.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Змінює пояс майданчика.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="timeZoneId">Новий пояс — ідентифікатор IANA.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає (<c>ECR-PRJ-0404</c>).</exception>
    /// <exception cref="DomainException">
    /// <c>ECR-CFG-4221</c> — значення не є відомим ідентифікатором IANA;
    /// <c>ECR-PRD-0409</c> — перший період уже відкривався (ФВ-1.1a).
    /// </exception>
    public async Task HandleAsync(int projectId, string timeZoneId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування — ДО гранта (див. пояснення в `ActivateProjectHandler`):
        // грант на неіснуючий `projectId` не буває виданий нікому.
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(ErrorCodes.ProjectNotFound, $"Проєкту {projectId} не існує.");

        // ⛔ Той самий патерн гранта на КОНКРЕТНИЙ проєкт, що й
        // Activate/Archive/Clone (Q-179): глобальне `Project.Manage` каже «ця
        // людина взагалі керує проєктами», грант — «саме цим».
        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.");
        }

        // Уся перевірка — в сутності: невідомий IANA-ідентифікатор і спроба
        // зміни після відкриття першого періоду обидва йдуть звідти
        // (`Project.ChangeTimeZone`), обробник нічого не дублює.
        project.ChangeTimeZone(timeZoneId);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
