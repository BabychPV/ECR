using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// W15z: перев'язка графа клону версії (група заголовків, стиль, каскад колонки; батько й стиль рядка; батько групи)
/// ОДНИМ set-based UPDATE на таблицю замість UPDATE на кожен рядок.
/// </summary>
/// <remarks>
/// ⛔ Чому. <see cref="TemplateVersionCloner"/> ставить ці ключі ПІСЛЯ першого збереження (нових Id ще не було), і EF
/// писав їх по рядку: на LAND_UI (15 134 колонки, 11 032 у групах, 1 606 зі стилем, 905 рядків) — понад 12 тисяч
/// UPDATE, кожен з тригером незмінності (<c>TR_ColumnDef_Immutable</c>, <c>TR_RowDef_Immutable</c>). Замір на копії
/// живої бази 23.11: ~45 с із ~104 с часу бази клону. Тут — один UPDATE на таблицю на кожні <see cref="ChunkSize"/>
/// рядків (тригер раз на команду, а не раз на рядок).
///
/// ⚠ Беруться лише записи, у яких змінені ВИКЛЮЧНО перелічені властивості: будь-яка інша зміна лишається EF, як і
/// була. Після запису знімок оригіналів = поточні значення, стан — Unchanged, тож наступний <c>SaveChanges</c> їх
/// не пише вдруге. Кількість оновлених рядків звіряється (fail-closed): розбіжність — виняток, і транзакція клону
/// відкочується.
/// </remarks>
internal static class TemplateCloneBulkRelink
{
    /// <summary>Рядків на одну команду UPDATE (обмежує вартість тригера незмінності на команду).</summary>
    internal const int ChunkSize = 500;

    /// <summary>Записує відкладені перев'язки клону. Повертає кількість оновлених рядків.</summary>
    /// <param name="db">Контекст клону (усередині транзакції клону).</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<int> ApplyAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var n = 0;
        n += await FlushAsync<ColumnDef>(
            db, [nameof(ColumnDef.HeaderGroupId), nameof(ColumnDef.StyleId), nameof(ColumnDef.CascadeFromColumnId)], ct)
            .ConfigureAwait(false);
        n += await FlushAsync<RowDef>(db, [nameof(RowDef.ParentRowDefId), nameof(RowDef.StyleId)], ct).ConfigureAwait(false);
        n += await FlushAsync<ColumnHeaderGroupDef>(db, [nameof(ColumnHeaderGroupDef.ParentGroupId)], ct).ConfigureAwait(false);
        return n;
    }

    private static async Task<int> FlushAsync<T>(EcrDbContext db, string[] properties, CancellationToken ct)
        where T : class
    {
        var allowed = new HashSet<string>(properties, StringComparer.Ordinal);
        var entries = db.ChangeTracker.Entries<T>()
            .Where(e => e.State == EntityState.Modified)
            .Where(e =>
            {
                var modified = e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
                return modified.Count > 0 && modified.All(allowed.Contains);
            })
            .ToList();
        if (entries.Count == 0)
        {
            return 0;
        }

        var entity = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"У моделі немає {typeof(T).Name}.");
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var key = entity.FindPrimaryKey()!.Properties.Single();
        var keyColumn = key.GetColumnName(table)!;
        var columns = properties.Select(p => entity.FindProperty(p)!.GetColumnName(table)!).ToArray();

        var rows = entries.Select(e =>
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = e.Property(key.Name).CurrentValue };
            for (var i = 0; i < properties.Length; i++)
            {
                row["p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = e.Property(properties[i]).CurrentValue;
            }

            return row;
        }).ToList();

        var set = string.Join(", ", columns.Select((c, i) => $"t.[{c}] = j.p{i}"));
        var with = string.Join(", ", columns.Select((_, i) => $"p{i} int '$.p{i}'"));
        var sql = $"UPDATE t SET {set} FROM [{table.Schema}].[{table.Name}] t " +
                  $"JOIN OPENJSON(@relink) WITH (k int '$.k', {with}) j ON j.k = t.[{keyColumn}];";
        // Порціями по ChunkSize: тригер незмінності з'єднує inserted × deleted без індексів (план може бути O(n^2) на одну
        // команду; урок збирача Land, Build-LandTemplate.ps1 «порціями по 200»). 500 рядків — до 250 тис. порівнянь на команду.
        var updated = 0;
        for (var at = 0; at < rows.Count; at += ChunkSize)
        {
            var chunk = rows.Skip(at).Take(ChunkSize).ToList();
            var parameter = new SqlParameter("@relink", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(chunk) };
            updated += await db.Database.ExecuteSqlRawAsync(sql, [parameter], ct).ConfigureAwait(false);
        }
        if (updated != entries.Count)
        {
            throw new InvalidOperationException(
                $"Перев'язка клону {table.Name}: оновлено {updated} рядків із {entries.Count} — клон розійшовся з базою.");
        }

        foreach (var e in entries)
        {
            AcceptCurrent(e);
        }

        return updated;
    }

    private static void AcceptCurrent(EntityEntry e)
    {
        e.OriginalValues.SetValues(e.CurrentValues);
        e.State = EntityState.Unchanged;
    }
}
