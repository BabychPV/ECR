using System.Data;
using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B49 (борг V-4): читання журналу комірок, що йдуть за колонкою через перенос проєкту між версіями шаблону. Перенос
/// переселяє комірку на <c>ColumnDefId</c> цільової версії, а <c>aud.CellChange</c> лишається на Id старої — родовід
/// колонки дає <see cref="ColumnPathMapper.LineageAsync"/> (та сама логіка, що в History інспектора, без нової).
/// </summary>
public sealed partial class AuditReader
{
    /// <summary>Найбільше адрес у одному запиті <see cref="LastChangesSql"/>: два параметри на адресу, стеля SQL Server — 2100.</summary>
    internal const int MaxLastChangeCellsPerQuery = 1000;

    /// <inheritdoc />
    /// <remarks>
    /// Звичайний випадок (жодна колонка не має інших версій) — родовід одним запитом, далі той самий запит, що й доти.
    /// Інакше кожна адреса розгортається на (рядок, Id колонки в кожній версії), а з знайденого для запитаної адреси
    /// лишається найновіша зміна; ключ відповіді — запитана (поточна) адреса.
    /// </remarks>
    public async Task<IReadOnlyDictionary<(long TableRowId, int ColumnDefId), LastCellChange>> ReadLastChangesAsync(
        long documentId,
        IReadOnlyCollection<(long TableRowId, int ColumnDefId)> cells,
        DateTime since,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var distinct = cells.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<(long TableRowId, int ColumnDefId), LastCellChange>();
        }

        var lineage = await new ColumnPathMapper(db)
            .LineageAsync([.. distinct.Select(c => c.ColumnDefId).Distinct()], ct)
            .ConfigureAwait(false);
        if (lineage.Values.All(ids => ids.Count <= 1))
        {
            return await ReadLastChangesExactAsync(documentId, distinct, since, ct).ConfigureAwait(false);
        }

        // Адреса журналу → запитані адреси, до чиїх родоводів вона належить.
        var owners = new Dictionary<(long TableRowId, int ColumnDefId), List<(long TableRowId, int ColumnDefId)>>();
        foreach (var cell in distinct)
        {
            foreach (var column in lineage.GetValueOrDefault(cell.ColumnDefId) ?? [cell.ColumnDefId])
            {
                var address = (cell.TableRowId, column);
                if (!owners.TryGetValue(address, out var list))
                {
                    owners[address] = list = [];
                }

                list.Add(cell);
            }
        }

        var result = new Dictionary<(long TableRowId, int ColumnDefId), LastCellChange>();
        foreach (var chunk in owners.Keys.Chunk(MaxLastChangeCellsPerQuery))
        {
            var found = await ReadLastChangesExactAsync(documentId, chunk, since, ct).ConfigureAwait(false);
            foreach (var (address, change) in found)
            {
                foreach (var requested in owners[address])
                {
                    if (!result.TryGetValue(requested, out var seen) || change.ChangedAt > seen.ChangedAt)
                    {
                        result[requested] = change;
                    }
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Спершу той самий запит, що й доти (<see cref="OutOfWindowCellsSql"/>). Порожньо — найчастіший випадок — і
    /// родовід не потрібен: родовід лише звужує «останню зміну» й перекладає Id, нових кандидатів не дає.
    /// </para>
    /// <para>
    /// Інакше: колонки поточних таблиць рядків-кандидатів (той самий екземпляр, що й зріз), і якщо хтось із кандидатів
    /// лежить на колонці іншої версії — родовід поточних колонок (<see cref="ColumnPathMapper.LineageAsync"/>, від
    /// ПОТОЧНОГО боку, тож ланцюжки перейменувань теж ведуть до поточної колонки) і запит
    /// <see cref="OutOfWindowCellsLineageSql"/>: «остання зміна» — по всіх Id родоводу, у відповіді — поточний Id.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyCollection<(long TableRowId, int ColumnDefId)>> ReadOutOfWindowCellsAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var exact = await ReadOutOfWindowCellsExactAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (exact.Count == 0)
        {
            return exact;
        }

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var current = new HashSet<int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = CurrentColumnsOfRowsSql;
            command.Parameters.Add("@periodKey", SqlDbType.Int).Value = periodKey;
            command.Parameters.Add("@rows", SqlDbType.NVarChar, -1).Value = JsonIds(exact.Select(c => c.TableRowId).Distinct());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                current.Add(reader.GetInt32(0));
            }
        }

        if (exact.All(c => current.Contains(c.ColumnDefId)))
        {
            return exact;
        }

        // Id журналу → поточна колонка. Поточна колонка — сама собі (першою), далі члени її родоводу.
        var lineage = await new ColumnPathMapper(db).LineageAsync(current, ct).ConfigureAwait(false);
        var map = current.Order().ToDictionary(id => id, id => id);
        foreach (var (column, ids) in lineage.OrderBy(p => p.Key))
        {
            foreach (var id in ids)
            {
                map.TryAdd(id, column);
            }
        }

        if (exact.All(c => !map.TryGetValue(c.ColumnDefId, out var to) || to == c.ColumnDefId))
        {
            return exact;
        }

        await using var lineageCommand = connection.CreateCommand();
        lineageCommand.CommandText = OutOfWindowCellsLineageSql;
        lineageCommand.Parameters.Add("@documentId", SqlDbType.BigInt).Value = documentId;
        lineageCommand.Parameters.Add("@periodKey", SqlDbType.Int).Value = periodKey;
        lineageCommand.Parameters.Add("@lineage", SqlDbType.NVarChar, -1).Value =
            "[" + string.Join(
                ",",
                map.Select(p => "{\"a\":" + p.Key.ToString(CultureInfo.InvariantCulture)
                                + ",\"k\":" + p.Value.ToString(CultureInfo.InvariantCulture) + "}")) + "]";

        var result = new List<(long TableRowId, int ColumnDefId)>();
        await using var rows = await lineageCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add((rows.GetInt64(0), rows.GetInt32(1)));
        }

