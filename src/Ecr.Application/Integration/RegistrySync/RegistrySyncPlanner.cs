// src/Ecr.Application/Integration/RegistrySync/RegistrySyncPlanner.cs
using System.Diagnostics.CodeAnalysis;
using Ecr.Application.Documents;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.RegistrySync;

/// <summary>
/// Чиста логіка синхронізації довідника з зовнішнім джерелом (PI AF):
/// знімок джерела + зв'язки + поточні значення + мапінги → план
/// (<c>ФВ-8.10</c>, <c>ФВ-8.11</c>; <c>docs/build/FEATURE-REGISTRY-SYNC.md</c>, крок S4).
/// </summary>
/// <remarks>
/// ⛔ Без бази й без портів запису. Планувальник НЕ пише: записує
/// <c>RegistrySyncJob</c> через спільний <c>RegistryEntryWriter</c> (крок S7), а
/// до того задача лише звіряє (S5). Тому все, що тут вирішується, перевіряється
/// юніт-тестом без СУБД.
/// <para>
/// Політика за <see cref="RegistrySourceKind"/> (<c>ФВ-8.9</c>, <c>D-202</c>):
/// </para>
/// <list type="bullet">
/// <item><c>External</c> — синк володіє всіма змапленими полями; поле з
/// ВИМКНЕНИМ мапінгом не пишеться, розбіжність іде подією
/// <see cref="RegistrySyncEventKind.Diverged"/> (запис через мапінг, який
/// адміністратор свідомо вимкнув, перетер би його рішення).</item>
/// <item><c>Hybrid</c> — синк володіє лише полями з АКТИВНИМ мапінгом; решта
/// полів — локальні, синк про них мовчить.</item>
/// <item><c>Local</c> — лише звірка (<c>D-49</c>): жодного оновлення, розбіжність —
/// <see cref="RegistrySyncEventKind.Diverged"/>.</item>
/// </list>
/// <para>
/// ⚠ Ручна правка перемагає (<c>D-118</c>): якщо останнім поле записала людина,
/// а джерело каже інше — <see cref="RegistrySyncEventKind.ConflictKeptManual"/>,
/// без запису. Зниклий елемент — лише подія (запис не видаляється), новий
/// елемент без зв'язку — лише подія (запис не створюється).
/// </para>
/// </remarks>
public static class RegistrySyncPlanner
{
    /// <summary>Будує план синхронізації.</summary>
    /// <param name="input">Знімок джерела й стан довідника.</param>
    /// <returns>План; <see cref="RegistrySyncPlan.IsEmpty"/> — якщо нічого не змінилося.</returns>
    /// <exception cref="ArgumentException">
    /// Дубль <c>ExternalId</c> у знімку або в зв'язках: це порушення контракту
    /// читача, а не дані, які можна «якось» спланувати.
    /// </exception>
    public static RegistrySyncPlan Plan(RegistrySyncInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // GUID/WebId AF регістронезалежні: той самий елемент, записаний іншим
        // регістром, не має ставати «зниклим» і «неприв'язаним» одночасно.
        var elements = ToUniqueMap(input.Elements, e => e.ExternalId, nameof(input.Elements));
        var links = ToUniqueMap(input.Links, l => l.ExternalId, nameof(input.Links));
        var entries = input.Entries.ToDictionary(e => e.RegistryEntryId);
        var mappings = input.Mappings
            .OrderBy(m => m.RegistryFieldDefId)
            .ThenBy(m => m.SourceAttribute, StringComparer.Ordinal)
            .ToList();

        var updates = new List<RegistrySyncUpdate>();
        var paths = new List<RegistrySyncPathChange>();
        var events = new List<RegistrySyncEvent>();

        foreach (var element in elements.Values.OrderBy(e => e.ExternalId, StringComparer.Ordinal))
        {
            if (!links.TryGetValue(element.ExternalId, out var link))
            {
                events.Add(new RegistrySyncEvent(RegistrySyncEventKind.ElementUnlinked, element.ExternalId, null));
                continue;
            }

            // Запис, якого немає в стані (видалений логічно чи поза вибіркою), не
            // плануємо: писати в нього синк однаково не має права.
            if (!entries.TryGetValue(link.RegistryEntryId, out var entry))
            {
                continue;
            }

            if (element.ExternalPath is { } path && !string.Equals(path, link.ExternalPath, StringComparison.Ordinal))
            {
                paths.Add(new RegistrySyncPathChange(element.ExternalId, entry.RegistryEntryId, link.ExternalPath, path));
            }

            foreach (var mapping in mappings)
            {
                PlanField(input.SourceKind, element, entry, mapping, updates, events);
            }
        }

        if (input.IsCompleteSnapshot)
        {
            foreach (var link in links.Values.OrderBy(l => l.ExternalId, StringComparer.Ordinal))
            {
                if (!elements.ContainsKey(link.ExternalId))
                {
                    events.Add(new RegistrySyncEvent(
                        RegistrySyncEventKind.SourceMissing, link.ExternalId, link.RegistryEntryId));
                }
            }
        }

        return new RegistrySyncPlan(updates, paths, events);
    }

