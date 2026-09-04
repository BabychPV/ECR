using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Імпорт із <c>.xlsx</c> — **завжди** через попередній перегляд diff (ФВ-4.3).
/// </summary>
/// <remarks>
/// Імпорт без перегляду — це спосіб непомітно перезаписати чужу роботу.
/// Тому застосування розділене на два кроки, і між ними користувач бачить,
/// що саме зміниться, що конфліктує і що буде відхилено.
/// </remarks>
public sealed class ExcelImporter(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ImportDiffBuilder diffBuilder) : IExcelImporter
{
    /// <inheritdoc />
    public Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) відкрити книгу; зіставити аркуші й колонки з шаблоном за кодами, " +
            "   не за позиціями — інакше зсув колонки в файлі зіпсує дані;\n" +
            "2) структура не відповідає шаблону → ECR-IMP-0422 з переліком розбіжностей;\n" +
            "3) прочитати поточні значення і побудувати diff;\n" +
            "4) прогнати кожну комірку через access — заборонені НЕ застосовувати " +
            "   і показати перелік (ФВ-4.4);\n" +
            "5) обчислені комірки завжди відхиляти (ECR-CELL-4221);\n" +
            "6) зберегти diff під previewToken з обмеженим часом життя.");

    /// <inheritdoc />
    public Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: підняти збережений diff; ПЕРЕВІРИТИ ЗАНОВО версії рядків — між переглядом " +
            "і застосуванням могла статися чужа правка; застосувати через той самий шлях, " +
            "що й batch-PATCH (Origin = 'Import'), щоб аудит і перерахунок працювали однаково.");
}
