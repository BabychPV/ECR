// src/Ecr.Infrastructure/Persistence/CalculationBindingStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="ICalculationBindingStore"/> над <see cref="EcrDbContext"/>.
/// </summary>
public sealed class CalculationBindingStore(EcrDbContext db) : ICalculationBindingStore
{
    /// <summary>Стеля вибірки: прив'язок на методологію — десятки, не тисячі.</summary>
    /// <remarks>
    /// Та сама межа й з тієї ж причини, що в решті сховищ: помилка в даних без
    /// неї виглядала б як повільність, а не як помилка.
    /// </remarks>
    private const int MaxBindings = 5_000;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Відстежувана навмисно: саме цей рядок повторний <c>PUT</c> змінює на
    /// місці, і <c>AsNoTracking</c> зробив би зміну нікуди не збереженою — а
    /// «створити наново» змінило б <c>Id</c>, на який уже посилаються.
    /// </remarks>
    public async Task<CalculationBinding?> FindAsync(
        int columnDefId, int methodologyId, string outputCode, CancellationToken ct)
        => await db.CalculationBindings
            .FirstOrDefaultAsync(
                b => b.ColumnDefId == columnDefId
                     && b.MethodologyId == methodologyId
                     && b.OutputCode == outputCode,
                ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalculationBinding>> ListAsync(
        int methodologyId, CancellationToken ct)
        => await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.MethodologyId == methodologyId)
            .OrderBy(b => b.ColumnDefId)
            .ThenBy(b => b.OutputCode)
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Видалена колонка не годиться: прив'язка на неї має <c>TableDefId</c>,
    /// але значення нікуди покласти — <c>GetTableSliceHandler</c> м'яко видалених
    /// колонок не віддає.
    /// </remarks>
    public async Task<int?> FindTableOfColumnAsync(int columnDefId, CancellationToken ct)
        => await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.Id == columnDefId && !c.IsDeleted)
            .Select(c => (int?)c.TableDefId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(CalculationBinding binding) => db.CalculationBindings.Add(binding);
}
