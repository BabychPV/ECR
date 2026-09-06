// src/Ecr.Application/Templates/DiffTemplateVersionsHandler.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>
/// Diff двох версій за **ідентичністю**, не за позицією (ФВ-2.7, АРХ-2).
/// </summary>
/// <remarks>
/// Порівняння за `Ordinal` дало б «змінено все» після будь-якого
/// перевпорядкування — саме та хиба, через яку в чинному рішенні неможливо
/// зрозуміти, що насправді змінилося.
/// </remarks>
public sealed class DiffTemplateVersionsHandler(
    IMetadataCache metadata,
    ITemplateVersionStore versions,
    ChangeClassifier classifier,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Порівнює дві версії.</summary>
    /// <param name="fromVersionId">Версія-джерело.</param>
    /// <param name="toVersionId">Версія-ціль.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<TemplateDiffDto> HandleAsync(int fromVersionId, int toVersionId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        var from = await metadata.GetAsync(fromVersionId, ct).ConfigureAwait(false);
        var to = await metadata.GetAsync(toVersionId, ct).ConfigureAwait(false);

        // ⚠ Вплив рахується від ВИХІДНОЇ версії: питання «що станеться, якщо
        // перейти» має сенс лише разом із «скільки документів це зачепить».
        // Diff без впливу — це список рядків, за яким рішення не ухвалюють.
        var hasDocuments = await versions.HasDocumentsAsync(fromVersionId, ct).ConfigureAwait(false);

        var changes = new List<TemplateChangeDto>();
        Compare(from, to, hasDocuments, changes);

        return new TemplateDiffDto(changes, hasDocuments ? 1 : 0);
    }

    /// <summary>Порівнює аркуші, таблиці, колонки і рядки за ідентичностями.</summary>
    private void Compare(
        TemplateVersionSnapshot from,
        TemplateVersionSnapshot to,
        bool hasDocuments,
        List<TemplateChangeDto> changes)
    {
        var fromSheets = from.Sheets.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var toSheets = to.Sheets.ToDictionary(s => s.Code, StringComparer.Ordinal);

        foreach (var (code, sheet) in fromSheets)
        {
            if (!toSheets.TryGetValue(code, out var other))
            {
                // Зникнення аркуша — Breaking: дані документів на ньому
                // лишаються без структури, тобто стають нечитними.
                changes.Add(Change(code, "Removed", classifier.ClassifyDeletion(nameof(SheetDef), hasDocuments), code, null));
                continue;
            }

            CompareTables(sheet, other, hasDocuments, changes);
        }

        foreach (var code in toSheets.Keys.Where(c => !fromSheets.ContainsKey(c)))
        {
            changes.Add(Change(code, "Added", ChangeClass.Safe, null, code));
        }
    }

    private void CompareTables(
        SheetDef from, SheetDef to, bool hasDocuments, List<TemplateChangeDto> changes)
    {
        var fromTables = from.Tables.ToDictionary(t => t.Code, StringComparer.Ordinal);
        var toTables = to.Tables.ToDictionary(t => t.Code, StringComparer.Ordinal);

        foreach (var (code, table) in fromTables)
        {
            var path = $"{from.Code}.{code}";
            if (!toTables.TryGetValue(code, out var other))
            {
                changes.Add(Change(path, "Removed", classifier.ClassifyDeletion(nameof(TableDef), hasDocuments), code, null));
                continue;
            }

            CompareColumns(path, table, other, hasDocuments, changes);
            CompareRows(path, table, other, hasDocuments, changes);
        }

        foreach (var code in toTables.Keys.Where(c => !fromTables.ContainsKey(c)))
        {
            changes.Add(Change($"{from.Code}.{code}", "Added", ChangeClass.Safe, null, code));
        }
    }

    private void CompareColumns(
        string tablePath, TableDef from, TableDef to, bool hasDocuments, List<TemplateChangeDto> changes)
    {
        var fromColumns = from.Columns.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var toColumns = to.Columns.ToDictionary(c => c.Code, StringComparer.Ordinal);

        foreach (var (code, column) in fromColumns)
        {
            var path = $"{tablePath}.{code}";
            if (!toColumns.TryGetValue(code, out var other))
            {
                changes.Add(Change(
                    path, "Removed", classifier.ClassifyDeletion(nameof(ColumnDef), hasDocuments), code, null));
                continue;
            }

            if (column.DataType != other.DataType)
            {
                changes.Add(Change(
                    path, "Modified",
                    classifier.Classify(nameof(ColumnDef), nameof(ColumnDef.DataType), hasDocuments),
                    column.DataType.ToString(), other.DataType.ToString()));
            }

            if (!column.IsRequired && other.IsRequired)
            {
                // Обов'язковість, додана на непорожні дані, робить наявні
                // документи невалідними — саме те, що ФВ-7.3 називає Guarded.
                changes.Add(Change(
                    path, "Modified",
                    classifier.Classify(nameof(ColumnDef), nameof(ColumnDef.IsRequired), hasDocuments),
                    "optional", "required"));
            }

            if (column.Ordinal != other.Ordinal)
            {
                // ⚠ Ordinal — ПРЕЗЕНТАЦІЯ, а не структура (ФВ-7.2). Нова версія
                // заради перестановки колонок не потрібна; сплутати ці два
                // класи означало б плодити версії на кожен косметичний рух.
                changes.Add(Change(
                    path, "Presentation", ChangeClass.Presentation,
                    column.Ordinal.ToString(CultureInfo.InvariantCulture),
                    other.Ordinal.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var code in toColumns.Keys.Where(c => !fromColumns.ContainsKey(c)))
        {
            changes.Add(Change(
                $"{tablePath}.{code}", "Added", classifier.ClassifyAddition(nameof(ColumnDef)), null, code));
        }
    }

    private void CompareRows(
        string tablePath, TableDef from, TableDef to, bool hasDocuments, List<TemplateChangeDto> changes)
    {
        var fromRows = from.Rows.ToDictionary(r => r.RowKeyValue, StringComparer.Ordinal);
        var toRows = to.Rows.ToDictionary(r => r.RowKeyValue, StringComparer.Ordinal);

        foreach (var (key, row) in fromRows)
        {
            var path = $"{tablePath}.{key}";
            if (!toRows.TryGetValue(key, out var other))
            {
                changes.Add(Change(path, "Removed", classifier.ClassifyDeletion(nameof(RowDef), hasDocuments), key, null));
                continue;
            }

            if (row.Ordinal != other.Ordinal)
            {
                changes.Add(Change(
                    path, "Presentation", ChangeClass.Presentation,
                    row.Ordinal.ToString(CultureInfo.InvariantCulture),
                    other.Ordinal.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var key in toRows.Keys.Where(k => !fromRows.ContainsKey(k)))
        {
            changes.Add(Change($"{tablePath}.{key}", "Added", ChangeClass.Safe, null, key));
        }
    }

    private static TemplateChangeDto Change(
        string path, string kind, ChangeClass changeClass, string? oldValue, string? newValue)
        => new(path, kind, changeClass, oldValue, newValue);
}
