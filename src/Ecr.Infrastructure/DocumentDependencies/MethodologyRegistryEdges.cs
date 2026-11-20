// src/Ecr.Infrastructure/DocumentDependencies/MethodologyRegistryEdges.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.DocumentDependencies;

/// <summary>
/// B179 L1: адаптер <see cref="IMethodologyRegistryEdges"/> — єдине місце графа, що читає таблиці методологій
/// (лише читання; схеми, публікації й UI методологій не змінює — дизайн §8).
/// </summary>
public sealed class MethodologyRegistryEdges(EcrDbContext db) : IMethodologyRegistryEdges
{
    /// <summary>Стеля ребер «читає результати» (правило 6: не «віддати все»); ребер у рази менше, ніж методологій у квадраті.</summary>
    private const int MaxResultReads = 50_000;

    /// <inheritdoc />
    public async Task<string> RevisionAsync(CancellationToken ct)
    {
        var versions = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.Status == TemplateVersionStatus.Published)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(v => v.Id), Last = g.Max(v => v.PublishedAt) })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Прив'язка не має відмітки часу: склад і активність — контрольною сумою (видалення, нова, вимкнення).
        var bindings = await db.CalculationBindings.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(b => (long)b.Id * (b.IsActive ? 1 : 2)) })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Перемикання рушія опублікованої версії (Formulas ↔ Script, script-migration) НЕ чіпає ні її статусу, ні
        // PublishedAt, але переписує ребра довідників і залежності методологій: ревізія бачить їх окремо — без цього
        // граф лишався б зі старими ребрами до стелі життя запису.
        var engines = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.Status == TemplateVersionStatus.Published)
            .GroupBy(_ => 1)
            .Select(g => new { Sum = g.Sum(v => (long)v.Id * ((int)v.Engine + 1)) })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var uses = await db.RegistryUses.AsNoTracking()
            .Where(u => u.SourceKind == RegistryUse.MethodologyVersionSource)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(u => u.Id), Sum = g.Sum(u => (long)u.Id) })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var dependencies = await db.MethodologyDependencies.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(d => d.Id), Sum = g.Sum(d => (long)d.Id) })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"mv{versions?.Count}:{versions?.MaxId}:{versions?.Last?.Ticks}:{engines?.Sum}|cb{bindings?.Count}:{bindings?.Sum}"
            + $"|mu{uses?.Count}:{uses?.MaxId}:{uses?.Sum}|md{dependencies?.Count}:{dependencies?.MaxId}:{dependencies?.Sum}");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyResultReadRow>> ListResultReadsAsync(CancellationToken ct)
        => await (
                from dependency in db.MethodologyDependencies.AsNoTracking()
                join reader in db.Methodologies.AsNoTracking() on dependency.FromMethodologyId equals reader.Id
                join source in db.Methodologies.AsNoTracking() on dependency.ToMethodologyId equals source.Id
                orderby dependency.Id
                select new MethodologyResultReadRow(reader.Id, reader.Code, source.Id, source.Code))
            .Take(MaxResultReads)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRegistryReadRow>> ListReadsAsync(CancellationToken ct)
        => await (
                from use in db.RegistryUses.AsNoTracking()
                where use.SourceKind == RegistryUse.MethodologyVersionSource
                join version in db.MethodologyVersions.AsNoTracking() on use.SourceId equals version.Id
                where version.Status == TemplateVersionStatus.Published
                join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
                orderby methodology.Id, version.Id, use.Id
                select new MethodologyRegistryReadRow(
                    methodology.Id,
                    methodology.Code,
                    version.Id,
                    use.RegistryDefId,
                    use.FormulaCode ?? string.Empty,
                    db.MethodologyFormulas
                        .Where(f => f.MethodologyVersionId == version.Id && f.Code == use.FormulaCode)
                        .Select(f => f.Expression)
                        .FirstOrDefault())
                {
                    ScriptText = version.Engine == MethodologyEngine.Script ? version.ScriptText : null,
                    NumericMode = version.NumericMode,
                    IsLibrary = methodology.Kind == MethodologyKind.Library,
                    IsModuleLevel = version.Level == CalculationLevel.Module,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyBindingRow>> ListBindingsAsync(CancellationToken ct)
        => await (
                from binding in db.CalculationBindings.AsNoTracking()
                where binding.IsActive
                join methodology in db.Methodologies.AsNoTracking() on binding.MethodologyId equals methodology.Id
                orderby binding.Id
                select new MethodologyBindingRow(
                    binding.Id, methodology.Id, methodology.Code, binding.TableDefId, binding.ColumnDefId, binding.OutputCode))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
