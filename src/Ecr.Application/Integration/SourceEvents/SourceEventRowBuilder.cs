// src/Ecr.Application/Integration/SourceEvents/SourceEventRowBuilder.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>Запис довідника, у який може лягти значення події.</summary>
/// <param name="Id">Id запису.</param>
/// <param name="Code">Код запису.</param>
/// <param name="Names">Назви запису всіма мовами.</param>
public sealed record SourceEventLookupEntry(long Id, string Code, IReadOnlyList<string> Names);

/// <summary>Поле мапінгу з усім, що потрібно, щоб перетворити значення події на значення комірки.</summary>
/// <param name="ColumnDefId">Колонка-адресат.</param>
/// <param name="ColumnCode">Код колонки — для звіту про незіставлене.</param>
/// <param name="ColumnType">Тип даних колонки.</param>
/// <param name="SourceAttribute">Атрибут події чи зарезервоване <c>$start</c>/<c>$end</c>/<c>$name</c>.</param>
/// <param name="Scope">Де лежить атрибут.</param>
/// <param name="ValueKind">Як значення лягає в колонку.</param>
/// <param name="SourceUnitId">Одиниця атрибута; <c>null</c> — без конверсії.</param>
/// <param name="TargetUnitId">Одиниця колонки; <c>null</c> — без конверсії.</param>
/// <param name="Entries">Для Lookup-колонки: живі активні записи її довідника.</param>
/// <param name="ValueMap">Для <see cref="SourceEventValueKind.ValueMap"/>: значення джерела (без регістру) → Id запису.</param>
public sealed record SourceEventFieldPlan(
    int ColumnDefId,
    string ColumnCode,
    CellDataType ColumnType,
    string SourceAttribute,
    SourceEventAttributeScope Scope,
    SourceEventValueKind ValueKind,
    int? SourceUnitId,
    int? TargetUnitId,
    IReadOnlyList<SourceEventLookupEntry> Entries,
    IReadOnlyDictionary<string, long> ValueMap);

/// <summary>Значення, яке не вдалося покласти в колонку.</summary>
/// <param name="Column">Код колонки.</param>
/// <param name="Value">Значення джерела; <c>null</c> — значення не було.</param>
public sealed record SourceEventUnmappedValue(string Column, string? Value);

/// <summary>Рядок події, готовий до запису, і те, що в нього не лягло.</summary>
/// <param name="Cells">Типізовані значення колонок.</param>
/// <param name="Unmapped">Значення без відповідника: не записані, у комірці нічого не вгадано.</param>
public sealed record SourceEventBuiltRow(
    IReadOnlyList<IntegrationRowCell> Cells,
    IReadOnlyList<SourceEventUnmappedValue> Unmapped);

/// <summary>
/// Перетворює подію на значення комірок за мапінгом (FEATURE-HSE301-VIEW §4.7.3, §4.7.4 крок 5).
/// Чиста функція: довідники, одиниці й пояс приходять готовими.
/// </summary>
/// <remarks>
/// ⛔ Вгадування немає. Значення, якому не знайшлося відповідника (запис довідника, число, одиниця),
/// у комірку НЕ пишеться, а йде в <see cref="SourceEventBuiltRow.Unmapped"/>: порожня комірка й
/// позначка чесніші за хибну категорію викидів. Неоднозначність (дві записи з тією самою назвою) —
/// теж «без відповідника».
/// <para>
/// ⚠ Каскадні колонки (<c>HmbCase</c> від <c>Stream</c>, §5.2) тут шукаються серед УСІХ записів довідника
/// колонки: обмеження записами батька — окремий крок; поки що назва, що збігається в кількох батьків,
/// лишається незіставленою, а не береться навмання.
/// </para>
/// </remarks>
public static class SourceEventRowBuilder
{
    /// <summary>Будує значення комірок події.</summary>
    /// <param name="ev">Подія; для запису — завершена.</param>
    /// <param name="fields">Поля мапінгу.</param>
    /// <param name="siteTimeZone">Пояс проєкту.</param>
    /// <param name="units">Довідник одиниць; потрібен, лише якщо є поле з обома одиницями.</param>
    public static SourceEventBuiltRow Build(
        SourceEvent ev, IReadOnlyList<SourceEventFieldPlan> fields, TimeZoneInfo siteTimeZone, UnitCatalogSnapshot? units)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        var cells = new List<IntegrationRowCell>();
        var unmapped = new List<SourceEventUnmappedValue>();

        foreach (var field in fields)
        {
            if (field.SourceAttribute.StartsWith('$'))
            {
                Reserved(ev, field, siteTimeZone, cells, unmapped);
                continue;
            }

            var attribute = ev.Attributes.FirstOrDefault(a => a.Scope == field.Scope
                && string.Equals(a.Name, field.SourceAttribute, StringComparison.OrdinalIgnoreCase));
            if (attribute is null)
            {
                // Джерело атрибута не дало: комірка лишається як є, не стирається.
                continue;
            }

            Attribute(attribute, field, siteTimeZone, units, cells, unmapped);
        }

