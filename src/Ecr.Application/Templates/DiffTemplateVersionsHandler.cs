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
    /// <summary>Порівнює дві версії одного шаблону.</summary>
    /// <param name="versionId">Версія, з якої відкрили порівняння.</param>
    /// <param name="otherVersionId">Друга версія того самого шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ R-08: напрям порівняння — ЗАВЖДИ від старшої версії до новішої,
    /// незалежно від того, з якої з двох його відкрили. Доти «від» була
    /// версія адресного рядка: з нової версії порівняння зі старою показувало
    /// додану колонку як «Removed», а питання «що зміниться при переході»
    /// отримувало відповідь навпаки. Старша — менший ідентифікатор: версії
    /// заводяться лише вперед (створення, клон), і порядок ідентифікаторів —
    /// це порядок появи.
    /// </remarks>
    /// <exception cref="Errors.NotFoundException">Однієї з версій немає — <c>ECR-TMPL-0404</c>.</exception>
    /// <exception cref="Errors.BusinessRuleException">
    /// R-09: версії належать різним шаблонам — <c>ECR-TMPL-0422</c>. Порівняння
    /// структур двох різних форм за кодами дає перелік збігів імен, а не змін.
    /// </exception>
    public async Task<TemplateDiffDto> HandleAsync(int versionId, int otherVersionId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Template.View", ct)
            .ConfigureAwait(false);

        var template = await TemplateOfAsync(versionId, ct).ConfigureAwait(false);
        var otherTemplate = await TemplateOfAsync(otherVersionId, ct).ConfigureAwait(false);

        if (template != otherTemplate)
        {
            throw new Errors.BusinessRuleException(
                Domain.Errors.ErrorCodes.TemplateInvalid,
                $"Версії {versionId} і {otherVersionId} належать різним шаблонам: порівнюються лише версії одного шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.diffOtherTemplate",
                    ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
                    ["otherVersionId"] = otherVersionId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var fromVersionId = Math.Min(versionId, otherVersionId);
        var toVersionId = Math.Max(versionId, otherVersionId);

        var from = await metadata.GetAsync(fromVersionId, ct).ConfigureAwait(false);
        var to = await metadata.GetAsync(toVersionId, ct).ConfigureAwait(false);

        // ⚠ Вплив рахується від ВИХІДНОЇ (старшої) версії: питання «що
        // станеться, якщо перейти» має сенс лише разом із «скільки документів
        // це зачепить».
        // ⛔ X-12: СПРАВЖНЯ кількість, а не прапорець. Доти тут стояло
        // `hasDocuments ? 1 : 0`, і «зачеплено документів: 1» показувалося і
        // для одного, і для тисячі.
        var affected = await versions.CountDocumentsAsync(fromVersionId, ct).ConfigureAwait(false);

        var changes = new List<TemplateChangeDto>();
        Compare(from, to, affected > 0, changes);

        return new TemplateDiffDto(changes, affected, fromVersionId, toVersionId);
    }

    /// <summary>Шаблон версії; версії немає — <c>404</c>.</summary>
    private async Task<int> TemplateOfAsync(int templateVersionId, CancellationToken ct)
    {
        var template = await versions.FindTemplateOfVersionAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new Errors.NotFoundException(
                Domain.Errors.ErrorCodes.TemplateNotFound,
                $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                });

        return template.Id;
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
