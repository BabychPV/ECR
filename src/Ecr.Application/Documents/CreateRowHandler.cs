using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Додає рядок у динамічну таблицю.</summary>
public sealed class CreateRowHandler(
    ICellStore cellStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Створює рядок і повертає його ключ.</summary>
    /// <exception cref="Errors.BusinessRuleException">
    /// Таблиця не дозволяє динамічні рядки, перевищено <c>MaxDynamicRows</c>,
    /// або <c>RowKey</c> уже існує (<c>ECR-ROW-0409</c>).
    /// </exception>
    public Task<RowKey> HandleAsync(long documentId, long tableInstanceId, RowKey? requestedKey,
                                    AccessProfile profile, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити TableDef.AllowsDynamicRows; " +
            "2) перевірити MaxDynamicRows проти поточної кількості; " +
            "3) ключ: requestedKey або RowKey.NewDynamic() — GUID у форматі 'N' (ФВ-2.5); " +
            "4) Id узяти з SEQUENCE doc.TableRowSeq ДО вставки — це дозволяє " +
            "завантажити рядок і комірки одним проходом; " +
            "5) Ordinal = max + 1; 6) перевірити унікальність (PeriodKey, TableInstanceId, RowKey).");
}
