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
}

/// <summary>Формула методики — рівно те, що потрібно розібрати вираз.</summary>
public sealed record VersionFormulaText(int Id, string Code, string Expression);