        return new SourceEventBuiltRow(cells, unmapped);
    }

    private static void Reserved(
        SourceEvent ev,
        SourceEventFieldPlan field,
        TimeZoneInfo tz,
        List<IntegrationRowCell> cells,
        List<SourceEventUnmappedValue> unmapped)
    {
        switch (field.SourceAttribute)
        {
            case SourceEventMap.StartAttribute:
                AddDate(field, ev.StartUtc, tz, cells, unmapped);
                break;

            case SourceEventMap.EndAttribute:
                if (ev.EndUtc is { } end)
                {
                    AddDate(field, end, tz, cells, unmapped);
                }

                break;

            case SourceEventMap.NameAttribute:
                if (!string.IsNullOrWhiteSpace(ev.Name))
                {
                    AddText(field, ev.Name, cells, unmapped);
                }

                break;

            default:
                unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, field.SourceAttribute));
                break;
        }
    }

    private static void AddDate(
        SourceEventFieldPlan field, DateTime utc, TimeZoneInfo tz, List<IntegrationRowCell> cells, List<SourceEventUnmappedValue> unmapped)
    {
        if (field.ColumnType == CellDataType.Date)
        {
            cells.Add(new IntegrationRowCell(field.ColumnDefId, IntegrationValue.Date(SourceEventPeriods.ToProjectTime(utc, tz))));
        }
        else
        {
            unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, utc.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    private static void AddText(
        SourceEventFieldPlan field, string text, List<IntegrationRowCell> cells, List<SourceEventUnmappedValue> unmapped)
    {
        if (field.ColumnType == CellDataType.String)
        {
            cells.Add(new IntegrationRowCell(field.ColumnDefId, IntegrationValue.Text(text)));
        }
        else
        {
            unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
        }
    }

    private static void Attribute(
        SourceEventAttribute attribute,
        SourceEventFieldPlan field,
        TimeZoneInfo tz,
        UnitCatalogSnapshot? units,
        List<IntegrationRowCell> cells,
        List<SourceEventUnmappedValue> unmapped)
    {
        var text = attribute.ValueString?.Trim() is { Length: > 0 } s
            ? s
            : attribute.ValueNumeric?.ToString(CultureInfo.InvariantCulture);

        // Порожнє значення в джерелі — «немає що писати», а не «стерти комірку» (R-B4).
        if (text is null)
        {
            return;
        }

        if (field.ValueKind != SourceEventValueKind.Direct || field.ColumnType == CellDataType.Lookup)
        {
            Lookup(field, text, cells, unmapped);
            return;
        }

        switch (field.ColumnType)
        {
            case CellDataType.Decimal or CellDataType.Int:
                Number(attribute, field, text, units, cells, unmapped);
                break;

            case CellDataType.String:
                cells.Add(new IntegrationRowCell(field.ColumnDefId, IntegrationValue.Text(text)));
                break;

            case CellDataType.Date:
                // Час без зони — UTC, як увесь час порту подій; у комірку — пояс проєкту.
                if (DateTimeOffset.TryParse(
                        text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                {
                    cells.Add(new IntegrationRowCell(
                        field.ColumnDefId, IntegrationValue.Date(SourceEventPeriods.ToProjectTime(moment.UtcDateTime, tz))));
                }
                else
                {
                    unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
                }

                break;

            default:
                // Тип, якого запис від інтеграції не вміє (Bool, Unit, формула): значення не гублять мовчки.
                unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
                break;
        }
    }

    private static void Number(
        SourceEventAttribute attribute,
        SourceEventFieldPlan field,
        string text,
        UnitCatalogSnapshot? units,
        List<IntegrationRowCell> cells,
        List<SourceEventUnmappedValue> unmapped)
    {
        var number = attribute.ValueNumeric
                     ?? (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                         ? parsed
                         : (decimal?)null);
        if (number is not { } value)
        {
            unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
            return;
        }

        // ⛔ ФВ-16.9 (L3-06): фактична одиниця атрибута події ≠ оголошеної — не
        // конвертувати за оголошеною (тиха помилка ×24/×1000), а не писати комірку.
        if (field.SourceUnitId is not null
            && units is not null
            && !BoundaryUnitConversion.IsDeclaredUnit(field.SourceUnitId, attribute.SourceUnitSymbol, units))
        {
            unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
            return;
        }

        if (field.SourceUnitId is { } from && field.TargetUnitId is { } to && from != to)
        {
            try
            {
                value = BoundaryUnitConversion.Convert(value, from, to, units ?? UnitCatalogSnapshot.Empty);
            }
            catch (Exception ex) when (ex is DomainException or BusinessRuleException)
            {
                // Несумісні одиниці (Sm3 ↔ m3) — не тихе число: комірка не пишеться.
                unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
                return;
            }
        }

        cells.Add(new IntegrationRowCell(field.ColumnDefId, IntegrationValue.Number(value)));
    }

    private static void Lookup(
        SourceEventFieldPlan field, string text, List<IntegrationRowCell> cells, List<SourceEventUnmappedValue> unmapped)
    {
        long? entryId = null;

        if (field.ColumnType == CellDataType.Lookup)
        {
            entryId = field.ValueKind switch
            {
                SourceEventValueKind.LookupByCode => Single(field.Entries.Where(
                    e => string.Equals(e.Code, text, StringComparison.OrdinalIgnoreCase))),
                SourceEventValueKind.LookupByName => Single(field.Entries.Where(
                    e => e.Names.Any(n => string.Equals(n?.Trim(), text, StringComparison.OrdinalIgnoreCase)))),
                SourceEventValueKind.ValueMap => field.ValueMap.TryGetValue(text, out var mapped) ? mapped : null,
                _ => null,
            };
        }

        if (entryId is { } id)
        {
            cells.Add(new IntegrationRowCell(field.ColumnDefId, IntegrationValue.Lookup(id)));
        }
        else
        {
            unmapped.Add(new SourceEventUnmappedValue(field.ColumnCode, text));
        }
    }

    // Рівно один запис — інакше «без відповідника»: нуль — немає такого, кілька — неоднозначно.
    private static long? Single(IEnumerable<SourceEventLookupEntry> matches)
    {
        var list = matches.Take(2).ToList();
        return list.Count == 1 ? list[0].Id : null;
    }
}