        return result;
    }

    /// <summary>Колонки поточних таблиць названих рядків періоду (екземпляр рядка несе поточний <c>TableDefId</c>).</summary>
    /// <remarks>Пошуки за ключами: рядок — <c>(PeriodKey, Id)</c>, екземпляр — <c>(PeriodKey, Id)</c>, колонки — за <c>TableDefId</c>.</remarks>
    internal const string CurrentColumnsOfRowsSql = """
        SELECT DISTINCT c.Id
          FROM OPENJSON(@rows) AS j
          JOIN doc.TableRow AS r ON r.PeriodKey = @periodKey AND r.Id = CAST(j.[value] AS bigint)
          JOIN doc.TableInstance AS i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
          JOIN cfg.ColumnDef AS c ON c.TableDefId = i.TableDefId;
        """;

    /// <summary>
    /// <see cref="OutOfWindowCellsSql"/> з родоводом: <c>@lineage</c> — пари «Id журналу <c>a</c> → поточна колонка
    /// <c>k</c>». Кандидат перекладається на <c>k</c>; «пізнішої зміни немає» — по всіх Id того самого <c>k</c>, тож
    /// правка поза вікном до переносу, перекрита звичайною правкою після нього, значка не дає.
    /// </summary>
    /// <remarks>
    /// Межа <c>@since</c>, фільтрований індекс <c>IX_CellChange_OutOfWindow</c> і розвід однакового <c>ChangedAt</c> за
    /// <c>Id</c> — дослівно як у <see cref="OutOfWindowCellsSql"/>. Id поза мапою лишаються самі собою.
    /// </remarks>
    internal const string OutOfWindowCellsLineageSql = """
        DECLARE @open datetime2(3) =
            (SELECT CASE WHEN p.ComputedOpenAt < CAST(p.PeriodStart AS datetime2(3))
                         THEN p.ComputedOpenAt ELSE CAST(p.PeriodStart AS datetime2(3)) END
               FROM doc.Period AS p
               JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
              WHERE d.Id = @documentId AND p.PeriodKey = @periodKey);
        DECLARE @since datetime2(3) =
            CASE WHEN @open > '2000-01-01' THEN DATEADD(DAY, -1, @open) ELSE '0001-01-01' END;

        DECLARE @lin TABLE (a int PRIMARY KEY, k int NOT NULL);
        INSERT @lin (a, k) SELECT a, k FROM OPENJSON(@lineage) WITH (a int, k int);

        SELECT o.TableRowId, COALESCE(lo.k, o.ColumnDefId) AS ColumnDefId
          FROM aud.CellChange AS o
          LEFT JOIN @lin AS lo ON lo.a = o.ColumnDefId
         WHERE o.DocumentId = @documentId
           AND o.PeriodKey = @periodKey
           AND o.IsOutOfWindow = 1
           AND o.ChangedAt >= @since
           AND NOT EXISTS (
                 SELECT 1
                   FROM aud.CellChange AS later
                  WHERE later.DocumentId = o.DocumentId
                    AND later.TableRowId = o.TableRowId
                    AND (later.ColumnDefId = o.ColumnDefId
                         OR later.ColumnDefId IN (SELECT l2.a FROM @lin AS l2 WHERE l2.k = lo.k))
                    AND later.ChangedAt >= o.ChangedAt
                    AND (later.ChangedAt > o.ChangedAt
                         OR (later.ChangedAt = o.ChangedAt AND later.Id > o.Id)));
        """;

    private static string JsonIds<T>(IEnumerable<T> ids)
        where T : IFormattable
        => "[" + string.Join(",", ids.Select(id => id.ToString(null, CultureInfo.InvariantCulture))) + "]";
}
