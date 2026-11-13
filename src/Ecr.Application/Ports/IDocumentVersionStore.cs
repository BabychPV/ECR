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
/// <remarks>
/// V-2: <c>TemplateVersionId</c> — версія, на якій знято зріз (перенос проєкту зріз не переписує);
/// <c>0</c> — невідома (двійники в тестах), читається як версія документа.
/// </remarks>
public sealed record DocumentVersionRecord(
    long Id, int SheetDefId, int PeriodKey, DateTime SubmittedAt, int SubmittedByUserId, string? SubmittedByDisplayName,
    int TemplateVersionId = 0);

/// <summary>Зріз подання з вмістом.</summary>
/// <remarks>V-2: <c>TemplateVersionId</c> — як у <see cref="DocumentVersionRecord"/>.</remarks>
public sealed record DocumentVersionPayload(long Id, int PeriodKey, string PayloadJson, int SheetDefId, int TemplateVersionId = 0);

/// <summary>Підпис рядка для людини.</summary>
public sealed record RowLabel(string TableCode, string RowKey);
