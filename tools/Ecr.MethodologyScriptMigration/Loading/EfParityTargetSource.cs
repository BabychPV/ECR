using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.MethodologyScriptMigration.Loading;

/// <summary>
/// Переліки для <see cref="ParityRunner"/> прямо з бази (лише читання): версії з тіньовим текстом на <c>Formulas</c> і
/// екземпляри таблиць-оригіналів, до яких активно прив'язана методологія.
/// </summary>
public sealed class EfParityTargetSource(EcrDbContext db) : IParityTargetSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ParityVersion>> ListShadowedVersionsAsync(string? methodologyCode, CancellationToken ct)
    {
        var query =
            from v in db.MethodologyVersions.AsNoTracking()
            join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
            where v.Engine == MethodologyEngine.Formulas
                  && v.Level == CalculationLevel.Configuration
                  && v.Status == TemplateVersionStatus.Published
                  && v.EffectiveFrom != null
                  && v.ScriptText != null
                  && v.ScriptText != string.Empty
                  && (methodologyCode == null || m.Code == methodologyCode)
            orderby m.Code, v.EffectiveFrom, v.Id
            select new { MethodologyId = m.Id, m.Code, VersionId = v.Id, v.Version, v.ScriptText, m.Kind };

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new ParityVersion(
            r.MethodologyId, r.Code, r.VersionId, r.Version, r.ScriptText!, r.Kind == MethodologyKind.Library))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListUncoveredVersionsAsync(string? methodologyCode, CancellationToken ct)
    {
        var rows = await (
                from v in db.MethodologyVersions.AsNoTracking()
                join m in db.Methodologies.AsNoTracking() on v.MethodologyId equals m.Id
                where v.Engine == MethodologyEngine.Formulas
                      && v.Status == TemplateVersionStatus.Published
                      && (methodologyCode == null || m.Code == methodologyCode)
                      && (v.Level != CalculationLevel.Configuration || v.EffectiveFrom == null
                          || v.ScriptText == null || v.ScriptText == string.Empty)
                orderby m.Code, v.EffectiveFrom, v.Id
                select new { m.Code, v.Version, v.Level, v.EffectiveFrom, v.ScriptText })
            .ToListAsync(ct).ConfigureAwait(false);
        return
        [
            .. rows.Select(r => r.Code + " " + r.Version + ": " + (
                r.Level != CalculationLevel.Configuration
                    ? $"level {r.Level}: parity is not implemented for it"
                    : r.EffectiveFrom is null
                        ? "no EffectiveFrom"
                        : "no shadow text (not converted, or conversion failed)")),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParityInstance>> ListInstancesAsync(int methodologyId, long? documentId, CancellationToken ct)
    {
        var tableDefIds = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.MethodologyId == methodologyId && b.IsActive)
            .Select(b => b.TableDefId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var rows = await db.TableInstances
            .AsNoTracking()
            .OriginalOnly()
            .Where(i => tableDefIds.Contains(i.TableDefId) && (documentId == null || i.DocumentId == documentId))
            .OrderBy(i => i.DocumentId)
            .ThenBy(i => i.PeriodKeyValue)
            .ThenBy(i => i.Id)
            .Select(i => new ParityInstance(i.DocumentId, i.PeriodKeyValue, i.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows;
    }
}
