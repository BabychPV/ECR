using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Мапінг подій/вікна, який не переїде: ключ для звіту й активність.</summary>
/// <param name="IsActive">Лише активні блокують перенос.</param>
/// <param name="Key">
/// <c>events SHEET.TABLE</c> або <c>window SHEET.TABLE.COLUMN</c> — лише шлях у шаблоні, без джерела й полів джерела.
/// </param>
public sealed record UnmappedExtendedMapping(bool IsActive, string Key);

/// <summary>Підсумок плану переносу мапінгів подій і вікон (D-1b RC20).</summary>
/// <param name="Remap">Що переносити.</param>
/// <param name="Remapped">Скільки мапінгів переїде.</param>
/// <param name="Unmapped">Мапінги, яким нема куди переїхати (активні й призупинені).</param>
public sealed record ExtendedMappingPlan(
    ExtendedMappingRemap Remap, int Remapped, IReadOnlyList<UnmappedExtendedMapping> Unmapped);

/// <summary>
/// Планує перенос мапінгів подій джерела (<c>ext.SourceEventMap</c>) і прив'язок вікна (<c>ext.RowWindowMap</c>)
/// між версіями шаблону (D-1b RC20) — чиста функція над планом переносу документів.
/// </summary>
/// <remarks>
/// ⚠ Відповідність — та сама, що й для комірок: <see cref="VersionMigrationPlan.Tables"/> і
/// <see cref="VersionMigrationPlan.Columns"/> (за кодами аркуш/таблиця/колонка), нової логіки відповідності немає.
/// <para>
/// Мапінг НЕ переїжджає (і тоді вважається «без відповідника»), коли: таблиці або будь-якої з його колонок немає в
/// цільовій версії; режим рядків таблиці змінився (подієвий мапінг лягає лише в динамічну таблицю); цільове місце
/// вже зайняте іншим мапінгом (<c>UQ_SourceEventMap</c>, <c>UQ_RowWindowMap_Target</c>) — такий перенос упав би
/// порушенням унікальності, а не мовчав.
/// </para>
/// </remarks>
public static class MappingRemapPlanner
{
    /// <summary>Будує план.</summary>
    /// <param name="source">Вихідна версія.</param>
    /// <param name="target">Цільова версія.</param>
    /// <param name="plan">План переносу документів.</param>
    /// <param name="eventMaps">Мапінги подій документів проєкту.</param>
    /// <param name="windowMaps">Прив'язки вікон на таблицях обох версій.</param>
    public static ExtendedMappingPlan Plan(
        TemplateVersionSnapshot source,
        TemplateVersionSnapshot target,
        VersionMigrationPlan plan,
        IReadOnlyList<EventMapRef> eventMaps,
        IReadOnlyList<RowWindowMapRef> windowMaps)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(eventMaps);
        ArgumentNullException.ThrowIfNull(windowMaps);

        var sourceTables = source.Sheets
            .SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t)))
            .ToDictionary(x => x.Table.Id);
        var targetTables = target.Sheets
            .SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t)))
            .ToDictionary(x => x.Table.Id);
        var columnPaths = source.Sheets
            .SelectMany(s => s.Tables.SelectMany(t => t.Columns.Select(c => (c.Id, Path: $"{s.Code}.{t.Code}.{c.Code}"))))
            .ToDictionary(x => x.Id, x => x.Path);
        var columns = plan.Columns.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId);

        var unmapped = new List<UnmappedExtendedMapping>();
        var events = new List<EventMapRemap>();
        var windows = new List<WindowMapRemap>();

        // ── Мапінги подій: за документом проєкту, ключ унікальності — (сутність, документ, таблиця) ──
        var takenEvents = eventMaps.Select(m => (m.SourceEntityId, m.DocumentId, m.TableDefId)).ToHashSet();
        foreach (var map in eventMaps.OrderBy(m => m.MapId))
        {
            if (!sourceTables.TryGetValue(map.TableDefId, out var from))
            {
                continue; // не таблиця вихідної версії — не наш мапінг
            }

            var key = $"events {from.Sheet.Code}.{from.Table.Code}";
            if (!plan.Tables.TryGetValue(map.TableDefId, out var newTable)
                || !targetTables.TryGetValue(newTable, out var to)
                || to.Table.RowMode != from.Table.RowMode
                || takenEvents.Contains((map.SourceEntityId, map.DocumentId, newTable))
                || map.Fields.Any(f => !columns.ContainsKey(f.ColumnDefId)))
            {
                unmapped.Add(new UnmappedExtendedMapping(map.IsActive, key));
                continue;
            }

            events.Add(new EventMapRemap(
                map.MapId, newTable, [.. map.Fields.Select(f => new EventFieldRemap(f.FieldId, columns[f.ColumnDefId]))]));
        }

        // ── Прив'язки вікна: ключ унікальності — (таблиця, колонка-ціль) ──
        var takenTargets = windowMaps.Select(m => (m.TableDefId, m.TargetColumnDefId)).ToHashSet();
        foreach (var map in windowMaps.OrderBy(m => m.MapId))
        {
            if (!sourceTables.ContainsKey(map.TableDefId))
            {
                continue; // прив'язка цільової версії (потрібна лише для виявлення зайнятого місця)
            }

            var key = "window " + (columnPaths.GetValueOrDefault(map.TargetColumnDefId) ?? $"table {map.TableDefId}");
            var used = new List<int> { map.TargetColumnDefId, map.StartColumnDefId, map.EndColumnDefId };
            if (map.SelectorColumnDefId is { } selector)
            {
                used.Add(selector);
            }

            if (!plan.Tables.TryGetValue(map.TableDefId, out var newTable)
                || used.Any(c => !columns.ContainsKey(c)))
            {
                unmapped.Add(new UnmappedExtendedMapping(map.IsActive, key));
                continue;
            }

            var newTarget = columns[map.TargetColumnDefId];
            if (takenTargets.Contains((newTable, newTarget)))
            {
                unmapped.Add(new UnmappedExtendedMapping(map.IsActive, key));
                continue;
            }

            windows.Add(new WindowMapRemap(
                map.MapId, newTable, newTarget,
                columns[map.StartColumnDefId], columns[map.EndColumnDefId],
                map.SelectorColumnDefId is { } s ? columns[s] : null));
        }

        return new ExtendedMappingPlan(
            new ExtendedMappingRemap(events, windows), events.Count + windows.Count, unmapped);
    }
}
