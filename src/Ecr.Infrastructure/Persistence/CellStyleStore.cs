using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICellStyleStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class CellStyleStore(EcrDbContext db) : ICellStyleStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CellStyle>> ReadInstanceAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var key = periodKey.Value;

        // ⚠ Рівність за PeriodKey на ОБОХ таблицях — відсічка партицій (як у NormalizedCellStore).
        return await db.CellStyles.AsNoTracking()
            .Where(s => s.PeriodKeyValue == key
                        && db.TableRows.Any(r => r.PeriodKeyValue == key
                                                 && r.Id == s.TableRowId
                                                 && r.TableInstanceId == tableInstanceId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellStyle>>> ReadInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var result = new Dictionary<long, IReadOnlyList<CellStyle>>();
        if (tableInstanceIds.Count == 0)
        {
            return result;
        }

        var key = periodKey.Value;
        var ids = tableInstanceIds.ToList();

        var pairs = await db.CellStyles.AsNoTracking()
            .Join(
                db.TableRows.Where(r => r.PeriodKeyValue == key && ids.Contains(r.TableInstanceId)),
                s => new { s.PeriodKeyValue, s.TableRowId },
                r => new { r.PeriodKeyValue, TableRowId = r.Id },
                (s, r) => new { r.TableInstanceId, Style = s })
            .Where(p => p.Style.PeriodKeyValue == key)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var group in pairs.GroupBy(p => p.TableInstanceId))
        {
            result[group.Key] = [.. group.Select(p => p.Style)];
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellStyle>> LoadForUpdateAsync(
        PeriodKey periodKey, IReadOnlyCollection<long> rowIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIds);

        if (rowIds.Count == 0)
        {
            return [];
        }

        var key = periodKey.Value;
        var result = new List<CellStyle>();

        // ⚠ Чанки по 1000: EF тут розгортає колекцію в окремі параметри (по одному на елемент), а ліміт SQL Server —
        // 2100 параметрів на запит; 2000 рядків + ключ періоду вкладалися б лише з запасом у 99.
        foreach (var chunk in rowIds.Distinct().Chunk(1000))
        {
            var ids = chunk.ToList();
            result.AddRange(await db.CellStyles
                .Where(s => s.PeriodKeyValue == key && ids.Contains(s.TableRowId))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return result;
    }

    /// <inheritdoc />
    public void Add(CellStyle style) => db.CellStyles.Add(style);

    /// <inheritdoc />
    public void Remove(CellStyle style) => db.CellStyles.Remove(style);
}
