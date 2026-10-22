// src/Ecr.Application/Permits/Bindings/PermitBindingValidator.cs
using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Чиста перевірка прив'язки таблиці аркуша до таблиці дозволу (Permits, PM-09): без БД, лише над фактами з
/// <see cref="PermitBindingContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Тут рішається, що саме можна записати, а не лише «чи форма заповнена»: прив'язка вирішує, які колонки
/// аркуша машина заповнить числами дозволу. Помилка в типі чи одиниці дала б у звітній формі не те число - без
/// жодного сигналу, бо конвертації у v1 немає.
/// </para>
/// <para>
/// ⚠ Усі ключі - повні літерали <c>err.ECR-PRM-0422.binding…</c>: сторож <c>ErrorTitleCatalogTests</c> звіряє
/// з сідом саме літерали.
/// </para>
/// </remarks>
public static class PermitBindingValidator
{
    /// <summary>Перевіряє прив'язку.</summary>
    /// <param name="input">Прив'язка із запиту.</param>
    /// <param name="context">Факти версії шаблону й типу дозволу.</param>
    public static PermitBindingValidation Validate(PermitBindingInput input, PermitBindingContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var errors = new List<PermitBindingIssue>();
        var warnings = new List<PermitBindingIssue>();

        if (!context.TypePublished)
        {
            errors.Add(Issue("err.ECR-PRM-0422.bindingTypeNotPublished", "Тип дозволу ще не опублікований."));
        }

        if (context.Sheet is not { } sheet)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingSheetNotInVersion",
                $"Таблиці {input.SheetTableDefId} у цій версії шаблону немає.",
                ("tableDefId", Str(input.SheetTableDefId))));
            return Result(errors, warnings);
        }

        CheckSelection(input, context, errors);

        if (input.Mode == PermitBindingMode.Formulas)
        {
            // Режим формул нічого не записує в комірки: зіставлення колонок у ньому було б мертвим і оманливим.
            if (input.Columns.Count > 0)
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingFormulasHaveColumns",
                    "У режимі формул колонки не зіставляються: формули самі читають дозвіл."));
            }

            return Result(errors, warnings);
        }

        CheckRowsMode(sheet, context, errors, warnings);
        CheckColumns(input, sheet, context, errors);

        return Result(errors, warnings);
    }

    private static void CheckSelection(PermitBindingInput input, PermitBindingContext context, List<PermitBindingIssue> errors)
    {
        if (input.SelectionRule == PermitSelectionRule.FromHeaderLookup)
        {
            var header = input.HeaderFieldDefId is { } id ? context.HeaderFields.FirstOrDefault(h => h.Id == id) : null;
            if (header is null)
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingHeaderFieldUnknown",
                    "Поля шапки, з якого береться дозвіл, у цій версії шаблону немає.",
                    ("headerFieldDefId", input.HeaderFieldDefId is { } v ? Str(v) : string.Empty)));
            }
            else if (header.DataType != CellDataType.Lookup)
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingHeaderFieldNotLookup",
                    $"Поле шапки «{header.Code}» не посилається на довідник.",
                    ("headerField", header.Code)));
            }

            return;
        }

        if (input.HeaderFieldDefId is not null)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingHeaderFieldUnexpected",
                "Поле шапки задається лише для правила «з поля шапки»."));
        }

        var facility = context.HeaderFields.FirstOrDefault(
            h => string.Equals(h.Code, PermitBindingFacility.HeaderFieldCode, StringComparison.OrdinalIgnoreCase));
        if (facility is null)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingFacilityFieldMissing",
                $"Для вибору дозволу за об'єктом шапка шаблону має містити поле «{PermitBindingFacility.HeaderFieldCode}».",
                ("fieldCode", PermitBindingFacility.HeaderFieldCode)));
        }
        else if (facility.DataType is not (CellDataType.String or CellDataType.Lookup))
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingFacilityFieldType",
                $"Поле шапки «{facility.Code}» має бути текстом або посиланням на довідник.",
                ("fieldCode", facility.Code), ("dataType", facility.DataType.ToString())));
        }
    }

    private static void CheckRowsMode(
        SheetTableInfo sheet, PermitBindingContext context, List<PermitBindingIssue> errors, List<PermitBindingIssue> warnings)
    {
        if (sheet.RowMode == TableRowMode.Fixed)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingRowsNeedDynamic",
                $"Таблиця «{sheet.Code}» має фіксовані рядки: рядки дозволу в неї не додати.",
                ("tableCode", sheet.Code)));
            return;
        }

        if (sheet.MaxDynamicRows is not { } max)
        {
            return;
        }

        if (context.PermitRowCount is not { } count)
        {
            warnings.Add(Issue(
                "binding.warning.rowCountUnknown",
                $"Кількість рядків у дозволах невідома; таблиця «{sheet.Code}» вміщує не більше {max}.",
                ("tableCode", sheet.Code), ("max", Str(max))));
        }
        else if (count > max)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingTooManyRows",
                $"У дозволі {count} рядків, а таблиця «{sheet.Code}» вміщує не більше {max}.",
                ("tableCode", sheet.Code), ("max", Str(max)), ("count", Str(count))));
        }
    }

    private static void CheckColumns(
        PermitBindingInput input, SheetTableInfo sheet, PermitBindingContext context, List<PermitBindingIssue> errors)
    {
        if (!input.Columns.Any(c => c.Role == PermitColumnRole.RowKey))
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingNoRowKey",
                "Потрібна хоча б одна колонка з роллю «ключ рядка»: без неї рядок аркуша не пов'язати з рядком дозволу."));
        }

        if (input.Columns.Count(c => c.Role == PermitColumnRole.Limit) > 1)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingManyLimits",
                "Роль «ліміт» може мати лише одна колонка."));
        }

        var seenFields = new HashSet<string>(StringComparer.Ordinal);
        var seenColumns = new HashSet<int>();

        foreach (var mapped in input.Columns)
        {
            var column = sheet.Columns.FirstOrDefault(c => c.Id == mapped.SheetColumnDefId);
            if (column is null)
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingColumnNotInSheet",
                    $"Колонки {mapped.SheetColumnDefId} у таблиці «{sheet.Code}» немає.",
                    ("columnId", Str(mapped.SheetColumnDefId)), ("tableCode", sheet.Code)));
                continue;
            }

            if (!seenColumns.Add(column.Id))
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingDuplicateColumn",
                    $"Колонка «{column.Code}» зіставлена двічі.",
                    ("columnCode", column.Code)));
            }

            var field = context.PermitFields.FirstOrDefault(f => string.Equals(f.Code, mapped.PermitFieldCode, StringComparison.Ordinal));
            if (field is null)
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingPermitFieldUnknown",
                    $"У таблиці дозволу немає поля «{mapped.PermitFieldCode}».",
                    ("fieldCode", mapped.PermitFieldCode)));
                continue;
            }

            if (!seenFields.Add(field.Code))
            {
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingDuplicateField",
                    $"Поле дозволу «{field.Code}» зіставлене двічі.",
                    ("fieldCode", field.Code)));
            }

            if (mapped.Role is PermitColumnRole.Limit or PermitColumnRole.Info && !column.IsReadOnly)
            {
                // Вимагаємо, а не ставимо самі: тихо змінити колонку шаблону - це правка структури поза волею автора.
                errors.Add(Issue(
                    "err.ECR-PRM-0422.bindingNotReadOnly",
                    $"Колонка «{column.Code}» має бути лише для читання: її значення пише машина.",
                    ("columnCode", column.Code)));
            }

            CheckCompatibility(field, column, errors);
        }
    }

    private static void CheckCompatibility(PermitFieldInfo field, SheetColumnInfo column, List<PermitBindingIssue> errors)
    {
        if (field.DataType != column.DataType)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingTypeMismatch",
                $"Поле «{field.Code}» ({field.DataType}) не підходить колонці «{column.Code}» ({column.DataType}): перетворення типів немає.",
                ("fieldCode", field.Code), ("columnCode", column.Code),
                ("permitType", field.DataType.ToString()), ("columnType", column.DataType.ToString())));
            return;
        }

        // PRMG (v1): це порівняння ДВОХ цілей (поле дозволу ↔ колонка), а не запису з ціллю. Колонка-група ≠ реєстр поля
        // дозволу, тож привʼязка поля `PRM_X` до колонки-групи відхиляється як і раніше (покрито DocumentTablesPermitBindingTests/
        // PermitBindingValidatorTests); розширювати на членство не потрібно - ліміти v1 не тягнемо.
        if (field.DataType == CellDataType.Lookup && (field.LookupRegistryDefId is null || field.LookupRegistryDefId != column.LookupRegistryDefId))
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingLookupMismatch",
                $"Поле «{field.Code}» і колонка «{column.Code}» посилаються на різні довідники.",
                ("fieldCode", field.Code), ("columnCode", column.Code)));
        }

        if (field.UnitId != column.UnitId)
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingUnitMismatch",
                $"Одиниці поля «{field.Code}» і колонки «{column.Code}» різні: перерахунку одиниць у v1 немає.",
                ("fieldCode", field.Code), ("columnCode", column.Code)));
        }

        if (field.DataType == CellDataType.Decimal && (field.Precision != column.Precision || field.Scale != column.Scale))
        {
            errors.Add(Issue(
                "err.ECR-PRM-0422.bindingPrecisionMismatch",
                $"Точність поля «{field.Code}» і колонки «{column.Code}» різні: значення довелося б округлювати.",
                ("fieldCode", field.Code), ("columnCode", column.Code)));
        }
    }

    private static PermitBindingValidation Result(List<PermitBindingIssue> errors, List<PermitBindingIssue> warnings)
        => new(errors, warnings);

    private static PermitBindingIssue Issue(string key, string text, params (string Name, string Value)[] args)
        => new(key, args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal), text);

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);
}
