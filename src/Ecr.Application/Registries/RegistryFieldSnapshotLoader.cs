using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries;

/// <summary>Одне поле одного запису довідника, яке читає <c>REGFIELD</c>.</summary>
/// <param name="EntryId">Запис довідника — значення Lookup-комірки.</param>
/// <param name="RegistryDefId">Довідник — <c>ColumnDef.LookupRegistryDefId</c> Lookup-колонки.</param>
/// <param name="FieldCode">Код поля — другий аргумент <c>REGFIELD</c>.</param>
public readonly record struct RegistryFieldRequest(long EntryId, int RegistryDefId, string FieldCode);

/// <summary>
/// Знімок полів довідника для <c>REGFIELD</c>: id запису → (код поля →
/// значення). Контексти обчислення синхронні, тож довідник читається
/// заздалегідь — і цим одним завантажувачем для ВСІХ контекстів.
/// </summary>
/// <remarks>
/// ⛔ Один код на перерахунок формул (<c>RecalculationService</c>) і на правила
/// валідації (<c>TableValidation</c>, D16-04). Доти знімок уміли будувати лише
/// для перерахунку, а контекст правил брав порожній — і <c>REGFIELD</c> у
/// правилі давав <c>#REF</c> на будь-який запис. Друга копія тих самих кроків
/// розійшлася б із першою мовчки (тип поля, регістр коду).
///
/// ⚠ Звернень стало (аудит P9): запит на ДОВІДНИК (раз на унікальний
/// <c>RegistryDefId</c> — їх стільки, скільки Lookup-колонок, а не рядків) і
/// одне пакетне читання значень УСІХ записів (порції — у сховищі). Доти
/// значення читалися запитом на кожен унікальний запис — сотні запитів на
/// таблицю з сотнею речовин. Порожній вхід — нуль звернень.
/// </remarks>
public static class RegistryFieldSnapshotLoader
{
    /// <summary>Читає з довідника рівно ті поля, які просять.</summary>
    /// <param name="registries">Сховище довідників.</param>
    /// <param name="requests">Що прочитати; повтори допустимі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c> — просити нічого, звернень до сховища не було.</returns>
    public static async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>?> LoadAsync(
        IRegistryStore registries, IEnumerable<RegistryFieldRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(requests);

        var neededFieldsByEntry = new Dictionary<long, HashSet<string>>();
        var registryDefByEntry = new Dictionary<long, int>();

        foreach (var (entryId, registryDefId, fieldCode) in requests)
        {
            registryDefByEntry[entryId] = registryDefId;

            if (!neededFieldsByEntry.TryGetValue(entryId, out var fields))
            {
                neededFieldsByEntry[entryId] = fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            fields.Add(fieldCode);
        }

        if (neededFieldsByEntry.Count == 0)
        {
            return null;
        }

        // Визначення полів — по одному запиту на УНІКАЛЬНИЙ довідник.
        var fieldDefsByRegistry = new Dictionary<int, IReadOnlyDictionary<string, RegistryFieldDef>>();

        foreach (var registryDefId in registryDefByEntry.Values.Distinct())
        {
            var definition = await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false);
            fieldDefsByRegistry[registryDefId] = definition?.Fields
                .ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, RegistryFieldDef>(StringComparer.OrdinalIgnoreCase);
        }

        var valuesByEntry = await ListValuesAsync(registries, [.. neededFieldsByEntry.Keys], ct).ConfigureAwait(false);
        var snapshot = new Dictionary<long, IReadOnlyDictionary<string, ExpressionValue>>();

        foreach (var (entryId, fieldCodes) in neededFieldsByEntry)
        {
            if (!fieldDefsByRegistry.TryGetValue(registryDefByEntry[entryId], out var fieldDefs))
            {
                continue;
            }

            var byFieldDefId = valuesByEntry.TryGetValue(entryId, out var registryValues)
                ? registryValues.ToDictionary(v => v.RegistryFieldDefId)
                : [];

            var perEntry = new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var fieldCode in fieldCodes)
            {
                if (!fieldDefs.TryGetValue(fieldCode, out var fieldDef)
                    || !byFieldDefId.TryGetValue(fieldDef.Id, out var registryValue))
                {
                    // Немає такого поля або запис ще не заповнив його —
                    // `GetRegistryField` віддасть #REF на відсутній ключ; тут
                    // просто нема що покласти в знімок.
                    continue;
                }

                if (ToExpressionValue(registryValue, fieldDef.DataType) is { } mapped)
                {
                    perEntry[fieldCode] = mapped;
                }
            }

            if (perEntry.Count > 0)
            {
                snapshot[entryId] = perEntry;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Значення всіх записів знімка — одним пакетним читанням (аудит P9), порціями всередині
    /// сховища (<see cref="IRegistryStore.ListValuesForEntriesAsync"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ Один запис — поштучний <see cref="IRegistryStore.ListValuesAsync"/>: це той самий один
    /// запит, лише без <c>IN (…)</c>. Звернень у будь-якому разі стало, скільки б записів не було.
    /// </remarks>
    private static async Task<Dictionary<long, List<RegistryValue>>> ListValuesAsync(
        IRegistryStore registries, IReadOnlyList<long> entryIds, CancellationToken ct)
    {
        if (entryIds.Count == 1)
        {
            return new Dictionary<long, List<RegistryValue>>
            {
                [entryIds[0]] = [.. await registries.ListValuesAsync(entryIds[0], ct).ConfigureAwait(false)],
            };
        }

        return (await registries.ListValuesForEntriesAsync(entryIds, ct).ConfigureAwait(false))
            .GroupBy(v => v.RegistryEntryId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Значення поля довідника як значення виразу; типізовано за <c>RegistryFieldDef.DataType</c>.</summary>
    /// <remarks>
    /// ⚠ <c>Lookup</c>/<c>Unit</c>/<c>Formula</c>/<c>Calculated</c> тут
    /// НЕМАЄ: перші два REGFIELD сьогодні не читає (задача — decimal/text/
    /// bool/date), а останні два в довіднику взагалі не існують
    /// (<c>RegistryValue.Set</c> їх забороняє при записі).
    /// </remarks>
    private static ExpressionValue? ToExpressionValue(RegistryValue value, CellDataType dataType)
        => dataType switch
        {
            CellDataType.Decimal or CellDataType.Int => value.ValueNumeric is { } n
                ? ExpressionValue.Number(n)
                : null,
            CellDataType.String => value.ValueString is { } s
                ? ExpressionValue.Text(s)
                : null,
            CellDataType.Bool => value.ValueBool is { } b
                ? ExpressionValue.Boolean(b)
                : null,
            CellDataType.Date => value.ValueDate is { } d
                ? ExpressionValue.Date(d)
                : null,
            _ => null,
        };
}
