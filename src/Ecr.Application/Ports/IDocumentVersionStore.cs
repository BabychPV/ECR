using Ecr.Application.Workflow;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання версій документа для порівняння (ФВ-5.22). Версія — зріз подання
/// <c>calc.SubmissionSnapshot</c>; «поточна» — живі комірки документа.
/// </summary>
public interface IDocumentVersionStore
{
    /// <summary>Зрізи подання документа за період, найновіші перші, не більше <paramref name="max"/>.</summary>
    public Task<IReadOnlyList<DocumentVersionRecord>> ListAsync(
        long documentId, PeriodKey periodKey, int max, CancellationToken ct);

    /// <summary>Зріз цього документа; <c>null</c> — немає або належить іншому документу.</summary>
    public Task<DocumentVersionPayload?> FindAsync(long documentId, long versionId, CancellationToken ct);

    /// <summary>
    /// Поточні комірки документа за період, записані тим самим <see cref="SubmissionPayload"/>,
    /// що й зріз подання (з типом дати, булевого, довідника, одиниці): видалені рядки не входять.
    /// </summary>
    public Task<IReadOnlyList<SubmissionPayloadCell>> ReadCurrentAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Таблиця й ключ рядків — зокрема вже видалених.</summary>
    public Task<IReadOnlyDictionary<long, RowLabel>> DescribeRowsAsync(
        PeriodKey periodKey, IReadOnlyCollection<long> rowIds, CancellationToken ct);

    /// <summary>Коди колонок за ідентифікаторами.</summary>
    public Task<IReadOnlyDictionary<int, string>> ColumnCodesAsync(
        IReadOnlyCollection<int> columnDefIds, CancellationToken ct);
}

/// <summary>Зріз подання без вмісту.</summary>
public sealed record DocumentVersionRecord(
    long Id, int SheetDefId, int PeriodKey, DateTime SubmittedAt, int SubmittedByUserId, string? SubmittedByDisplayName);

/// <summary>Зріз подання з вмістом.</summary>
public sealed record DocumentVersionPayload(long Id, int PeriodKey, string PayloadJson);

/// <summary>Підпис рядка для людини.</summary>
public sealed record RowLabel(string TableCode, string RowKey);
