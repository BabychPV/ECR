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
    public ApprovalRoute(
        EcrCode code, LocalizedText name, int? projectId = null, int? templateVersionId = null)
    {
        Code = code.Value;
        NameL10n = name;
        IsActive = true;
        ProjectId = projectId;
        TemplateVersionId = templateVersionId;
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

    public IReadOnlyList<ApprovalStep> Steps => _steps;

    /// <summary>
    /// Наскільки маршрут конкретний: більше — важливіше.
    /// </summary>
    /// <remarks>
    /// ⚠ Обчислюване, а не збережене поле. Збережений пріоритет розійшовся б
    /// з областю дії при першій же правці, і маршрут почав би вигравати не
    /// там, де налаштований.
    /// </remarks>
    public int Specificity
        => (ProjectId is null ? 0 : 2) + (TemplateVersionId is null ? 0 : 1);

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
