// src/Ecr.Domain/Entities/Workflow/ApprovalRoute.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Маршрут погодження: багатоетапне затвердження конфігурується на рівні
/// проєкту (ФВ-5.17).
/// </summary>
/// <remarks>
/// Кроки посилаються на ролі **за ідентифікаторами**, тому перейменування ролі
/// не ламає вже налаштований маршрут.
///
/// ⛔ Маршрут належить ПРОЄКТУ (<c>ФВ-5.17</c>), і обидві координати області
/// дії необов'язкові: <c>null</c> означає «для всіх». Порожній
/// <see cref="ProjectId"/> і порожній <see cref="TemplateVersionId"/> разом
/// дають типовий маршрут майданчика.
///
/// ⛔ Кроки додаються ТІЛЬКИ через <see cref="AddStep"/>. До цього список був
/// приватний і не мав жодного способу наповнення: сутність існувала, таблиця
/// існувала, а маршрут із двох кроків створити було неможливо — той самий
/// клас, що й `A7-25`.
/// </remarks>
public sealed class ApprovalRoute : Entity<int>
{
    private readonly List<ApprovalStep> _steps = [];

    private ApprovalRoute() { }

    /// <summary>Створює маршрут.</summary>
    /// <param name="code">Код маршруту.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="projectId">Проєкт; <c>null</c> — для всіх проєктів.</param>
    /// <param name="templateVersionId">Версія шаблону; <c>null</c> — для всіх версій.</param>
    /// <param name="templateId">✎ B155: шаблон (усі його версії); <c>null</c> — для всіх шаблонів.</param>
    public ApprovalRoute(
        EcrCode code, LocalizedText name, int? projectId = null, int? templateVersionId = null, int? templateId = null)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
        ProjectId = projectId;
        TemplateVersionId = templateVersionId;
        TemplateId = templateId;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    /// <summary>Проєкт, якому належить маршрут; <c>null</c> — типовий для всіх.</summary>
    /// <remarks>
    /// ⚠ Другої половини області дії бракувало з самого початку: у схемі був
    /// лише <see cref="TemplateVersionId"/>, і «маршрут проєкту» з
    /// <c>ФВ-5.17</c> не мав де зберігатися.
    /// </remarks>
    public int? ProjectId { get; private set; }

    /// <summary>Версія шаблону, до якої прив'язаний маршрут; <c>null</c> — спільний.</summary>
    /// <remarks>
    /// Необов'язкове навмисно: більшість маршрутів однакові для всіх версій,
    /// і вимога вказувати версію означала б переоформлення маршрутів на кожну
    /// публікацію шаблону.
    /// </remarks>
    public int? TemplateVersionId { get; private set; }

    /// <summary>
    /// ✎ B155 (варіант C, 16.11): шаблон, до якого прив'язаний маршрут, — УСІ його версії; <c>null</c> — будь-який.
    /// </summary>
    /// <remarks>
    /// ⚠ Шаблон, а не версія: версія міняється з кожною публікацією, а роль погодження людина ставить на шаблон.
    /// </remarks>
    public int? TemplateId { get; private set; }

    /// <summary>
    /// ✎ B155: крок маршруту ШАБЛОНУ задовольняє його роль АБО вбудована загальна роль <c>Approver</c> (слово людини
    /// 16.11: власна роль шаблону ДОДАЄТЬСЯ до загальної, а не замінює її). Маршрути проєкту, майданчика й версії —
    /// без змін: лише роль кроку.
    /// </summary>
    public bool AllowsGeneralApprover => TemplateId is not null && TemplateVersionId is null;

    public IReadOnlyList<ApprovalStep> Steps => _steps;

    /// <summary>
    /// Наскільки маршрут конкретний: більше — важливіше.
    /// </summary>
    /// <remarks>
    /// ⚠ Обчислюване, а не збережене поле. Збережений пріоритет розійшовся б
    /// з областю дії при першій же правці, і маршрут почав би вигравати не
    /// там, де налаштований.
    /// <para>
    /// ✎ B155 (порядок затверджено координатором 16.11): версія (проєкт і версія &gt; версія) → (проєкт, шаблон) →
    /// (усі проєкти, шаблон) → проєкт → типовий майданчика. Доти маршрут проєкту стояв вище за маршрут лише версії;
    /// маршрутів версії API не створює.
    /// </para>
    /// </remarks>
    public int Specificity => SpecificityOf(ProjectId, TemplateId, TemplateVersionId);

