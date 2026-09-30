// src/Ecr.Application/Registries/Keys/RegistryBatchKeys.cs
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Registries.Keys;

/// <summary>Рядок пакета запису для звірки ключів у межах пакета (RT-10b, §4.3 крок 3).</summary>
/// <param name="Number">Номер рядка (у файлі чи в пакеті).</param>
/// <param name="Entry">Запис рядка — його вікно чинності.</param>
/// <param name="Values">Значення полів ПІСЛЯ застосування рядка (<see cref="RegistryBatchKeys.EffectiveValues"/>).</param>
internal sealed record RegistryBatchKeyRow(
    int Number, RegistryEntry Entry, IReadOnlyDictionary<int, RegistryValue> Values);

/// <summary>
/// Звірка ключів записів ОДНОГО пакета між собою — до транзакції запису (FEATURE-REGISTRY-TABLES
/// §4.3 крок 3, §4.6). Спільна для імпорту CSV (<c>keyDuplicateInFile</c>) і
/// <see cref="RegistryEntryWriter"/> (<c>keyDuplicateInBatch</c>).
/// </summary>
/// <remarks>
/// ⛔ <see cref="RegistryKeyService.ApplyAsync(RegistryDef, IReadOnlyList{RegistryKeyDef}, IReadOnlyCollection{RegistryEntry}, CancellationToken)"/>
/// записи пакета один з одним свідомо НЕ звіряє (тримачі з пакета виключено з перевірки проти
/// бази): це обов'язок виклику. Без цієї звірки нетемпоральний дубль доходить до
/// <c>UX_RegistryEntryKey_Live</c> (<c>keyTakenConcurrently</c> на весь пакет), а темпоральний
/// з різним <c>ValidFrom</c> і вікнами, що перетинаються, записується як справжній дубль.
/// </remarks>
internal static class RegistryBatchKeys
{
    /// <summary>
    /// Значення полів запису такими, якими їх залишить рядок: наявні, поверх них — передані.
    /// Для обчислення ключа в пам'яті, без звернення до бази.
    /// </summary>
    /// <remarks>
    /// ⚠ Передані значення — пробні об'єкти поза контекстом. Значення проходить
    /// <see cref="CellValueReader.Normalize"/>, як у <see cref="RegistryEntryWriter.ApplyValuesAsync"/>
    /// (для рядка CSV чи числа — тотожність). Поля, якого немає в словнику, рядок не змінює.
    /// </remarks>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="existing">Наявні значення запису (порожньо — новий запис).</param>
    /// <param name="values">Значення рядка за кодами полів.</param>
    public static Dictionary<int, RegistryValue> EffectiveValues(
        RegistryDef definition, IReadOnlyList<RegistryValue> existing, IReadOnlyDictionary<string, object?> values)
    {
        var result = existing.ToDictionary(v => v.RegistryFieldDefId);
        var fields = definition.Fields.ToDictionary(f => f.Code, StringComparer.Ordinal);

        foreach (var (code, raw) in values)
        {
            if (fields.TryGetValue(code, out var field))
            {
                var probe = new RegistryValue(0L, field.Id);
                probe.Set(field.DataType, CellValueReader.Normalize(raw), field.UnitId);
                result[field.Id] = probe;
            }
        }

        return result;
    }

    /// <summary>
    /// Рядки, чий ключ має ще хоч один рядок пакета (для темпорального довідника — у вікні, що
    /// перетинається, та сама умова, що <see cref="RegistryKeyService.Overlaps"/>).
    /// </summary>
    /// <param name="definition">Опис довідника (з полями).</param>
    /// <param name="keyDefs">Активні ключі довідника.</param>
    /// <param name="rows">Рядки пакета, що пройшли перевірку значень.</param>
    /// <returns>Кожен такий рядок (за зростанням номера) — з полями першого ключа, що збігся.</returns>
    public static List<(RegistryBatchKeyRow Row, string Fields)> DuplicateKeyRows(
        RegistryDef definition, IReadOnlyList<RegistryKeyDef> keyDefs, IReadOnlyList<RegistryBatchKeyRow> rows)
    {
        var fieldCodes = definition.Fields.ToDictionary(f => f.Id, f => f.Code);
        var found = new SortedDictionary<int, (RegistryBatchKeyRow Row, string Fields)>();

        foreach (var key in keyDefs)
        {
            var fields = string.Join(
                ", ", key.Fields.OrderBy(f => f.Ordinal).Select(f => fieldCodes[f.RegistryFieldDefId]));

            var groups = rows
                .Select(r => (Row: r, Hash: RegistryKeyService.HashOf(definition, key, r.Values)))
                .Where(x => x.Hash is not null)
                .GroupBy(x => Convert.ToHexString(x.Hash!), StringComparer.Ordinal)
                .Where(g => g.Skip(1).Any());

            foreach (var group in groups)
            {
                var members = group.Select(x => x.Row).ToList();
                for (var a = 0; a < members.Count; a++)
                {
                    for (var b = a + 1; b < members.Count; b++)
                    {
                        if (definition.IsTemporal
                            && !RegistryKeyService.Overlaps(members[a].Entry.Window, members[b].Entry.Window))
                        {
                            continue;
                        }

                        found.TryAdd(members[a].Number, (members[a], fields));
                        found.TryAdd(members[b].Number, (members[b], fields));
                    }
                }
            }
        }

        return [.. found.Values];
    }
}
