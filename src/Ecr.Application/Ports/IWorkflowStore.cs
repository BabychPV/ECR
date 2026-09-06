using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до стану робочого процесу і періоду для операцій подання,
/// затвердження і повернення в роботу.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не <see cref="IRepository{T, TId}"/>, з двох причин.
/// Перша: <c>wf.ApprovalState</c> адресується не сурогатним <c>Id</c>, а
/// трійкою «документ × аркуш × період» — саме нею оперують усі use-cases.
/// Друга і важливіша: <see cref="LockPeriodAsync"/> мусить брати рядок періоду
/// з <c>UPDLOCK</c>, бо інакше <c>Reopen</c> і <c>PeriodStateJob</c>
/// перегоняють одне одного (ФВ-1.10a), а «взяти з блокуванням» — не те, що
/// можна виразити узагальненим сховищем.
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>Стан аркуша за період; створює <c>Draft</c>, якщо його ще немає.</summary>
    public Task<ApprovalState> GetOrCreateAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Стани всіх аркушів документа за період.</summary>
    public Task<IReadOnlyList<ApprovalState>> GetSheetsAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Бере період документа **з <c>UPDLOCK</c>** до кінця транзакції.
    /// </summary>
    /// <remarks>
    /// Програвший у гонці побачить актуальний стан, а не застосується до вже
    /// закритого періоду.
    /// </remarks>
    public Task<Period> LockPeriodAsync(long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Зберігає іммутабельний зріз поданих даних (ФВ-5.7).</summary>
    /// <returns>Ідентифікатор зрізу.</returns>
    public Task<long> SaveSnapshotAsync(SubmissionSnapshotRecord snapshot, CancellationToken ct);

    /// <summary>Зрізи аркуша за період, від найновішого.</summary>
    public Task<IReadOnlyList<SubmissionSnapshotRecord>> GetSnapshotsAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Чи має проєкт хоч один поданий аркуш у цьому періоді.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Питання «чи є хоч один», а не перелік: поданий зріз не перераховується
    /// взагалі (ФВ-9.17), і для відмови достатньо одного. Тягнути всі аркуші
    /// заради <c>Count &gt; 0</c> означало б читати таблицю на кожен запуск
    /// перерахунку.
    /// </remarks>
    public Task<bool> HasSubmittedSheetsAsync(int projectId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Маршрут погодження для проєкту і версії шаблону — найконкретніший із
    /// придатних (<c>ФВ-5.17</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Порядок від конкретного до загального:
    /// <list type="number">
    /// <item>проєкт <b>і</b> версія;</item>
    /// <item>проєкт, версія будь-яка;</item>
    /// <item>версія, проєкт будь-який;</item>
    /// <item>типовий — обидві координати порожні;</item>
    /// <item>маршруту немає → затвердження ОДНОЕТАПНЕ, як було.</item>
    /// </list>
    ///
    /// ⚠ Третій рівень директива не називає, але поле
    /// <c>TemplateVersionId</c> існувало в схемі до <c>ProjectId</c>, і
    /// маршрут, налаштований лише на версію, без нього був би тихо мертвим —
    /// рівно той дефект, від якого весь <c>A7</c>.
    ///
    /// ⛔ Пункт 5 — головний. Порожня таблиця маршрутів означає поведінку
    /// **без змін**: seed не створює жодного, і багатоетапність вмикається
    /// тим, що хтось завів маршрут, а не тим, що вийшла нова версія системи.
    /// </remarks>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="templateVersionId">Версія шаблону проєкту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Маршрут із завантаженими кроками або <c>null</c>.</returns>
    public Task<ApprovalRoute?> FindRouteAsync(
        int projectId, int templateVersionId, CancellationToken ct);

    /// <summary>Власний маршрут проєкту (<c>ProjectId = @id</c>) або <c>null</c>.</summary>
    /// <remarks>
    /// ⚠ Саме ВЛАСНИЙ, а не той, що діє. Екран налаштування має показувати,
    /// що налаштовано тут, а не те, що успадковано: інакше «прибрати
    /// маршрут» виглядало б як «нічого не змінилося».
    /// </remarks>
    public Task<ApprovalRoute?> FindProjectRouteAsync(int projectId, CancellationToken ct);

    /// <summary>Додає маршрут.</summary>
    public Task AddRouteAsync(ApprovalRoute route, CancellationToken ct);

    /// <summary>Прибирає маршрут разом із кроками.</summary>
    public Task RemoveRouteAsync(ApprovalRoute route, CancellationToken ct);

    /// <summary>Прибирає всі кроки маршруту, лишаючи сам маршрут.</summary>
    /// <remarks>
    /// ⛔ Окремий метод, бо «очистити список у пам'яті» недостатньо: EF
    /// відмовляється зберігати відв'язані обов'язкові звʼязки і валить
    /// операцію цілком. Кроки мають бути позначені видаленими ЯВНО.
    /// </remarks>
    public Task RemoveStepsAsync(ApprovalRoute route, CancellationToken ct);

    /// <summary>Чи існує роль із таким ідентифікатором.</summary>
    /// <remarks>
    /// ⚠ Крок маршруту посилається на роль числом. Неіснуюча роль дала б
    /// маршрут, який неможливо пройти: документ подали б і не затвердили
    /// ніколи, а причина була б видима лише в базі.
    /// </remarks>
    public Task<bool> RoleExistsAsync(int roleId, CancellationToken ct);
}

/// <summary>
/// Іммутабельний зріз поданих даних.
/// </summary>
/// <remarks>
/// ⚠ Зріз фіксує не лише значення, а й ВЕРСІЇ, за якими їх рахували: шаблон,
/// методології, режими чисел і календаря. Без них «перерахувати як тоді»
/// неможливо, і поданий звіт стає незвіряним (ФВ-9.4).
/// </remarks>
/// <param name="DocumentId">Документ.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="TemplateVersionId">Версія шаблону на момент подання.</param>
/// <param name="MethodologyVersionsJson">Версії методологій.</param>
/// <param name="NumericMode">Режим чисел (ФВ-9.9).</param>
/// <param name="CalendarMode">Календарна конвенція (D-78).</param>
/// <param name="PayloadJson">Значення комірок.</param>
/// <param name="ContentHash">Контрольна сума вмісту.</param>
/// <param name="SubmittedAt">Момент подання.</param>
/// <param name="SubmittedByUserId">Хто подав.</param>
public sealed record SubmissionSnapshotRecord(
    long DocumentId,
    int SheetDefId,
    int PeriodKey,
    int TemplateVersionId,
    string? MethodologyVersionsJson,
    byte NumericMode,
    byte CalendarMode,
    string PayloadJson,
    string ContentHash,
    DateTime SubmittedAt,
    int SubmittedByUserId);
