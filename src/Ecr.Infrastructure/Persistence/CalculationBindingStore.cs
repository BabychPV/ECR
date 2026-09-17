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
    public async Task<BoundColumnRef?> FindColumnAsync(int columnDefId, CancellationToken ct)
        => await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.Id == columnDefId && !c.IsDeleted)
            .Select(c => new BoundColumnRef(c.TableDefId, c.Code, c.DataType))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> ListColumnCodesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        if (tableDefIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<string>>();
        }

        var rows = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => tableDefIds.Contains(c.TableDefId) && !c.IsDeleted)
            .Select(c => new { c.TableDefId, c.Code })
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.TableDefId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Code).ToList());
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ З'єднання йде через <c>ColumnDef → TableDef → SheetDef</c>, бо
    /// <c>cfg.CalculationBinding</c> власного <c>TemplateVersionId</c> не має.
    /// Вибірка за самим <c>TableDefId</c> прив'язки була б коротшою і хибною:
    /// адреса значення — колонка, і саме її належність версії питають.
    /// </remarks>
    public async Task<IReadOnlySet<int>> ListBoundColumnIdsAsync(
        int templateVersionId, CancellationToken ct)
    {
        var ids = await (
                from binding in db.CalculationBindings.AsNoTracking()
                where binding.IsActive
                join column in db.ColumnDefs.AsNoTracking()
                    on binding.ColumnDefId equals column.Id
                join table in db.TableDefs.AsNoTracking()
                    on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking()
                    on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == templateVersionId
                select column.Id)
            .Distinct()
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids.ToHashSet();
    }

    /// <inheritdoc />
    public void Add(CalculationBinding binding) => db.CalculationBindings.Add(binding);
}