    /// <summary>Конкретність області (проєкт, шаблон, версія) — те саме число, що <see cref="Specificity"/>.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі.</param>
    /// <param name="templateId">Шаблон; <c>null</c> — усі.</param>
    /// <param name="templateVersionId">Версія; <c>null</c> — усі.</param>
    public static int SpecificityOf(int? projectId, int? templateId, int? templateVersionId)
        => templateVersionId is not null ? (projectId is null ? 5 : 6)
            : templateId is not null ? (projectId is null ? 3 : 4)
            : projectId is null ? 0 : 2;

    /// <summary>
    /// Найконкретніший чинний маршрут для проєкту, шаблону й версії шаблону; <c>null</c> — маршруту немає
    /// (затвердження одноетапне).
    /// </summary>
    /// <param name="routes">Маршрути-кандидати (з кроками).</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="templateId">Шаблон документа (шаблон версії); <c>null</c> — невідомий: маршрути шаблону не діють.</param>
    /// <param name="templateVersionId">Версія шаблону документа за період.</param>
    /// <remarks>
    /// ⛔ ЄДИНЕ правило вибору: його викликають і <c>WorkflowStore.FindRouteAsync</c> (затвердження), і черга
    /// «До погодження» (ФВ-12.5), і перезапуск поданих при зміні маршруту (B155). Друга копія розійшлася б із першою
    /// мовчки — користувач бачив би в черзі аркуш, який сервер відмовляється йому затвердити, або навпаки. Рівна
    /// конкретність — стабільний порядок за кодом.
    /// </remarks>
    public static ApprovalRoute? Resolve(
        IEnumerable<ApprovalRoute> routes, int projectId, int? templateId, int templateVersionId)
        => Applicable(routes, projectId, templateId, templateVersionId)
            .OrderByDescending(r => r.Specificity)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// ✎ B155: найконкретніший чинний маршрут ОБЛАСТІ налаштування (без версії): що діє для (проєкт?, шаблон?), якщо
    /// власного маршруту там немає, — для показу «успадковано від …» у редакторі.
    /// </summary>
    /// <param name="routes">Маршрути-кандидати.</param>
    /// <param name="projectId">Проєкт; <c>null</c> — область «усі проєкти»: маршрути проєктів не діють.</param>
    /// <param name="templateId">Шаблон; <c>null</c> — маршрути шаблонів не діють.</param>
    /// <remarks>Маршрути версій не враховуються: API їх не створює, і вони залежать від періоду документа.</remarks>
    public static ApprovalRoute? ResolveForScope(IEnumerable<ApprovalRoute> routes, int? projectId, int? templateId)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes
            .Where(r => r.IsActive
                        && r.TemplateVersionId is null
                        && (r.ProjectId is null || r.ProjectId == projectId)
                        && (r.TemplateId is null || r.TemplateId == templateId))
            .OrderByDescending(r => r.Specificity)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// ✎ B155: чи маршрут області (<paramref name="scopeProjectId"/>, <paramref name="scopeTemplateId"/>) — є він
    /// чи ні — вирішує долю аркуша (проєкт, шаблон, версія): область стосується аркуша, і жоден КОНКРЕТНІШИЙ чинний
    /// маршрут його не перекриває.
    /// </summary>
    /// <param name="routes">Чинні маршрути.</param>
    /// <param name="scopeProjectId">Проєкт області; <c>null</c> — усі.</param>
    /// <param name="scopeTemplateId">Шаблон області; <c>null</c> — усі.</param>
    /// <param name="projectId">Проєкт аркуша.</param>
    /// <param name="templateId">Шаблон аркуша.</param>
    /// <param name="templateVersionId">Версія аркуша за період.</param>
    /// <remarks>
    /// ⛔ Той самий порядок, що й <see cref="Resolve"/> (<see cref="SpecificityOf"/>): аркуш, якого зміна маршруту
    /// області торкається, — рівно той, для якого <see cref="Resolve"/> вибирає (або вибрав би) маршрут цієї області.
    /// Тому лічильник «перезапуститься N поданих» і сам перезапуск рахують одне й те саме.
    /// </remarks>
    public static bool ScopeGoverns(
        IEnumerable<ApprovalRoute> routes,
        int? scopeProjectId,
        int? scopeTemplateId,
        int projectId,
        int? templateId,
        int templateVersionId)
    {
        if ((scopeProjectId is { } p && p != projectId) || (scopeTemplateId is { } t && t != templateId))
        {
            return false;
        }

        var rank = SpecificityOf(scopeProjectId, scopeTemplateId, null);

        return !Applicable(routes, projectId, templateId, templateVersionId)
            .Any(r => r.Specificity > rank);
    }

