// src/Ecr.Application/Documents/Dto/PatchCellsResponse.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>Результат пакетної зміни.</summary>
/// <param name="AppliedCells">Скільки комірок записано.</param>
/// <param name="RowVersions">Нові версії зачеплених рядків: <c>RowKey</c> → hex.</param>
/// <param name="Validation">Результати валідації рівнів, які не блокують запис (R-B3).</param>
/// <param name="RecalculationJobId">
/// Ідентифікатор поставленої задачі перерахунку формул; <c>null</c> —
/// перерахунку НЕ поставлено (`BE-05`).
/// </param>
/// <remarks>
/// ⛔ <c>RecalculationJobId</c> існує тому, що без нього клієнт міг лише
/// вгадувати таймером, коли обчислені колонки оновляться: відповідь казала
/// «записано», а про асинхронний перерахунок не казала нічого
/// (`useCellPatch.applyPatchLocally` прямо називав це другою половиною
/// `CL-01`, заблокованою серверною зміною).
///
/// ⚠ <c>null</c> — ЧЕСНА відповідь, а не «немає даних»: у гілці
/// <c>deferRecalculationUntilMi02</c> (імпорт книги, `DAT-05`) обробник задачі
/// не ставить узагалі — її поставить викликач після коміту СВОЄЇ транзакції, і
/// назвати тут чужий ідентифікатор означало б збрехати про те, що вже сталося.
/// Порожній рядок у цьому місці був би гіршим за <c>null</c>: клієнт пішов би
/// опитувати <c>GET /jobs/</c> без сегмента.
/// </remarks>
public sealed record PatchCellsResponse(
    int AppliedCells,
    IReadOnlyDictionary<string, string> RowVersions,
    IReadOnlyList<ValidationMessageDto> Validation,
    string? RecalculationJobId = null);

/// <summary>Повідомлення валідації.</summary>
/// <param name="Severity">Рівень: <c>Info</c>/<c>Warning</c>/<c>Error</c>.</param>
/// <param name="RuleCode">Код правила з <c>cfg.ValidationRule</c>.</param>
/// <param name="Message">Локалізований текст.</param>
/// <param name="RowKey">Рядок, якого стосується; <c>null</c> — рівень таблиці.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — рівень рядка.</param>
public sealed record ValidationMessageDto(
    string Severity,
    string RuleCode,
    string Message,
    string? RowKey,
    string? ColumnCode);
