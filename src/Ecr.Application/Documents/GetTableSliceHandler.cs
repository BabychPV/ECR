using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Зріз таблиці для grid. **Найважчий регулярний запит системи**: бюджет
/// p95 1.5 с на 500×60, з яких 600 мс — вибірка з SQL (tz/08 §8.2).
/// </summary>
public sealed class GetTableSliceHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access)
{
    /// <summary>Читає зріз.</summary>
    public Task<TableSliceDto> HandleAsync(long documentId, long tableInstanceId,
                                           AccessProfile profile, string language, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) метадані таблиці з кешу; 2) cellStore.ReadSliceAsync — ОДИН запит; " +
            "3) access.CanEditSliceAsync — ОДИН виклик на весь зріз; " +
            "4) спроєктувати в TableSliceDto: порожні комірки НЕ включати (ФВ-3.8), " +
            "клієнт візьме DefaultValue; " +
            "5) CellPermissions — компактна мапа 'rowKey:columnCode' → причина заборони, " +
            "щоб grid одразу знав, що read-only, а що приховати. " +
            "Заборонено: N+1, звернення до БД за метаданими, поштучна перевірка прав.");
}
