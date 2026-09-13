// src/Ecr.Infrastructure/Security/ResourceNameResolver.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Реалізація <see cref="IResourceNameResolver"/> над <see cref="EcrDbContext"/>
/// (<c>Q-299</c>).
/// </summary>
/// <remarks>
/// ⚠ Чотири окремі запити (по одному на вид), а не один із <c>UNION</c>:
/// кожен вид живе у своїй таблиці з власним <c>Id</c> (<c>cfg.Project</c>,
/// <c>cfg.SheetDef</c>, <c>cfg.TableDef</c>, <c>cfg.ColumnDef</c>), і
/// перелік грантів однієї ролі — це щонайбільше кілька видів одночасно
/// (рідко всі чотири), тож зайвого попиту в базу це не додає.
///
/// ⚠ Іменем ресурсу тут навмисно взято лише <c>Code</c>, не
/// <c>NameL10n</c>/<c>HeaderL10n</c>: код — це ідентичність ресурсу
/// (`AddTable`/`AddColumn` у доменних сутностях відмовляють дублікат коду
/// саме тому), і той самий підхід уже прийнятий для проєкту —
/// <c>ProjectSummary</c> (<c>ProjectQueryHandlers.cs</c>) показує в переліку
/// лише <c>Code</c>, без назви мовами каталогу.
/// </remarks>
public sealed class ResourceNameResolver(EcrDbContext db) : IResourceNameResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(ResourceKind Kind, int Id), string>> ResolveAsync(
        IReadOnlyCollection<(ResourceKind Kind, int Id)> resources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var result = new Dictionary<(ResourceKind, int), string>();

        var projectIds = IdsOf(resources, ResourceKind.Project);
        if (projectIds.Count > 0)
        {
            var rows = await db.Projects.AsNoTracking()
                .Where(p => projectIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[(ResourceKind.Project, row.Id)] = row.Code;
            }
        }

        var sheetIds = IdsOf(resources, ResourceKind.Sheet);
        if (sheetIds.Count > 0)
        {
            var rows = await db.Set<SheetDef>().AsNoTracking()
                .Where(s => sheetIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[(ResourceKind.Sheet, row.Id)] = row.Code;
            }
        }

        var tableIds = IdsOf(resources, ResourceKind.Table);
        if (tableIds.Count > 0)
        {
            var rows = await db.Set<TableDef>().AsNoTracking()
                .Where(t => tableIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[(ResourceKind.Table, row.Id)] = row.Code;
            }
        }

        var columnIds = IdsOf(resources, ResourceKind.Column);
        if (columnIds.Count > 0)
        {
            var rows = await db.Set<ColumnDef>().AsNoTracking()
                .Where(c => columnIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[(ResourceKind.Column, row.Id)] = row.Code;
            }
        }

        return result;
    }

    private static List<int> IdsOf(
        IReadOnlyCollection<(ResourceKind Kind, int Id)> resources, ResourceKind kind)
        => [.. resources.Where(r => r.Kind == kind).Select(r => r.Id).Distinct()];
}
