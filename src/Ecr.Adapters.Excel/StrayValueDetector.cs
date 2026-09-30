using ClosedXML.Excel;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Значення книги, які стоять ПОЗА рядками таблиць карти — під таблицею, у
/// таблиці без жодного рядка, між блоками.
/// </summary>
/// <remarks>
/// ⛔ `V-10`. Diff обходить лише комірки рядків із карти (<see cref="ExcelTableBlock.Rows"/>).
/// На порожньому документі в таблиці немає жодного рядка, і число, введене
/// користувачем одразу під заголовком, мовчки ігнорувалося — а діалог казав
/// «The file matches the sheet». Тепер таке значення — відмова з поясненням
/// («імпорт рядків не створює»), а не тиша.
///
/// ⚠ «Відоме» — рівно те, що пише експорт: назва таблиці (рядок над
/// заголовком, колонка 1), рядок заголовка і комірки рядків карти. Усе інше
/// непорожнє на аркуші таблиць — вміст, якого система не прийме.
/// </remarks>
public static class StrayValueDetector
{
    /// <summary>Відмови на кожне значення поза рядками таблиць аркуша.</summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="blocks">Блоки карти, що лежать на цьому аркуші, з описами таблиць.</param>
    public static IReadOnlyList<ImportRejection> Find(
        IXLWorksheet worksheet, IReadOnlyList<(ExcelTableBlock Block, TableDef Table)> blocks)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(blocks);

        var known = new HashSet<(int Row, int Column)>();
        var ordered = blocks.OrderBy(b => b.Block.HeaderRow).ToList();

        foreach (var (block, _) in ordered)
        {
            known.Add((block.HeaderRow - 1, 1));

            foreach (var column in block.Columns)
            {
                known.Add((block.HeaderRow, column.Number));

                foreach (var row in block.Rows)
                {
                    known.Add((row.Number, column.Number));
                }
            }
        }

        var rejected = new List<ImportRejection>();

        foreach (var cell in worksheet.CellsUsed(XLCellsUsedOptions.Contents))
        {
            var address = cell.Address;

            if (known.Contains((address.RowNumber, address.ColumnNumber))
                || (!cell.HasFormula && string.IsNullOrWhiteSpace(cell.GetString())))
            {
                continue;
            }

            // Таблиця — найближча ВИЩЕ за комірку: саме під її рядками людина
            // й дописала значення.
            var owner = ordered.LastOrDefault(b => b.Block.HeaderRow - 1 <= address.RowNumber);
            var column = owner.Block?.Columns.FirstOrDefault(c => c.Number == address.ColumnNumber);

            rejected.Add(new ImportRejection(
                "—",
                column?.Code ?? "—",
                "ECR-ROW-0404",
                $"Значення {address} стоїть поза рядками таблиці: імпорт рядків не створює.",
                owner.Table?.Code,
                owner.Table?.NameL10n,
                ImportMessageKeys.OutsideRows,
                address.ToString()));
        }

        return rejected;
    }
}
