using Ecr.Application.Ports;

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
}
