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
/// ⚠ П'ять окремих запитів (по одному на вид), а не один із <c>UNION</c>:
/// кожен вид живе у своїй таблиці з власним <c>Id</c> (<c>cfg.Project</c>,
/// <c>cfg.SheetDef</c>, <c>cfg.TableDef</c>, <c>cfg.ColumnDef</c>,
/// <c>cfg.RegistryDef</c>), і перелік грантів однієї ролі — це щонайбільше
/// кілька видів одночасно (рідко всі п'ять), тож зайвого попиту в базу це
/// не додає.
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
    /// <summary>Межа довідника проєктів для видачі грантів.</summary>
    public const int MaxProjects = 10_000;

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

        var registryIds = IdsOf(resources, ResourceKind.Registry);
        if (registryIds.Count > 0)
        {
            var rows = await db.RegistryDefs.AsNoTracking()
                .Where(r => registryIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Code })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[(ResourceKind.Registry, row.Id)] = row.Code;
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Проєкція рівно з трьох колонок (<c>Id</c>, <c>Code</c>, <c>NameL10n</c>):
    /// перелік існує для адміністратора безпеки БЕЗ грантів на проєкти, і все,
    /// що понад ідентичність проєкту (стан, пояс, періоди), тут було б витоком.
    ///
    /// ⚠ Межа <see cref="MaxProjects"/> (правило 6: без межі — «віддати весь
    /// реєстр»): проєкт — це майданчик підприємства, їх десятки чи сотні, і
    /// межа на порядки вища за реальний обсяг, а не тихе обрізання списку.
    /// </remarks>
    public async Task<IReadOnlyList<Ecr.Application.Security.GrantableProject>> ListProjectsAsync(CancellationToken ct)
    {
        var rows = await db.Projects.AsNoTracking()
            .OrderBy(p => p.Code)
            .Take(MaxProjects)
            .Select(p => new { p.Id, p.Code, p.NameL10n })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(p => new Ecr.Application.Security.GrantableProject(p.Id, p.Code, p.NameL10n));
    }

    private static List<int> IdsOf(
        IReadOnlyCollection<(ResourceKind Kind, int Id)> resources, ResourceKind kind)
        => [.. resources.Where(r => r.Kind == kind).Select(r => r.Id).Distinct()];
}
