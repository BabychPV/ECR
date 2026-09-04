using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Повна валідація документа перед поданням (ФВ-5.1).</summary>
public sealed class ValidateDocumentHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    ValidationEngine engine,
    IUnitOfWork uow)
{
    /// <summary>Виконує валідацію всіх аркушів документа за період.</summary>
    /// <returns>Повідомлення трьох рівнів; наявність <c>Error</c> блокує <c>Submit</c>.</returns>
    public Task<IReadOnlyList<ValidationMessage>> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прогнати правила рівнів Cell/Row/Table/Document по всіх таблицях документа; " +
            "зберегти підсумок у wf.ValidationResult; повернути повний перелік. " +
            "Бюджет — 3 с p95 на весь документ, тому читати дані пакетно, а не по таблиці.");
}
