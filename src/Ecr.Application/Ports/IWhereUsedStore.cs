// src/Ecr.Application/Ports/IWhereUsedStore.cs
using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>«Де використовується» колонка шаблону (ФВ-8.14).</summary>
/// <remarks>✎ P-1124-1: «де використовується» формульної константи прибрано разом з формульним UI.</remarks>
public interface IWhereUsedStore
{
    /// <summary>Чи існує невидалена колонка.</summary>
    public Task<bool> ColumnExistsAsync(int columnDefId, CancellationToken ct);

    /// <summary>Перші <paramref name="take"/> посилань на колонку і їх загальна кількість.</summary>
    public Task<UsageResponse> FindColumnUsageAsync(int columnDefId, int take, CancellationToken ct);

    /// <summary>
    /// Правила валідації ТАБЛИЦІ колонки — кандидати «де використовується» (ФВ-8.14), упорядковані за Id.
    /// Чи справді правило посилається на колонку, вирішує обробник розбором виразу.
    /// </summary>
    public Task<IReadOnlyList<ColumnRuleCandidate>> ListColumnRuleCandidatesAsync(int columnDefId, CancellationToken ct);
}

/// <summary>Правило валідації таблиці колонки з готовим рядком переліку.</summary>
/// <param name="Item">Рядок «де використовується» (вид <see cref="UsageKinds.ValidationRule"/>).</param>
/// <param name="Expression">Текст виразу правила.</param>
/// <param name="BoundToColumn">Правило прив'язане саме до цієї колонки (<c>ColumnDefId</c>).</param>
/// <param name="ColumnCode">Код колонки, яку питають, — з ним звіряються посилання виразу.</param>
public sealed record ColumnRuleCandidate(UsageItemDto Item, string Expression, bool BoundToColumn, string ColumnCode);
