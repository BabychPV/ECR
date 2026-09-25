// src/Ecr.Infrastructure/Persistence/ColumnDefSearchStore.cs
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IColumnDefSearchStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ <c>HeaderL10n</c> зберігається через <c>HasConversion</c> у
/// <c>nvarchar(max)</c> JSON (<c>TemplateStructureConfiguration.cs</c>): LINQ
/// над ним у <c>WHERE</c> не перекладається, тому заголовок звужується
/// <c>LIKE</c> над сирим текстом JSON (підзапит ідентифікаторів), а точна
/// перевірка «підрядок саме ЗНАЧЕННЯ, не ключа мови» лишається в пам'яті.
///
/// ⛔ F-03 (четвертий раунд UX): доти фільтр ішов ПІСЛЯ <c>Take(5000)</c> за
/// абеткою коду, тобто пошук бачив лише перші 5000 колонок бази. На стенді їх
/// 5991, і <c>EMISSION</c> давав <c>[]</c> — колонку неможливо було прив'язати з
/// інтерфейсу. Тепер обмеження стоїть ПІСЛЯ фільтра в SQL.
/// </remarks>
public sealed class ColumnDefSearchStore(EcrDbContext db) : IColumnDefSearchStore
{
    /// <summary>Символ екранування шаблону <c>LIKE</c>.</summary>
    /// <remarks>
    /// ⚠ Не <c>\</c>: екранований JSON сам несе зворотні скісні
    /// (<c>\u0415</c>), і вони мусять лишатися звичайними символами шаблону.
    /// </remarks>
    private const char LikeEscape = '!';

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
        var trimmed = query?.Trim();

        var columns = db.ColumnDefs.AsNoTracking().Where(c => !c.IsDeleted);

        if (!string.IsNullOrEmpty(trimmed))
        {
            var codeLike = "%" + EscapeLike(trimmed) + "%";

            // ⚠ Два шаблони заголовка: як написано (рядки, вставлені SQL-ом сіду,
            // лежать неекранованими) і як його записує `LocalizedText.ToJson`
            // (`System.Text.Json` екранує не-ASCII в `\uXXXX`). `:"%` попереду —
            // щоб підрядок шукався після першого значення, а не в ключі мови
            // (`"en"` є в кожному рядку).
            var rawLike = "%:\"%" + EscapeLike(trimmed) + "%";
            var jsonLike = "%:\"%" + EscapeLike(JsonText(trimmed)) + "%";

            var headerIds = db.Database.SqlQuery<int>($"""
                SELECT c.Id AS Value
                  FROM cfg.ColumnDef AS c
                 WHERE c.IsDeleted = 0
                   AND (c.HeaderL10n LIKE {rawLike} ESCAPE '!' OR c.HeaderL10n LIKE {jsonLike} ESCAPE '!')
                """);

            columns = columns.Where(c =>
                EF.Functions.Like(c.Code, codeLike, LikeEscape.ToString()) || headerIds.Contains(c.Id));
        }

        var candidates = await (
            from column in columns
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

    /// <summary>Екранує метасимволи <c>LIKE</c> (<c>%</c>, <c>_</c>, <c>[</c>, сам <c>!</c>).</summary>
    private static string EscapeLike(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (ch is '%' or '_' or '[' or LikeEscape)
            {
                builder.Append(LikeEscape);
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Рядок так, як його пише <c>LocalizedText.ToJson</c>, без лапок.</summary>
    private static string JsonText(string value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return json[1..^1];
    }
}
