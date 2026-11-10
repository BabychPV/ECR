// src/Ecr.Application/Ports/IWhereUsedStore.cs
using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>«Де використовується» константа методики і колонка шаблону (ФВ-8.14).</summary>
public interface IWhereUsedStore
{
    /// <summary>Методологія версії, якщо в ній є константа з таким кодом; інакше <c>null</c>.</summary>
    public Task<int?> FindConstantMethodologyAsync(int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Формули версії методики з текстом виразу, впорядковані за ідентифікатором.</summary>
    public Task<IReadOnlyList<VersionFormulaText>> ListVersionFormulasAsync(
        int methodologyVersionId, CancellationToken ct);

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

/// <summary>Формула методики — рівно те, що потрібно розібрати вираз.</summary>
public sealed record VersionFormulaText(int Id, string Code, string Expression);

/// <summary>Правило валідації таблиці колонки з готовим рядком переліку.</summary>
/// <param name="Item">Рядок «де використовується» (вид <see cref="UsageKinds.ValidationRule"/>).</param>
/// <param name="Expression">Текст виразу правила.</param>
/// <param name="BoundToColumn">Правило прив'язане саме до цієї колонки (<c>ColumnDefId</c>).</param>
/// <param name="ColumnCode">Код колонки, яку питають, — з ним звіряються посилання виразу.</param>
public sealed record ColumnRuleCandidate(UsageItemDto Item, string Expression, bool BoundToColumn, string ColumnCode);
