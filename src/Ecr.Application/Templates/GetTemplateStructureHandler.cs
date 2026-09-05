// src/Ecr.Application/Templates/GetTemplateStructureHandler.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Templates;

/// <summary>
/// Структура опублікованої версії — **з кешу, без звернення до БД**.
/// Ключ `v{id}:r{rev}` (ФВ-2.5) робить інвалідацію непотрібною: інша
/// ревізія — інший ключ.
/// </summary>
public sealed class GetTemplateStructureHandler(IMetadataCache metadata)
{
    /// <summary>Повертає структуру версії.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<TemplateStructureDto> HandleAsync(int templateVersionId, CancellationToken ct)
    {
        // ⛔ Жодного звернення до DbContext звідси: воно заборонене
        // архітектурним тестом, і не заради чистоти шарів. Структура читається
        // на кожне відкриття таблиці; похід у базу тут з'їв би весь бюджет
        // запиту ще до того, як почнеться читання даних.
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var sheets = snapshot.Sheets
            .OrderBy(s => s.Ordinal)
            .Select(sheet => new SheetDto(
                sheet.Id,
                sheet.Code,
                sheet.NameL10n,
                sheet.Ordinal,
                [.. sheet.Tables.OrderBy(t => t.Ordinal).Select(Table)]))
            .ToList();

        return new TemplateStructureDto(
            snapshot.TemplateVersionId, snapshot.PresentationRevision, sheets);
    }

    private static TableDto Table(TableDef table)
        => new(
            table.Id,
            table.Code,
            table.LayoutKind,
            table.RowMode,
            table.MaxDynamicRows,
            [.. table.Columns.OrderBy(c => c.Ordinal).Select(Column)],
            [.. RowsOf(table)]);

    /// <summary>Рядки таблиці з розгорнутою ієрархією за ключами.</summary>
    private static IEnumerable<TemplateRowDto> RowsOf(TableDef table)
    {
        var keysById = table.Rows.ToDictionary(r => r.Id, r => r.RowKeyValue);
        return table.Rows.OrderBy(r => r.Ordinal).Select(r => Row(r, keysById));
    }

    private static TemplateColumnDto Column(ColumnDef column)
        => new(
            column.Id,
            column.Code,

            // ⚠ Заголовок віддається ВСІМА мовами, а не однією. Екран
            // структури — це редактор презентаційного шару, і правка підпису
            // англійською не має стирати російський із казахським.
            column.HeaderL10n,
            column.DataType.ToString(),
            column.Ordinal,
            column.IsReadOnly,
            column.IsRequired,
            column.IsHidden,
            column.DisplayFormat,
            UnitSymbol: null);

    private static TemplateRowDto Row(RowDef row, Dictionary<int, string> keysById)
        => new(
            row.RowKeyValue,
            row.Ordinal,
            row.RowKind.ToString(),
            row.LabelL10n.Get("en"),

            // ⚠ Батько віддається КЛЮЧЕМ, а не Id: клієнт будує ієрархію за
            // ідентичностями, які переживають клон версії. Id після клону інші.
            row.ParentRowDefId is { } parent && keysById.TryGetValue(parent, out var key) ? key : null,
            row.IsReadOnly);
}
