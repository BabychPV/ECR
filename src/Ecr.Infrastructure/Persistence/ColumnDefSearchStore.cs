// src/Ecr.Infrastructure/Persistence/ColumnDefSearchStore.cs
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IColumnDefSearchStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ <c>HeaderL10n</c> зберігається через <c>HasConversion</c> у
/// <c>nvarchar(max)</c> JSON (<c>DocumentConfiguration</c>-подібна конфігурація
/// в <c>TemplateStructureConfiguration.cs</c>): SQL Server тут бачить рядок
/// без структури, а не об'єкт, тож фільтр за перекладом заголовка не
/// перекладається у <c>WHERE</c> — ЄДИНИЙ спосіб застосувати його чесно — це
/// звузити кандидатів обмеженим <c>Take</c> (той самий прийом, що вже дають
/// <c>DocumentStore.MaxRules</c>/<c>MaxSheets</c>) і відфільтрувати в пам'яті.
/// Це не шлях документа й не сітка — адмінський пошук під час налаштування
/// методології, тому бюджет тут не той, що на гарячому шляху.
/// </remarks>
public sealed class ColumnDefSearchStore(EcrDbContext db) : IColumnDefSearchStore
{
    /// <summary>Стеля кандидатів, які взагалі розглядаються перед фільтром у пам'яті.</summary>
    private const int MaxScanned = 5000;

    /// <summary>Стеля результатів, якщо викликач не задав свою (чи задав некоректну).</summary>
    private const int DefaultLimit = 50;

    /// <summary>Абсolютна стеля результатів незалежно від того, що попросив викликач.</summary>
    private const int MaxLimit = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColumnDefSearchResult>> SearchAsync(
        string? query, int limit, CancellationToken ct)
    {
        var take = limit is > 0 and <= MaxLimit ? limit : DefaultLimit;

        var candidates = await (
            from column in db.ColumnDefs.AsNoTracking()
            where !column.IsDeleted
            join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
            join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
            orderby column.Code, column.Id
            select new
            {
                Column = column,
                TableCode = table.Code,
                sheet.Id,
                SheetCode = sheet.Code,
                sheet.TemplateVersionId,
            })
            .Take(MaxScanned)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var trimmed = query?.Trim();

        var matched = string.IsNullOrEmpty(trimmed)
            ? candidates
            : candidates.Where(c =>
                c.Column.Code.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || c.Column.HeaderL10n.Values.Values.Any(
                    v => v.Contains(trimmed, StringComparison.OrdinalIgnoreCase)));

        return [.. matched
            .Take(take)
            .Select(c => new ColumnDefSearchResult(
                c.Column.Id, c.Column.Code, c.Column.HeaderL10n,
                c.Column.TableDefId, c.TableCode, c.Id, c.SheetCode, c.TemplateVersionId))];
    }
}
