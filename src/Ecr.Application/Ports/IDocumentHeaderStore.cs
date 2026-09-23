// src/Ecr.Application/Ports/IDocumentHeaderStore.cs

using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до значень шапки документа (<c>doc.DocumentHeaderValue</c>).
/// </summary>
public interface IDocumentHeaderStore
{
    /// <summary>
    /// Сирі значення шапки документа: <c>HeaderFieldDefId</c> → значення.
    /// Поле без запису в результат не потрапляє (порожня шапка — нормальний
    /// стан щойно створеного документа).
    /// </summary>
    public Task<IReadOnlyDictionary<int, DocumentHeaderValueData>> GetValuesAsync(
        long documentId, CancellationToken ct);

    /// <summary>
    /// Значення шапки як значення виразів, ключовані кодом поля — готові для
    /// <c>IEvaluationContext.GetHeader</c> (<c>HDR.Code</c>). Поле без
    /// значення в результат не потрапляє: відсутній ключ і порожнє значення —
    /// той самий <see cref="ExpressionValue.Null"/> для читача, тому окремо
    /// не розрізняються.
    /// </summary>
    public Task<IReadOnlyDictionary<string, ExpressionValue>> GetExpressionValuesAsync(
        long documentId, CancellationToken ct);

    /// <summary>
    /// Записує (створює або оновлює) значення полів шапки. Хто і коли —
    /// відповідальність викликача (аудит пишеться окремо, той самий поділ,
    /// що й у <c>ICellStore.ApplyAsync</c>/<c>IAuditWriter</c>).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="values"><c>HeaderFieldDefId</c> → нове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task SaveValuesAsync(
        long documentId,
        IReadOnlyDictionary<int, DocumentHeaderValueData> values,
        CancellationToken ct);
}
