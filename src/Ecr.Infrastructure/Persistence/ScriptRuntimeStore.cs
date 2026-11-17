using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IScriptRuntimeStore"/> над <see cref="EcrDbContext"/> (MS, план §13.1, §13.9).
/// </summary>
public sealed class ScriptRuntimeStore(EcrDbContext db) : IScriptRuntimeStore
{
    /// <summary>Стеля вибірки: методологій і таблиць у такому читанні — десятки.</summary>
    private const int Max = 10_000;

    /// <summary>Глибина замикання: ланцюг «читач → джерело» довшим за це — помилка даних, а не граф.</summary>
    private const int MaxDepth = 64;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> GetTemplateCodesAsync(
        IReadOnlyCollection<int> templateVersionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templateVersionIds);
        if (templateVersionIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var ids = templateVersionIds.Distinct().ToList();
        var rows = await (
                from v in db.TemplateVersions.AsNoTracking()
                join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                where ids.Contains(v.Id)
                select new { v.Id, t.Code })
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.Code);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetReadTableCodesAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyIds.Distinct().ToList();
        return await (
                from r in db.MethodologyReadTables.AsNoTracking()
                join v in db.MethodologyVersions.AsNoTracking() on r.MethodologyVersionId equals v.Id
                where ids.Contains(v.MethodologyId)
                      && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated)
                select r.TableCode)
            .Distinct()
            .OrderBy(c => c)
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadTableReader>> GetReadersOfTablesAsync(
        IReadOnlyCollection<string> tableCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableCodes);
        if (tableCodes.Count == 0)
        {
            return [];
        }

        var codes = tableCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = await (
                from r in db.MethodologyReadTables.AsNoTracking()
                join v in db.MethodologyVersions.AsNoTracking() on r.MethodologyVersionId equals v.Id
                where codes.Contains(r.TableCode)
                      && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated)
                select new { r.TableCode, v.MethodologyId })
            .Distinct()
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new ReadTableReader(r.TableCode, r.MethodologyId))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyEdge>> GetDependencyEdgesAsync(CancellationToken ct)
        => await db.MethodologyDependencies
            .AsNoTracking()
            .Where(d => d.FromMethodologyId != d.ToMethodologyId)
            .Select(d => new MethodologyEdge(d.FromMethodologyId, d.ToMethodologyId))
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> GetSourceClosureAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
        => ClosureAsync(methodologyIds, towardsSources: true, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> GetReaderClosureAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
        => ClosureAsync(methodologyIds, towardsSources: false, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetBoundTableDefIdsAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var ids = methodologyIds.Distinct().ToList();
        return await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && ids.Contains(b.MethodologyId))
            .Select(b => b.TableDefId)
            .Distinct()
            .OrderBy(id => id)
            .Take(Max)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<int>> ClosureAsync(
        IReadOnlyCollection<int> methodologyIds, bool towardsSources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);

        var seen = new SortedSet<int>(methodologyIds);
        var frontier = seen.ToList();

        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var current = frontier;
            var next = towardsSources
                ? await db.MethodologyDependencies
                    .AsNoTracking()
                    .Where(d => current.Contains(d.FromMethodologyId))
                    .Select(d => d.ToMethodologyId)
                    .Distinct()
                    .Take(Max)
                    .ToListAsync(ct)
                    .ConfigureAwait(false)
                : await db.MethodologyDependencies
                    .AsNoTracking()
                    .Where(d => current.Contains(d.ToMethodologyId))
                    .Select(d => d.FromMethodologyId)
                    .Distinct()
                    .Take(Max)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

            frontier = [.. next.Where(seen.Add)];
        }

        return [.. seen];
    }
}
