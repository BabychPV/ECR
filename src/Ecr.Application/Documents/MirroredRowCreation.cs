using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// ✎ B134 (D-457 п.3): дзеркальне створення рядків таблиці «місяці в колонках» (Q-17) — склад рядків спільний на рік,
/// якір авторитетний. Спільне для <c>POST …/rows</c> (<see cref="CreateRowHandler"/>) і PATCH/книги Excel
/// (<see cref="PatchCellsHandler"/>): екземпляри року разом із лінивим якорем і набори створення «у кожному екземплярі —
/// ключі, яких там ще немає».
/// </summary>
/// <remarks>
/// ⚠ Замки періодів (виняткові, за зростанням ключа), стелю й дубль ключа за якорем бере викликач: у кожного з них свій
/// порядок блокувань і своя відмова. Тут — лише читання складу й побудова наборів під уже взятими замками.
/// </remarks>
internal static class MirroredRowCreation
{
    /// <summary>Екземпляри таблиці документа за рік запиту; якоря немає — створюється ліниво й вузько (лише ця таблиця).</summary>
    /// <param name="rows">Сховище рядків.</param>
    /// <param name="instance">Екземпляр запиту.</param>
    /// <param name="anchor">Якір року.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Екземпляри за зростанням періоду і екземпляр якоря.</returns>
    public static async Task<(List<TableInstanceRef> Instances, TableInstanceRef Anchor)> InstancesWithAnchorAsync(
        IRowStore rows, TableInstanceRef instance, PeriodKey anchor, CancellationToken ct)
    {
        var instances = (await rows
                .GetTableInstancesOfTableInYearAsync(instance.DocumentId, instance.TableDefId, new PeriodKey(instance.PeriodKey).Year, ct)
                .ConfigureAwait(false))
            .ToList();

        if (!instances.Exists(i => i.PeriodKey == anchor.Value))
        {
            instances.Add(await rows
                .EnsureTableInstanceAsync(instance.DocumentId, instance.TableDefId, anchor, ct)
                .ConfigureAwait(false));
        }

        // Екземпляр запиту — завжди серед них (перелік міг бути прочитаний до його появи).
        if (!instances.Exists(i => i.TableInstanceId == instance.TableInstanceId))
        {
            instances.Add(instance);
        }

        instances.Sort((a, b) => a.PeriodKey.CompareTo(b.PeriodKey));
        return (instances, instances.First(i => i.PeriodKey == anchor.Value));
    }

    /// <summary>
    /// Набори створення: у кожному екземпляри (за зростанням періоду) — ключі з <paramref name="keys"/>, яких там ще немає
    /// (розбіжність складу не дає дубля). Порядок ключів у наборі — порядок <paramref name="keys"/>.
    /// </summary>
    /// <param name="rows">Сховище рядків.</param>
    /// <param name="instances">Екземпляри року (<see cref="InstancesWithAnchorAsync"/>).</param>
    /// <param name="keys">Ключі нових рядків.</param>
    /// <param name="ordinal">Порядковий номер нових рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Непорожні набори і наявні рядки цих ключів по екземплярах (<c>TableInstanceId</c> → ключ → <c>TableRow.Id</c>).</returns>
    public static async Task<(List<RowCreationBatch> Batches, Dictionary<long, Dictionary<string, long>> Present)> BatchesAsync(
        IRowStore rows, IReadOnlyList<TableInstanceRef> instances, IReadOnlyList<RowKey> keys, int ordinal, CancellationToken ct)
    {
        var batches = new List<RowCreationBatch>(instances.Count);
        var present = new Dictionary<long, Dictionary<string, long>>();
        foreach (var target in instances.OrderBy(i => i.PeriodKey))
        {
            var period = new PeriodKey(target.PeriodKey);
            var existing = await rows.GetRowIdsAsync(target.TableInstanceId, period, ct).ConfigureAwait(false);

            var missing = new List<RowKey>(keys.Count);
            foreach (var key in keys)
            {
                if (existing.TryGetValue(key.Value, out var id))
                {
                    (present.TryGetValue(target.TableInstanceId, out var found)
                        ? found
                        : present[target.TableInstanceId] = new Dictionary<string, long>(StringComparer.Ordinal))[key.Value] = id;
                }
                else
                {
                    missing.Add(key);
                }
            }

            if (missing.Count > 0)
            {
                batches.Add(new RowCreationBatch(target.TableInstanceId, period, missing, ordinal));
            }
        }

        return (batches, present);
    }
}
