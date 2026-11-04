using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>Рядок складу документа, що є оригіналом аркуша або його копією.</summary>
/// <param name="Id">Ідентифікатор (<c>doc.DocumentSheet.Id</c>) — він же <c>SheetCopyId</c> у даних копії.</param>
/// <param name="OriginalId">Оригінал групи (для оригіналу — він сам).</param>
/// <param name="SheetDefId">Аркуш шаблону.</param>
/// <param name="Ordinal">Номер у групі: 1 — оригінал, 2… — копії.</param>
/// <param name="IsCopy">Чи це копія.</param>
public sealed record SheetCopyRef(long Id, long OriginalId, int SheetDefId, short Ordinal, bool IsCopy);

/// <summary>Стан копії у періоді, що забороняє її видалення (<c>Submitted</c>/<c>Approved</c>).</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="Status">Стан.</param>
public sealed record SheetCopyWorkflowBlock(int PeriodKey, DocumentStatus Status);

/// <summary>Копія в групі: Id і номер.</summary>
/// <param name="SheetCopyId">Id копії (<c>doc.DocumentSheet.Id</c>).</param>
/// <param name="Ordinal">Номер у групі (2…).</param>
public sealed record SheetCopyOrdinal(long SheetCopyId, short Ordinal);

/// <summary>Назва й копії аркуша у складі документа — для показу «&lt;Назва&gt; N» (<c>SheetCopyNaming</c>).</summary>
/// <param name="SheetDefId">Аркуш шаблону.</param>
/// <param name="NameOverride">Назва групи, задана в документі; <c>null</c> — з шаблону.</param>
/// <param name="Copies">Копії за зростанням номера; порожньо — копій немає.</param>
public sealed record SheetCopyGroupInfo(
    int SheetDefId, LocalizedText? NameOverride, IReadOnlyList<SheetCopyOrdinal> Copies);

/// <summary>Група аркуша в документі: оригінал і його копії.</summary>
/// <param name="OriginalId">Оригінал.</param>
/// <param name="SheetDefId">Аркуш шаблону.</param>
/// <param name="NameOverride">Назва групи, задана в документі.</param>
/// <param name="CopyCount">Скільки копій.</param>
public sealed record SheetCopyGroup(long OriginalId, int SheetDefId, LocalizedText? NameOverride, int CopyCount);

/// <summary>Копії аркушів документа: склад (<c>doc.DocumentSheet</c>) і його наслідки в даних.</summary>
/// <remarks>
/// ⛔ Усі методи — лише ВСЕРЕДИНІ транзакції, у якій викликач уже тримає ВИНЯТКОВЕ блокування структури документа
/// (<see cref="ISheetEditGate.EnterStructureAsync"/>): воно виключає паралельні правки, подання й імпорт, тож
/// «перевірив — змінив» атомарне, а дві одночасні операції над групою не змішуються.
/// </remarks>
public interface IDocumentSheetCopyStore
{
    /// <summary>
    /// Назви груп і копії усіх аркушів документа — читання для показу (Excel, API). Не вимагає блокування структури.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Лише групи з назвою в документі або з копіями; решта показується за шаблоном.</returns>
    public Task<IReadOnlyList<SheetCopyGroupInfo>> ListGroupsAsync(long documentId, CancellationToken ct);

    /// <summary>Група аркуша в складі документа; <c>null</c> — аркуша в складі немає.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SheetCopyGroup?> FindGroupAsync(long documentId, int sheetDefId, CancellationToken ct);

    /// <summary>Рядок складу документа за Id (оригінал чи копія); <c>null</c> — не належить документу.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetCopyId">Рядок складу.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SheetCopyRef?> FindAsync(long documentId, long sheetCopyId, CancellationToken ct);

    /// <summary>Додає порожню копію оригіналу з наступним номером групи.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Id копії та її номер. Екземпляри таблиць у періодах заводить <c>RowStore</c> ліниво.</returns>
    public Task<(long SheetCopyId, short Ordinal)> AddCopyAsync(long documentId, int sheetDefId, CancellationToken ct);

    /// <summary>Задає назву групи на оригіналі.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="name">Нова назва.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RenameGroupAsync(long documentId, int sheetDefId, LocalizedText name, CancellationToken ct);

    /// <summary>
    /// Перший (за періодом) стан копії <c>Submitted</c>/<c>Approved</c>, прочитаний ПІД блокуванням; <c>null</c> — немає.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetCopyId">Копія.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SheetCopyWorkflowBlock?> FindBlockingStateAsync(long documentId, long sheetCopyId, CancellationToken ct);

    /// <summary>
    /// Видаляє копію: екземпляри, рядки й комірки в кожному періоді, стани й журнал подій, сам рядок складу — і
    /// перенумеровує решту копій групи (3→2). Зрізи подань (<c>calc.SubmissionSnapshot</c>) лишаються.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetCopyId">Копія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки комірок видалено.</returns>
    public Task<int> DeleteCopyAsync(long documentId, long sheetCopyId, CancellationToken ct);
}