    private static void PlanField(
        RegistrySourceKind sourceKind,
        RegistrySyncSourceElement element,
        RegistrySyncEntryState entry,
        RegistrySyncFieldMapping mapping,
        List<RegistrySyncUpdate> updates,
        List<RegistrySyncEvent> events)
    {
        // Hybrid і Local: поле без активного мапінгу — локальне, синк про нього
        // не знає. External: поле лишається зовнішнім і з вимкненим мапінгом —
        // писати не можна, але розбіжність видно (нижче).
        if (!mapping.IsActive && sourceKind != RegistrySourceKind.External)
        {
            return;
        }

        // Атрибута немає у знімку — джерело про поле нічого не сказало. Це не
        // «порожньо»: стерти значення через те, що атрибут не прочитався,
        // означало б видати збій читання за дані.
        if (!element.Attributes.TryGetValue(mapping.SourceAttribute, out var raw))
        {
            return;
        }

        if (!TryConvert(mapping, raw, out var incoming, out var rejection))
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ValueRejected,
                element.ExternalId,
                entry.RegistryEntryId,
                mapping.FieldCode,
                SourceValue: raw,
                ErrorCode: rejection.ErrorCode,
                MessageKey: rejection.Details?.GetValueOrDefault("messageKey") as string));
            return;
        }

        var current = entry.Values.TryGetValue(mapping.RegistryFieldDefId, out var known) ? known : null;
        var currentValue = current?.Value;

        // Ідемпотентність: те саме типізоване значення — нічого не пишеться і
        // нічого не повідомляється. decimal порівнюється за значенням: 1.0 == 1.00.
        if (Equals(currentValue, incoming))
        {
            return;
        }

        if (sourceKind == RegistrySourceKind.Local || !mapping.IsActive)
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.Diverged, element.ExternalId, entry.RegistryEntryId,
                mapping.FieldCode, currentValue, incoming));
            return;
        }

        if (current is { LastWriterIsHuman: true })
        {
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ConflictKeptManual, element.ExternalId, entry.RegistryEntryId,
                mapping.FieldCode, currentValue, incoming));
            return;
        }

        updates.Add(new RegistrySyncUpdate(
            entry.RegistryEntryId, mapping.RegistryFieldDefId, mapping.FieldCode, currentValue, incoming));
    }

    /// <summary>
    /// Приводить значення джерела до типу поля ТИМ САМИМ механізмом, що й ручний
    /// запис і імпорт: <see cref="CellValueReader.Normalize"/> +
    /// <see cref="RegistryValue.Set"/> (їх так само кличе
    /// <c>UpsertRegistryEntryHandler.ApplyValuesAsync</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється лише ТИП (і одиниця для нечислових полів). Існування цілі
    /// <c>Lookup</c>, обов'язковість, ключі й правила довідника перевіряє
    /// <c>RegistryEntryWriter</c> під час запису — їм потрібна база.
    /// <para>
    /// ⚠ Проєкція типізованого значення нижче — шість рядків, структурно ті самі,
    /// що приватний <c>UpsertRegistryEntryHandler.RawValue</c>: той метод
    /// приватний, а файл — поза зоною цього кроку. Кандидат на винесення в
    /// <c>RegistryValue</c> у спільному кроці з <c>RegistryEntryWriter</c>.
    /// </para>
    /// </remarks>
    private static bool TryConvert(
        RegistrySyncFieldMapping mapping,
        object? raw,
        out object? typed,
        [NotNullWhen(false)] out DomainException? rejection)
    {
        var probe = new RegistryValue(registryEntryId: 0, mapping.RegistryFieldDefId);

        try
        {
            probe.Set(mapping.DataType, CellValueReader.Normalize(raw), mapping.UnitId);
        }
        catch (DomainException ex)
        {
            typed = null;
            rejection = ex;
            return false;
        }

        typed = mapping.DataType switch
        {
            CellDataType.String => probe.ValueString,
            CellDataType.Int or CellDataType.Decimal => probe.ValueNumeric,
            CellDataType.Bool => probe.ValueBool,
            CellDataType.Date => probe.ValueDate,
            CellDataType.Lookup => probe.ValueRefEntryId,
            CellDataType.Unit => probe.ValueUnitId,
            _ => null,
        };
        rejection = null;
        return true;
    }

    private static Dictionary<string, T> ToUniqueMap<T>(
        IEnumerable<T> items, Func<T, string> key, string parameter)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!map.TryAdd(key(item), item))
            {
                throw new ArgumentException($"Дубль зовнішнього ідентифікатора «{key(item)}».", parameter);
            }
        }

        return map;
    }
}
