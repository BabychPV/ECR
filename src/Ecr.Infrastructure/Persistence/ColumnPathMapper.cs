// src/Ecr.Infrastructure/Persistence/ColumnPathMapper.cs
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IColumnPathMapper"/> (C1): шлях «аркуш → таблиця → колонка» за кодами.</summary>
/// <remarks>
/// Два запити: шляхи вихідних колонок і колонки цільової версії з тими самими кодами. Якщо всі вихідні колонки вже
/// належать цільовій версії (документ на тій самій версії, що й правила), другий запит не потрібен — це найчастіший
/// випадок, і він не додає навантаження до гарячого читання зрізу.
/// </remarks>
public sealed class ColumnPathMapper(EcrDbContext db) : IColumnPathMapper
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> GetTemplateVersionsOfTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);

        if (tableDefIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var wanted = tableDefIds.Distinct().ToList();
        return await (
                from table in db.TableDefs.AsNoTracking()
                where wanted.Contains(table.Id)
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                select new { table.Id, sheet.TemplateVersionId })
            .ToDictionaryAsync(t => t.Id, t => t.TemplateVersionId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> MapToVersionAsync(
        IReadOnlyCollection<int> sourceColumnIds, int targetTemplateVersionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceColumnIds);

        if (sourceColumnIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var wanted = sourceColumnIds.Distinct().ToList();
        var sources = await (
                from column in db.ColumnDefs.AsNoTracking()
                where wanted.Contains(column.Id)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                select new { column.Id, sheet.TemplateVersionId, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<int, int>();

        // Колонка цільової версії відображається сама на себе (навіть вилучена: правило, що її згадує, — її власне).
        foreach (var own in sources.Where(s => s.TemplateVersionId == targetTemplateVersionId))
        {
            result[own.Id] = own.Id;
        }

        var foreign = sources.Where(s => s.TemplateVersionId != targetTemplateVersionId).ToList();
        if (foreign.Count == 0)
        {
            return result;
        }

        var columnCodes = foreign.Select(s => s.ColumnCode).Distinct().ToList();
        var targets = await (
                from column in db.ColumnDefs.AsNoTracking()
                where !column.IsDeleted && columnCodes.Contains(column.Code)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                where sheet.TemplateVersionId == targetTemplateVersionId && !table.IsDeleted && !sheet.IsDeleted
                select new { column.Id, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byPath = targets
            .GroupBy(t => (t.SheetCode, t.TableCode, t.ColumnCode))
            .ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var source in foreign)
        {
            if (byPath.TryGetValue((source.SheetCode, source.TableCode, source.ColumnCode), out var local))
            {
                result[source.Id] = local;
            }
        }

        return result;
    }
}