    /// <summary>Чинні маршрути, область яких стосується аркуша (проєкт, шаблон, версія).</summary>
    private static IEnumerable<ApprovalRoute> Applicable(
        IEnumerable<ApprovalRoute> routes, int projectId, int? templateId, int templateVersionId)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes.Where(r => r.IsActive
                                 && (r.ProjectId is null || r.ProjectId == projectId)
                                 && (r.TemplateVersionId is null || r.TemplateVersionId == templateVersionId)
                                 && (r.TemplateId is null || (templateId is not null && r.TemplateId == templateId)));
    }

    /// <summary>✎ B155: код маршруту області, що його заводить API: один маршрут на область.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі.</param>
    /// <param name="templateId">Шаблон; <c>null</c> — усі.</param>
    /// <remarks>⚠ <c>PRJ_{id}</c> — той самий код, що й у наявного маршруту проєкту (<c>ReplaceApprovalRouteHandler</c>).</remarks>
    public static string ScopeCode(int? projectId, int? templateId)
        => (projectId, templateId) switch
        {
            ({ } p, null) => $"PRJ_{p}",
            ({ } p, { } t) => $"PRJ_{p}_TPL_{t}",
            (null, { } t) => $"TPL_{t}",
            _ => "SITE_DEFAULT",
        };

    /// <summary>Додає крок у кінець маршруту.</summary>
    /// <param name="roleId">Роль, яка затверджує на цьому кроці.</param>
    /// <param name="isOptional">Крок можна пропустити.</param>
    /// <returns>Доданий крок.</returns>
    /// <remarks>
    /// ⚠ Порядковий номер призначає сам маршрут. Дозволити його ззовні
    /// означало б допустити два кроки з однаковим номером — і «наступний
    /// крок» став би невизначеним саме тоді, коли документ уже подано.
    /// </remarks>
    public ApprovalStep AddStep(int roleId, bool isOptional = false)
    {
        var step = new ApprovalStep(Id, _steps.Count + 1, roleId, isOptional);
        _steps.Add(step);

        return step;
    }

    /// <summary>Прибирає всі кроки — маршрут переналаштовується цілком.</summary>
    /// <remarks>
    /// ⛔ Часткова правка кроків не передбачена навмисно: маршрут — це
    /// послідовність, і «змінити третій крок» у ній означає змінити те, після
    /// чого він іде. Заміна цілим набором лишає в аудиті один зрозумілий факт.
    /// </remarks>
    public void ClearSteps() => _steps.Clear();

    /// <summary>
    /// Кроки в порядку проходження.
    /// </summary>
    /// <remarks>
    /// ⛔ Сортування ЯВНЕ. З бази колекція приходить у порядку, який ніхто не
    /// обіцяв: без `ORDER BY` SQL Server має право віддати рядки як завгодно,
    /// і «перший крок маршруту» став би випадковим. Помітили б це не одразу —
    /// на маршруті з двох кроків невдалий порядок трапляється в половині
    /// випадків, а на трьох уже рідше.
    /// </remarks>
    private List<ApprovalStep> Ordered => [.. _steps.OrderBy(s => s.Ordinal)];

    /// <summary>Перший крок маршруту; <c>null</c> — кроків немає.</summary>
    public ApprovalStep? FirstStep => _steps.Count == 0 ? null : Ordered[0];

    /// <summary>
    /// Крок, на якому стоїть погодження.
    /// </summary>
    /// <param name="stepId">Збережений крок; <c>null</c> — маршрут ще не почато.</param>
    /// <remarks>
    /// ⚠ Невідомий крок трактується як «почато не за цим маршрутом» і веде на
    /// ПЕРШИЙ, а не в порожнечу. Інакше зміна маршруту посеред погодження
    /// зробила б документ незатверджуваним назавжди.
    /// </remarks>
    public ApprovalStep? StepAt(int? stepId)
    {
        if (stepId is not { } id)
        {
            return FirstStep;
        }

        return _steps.Find(s => s.Id == id) ?? FirstStep;
    }


    /// <summary>Крок після заданого; <c>null</c> — заданий був останнім.</summary>
    /// <param name="stepId">Поточний крок; <c>null</c> — маршрут ще не почато.</param>
    public ApprovalStep? StepAfter(int? stepId)
    {
        if (stepId is not { } id)
        {
            return FirstStep;
        }

        var ordered = Ordered;
        var index = ordered.FindIndex(s => s.Id == id);

        // Невідомий крок — див. `StepAt`: маршрут починається спочатку.
        if (index < 0)
        {
            return FirstStep;
        }

        return index + 1 < ordered.Count ? ordered[index + 1] : null;
    }
}
