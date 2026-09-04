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
