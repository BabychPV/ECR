using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Групи заголовків колонок: заміна дерева і перевірка після презентаційного патча.</summary>
public sealed partial class TemplateVersionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<HeaderGroupRow>> ReplaceHeaderGroupsAsync(
        int templateVersionId, int tableDefId, IReadOnlyList<HeaderGroupInput> groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var table = await (from t in db.TableDefs.AsNoTracking()
                           join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                           where t.Id == tableDefId && s.TemplateVersionId == templateVersionId && !t.IsDeleted
                           select new { t.Id, t.Code }).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                "ECR-TMPL-0404",
                $"TableDef {tableDefId} не належить версії {templateVersionId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.presentationTarget",
                    ["entityType"] = "TableDef",
                    ["entityId"] = tableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        var existing = await db.ColumnHeaderGroupDefs.Where(g => g.TableDefId == tableDefId)
            .ToListAsync(ct).ConfigureAwait(false);
        var columns = await db.ColumnDefs.Where(c => c.TableDefId == tableDefId && !c.IsDeleted)
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = groups.Select(g => g.Code).ToHashSet(StringComparer.Ordinal);
        var codeById = existing.ToDictionary(g => g.Id, g => g.Code);

        // Перевірка ФІНАЛЬНОГО стану: колонки груп, яких більше немає, групу втрачають.
        var placements = columns
            .Select(c => new HeaderColumnPlacement(
                c.Code, c.Ordinal,
                c.HeaderGroupId is { } gid && codeById.TryGetValue(gid, out var gc) && wanted.Contains(gc) ? gc : null))
            .ToList();
        Throw(table.Code, ColumnHeaderGroupRules.Validate(
            table.Code, [.. groups.Select(g => new HeaderGroupNode(g.Code, g.ParentCode))], placements));

        var removed = existing.Where(g => !wanted.Contains(g.Code)).ToList();
        var removedIds = removed.Select(g => g.Id).ToHashSet();

        foreach (var column in columns.Where(c => c.HeaderGroupId is { } id && removedIds.Contains(id)))
        {
            column.SetHeaderGroup(null);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 1) створити/оновити БЕЗ батьків (батько може бути ще не збережений);
        var byCode = existing.ToDictionary(g => g.Code, StringComparer.Ordinal);
        foreach (var input in groups)
        {
            if (byCode.TryGetValue(input.Code, out var group))
            {
                group.Update(input.HeaderL10n, input.Ordinal, null);
            }
            else
            {
                group = new ColumnHeaderGroupDef(tableDefId, EcrCode.Create(input.Code), input.HeaderL10n, input.Ordinal);
                db.ColumnHeaderGroupDefs.Add(group);
                byCode[input.Code] = group;
            }
        }

        // Прибрані групи теж відчіплюються від батьків — до видалення.
        foreach (var gone in removed)
        {
            gone.SetParent(null);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 2) проставити батьків уже зі збереженими Id;
        foreach (var input in groups)
        {
            byCode[input.Code].SetParent(input.ParentCode is { } p ? byCode[p].Id : null);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 3) видалити прибрані.
        db.ColumnHeaderGroupDefs.RemoveRange(removed);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return [.. groups.Select(i =>
        {
            var g = byCode[i.Code];
            return new HeaderGroupRow(g.Id, g.ParentGroupId, g.Code, g.Ordinal, g.HeaderL10n);
        })];
    }

    /// <summary>
    /// Після презентаційного патча: групи й колонки версії досі відповідають правилам (суміжність, та сама таблиця).
    /// Перевіряється лише коли патч зачепив <c>Ordinal</c> або <c>HeaderGroupId</c> колонок. Викидає 422 — транзакція
    /// обробника відкотить весь патч.
    /// </summary>
    private async Task EnsureHeaderGroupsValidAsync(
        int templateVersionId, IReadOnlyList<PresentationChange> changes, CancellationToken ct)
    {
        if (!changes.Any(c => c.EntityType == "ColumnDef" && c.Field is "Ordinal" or "HeaderGroupId"))
        {
            return;
        }

        var tables = await (from t in db.TableDefs.AsNoTracking()
                            join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                            where s.TemplateVersionId == templateVersionId && !t.IsDeleted
                            select new { t.Id, t.Code }).ToListAsync(ct).ConfigureAwait(false);
        var tableIds = tables.Select(t => t.Id).ToList();

        var groups = await db.ColumnHeaderGroupDefs.AsNoTracking()
            .Where(g => tableIds.Contains(g.TableDefId)).ToListAsync(ct).ConfigureAwait(false);
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted
                && (c.HeaderGroupId != null || db.ColumnHeaderGroupDefs.Any(g => g.TableDefId == c.TableDefId)))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var table in tables)
        {
            var tableGroups = groups.Where(g => g.TableDefId == table.Id).ToList();
            var codeById = tableGroups.ToDictionary(g => g.Id, g => g.Code);
            var nodes = tableGroups
                .Select(g => new HeaderGroupNode(g.Code, g.ParentGroupId is { } p && codeById.TryGetValue(p, out var pc) ? pc : null))
                .ToList();
            var placements = columns.Where(c => c.TableDefId == table.Id)
                .Select(c => new HeaderColumnPlacement(
                    c.Code, c.Ordinal,
                    c.HeaderGroupId is { } gid ? (codeById.TryGetValue(gid, out var gc) ? gc : $"#{gid}") : null))
                .ToList();

            Throw(table.Code, ColumnHeaderGroupRules.Validate(table.Code, nodes, placements));
        }
    }

    private static void Throw(string tableCode, IReadOnlyList<HeaderGroupViolation> violations)
    {
        if (violations.Count == 0)
        {
            return;
        }

        var first = violations[0];
        var details = new Dictionary<string, object?>(first.Args.Select(a => new KeyValuePair<string, object?>(a.Key, a.Value)))
        {
            ["messageKey"] = first.MessageKey,
            ["tableCode"] = tableCode,
            ["violationCount"] = violations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        throw new BusinessRuleException(ErrorCodes.HeaderGroupInvalid, first.Message, details);
    }
}
